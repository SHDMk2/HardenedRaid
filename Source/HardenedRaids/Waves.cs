using System.Collections.Generic;
using System.Linq;
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
        /// <summary>defNames des véhicules prévus pour cette vague (null = aucun ; save v1.0 : aucun, jamais d'achat).</summary>
        public List<string> vehicles;

        /// <summary>Véhicules de la vague encore présents dans le jeu (defs retirées ignorées).</summary>
        public List<ThingDef> ResolveVehicles()
        {
            if (vehicles == null || vehicles.Count == 0) return null;
            var res = new List<ThingDef>();
            foreach (string n in vehicles)
            {
                ThingDef d = n.NullOrEmpty() ? null : DefDatabase<ThingDef>.GetNamedSilentFail(n);
                if (d != null) res.Add(d);
            }
            return res.Count > 0 ? res : null;
        }

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
            Scribe_Collections.Look(ref vehicles, "vehicles", LookMode.Value);
        }
    }

    /// <summary>Une vague suivante prévue : effectif et part des points du raid.</summary>
    public class WavePlanItem
    {
        public int count;
        public float share;   // fraction des points du raid emportée par cette vague
        public List<ThingDef> vehicles;   // part des véhicules du raid (null = aucun)
    }

    /// <summary>Vagues suivantes prévues pendant la génération, programmées seulement si le raid réussit.</summary>
    public class WavePlan
    {
        public int total;      // effectif total du raid (toutes vagues)
        public int waves;
        public float tierX;
        public bool vanilla;   // génération vanilla plafonnée (repli, non-humains)
        public List<WavePlanItem> next = new List<WavePlanItem>();   // vagues 2..n
    }

    public static class WaveScheduler
    {
        /// <summary>
        /// Plan des vagues 2+ d'un raid Hardened : effectifs égaux (ou <paramref name="counts"/> quand les véhicules
        /// sont répartis), points au prorata, part des véhicules du raid.
        /// </summary>
        public static WavePlan FromRaidPlan(RaidPlan plan, int[] counts = null, List<List<ThingDef>> vehicles = null)
        {
            var wp = new WavePlan { total = plan.total, waves = plan.waves, tierX = plan.tierX };
            int remaining = plan.total - (counts != null ? counts[0] : plan.thisWave);
            for (int i = 2; i <= plan.waves && remaining > 0; i++)
            {
                int n = counts != null && counts.Length >= i ? Mathf.Min(counts[i - 1], remaining) : Mathf.Min(plan.thisWave, remaining);
                if (n <= 0) continue;
                remaining -= n;
                wp.next.Add(new WavePlanItem
                {
                    count = n, share = n / (float)plan.total,
                    vehicles = vehicles != null && vehicles.Count >= i && vehicles[i - 1].Count > 0 ? vehicles[i - 1] : null,
                });
            }
            return wp;
        }

        /// <summary>Programme les vagues prévues par ce raid ; appelé après un TryGenerateRaidInfo réussi.</summary>
        public static void Commit(RaidFrame frame)
        {
            WavePlan wp = frame?.Planned;
            if (frame != null) frame.Planned = null;
            if (wp == null || wp.next.Count == 0 || frame.DebugTest || frame.Wave != null) return;
            IncidentParms parms = frame.Parms;
            if (parms?.target is not Map map || parms.faction == null) return;
            var comp = Current.Game.GetComponent<GameComponent_Waves>();
            if (comp == null) return;
            float basePoints = frame.PreAdjustPoints > 0f ? frame.PreAdjustPoints : parms.points;
            int tick = Find.TickManager.TicksGame;
            int index = 2;
            foreach (WavePlanItem it in wp.next)
            {
                tick += Mathf.RoundToInt(Rand.Range(HRSettings.WaveDelayHoursMin, HRSettings.WaveDelayHoursMax) * GenDate.TicksPerHour);
                comp.pending.Add(new WaveEntry
                {
                    fireTick = tick, map = map, faction = parms.faction, strategy = parms.raidStrategy, arrival = parms.raidArrivalMode,
                    points = basePoints * it.share, tierX = wp.tierX, count = it.count, index = index++, totalWaves = wp.waves,
                    vehicles = it.vehicles?.Select(v => v.defName).ToList(),
                });
            }
            Messages.Message(Strings.T("MsgWaves", parms.faction.Name, wp.waves, wp.total,
                                 $"{HRSettings.WaveDelayHoursMin:F0}-{HRSettings.WaveDelayHoursMax:F0} h"),
                             MessageTypeDefOf.ThreatBig);
        }
    }

    public class GameComponent_Waves : GameComponent
    {
        public List<WaveEntry> pending = new List<WaveEntry>();

        public GameComponent_Waves(Game game) { RaidContext.Reset(); }

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
            try
            {
                if (!TryFire(w, w.arrival) && !TryFire(w, null))
                    HRLog.Msg($"vague {w.index}/{w.totalWaves} : le raid n'a pas pu être lancé.");
            }
            finally
            {
                RaidContext.NextWave = null;
            }
        }

        private static bool TryFire(WaveEntry w, PawnsArrivalModeDef arrival)
        {
            var parms = new IncidentParms
            {
                target = w.map, faction = w.faction, points = w.points, raidStrategy = w.strategy,
                raidArrivalMode = arrival, forced = true,
            };
            RaidContext.NextWave = w;   // attachée au cadre ouvert par ce raid (pas à un raid imbriqué)
            return IncidentDefOf.RaidEnemy.Worker.TryExecute(parms);
        }
    }
}
