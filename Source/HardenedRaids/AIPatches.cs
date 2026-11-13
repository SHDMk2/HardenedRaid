using System;
using System.Reflection;
using HarmonyLib;
using RimWorld;
using UnityEngine;
using Verse;
using Verse.AI;

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

    /// <summary>Les raiders T2+ contournent toujours les zones couvertes par les tourelles (grille d'évitement vanilla).</summary>
    [HarmonyPatch(typeof(PawnUtility), nameof(PawnUtility.TryGetAvoidGrid))]
    public static class Patch_AvoidGrid
    {
        public static void Postfix(Pawn p, ref AvoidGrid grid, ref bool __result)
        {
            if (__result || !HRSettings.VeteransAvoidTurrets || p == null || !p.Spawned || p.Faction == null) return;
            if (!p.Faction.def.canUseAvoidGrid || p.Faction.IsPlayer || !p.Faction.HostileTo(Faction.OfPlayer)) return;
            if (PawnTuning.TierOf(p) < 2) return;
            grid = p.Map.avoidGrid;
            __result = grid != null;
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
