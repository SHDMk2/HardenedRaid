using HarmonyLib;
using RimWorld;
using UnityEngine;
using Verse;

namespace HardenedRaids
{
    /// <summary>
    /// Infestations (A12 / D5). Vanilla : ruches = points × 0,7→0,45 ÷ 220, soit ~20 ruches au plus avec l'ancien
    /// plafond de 10 000 pts ; sans plafond, 100 000 pts donnent ~200 ruches. Chaque ruche fait naître 200 pts
    /// d'insectes et en entretient jusqu'à 500 (Hive.MaxSpawnedPawnsPoints), et se reproduit jusqu'à 30 ruches par
    /// carte : la menace d'une infestation, c'est sa population à terme, pas ses insectes de départ.
    /// Limite retenue : un plafond de ruches par vague tel que la population à terme reste sous le plafond low-tech
    /// (CapLowTech insectes : 300 → ~53 ruches) ; les ruches en trop creusent plus tard, en vagues d'infestation,
    /// chacune seulement s'il reste de la place sous ce plafond (ruches déjà présentes comprises) et si vanilla
    /// accepte l'infestation (moins de 30 ruches, emplacement trouvé) ; sinon elle est repoussée (3 fois au plus).
    /// Les infestations de quêtes qui n'appellent pas IncidentWorker_Infestation ne sont pas concernées.
    /// </summary>
    public static class InfestationWaves
    {
        internal static IncidentParms active;   // infestation en cours (IncidentWorker_Infestation.TryExecuteWorker)
        internal static int forcedHives = -1;   // vague différée : nombre de ruches imposé
        public const int MaxPostpone = 3;

        private static float AvgInsectPower()
        {
            float sum = 0f;
            int n = 0;
            foreach (PawnKindDef k in Hive.spawnablePawnKinds)
                if (k != null && k.combatPower > 0f) { sum += k.combatPower; n++; }
            return n > 0 ? sum / n : 88f;
        }

        /// <summary>Ruches par vague : population à terme (500 pts par ruche) ≈ CapLowTech insectes.</summary>
        public static int HiveCap => Mathf.Max(5, Mathf.RoundToInt(HRSettings.CapLowTech * AvgInsectPower() / Hive.MaxSpawnedPawnsPoints));

        internal static void ScheduleOverflow(IncidentParms parms, Map map, int spawned, int overflow)
        {
            int cap = HiveCap;
            int total = spawned + overflow;
            var wp = new WavePlan { total = total, waves = 1 + Mathf.CeilToInt(overflow / (float)cap), tierX = -1f, vanilla = true, incident = IncidentDefOf.Infestation };
            for (int left = overflow; left > 0; left -= cap)
            {
                int n = Mathf.Min(cap, left);
                wp.next.Add(new WavePlanItem { count = n, share = n / (float)total });
            }
            float pts = Mathf.Max(parms.points, 1f);
            WaveScheduler.Schedule(wp, parms, map, Faction.OfInsects, null, null, pts, pts, IntVec3.Invalid);
            Messages.Message(Strings.T("MsgInfestationWaves", total, spawned, wp.next.Count,
                                 $"{HRSettings.WaveDelayHoursMin:F0}-{HRSettings.WaveDelayHoursMax:F0} h"),
                             MessageTypeDefOf.ThreatBig);
            HRLog.Msg($"infestation plafonnée : {spawned}/{total} ruches, {overflow} en {wp.next.Count} vague(s).");
        }

        internal static void Fire(WaveEntry w, Quest quest)
        {
            Map map = w.map;
            int n = Mathf.Min(w.count, HiveCap - HiveUtility.TotalSpawnedHivesCount(map));
            IncidentParms parms = w.raidParms != null ? w.raidParms.ShallowCopy() : new IncidentParms();
            parms.target = map;
            parms.forced = true;
            parms.quest = quest;
            parms.points = Mathf.Max(w.adjustedPoints, 1f);
            IncidentWorker worker = IncidentDefOf.Infestation.Worker;
            if (n < 1 || !worker.CanFireNow(parms))
            {
                if (w.postponed < MaxPostpone && Current.Game.GetComponent<GameComponent_Waves>() is GameComponent_Waves comp)
                {
                    w.postponed++;
                    w.fireTick = Find.TickManager.TicksGame + Mathf.RoundToInt(Rand.Range(HRSettings.WaveDelayHoursMin, HRSettings.WaveDelayHoursMax) * GenDate.TicksPerHour);
                    comp.pending.Add(w);
                    HRLog.Msg($"infestation {w.index}/{w.totalWaves} repoussée ({HiveUtility.TotalSpawnedHivesCount(map)} ruches sur la carte).");
                }
                else HRLog.Msg($"infestation {w.index}/{w.totalWaves} annulée (pas de place).");
                return;
            }
            forcedHives = n;
            try
            {
                worker.TryExecute(parms);
            }
            finally
            {
                forcedHives = -1;
            }
        }
    }

    [HarmonyPatch(typeof(IncidentWorker_Infestation), "TryExecuteWorker")]
    public static class Patch_InfestationContext
    {
        public static void Prefix(IncidentParms parms, out IncidentParms __state)
        {
            __state = InfestationWaves.active;
            InfestationWaves.active = parms;
        }

        public static void Finalizer(IncidentParms __state) => InfestationWaves.active = __state;
    }

    [HarmonyPatch(typeof(InfestationUtility), nameof(InfestationUtility.SpawnTunnels))]
    public static class Patch_InfestationHiveCap
    {
        public static void Prefix(ref int hiveCount, out int __state)
        {
            __state = 0;
            if (InfestationWaves.active == null) return;
            if (InfestationWaves.forcedHives > 0)
            {
                hiveCount = InfestationWaves.forcedHives;
                InfestationWaves.forcedHives = -1;
                return;
            }
            int cap = InfestationWaves.HiveCap;
            if (hiveCount <= cap * 1.1f) return;
            __state = hiveCount - cap;
            hiveCount = cap;
        }

        public static void Postfix(Thing __result, int hiveCount, Map map, int __state)
        {
            if (__state <= 0 || __result == null || InfestationWaves.active == null || map == null) return;
            try { InfestationWaves.ScheduleOverflow(InfestationWaves.active, map, hiveCount, __state); }
            catch (System.Exception e) { Log.Warning("[Hardened Raids] infestation waves: " + e.Message); }
        }
    }
}
