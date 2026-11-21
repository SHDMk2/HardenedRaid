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

    // ---- siege : siège motorisé, passagers à bord (R22)
    /// <summary>
    /// Pawn d'un lord transporté dans un véhicule (VF) : non apparu, et son conteneur (VehicleRoleHandler) a pour
    /// parent un véhicule apparu sur la carte. Sans type VF : <see cref="Thing.SpawnedParentOrMe"/> remonte les
    /// conteneurs (pawn → VehicleRoleHandler → VehiclePawn) et rend le véhicule.
    /// </summary>
    public static class SiegeAboard
    {
        public static bool Aboard(Pawn p, Map map) =>
            p != null && !p.Spawned && p.SpawnedParentOrMe is Pawn v && v != p && v.Map == map && VehicleGate.IsVehicle(v);
    }

    /// <summary>
    /// Arrivée d'un siège motorisé. Vanilla (LordToil_Travel.LordToilTick, toutes les 205 ticks) n'envoie
    /// TravelArrived que si TOUS les membres sont à 10 cases du point et CanReach, qui est faux pour un pawn non
    /// apparu : tant qu'un passager est à bord, le siège n'arrive jamais. Ici un passager à bord compte comme
    /// arrivé (il arrive avec le convoi, qui se gare au camp), le test vanilla reste pour les autres, et sans
    /// aucun pawn apparu rien n'est envoyé (on attend le débarquement ; le délai vanilla de 5000 ticks reste).
    /// Prefix plutôt que transpiler : il ne prend la main que dans ce seul cas (lord LordJob_Siege, toil
    /// LordToil_Travel exact, au moins un passager à bord, tick de test) ; partout ailleurs, la méthode vanilla et
    /// les patchs d'autres mods s'exécutent sans changement, et rien ne dépend de la forme de l'IL.
    /// </summary>
    [HarmonyPatch(typeof(LordToil_Travel), nameof(LordToil_Travel.LordToilTick))]
    public static class Patch_SiegeArrivalAboard
    {
        private const float Radius = 10f;   // LordToil_Travel.AllArrivedCheckRadius (non surchargé pour le type exact)

        public static bool Prefix(LordToil_Travel __instance)
        {
            if (Find.TickManager.TicksGame % 205 != 0 || !VehicleGate.Active) return true;
            try
            {
                Lord lord = __instance.lord;
                if (lord?.LordJob is not LordJob_Siege || __instance.GetType() != typeof(LordToil_Travel)) return true;
                Map map = lord.Map;
                List<Pawn> pawns = lord.ownedPawns;
                if (map == null || !pawns.Any(p => SiegeAboard.Aboard(p, map))) return true;
                IntVec3 dest = __instance.FlagLoc;
                bool anyOnFoot = false;
                foreach (Pawn p in pawns)
                {
                    if (SiegeAboard.Aboard(p, map)) continue;
                    if (!p.Spawned || !p.Position.InHorDistOf(dest, Radius) || !p.CanReach(dest, PathEndMode.ClosestTouch, Danger.Deadly)) return false;
                    anyOnFoot = true;
                }
                if (anyOnFoot)
                {
                    HRLog.Msg($"siège {lord.faction?.Name} : arrivé au camp {dest} ({pawns.Count(p => SiegeAboard.Aboard(p, map))} passager(s) encore à bord).");
                    lord.ReceiveMemo("TravelArrived");
                }
                return false;
            }
            catch (Exception e)
            {
                Log.WarningOnce("[Hardened Raids] siege arrival: " + e, 0x48525341);
                return true;
            }
        }
    }

    /// <summary>
    /// (D6) Un passager à bord tué ou mis à terre (signal PawnLost : vanilla ne signale pas les simples
    /// blessures d'un pawn non apparu, Pawn_HealthTracker.PostApplyDamage) ne fait basculer ni le voyage en
    /// défense, ni le camp en assaut. Test « non apparu » seul : à la mort, VF (Patch_Misc.MoveOnDeath) puis
    /// vanilla (Pawn.Kill) l'ont déjà sorti du véhicule. Un pawn apparu blessé déclenche comme en vanilla.
    /// Nouvelle classe, à côté de <see cref="Patch_SiegeHoldUnderFire"/> (inchangé).
    /// </summary>
    [HarmonyPatch(typeof(Trigger_PawnHarmed), nameof(Trigger_PawnHarmed.ActivateOn))]
    public static class Patch_SiegeIgnoreHarmAboard
    {
        public static bool Prefix(Lord lord, TriggerSignal signal, ref bool __result)
        {
            if (lord?.LordJob is not LordJob_Siege || !VehicleGate.Active) return true;
            Pawn p = signal.Pawn;
            if (p == null || p.Spawned || !Trigger_PawnHarmed.SignalIsHarm(signal)) return true;
            __result = false;
            return false;
        }
    }

    /// <summary>
    /// Défense d'un siège en voyage (TransitionAction_SetDefendLocalGroup) : vanilla prend la Position d'un
    /// membre au hasard, passager compris, dont la position est périmée (celle de son apparition au bord, DeSpawn
    /// ne la remet pas à zéro). Pour un siège : un membre apparu ; à défaut, le véhicule qui le porte.
    /// </summary>
    [HarmonyPatch(typeof(TransitionAction_SetDefendLocalGroup), nameof(TransitionAction_SetDefendLocalGroup.DoAction))]
    public static class Patch_SiegeDefendPointAboard
    {
        public static bool Prefix(Transition trans)
        {
            try
            {
                if (trans?.target is not LordToil_DefendPoint toil || toil.lord?.LordJob is not LordJob_Siege || toil.lord.Map == null) return true;
                List<Pawn> pawns = toil.lord.ownedPawns;
                if (pawns.Count == 0 || pawns.All(p => p.Spawned)) return true;
                Thing at = pawns.Where(p => p.Spawned).RandomElementWithFallback()
                           ?? pawns.Select(p => p.SpawnedParentOrMe).Where(t => t != null && t.Map == toil.lord.Map).RandomElementWithFallback();
                if (at == null) return true;
                toil.SetDefendPoint(at.Position);
                return false;
            }
            catch (Exception e)
            {
                Log.WarningOnce("[Hardened Raids] siege defend point: " + e, 0x48525344);
                return true;
            }
        }
    }
}
