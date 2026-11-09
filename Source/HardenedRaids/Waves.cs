using System.Collections.Generic;
using RimWorld;
using UnityEngine;
using Verse;

namespace HardenedRaids
{
    public class WaveEntry : IExposable
    {
        public int fireTick;
        public Map map;
        public Faction faction;
        public RaidStrategyDef strategy;
        public PawnsArrivalModeDef arrival;
        public float points;
        public float tierX;
        public int count;
        public int index;
        public int totalWaves;

        public void ExposeData()
        {
            Scribe_Values.Look(ref fireTick, "fireTick");
            Scribe_References.Look(ref map, "map");
            Scribe_References.Look(ref faction, "faction");
            Scribe_Defs.Look(ref strategy, "strategy");
            Scribe_Defs.Look(ref arrival, "arrival");
            Scribe_Values.Look(ref points, "points");
            Scribe_Values.Look(ref tierX, "tierX");
            Scribe_Values.Look(ref count, "count");
            Scribe_Values.Look(ref index, "index");
            Scribe_Values.Look(ref totalWaves, "totalWaves");
        }
    }

    public static class WaveScheduler
    {
        public static void Schedule(RaidPlan plan, IncidentParms parms)
        {
            if (parms?.target is not Map map || parms.faction == null) return;
            var comp = Current.Game.GetComponent<GameComponent_Waves>();
            if (comp == null) return;
            float basePoints = RaidContext.PreAdjustPoints > 0f ? RaidContext.PreAdjustPoints : parms.points;
            int remaining = plan.total - plan.thisWave;
            int tick = Find.TickManager.TicksGame;
            for (int i = 2; i <= plan.waves && remaining > 0; i++)
            {
                int n = Mathf.Min(plan.thisWave, remaining);
                remaining -= n;
                tick += Mathf.RoundToInt(Rand.Range(HRSettings.WaveDelayHoursMin, HRSettings.WaveDelayHoursMax) * GenDate.TicksPerHour);
                comp.pending.Add(new WaveEntry
                {
                    fireTick = tick, map = map, faction = parms.faction, strategy = parms.raidStrategy, arrival = parms.raidArrivalMode,
                    points = basePoints * n / plan.total, tierX = plan.tierX, count = n, index = i, totalWaves = plan.waves,
                });
            }
            Messages.Message($"Hardened Raids : {parms.faction.Name} attaque en {plan.waves} vagues ({plan.total} raiders au total). " +
                             $"Prochaine vague dans {HRSettings.WaveDelayHoursMin:F0}-{HRSettings.WaveDelayHoursMax:F0} h.",
                             MessageTypeDefOf.ThreatBig);
        }
    }

    public class GameComponent_Waves : GameComponent
    {
        public List<WaveEntry> pending = new List<WaveEntry>();

        public GameComponent_Waves(Game game) { }

        public override void ExposeData()
        {
            Scribe_Collections.Look(ref pending, "pending", LookMode.Deep);
            if (Scribe.mode == LoadSaveMode.PostLoadInit) pending ??= new List<WaveEntry>();
        }

        public override void GameComponentTick()
        {
            if (pending.Count == 0 || Find.TickManager.TicksGame % 250 != 0) return;
            int now = Find.TickManager.TicksGame;
            for (int i = pending.Count - 1; i >= 0; i--)
            {
                WaveEntry w = pending[i];
                if (w.fireTick > now) continue;
                pending.RemoveAt(i);
                Fire(w);
            }
        }

        private static void Fire(WaveEntry w)
        {
            if (w.map == null || !Find.Maps.Contains(w.map) || w.faction == null || w.faction.defeated || !w.faction.HostileTo(Faction.OfPlayer))
            {
                HRLog.Msg($"vague {w.index}/{w.totalWaves} annulée (carte ou faction plus valide).");
                return;
            }
            RaidContext.PendingWave = w;
            try
            {
                if (!TryFire(w, w.arrival) && !TryFire(w, null))
                    HRLog.Msg($"vague {w.index}/{w.totalWaves} : le raid n'a pas pu être lancé.");
            }
            finally
            {
                RaidContext.PendingWave = null;
            }
        }

        private static bool TryFire(WaveEntry w, PawnsArrivalModeDef arrival)
        {
            var parms = new IncidentParms
            {
                target = w.map, faction = w.faction, points = w.points, raidStrategy = w.strategy,
                raidArrivalMode = arrival, forced = true,
            };
            return IncidentDefOf.RaidEnemy.Worker.TryExecute(parms);
        }
    }
}
