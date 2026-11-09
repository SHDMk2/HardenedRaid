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
}
