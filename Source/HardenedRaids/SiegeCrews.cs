using System;
using System.Collections.Generic;
using System.Linq;
using HarmonyLib;
using RimWorld;
using Verse;
using Verse.AI;
using Verse.AI.Group;

namespace HardenedRaids
{
    /// <summary>
    /// Un camp de siège qui tient (camps améliorés : réglage SiegeUpgrade, et équipes d'appui) :
    ///  - (E) les blessures ne déclenchent plus l'assaut (Trigger_PawnHarmed de LordJob_Siege neutralisé pendant
    ///    LordToil_Siege, réglage SiegeHoldUnderFire) ; restent les 30 % de pertes, NoBuilders, NoArtillery et le délai ;
    ///  - (F) au passage à l'assaut, l'artillerie et les tourelles gardent leur faction et continuent de tirer :
    ///    retirées des bâtiments du lord avant le Cleanup vanilla (PreAction), puis confiées avec leurs servants
    ///    attitrés (<see cref="TurretManning"/>) à un lord <see cref="LordJob_HRTurretCrew"/> (PostAction), déjà
    ///    ravitaillé et converti par la désactivation. Ses pièces redeviennent neutres à la fin de ce lord.
    /// Le graphe de LordJob_Siege garde ses toils et triggers (données sauvegardées par index) : seules deux
    /// TransitionAction (non sauvegardées) sont ajoutées à la transition siège → assaut.
    /// </summary>
    public static class SiegeCrews
    {
        // pièces et servants retenus entre la PreAction et la PostAction d'une même transition (même pile d'appel)
        private static Lord pendingLord;
        private static List<Building> pendingPieces;
        private static List<Pawn> pendingGunners;

        private static void Clear()
        {
            pendingLord = null;
            pendingPieces = null;
            pendingGunners = null;
        }

        /// <summary>PreAction (avant LordToil_Siege.Cleanup, qui rend neutres les bâtiments du lord).</summary>
        public static void BeforeAssault(Lord lord)
        {
            Clear();
            if (lord?.Map == null || lord.faction == null || lord.CurLordToil is not LordToil_Siege || !SiegeCamp.Enhanced(lord)) return;
            List<Building> pieces = lord.ownedBuildings.Where(b => b != null && b.Spawned && !b.Destroyed && SiegeCamp.IsPiece(b.def)).ToList();
            if (pieces.Count == 0) return;
            List<Pawn> gunners = lord.ownedPawns.Where(p => p.Spawned && !p.Dead && !p.Downed && p.mindState?.duty?.def == DutyDefOf.ManClosestTurret).ToList();
            // le lord de siège garde au moins un pawn : vide, il serait détruit et l'équipage croirait le raid fini
            if (gunners.Count > 0 && gunners.Count >= lord.ownedPawns.Count) gunners.RemoveAt(gunners.Count - 1);
            if (gunners.Count == 0) return;   // personne pour servir : vanilla rend les pièces neutres, comme avant
            foreach (Building b in pieces) lord.ownedBuildings.Remove(b);
            pendingLord = lord;
            pendingPieces = pieces;
            pendingGunners = gunners;
        }

        /// <summary>PostAction (le lord est passé à l'assaut) : lord d'équipage pour les pièces et leurs servants.</summary>
        public static void AfterAssault(Lord lord)
        {
            if (lord == null || lord != pendingLord) { Clear(); return; }
            List<Building> pieces = pendingPieces.Where(b => b.Spawned && !b.Destroyed && b.Faction == lord.faction).ToList();
            List<Pawn> gunners = pendingGunners.Where(p => p.Spawned && !p.Dead && p.GetLord() == lord).ToList();
            Clear();
            if (pieces.Count == 0) return;
            if (gunners.Count == 0)
            {
                foreach (Building b in pieces) b.SetFaction(null);   // ce qu'aurait fait le Cleanup vanilla
                return;
            }
            Map map = lord.Map;
            foreach (Pawn p in gunners) lord.Notify_PawnLost(p, PawnLostCondition.ForcedToJoinOtherLord);
            Lord crew = LordMaker.MakeNewLord(lord.faction, new LordJob_HRTurretCrew(lord.faction) { siegeCamp = true }, map, gunners);
            foreach (Building b in pieces)
            {
                crew.AddBuilding(b);
                if (b is Building_Turret t) MapComponent_HostileTurretSupply.Register(t);   // ravitaillement pendant l'assaut
            }
            HRLog.Msg($"siège {lord.faction.Name} : assaut, {pieces.Count} pièce(s) gardée(s) en service avec {gunners.Count} servant(s) "
                      + $"({string.Join(", ", pieces.GroupBy(b => b.def.label).Select(g => g.Count() + "× " + g.Key))}).");
        }
    }

    public class TransitionAction_HRKeepSiegePieces : TransitionAction
    {
        public override void DoAction(Transition trans)
        {
            try { SiegeCrews.BeforeAssault(trans.target?.lord); }
            catch (Exception e) { Log.Warning("[Hardened Raids] siege crews (before assault): " + e); }
        }
    }

    public class TransitionAction_HRSiegeCrews : TransitionAction
    {
        public override void DoAction(Transition trans)
        {
            try { SiegeCrews.AfterAssault(trans.target?.lord); }
            catch (Exception e) { Log.Warning("[Hardened Raids] siege crews (after assault): " + e); }
        }
    }

    /// <summary>Transition siège → assaut : actions ajoutées, toils et triggers inchangés (ni nombre, ni ordre).</summary>
    [HarmonyPatch(typeof(LordJob_Siege), nameof(LordJob_Siege.CreateGraph))]
    public static class Patch_SiegeGraphCrews
    {
        public static void Postfix(StateGraph __result)
        {
            if (__result?.transitions == null) return;
            foreach (Transition t in __result.transitions)
            {
                if (t.target == null || t.target is LordToil_ExitMap || t.target is LordToil_Siege) continue;
                if (t.sources == null || !t.sources.Any(s => s is LordToil_Siege)) continue;
                t.AddPreAction(new TransitionAction_HRKeepSiegePieces());
                t.AddPostAction(new TransitionAction_HRSiegeCrews());
            }
        }
    }

    /// <summary>(E) Camp amélioré en place : une blessure ne lance pas l'assaut.</summary>
    [HarmonyPatch(typeof(Trigger_PawnHarmed), nameof(Trigger_PawnHarmed.ActivateOn))]
    public static class Patch_SiegeHoldUnderFire
    {
        public static bool Prefix(Lord lord, ref bool __result)
        {
            if (!HRSettings.SiegeHoldUnderFire || lord?.LordJob is not LordJob_Siege || lord.CurLordToil is not LordToil_Siege || !SiegeCamp.Enhanced(lord))
                return true;
            __result = false;
            return false;
        }
    }
}
