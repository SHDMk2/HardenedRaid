using System;
using System.Collections.Generic;
using System.Reflection;
using System.Reflection.Emit;
using HarmonyLib;
using RimWorld;
using UnityEngine;
using Verse;
using Verse.AI;
using Verse.AI.Group;

namespace HardenedRaids
{
    /// <summary>Plus le palier est haut, plus les raids choisissent des stratégies intelligentes (smart, brèche, sape).</summary>
    [HarmonyPatch(typeof(RaidStrategyWorker), nameof(RaidStrategyWorker.SelectionWeightForFaction))]
    public static class Patch_StrategyWeight
    {
        public static void Postfix(RaidStrategyWorker __instance, Faction faction, float basePoints, ref float __result)
        {
            if (!HRSettings.SmarterStrategies || __result <= 0f || faction == null || !faction.def.humanlikeFaction || !faction.HostileTo(Faction.OfPlayer))
                return;
            float x = Tiers.TierX(faction.def, basePoints);
            if (x < 2f) return;
            float s = Mathf.Clamp01((x - 1f) / 4f); // 0 au palier 1, 1 au palier 5+
            switch (__instance.def.defName)
            {
                case "ImmediateAttack": __result *= 1f - 0.6f * s; break;
                case "ImmediateAttackSmart": __result *= 1f + 2f * s; break;
                case "ImmediateAttackBreaching": __result *= 1f + 2f * s; break;
                case "ImmediateAttackBreachingSmart": __result *= 1f + 3f * s; break;
                case "ImmediateAttackSappers": __result *= 1f + 1.5f * s; break;
                case "Siege": __result *= 1f + 1.5f * s; break;
            }
        }
    }

    /// <summary>
    /// Les raiders T2+ contournent les zones couvertes par les tourelles du joueur (grille d'évitement vanilla),
    /// même quand leur lord ne l'active pas (assaut « non smart »). Vanilla (PawnUtility.TryGetAvoidGrid) :
    /// grille si kindDef.canUseAvoidGrid, sinon si onlyIfLordAllows == false (CastPositionFinder), sinon seulement
    /// si CurLordToil.useAvoidGrid (assaut smart, fuite, enlèvement, vol, sortie de carte l'ont déjà). On n'ajoute
    /// la grille que si vanilla l'a refusée ET que :
    ///  - l'appel respecte le lord (onlyIfLordAllows) ;
    ///  - le pawn est dans un toil d'assaut de colonie (pas de grille ajoutée en étape, défense, siège, garde, sans lord…) ;
    ///  - il n'est pas en état mental, ne porte rien (otage, butin) et son job n'est ni fuite, ni enlèvement, ni vol,
    ///    ni sortie de carte ;
    ///  - l'appel ne vient pas de Region.Allows : là, une case de porte saturée (255) rend la porte infranchissable ;
    ///    notre grille ne fait que renchérir le chemin, elle ne bloque jamais un pawn.
    /// La grille elle-même est celle de vanilla (Map.avoidGrid : tourelles du joueur seulement, régénérée
    /// paresseusement quand une tourelle / un mur apparaît ou disparaît) : aucun coût ajouté.
    /// </summary>
    [HarmonyPatch(typeof(PawnUtility), nameof(PawnUtility.TryGetAvoidGrid))]
    public static class Patch_AvoidGrid
    {
        /// <summary>&gt; 0 pendant l'appel de TryGetAvoidGrid fait par Region.Allows (voir Patch_RegionAllowsAvoidGrid).</summary>
        [ThreadStatic] private static int regionDepth;

        public static void Postfix(Pawn p, bool onlyIfLordAllows, ref AvoidGrid grid, ref bool __result)
        {
            if (__result || !onlyIfLordAllows || regionDepth > 0 || !HRSettings.VeteransAvoidTurrets || p == null || !p.Spawned) return;
            Faction f = p.Faction;
            if (f == null || !f.def.canUseAvoidGrid || f.IsPlayer || !f.HostileTo(Faction.OfPlayer)) return;
            LordToil toil = p.GetLord()?.CurLordToil;
            if (!(toil is LordToil_AssaultColony || toil is LordToil_AssaultColonySappers || toil is LordToil_AssaultColonyBreaching)) return;
            if (p.InMentalState || p.carryTracker?.CarriedThing != null) return;
            Job job = p.CurJob;
            if (job != null && (job.exitMapOnArrival || job.def == JobDefOf.Flee || job.def == JobDefOf.FleeAndCower
                || job.def == JobDefOf.Kidnap || job.def == JobDefOf.Steal || job.def == JobDefOf.CarryDownedPawnToExit)) return;
            if (PawnTuning.TierOf(p) < 2) return;
            grid = p.Map.avoidGrid;
            __result = grid != null;
        }

        /// <summary>Appelé à la place de TryGetAvoidGrid dans Region.Allows (test des portes) : vanilla seul, sans notre ajout.</summary>
        public static bool TryGetForRegion(Pawn p, out AvoidGrid grid, bool onlyIfLordAllows)
        {
            regionDepth++;
            try { return p.TryGetAvoidGrid(out grid, onlyIfLordAllows); }
            finally { regionDepth--; }
        }
    }

    /// <summary>
    /// Region.Allows : une porte dont une case vaut 255 dans la grille d'évitement est infranchissable. On redirige
    /// son appel à TryGetAvoidGrid pour que notre grille ajoutée n'y compte pas (seul vanilla peut bloquer une porte).
    /// Le détour n'est pris que sur le chemin « porte + pawn », pas à chaque appel de Region.Allows.
    /// </summary>
    [HarmonyPatch(typeof(Region), nameof(Region.Allows))]
    public static class Patch_RegionAllowsAvoidGrid
    {
        public static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions)
        {
            MethodInfo from = AccessTools.Method(typeof(PawnUtility), nameof(PawnUtility.TryGetAvoidGrid));
            MethodInfo to = AccessTools.Method(typeof(Patch_AvoidGrid), nameof(Patch_AvoidGrid.TryGetForRegion));
            int n = 0;
            foreach (CodeInstruction ci in instructions)
            {
                if (ci.Calls(from)) { ci.opcode = OpCodes.Call; ci.operand = to; n++; }
                yield return ci;
            }
            if (n == 0) Log.Warning("[Hardened Raids] Region.Allows : appel à TryGetAvoidGrid introuvable, les portes peuvent bloquer les vétérans.");
        }
    }

    /// <summary>
    /// Tourelle hors service = plus une menace : vanilla ne regarde que le courant et le servant ; on ajoute le
    /// chargeur vide (CE) et le carburant / les munitions épuisés (CompRefuelable). L'IA cesse donc de la viser.
    /// </summary>
    [HarmonyPatch(typeof(Building_Turret), nameof(Building_Turret.ThreatDisabled))]
    public static class Patch_TurretThreat
    {
        private static bool init;
        private static Type ammoUser;
        private static PropertyInfo useAmmo, emptyMag;

        public static void Postfix(Building_Turret __instance, ref bool __result)
        {
            if (__result || !HRSettings.IgnoreDeadTurrets) return;
            try { __result = OutOfService(__instance); }
            catch { }
        }

        public static bool OutOfService(Building_Turret t)
        {
            CompRefuelable fuel = t.GetComp<CompRefuelable>();
            if (fuel != null && !fuel.HasFuel) return true;
            if (!init)
            {
                init = true;
                ammoUser = AccessTools.TypeByName("CombatExtended.CompAmmoUser");
                if (ammoUser != null)
                {
                    useAmmo = AccessTools.Property(ammoUser, "UseAmmo");
                    emptyMag = AccessTools.Property(ammoUser, "EmptyMagazine");
                }
            }
            if (useAmmo == null || emptyMag == null) return false;
            if (t is Building_TurretGun vg) return Empty(vg) || Empty(vg.gun as ThingWithComps);
            if (!gunProps.TryGetValue(t.GetType(), out PropertyInfo gp))
                gunProps[t.GetType()] = gp = AccessTools.Property(t.GetType(), "Gun");
            return Empty(t) || Empty(gp?.GetValue(t) as ThingWithComps);
        }

        private static readonly System.Collections.Generic.Dictionary<Type, PropertyInfo> gunProps = new System.Collections.Generic.Dictionary<Type, PropertyInfo>();

        private static bool Empty(ThingWithComps th)
        {
            if (th == null) return false;
            foreach (ThingComp comp in th.AllComps)
                if (ammoUser.IsInstanceOfType(comp)) return (bool)useAmmo.GetValue(comp) && (bool)emptyMag.GetValue(comp);
            return false;
        }
    }

    /// <summary>Les raiders ne « saccagent » pas (tir à volonté) une tourelle hors service : ils passent aux vraies cibles.</summary>
    [HarmonyPatch(typeof(TrashUtility), nameof(TrashUtility.ShouldTrashBuilding), new[] { typeof(Pawn), typeof(Building), typeof(bool) })]
    public static class Patch_TrashDeadTurret
    {
        public static void Postfix(Pawn pawn, Building b, ref bool __result)
        {
            if (!__result || !HRSettings.IgnoreDeadTurrets || pawn?.Faction == null || pawn.Faction.IsPlayer) return;
            if (b is Building_Turret t && t.ThreatDisabled(pawn)) __result = false;
        }
    }
}
