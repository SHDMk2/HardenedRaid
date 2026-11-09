using System.Linq;
using System.Text;
using LudeonTK;
using RimWorld;
using Verse;

namespace HardenedRaids
{
    public static class HRDebugActions
    {
        [DebugAction("Hardened Raids", "Aperçu des plans de raid (log)", allowedGameStates = AllowedGameStates.PlayingOnMap)]
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
                }
            }
            Log.Message(sb.ToString());
        }

        [DebugAction("Hardened Raids", "Vagues en attente (log)", allowedGameStates = AllowedGameStates.Playing)]
        public static void ListWaves()
        {
            var comp = Current.Game.GetComponent<GameComponent_Waves>();
            if (comp == null || comp.pending.Count == 0) { Log.Message("[Hardened Raids] aucune vague en attente."); return; }
            int now = Find.TickManager.TicksGame;
            Log.Message("[Hardened Raids] vagues en attente :\n" + string.Join("\n", comp.pending.Select(w =>
                $"  {w.faction?.Name} vague {w.index}/{w.totalWaves} : {w.count} raiders dans {(w.fireTick - now) / (float)GenDate.TicksPerHour:F1} h")));
        }
    }
}
