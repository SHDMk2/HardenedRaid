using System.Collections.Generic;
using System.Linq;
using HarmonyLib;
using System.Text;
using LudeonTK;
using RimWorld;
using Verse;

namespace HardenedRaids
{
    /// <summary>Menus dev « Execute raid with points / faction / specifics » : ajoute des valeurs au-delà de 10 000
    /// et les points actuels de la carte.</summary>
    [HarmonyPatch(typeof(DebugActionsUtility), nameof(DebugActionsUtility.PointsOptions))]
    public static class Patch_DebugPointsOptions
    {
        private static readonly float[] Extra = { 15000f, 20000f, 30000f, 50000f, 75000f, 100000f, 150000f, 200000f, 300000f };

        public static void Postfix(bool extended, ref IEnumerable<float> __result)
        {
            if (!extended) return;
            var list = __result.ToList();
            list.AddRange(Extra);
            if (Find.CurrentMap != null)
            {
                try { list.Add(UnityEngine.Mathf.Round(StorytellerUtility.DefaultThreatPointsNow(Find.CurrentMap))); } catch { }
            }
            __result = list.Distinct().OrderBy(x => x).ToList();
        }
    }

    public static class HRDebugActions
    {
        [DebugAction("Hardened Raids", "Preview raid plans (log)", allowedGameStates = AllowedGameStates.PlayingOnMap)]
        public static void PreviewPlans()
        {
            float now = StorytellerUtility.DefaultThreatPointsNow(Find.CurrentMap);
            var sb = new StringBuilder();
            sb.AppendLine($"[Hardened Raids] Aperçu — points actuels sur la carte : {now:F0}");
            float[] pts = { 1500f, 4000f, 10000f, 35000f, now };
            var rng = new System.Random();
            foreach (Faction f in Find.FactionManager.AllFactionsListForReading
                         .Where(f => !f.IsPlayer && f.def.humanlikeFaction && f.HostileTo(Faction.OfPlayer) && f.def.pawnGroupMakers != null
                                     && f.def.pawnGroupMakers.Any(g => g.kindDef == PawnGroupKindDefOf.Combat))
                         .GroupBy(f => f.def).Select(g => g.First()))
            {
                sb.AppendLine($"=== {f.Name} ({f.def.defName}, {f.def.techLevel}, max {Tiers.WaveCapFor(f.def)}/vague)");
                foreach (float p in pts)
                {
                    RaidPlan plan = RaidPlan.Make(f.def, p, null, null, false, rng);
                    sb.AppendLine(plan == null ? $"{p:F0} pts : vanilla" : plan.Describe());
                    if (plan != null && HRSettings.EnableVehicles && VehicleGate.Active && f.def.techLevel >= TechLevel.Industrial)
                    {
                        var v0 = VehicleCompat.Plan(f, p, plan.tierX, 0, rng, out _);
                        var tr = VehicleCompat.PlanTransports(f, p, plan.tierX, plan.thisWave - VehicleCompat.SeatsIn(v0), rng, out float tCost);
                        var v = VehicleCompat.Plan(f, p, plan.tierX, tr.Count, rng, out float cost);
                        v.AddRange(tr);
                        cost += tCost;
                        if (v.Count > 0)
                            sb.AppendLine($"   [véhicules, si arrivée à pied] {string.Join(", ", v.Select(d => d.label))} ({cost:F0} pts retirés à l'infanterie)");
                    }
                }
            }
            Log.Message(sb.ToString());
        }

        [DebugAction("Hardened Raids", "Pending waves (log)", allowedGameStates = AllowedGameStates.Playing)]
        public static void ListWaves()
        {
            var comp = Current.Game.GetComponent<GameComponent_Waves>();
            if (comp == null || comp.pending.Count == 0) { Log.Message("[Hardened Raids] no pending waves."); return; }
            int now = Find.TickManager.TicksGame;
            Log.Message("[Hardened Raids] pending waves:\n" + string.Join("\n", comp.pending.Select(w =>
                $"  {w.faction?.Name} vague {w.index}/{w.totalWaves} : {w.count} raiders dans {(w.fireTick - now) / (float)GenDate.TicksPerHour:F1} h")));
        }
    }
}
