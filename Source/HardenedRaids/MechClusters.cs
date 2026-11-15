using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;
using HarmonyLib;
using RimWorld;
using RimWorld.SketchGen;
using UnityEngine;
using Verse;
using Verse.AI.Group;
using SketchGenerator = RimWorld.SketchGen.SketchGen;

namespace HardenedRaids
{
    // ================================================================ grappes mécanoïdes (B1, B2, D3)
    //
    // - Une grappe ne dépasse jamais HRSettings.MechClusterBudget points (vanilla : 10 000). Au-delà, l'event grappe et le
    //   siège mécanoïde sont scindés en N grappes égales posées à des emplacements distincts (chacune avec son lord, ses
    //   activateurs, sa dormance et sa lettre vanilla). Au-delà de MaxSimultaneousClusters, le reste arrive en grappes
    //   différées : incidents « MechCluster » vanilla mis dans la file d'incidents du conteur (IncidentQueue, sauvegardée par
    //   le jeu, réessayés pendant RetryTicks). Au plus MaxTotal grappes par incident, surplus perdu. Aucun état propre au
    //   mod : sans le mod, ces incidents restent des grappes vanilla (plafonnées à 10 000).
    // - Quêtes (QuestNode_SpawnMechCluster) et sites (GenStep_MechCluster) : une seule grappe plafonnée au budget (la quête
    //   porte un seul croquis et attend « tag.AllEnemiesDefeated », envoyé par le premier lord vaincu).
    // - Taille : courbe vanilla prolongée, plafonnée à MaxSide APRÈS le facteur aléatoire 0,8-2 ; la recherche du plus grand
    //   rectangle constructible (LargestAreaFinder, 3 passes sur toute la carte) est remplacée par un carré max en O(cases).
    // - Placement des mécas linéaire (vanilla : O(mécas² × cases)) ; FindClusterPosition : test « près de la colonie » par
    //   grille précalculée (vanilla : chaque case × tous les bâtiments de la colonie, à chaque essai).

    public static class ClusterSplit
    {
        public const int MaxSide = 60;              // côté max d'une grappe, après le facteur aléatoire
        public const int StaggerTicks = 120;        // écart entre deux grappes différées d'un même groupe
        // fenêtre de réessai d'une grappe différée dont le tir échoue (pas d'emplacement…) : la file vanilla la retente
        // toutes les ~833 ticks (QueuedIncident.RetryIntervalTicks) pendant 1 jour, puis l'abandonne
        public const int RetryTicks = GenDate.TicksPerDay;

        public static float Budget => Mathf.Max(10000f, HRSettings.MechClusterBudget);
        public static int MaxSimultaneous => Mathf.Max(1, HRSettings.MaxSimultaneousClusters);

        /// <summary>Plafond d'une grappe unique (tous les appelants de GenerateClusterSketch).</summary>
        public static float SingleCap => HRSettings.UncapPoints ? Budget : MechClusterGenerator.MaxPoints;

        // D9 : grappes par incident (simultanées + différées) ; au-delà, le surplus est perdu (5M pts = 250 grappes sinon).
        // Réglage MaxSimultaneousClusters ≤ 6 : avec 3 par défaut, 3 maintenant + 7 différées.
        public const int MaxTotal = 10;

        /// <summary>Grappes nécessaires pour ces points (chacune ≤ Budget), avant le plafond MaxTotal.</summary>
        public static int CountFor(float points) =>
            HRSettings.UncapPoints && points > Budget ? Mathf.CeilToInt(points / Budget) : 1;

        /// <summary>Nombre de grappes réellement posées (simultanées + différées) : au plus MaxTotal.</summary>
        public static int Capped(int needed)
        {
            if (needed <= MaxTotal) return needed;
            HRLog.Msg($"grappes mécanoïdes : {needed} nécessaires, plafonnées à {MaxTotal} (surplus perdu)");
            return MaxTotal;
        }

        // drapeaux de l'appel en cours (thread principal uniquement)
        internal static bool Reentrant;
        internal static bool Strict;   // grappe supplémentaire : pas d'emplacement distinct = pas de grappe (différée)
        internal static float GatePoints = -1f;   // menace totale d'une grappe scindée : blocage des blindés comme pour le tout

        /// <summary>Grappes différées : groupes de MaxSimultaneous, délai des vagues entre deux groupes.</summary>
        public static void Defer(Map map, float perCluster, int count)
        {
            if (count <= 0 || map == null || IncidentDefOf.MechCluster == null) return;
            int tick = Find.TickManager.TicksGame;
            int group = MaxSimultaneous;
            for (int i = 0; i < count; i++)
            {
                if (i % group == 0) tick += Mathf.RoundToInt(Rand.Range(HRSettings.WaveDelayHoursMin, HRSettings.WaveDelayHoursMax) * GenDate.TicksPerHour);
                else tick += StaggerTicks;
                var parms = new IncidentParms { target = map, points = perCluster, forced = true };
                Find.Storyteller.incidentQueue.Add(IncidentDefOf.MechCluster, tick, parms, RetryTicks);
            }
        }

        public static void Announce(int total, int now, int later, float per)
        {
            HRLog.Msg($"grappes mécanoïdes : {total} × {per:F0} pts, {now} maintenant, {later} différées");
            if (later <= 0 && now <= 1) return;
            Messages.Message(Strings.T("MsgClusters", total, now, later, $"{HRSettings.WaveDelayHoursMin:F0}-{HRSettings.WaveDelayHoursMax:F0} h"),
                             MessageTypeDefOf.ThreatBig);
        }
    }

    /// <summary>Event grappe mécanoïde : scission en grappes distinctes + différées.</summary>
    [HarmonyPatch(typeof(IncidentWorker_MechCluster), "TryExecuteWorker")]
    public static class Patch_MechClusterIncident
    {
        private static readonly Func<IncidentWorker_MechCluster, IncidentParms, bool> Exec =
            AccessTools.MethodDelegate<Func<IncidentWorker_MechCluster, IncidentParms, bool>>(
                AccessTools.Method(typeof(IncidentWorker_MechCluster), "TryExecuteWorker"), null, false, null);

        public static bool Prefix(IncidentWorker_MechCluster __instance, IncidentParms parms, ref bool __result)
        {
            if (ClusterSplit.Reentrant || parms?.target is not Map map) return true;
            int needed = ClusterSplit.CountFor(parms.points);
            if (needed <= 1) return true;
            float total = parms.points, per = total / needed;   // chaque grappe ≤ budget, même plafonnées
            int n = ClusterSplit.Capped(needed);
            int simult = Mathf.Min(n, ClusterSplit.MaxSimultaneous), spawned = 0;
            try
            {
                parms.points = per;
                ClusterSplit.GatePoints = total;
                for (int i = 0; i < simult; i++)
                {
                    bool ok;
                    ClusterSplit.Reentrant = true;
                    ClusterSplit.Strict = i > 0;
                    try { ok = Exec(__instance, parms); }   // corps vanilla (lettre comprise), points d'une grappe
                    catch (Exception e)
                    {
                        Log.Error("[Hardened Raids] mech cluster " + (i + 1) + "/" + n + " failed: " + e);
                        ok = false;
                    }
                    finally { ClusterSplit.Reentrant = false; ClusterSplit.Strict = false; }
                    if (ok) spawned++;
                    else if (i == 0) break;      // comme vanilla : l'incident échoue
                }
            }
            finally { parms.points = total; ClusterSplit.GatePoints = -1f; }
            if (spawned == 0) { __result = false; return false; }
            ClusterSplit.Defer(map, per, n - spawned);
            ClusterSplit.Announce(n, spawned, n - spawned, per);
            __result = true;
            return false;
        }
    }

    /// <summary>
    /// Siège mécanoïde : la grappe principale suit le flux du raid (position ClusterDrop, lettre de raid) ; les grappes
    /// supplémentaires simultanées tombent en même temps à d'autres endroits et leurs mécas s'ajoutent à la lettre du raid ;
    /// le reste arrive plus tard comme grappes ordinaires.
    /// </summary>
    [HarmonyPatch(typeof(RaidStrategyWorker_SiegeMechanoid), nameof(RaidStrategyWorker_SiegeMechanoid.TryGenerateThreats))]
    public static class Patch_SiegeMechanoidGenerate
    {
        internal sealed class Split { public int n; public float per, total; }
        internal static readonly ConditionalWeakTable<IncidentParms, Split> Pending = new ConditionalWeakTable<IncidentParms, Split>();

        public static bool Prefix(IncidentParms parms)
        {
            if (parms == null) return true;
            Pending.Remove(parms);
            int needed = ClusterSplit.CountFor(parms.points);
            if (needed <= 1) return true;
            var split = new Split { n = ClusterSplit.Capped(needed), per = parms.points / needed, total = parms.points };
            ClusterSplit.GatePoints = split.total;
            try { parms.mechClusterSketch = MechClusterGenerator.GenerateClusterSketch(split.per, parms.target as Map); }
            finally { ClusterSplit.GatePoints = -1f; }
            Pending.Add(parms, split);
            return false;
        }
    }

    [HarmonyPatch(typeof(RaidStrategyWorker_SiegeMechanoid), nameof(RaidStrategyWorker_SiegeMechanoid.SpawnThreats))]
    public static class Patch_SiegeMechanoidSpawn
    {
        public static void Postfix(IncidentParms parms, List<Pawn> __result)
        {
            if (parms == null || !Patch_SiegeMechanoidGenerate.Pending.TryGetValue(parms, out Patch_SiegeMechanoidGenerate.Split split)) return;
            Patch_SiegeMechanoidGenerate.Pending.Remove(parms);
            if (parms.target is not Map map) return;
            int simult = Mathf.Min(split.n, ClusterSplit.MaxSimultaneous), spawned = 1;
            for (int i = 1; i < simult; i++)
            {
                try
                {
                    MechClusterSketch sketch;
                    ClusterSplit.GatePoints = split.total;
                    try { sketch = MechClusterGenerator.GenerateClusterSketch(split.per, map); }
                    finally { ClusterSplit.GatePoints = -1f; }
                    IntVec3 center;
                    ClusterSplit.Strict = true;
                    try { center = MechClusterUtility.FindClusterPosition(map, sketch, 100, 0.5f); }
                    finally { ClusterSplit.Strict = false; }
                    if (!center.IsValid) continue;
                    List<Thing> things = MechClusterUtility.SpawnCluster(center, map, sketch, dropInPods: true, canAssaultColony: true, parms.questTag);
                    __result?.AddRange(things.OfType<Pawn>());
                    spawned++;
                }
                catch (Exception e)
                {
                    Log.Error("[Hardened Raids] extra siege mech cluster failed: " + e);
                    break;
                }
            }
            ClusterSplit.Defer(map, split.per, split.n - spawned);
            ClusterSplit.Announce(split.n, spawned, split.n - spawned, split.per);
        }
    }

    /// <summary>Prix des tourelles de grappe (×ClusterTurretCostFactor), le temps d'une génération ; imbrication sûre.</summary>
    public static class TurretPricing
    {
        private static List<ThingDef> turrets;
        private static readonly Dictionary<ThingDef, float> saved = new Dictionary<ThingDef, float>();
        private static int depth;

        private static List<ThingDef> Turrets => turrets ??= DefDatabase<ThingDef>.AllDefsListForReading
            .Where(d => d.building?.buildingTags != null && d.building.IsTurret && d.building.buildingTags.Contains(MechClusterGenerator.MechClusterCombatThreatTag))
            .ToList();

        public static void Push()
        {
            if (depth++ > 0) return;
            float factor = HRSettings.ClusterTurretCostFactor;
            if (factor == 1f) return;
            foreach (ThingDef d in Turrets)
            {
                saved[d] = d.building.combatPower;
                d.building.combatPower *= factor;
            }
        }

        public static void Pop()
        {
            if (depth == 0 || --depth > 0) return;
            foreach (KeyValuePair<ThingDef, float> kv in saved) kv.Key.building.combatPower = kv.Value;
            saved.Clear();
        }
    }

    /// <summary>
    /// GenerateClusterSketch remplacé (même logique que vanilla) : plafond du budget, tourelles renchéries, mécas blindés au prix
    /// Hardened, placement des mécas en O(cases + mécas).
    /// </summary>
    [HarmonyPatch(typeof(MechClusterGenerator), nameof(MechClusterGenerator.GenerateClusterSketch))]
    public static class Patch_MechCluster
    {
        public static bool Prefix(float points, Map map, bool startDormant, bool forceNoConditionCauser, ref MechClusterSketch __result)
        {
            if (!ModsConfig.RoyaltyActive) return true;   // vanilla : croquis vide + message
            points = Mathf.Min(points, ClusterSplit.SingleCap);
            List<PawnKindDef> kinds = DefDatabase<PawnKindDef>.AllDefsListForReading.Where(MechClusterGenerator.MechKindSuitableForCluster).ToList();
            TurretPricing.Push();
            bool priced = MechPricing.Push(kinds, Mathf.Max(points, ClusterSplit.GatePoints));
            try
            {
                __result = Build(points, map, startDormant, forceNoConditionCauser, kinds);
            }
            finally
            {
                if (priced) MechPricing.Pop();
                TurretPricing.Pop();
            }
            HRLog.Msg($"grappe mécanoïde : {points:F0} pts, tourelles ×{HRSettings.ClusterTurretCostFactor:F1}, {__result.pawns?.Count ?? 0} mécas, " +
                      $"{__result.buildingsSketch.Things.Count} bâtiments, {__result.buildingsSketch.OccupiedSize.x}×{__result.buildingsSketch.OccupiedSize.z}");
            return false;
        }

        private static MechClusterSketch Build(float points, Map map, bool startDormant, bool forceNoConditionCauser, List<PawnKindDef> kinds)
        {
            float num = points;
            List<MechClusterSketch.Mech> list = null;
            if (Rand.Chance(MechClusterGenerator.PointsToPawnsChanceCurve.Evaluate(points)) && kinds.Count > 0)
            {
                list = new List<MechClusterSketch.Mech>();
                float a = Rand.ByCurve(MechClusterGenerator.PawnPointsRandomPercentOfTotalCurve) * num;
                a = Mathf.Max(a, kinds.Min(x => x.combatPower));
                float left = a;
                var affordable = new List<PawnKindDef>(kinds.Count);
                while (left > 0f)
                {
                    affordable.Clear();
                    foreach (PawnKindDef k in kinds) if (k.combatPower <= left) affordable.Add(k);
                    if (!affordable.TryRandomElement(out PawnKindDef pick)) break;
                    left -= pick.combatPower;
                    list.Add(new MechClusterSketch.Mech(pick));
                }
                num -= a - left;
            }
            Sketch buildings = SketchGenerator.Generate(SketchResolverDefOf.MechCluster, new SketchResolveParams
            {
                points = num,
                totalPoints = points,
                mechClusterDormant = startDormant,
                sketch = new Sketch(),
                mechClusterForMap = map,
                forceNoConditionCauser = forceNoConditionCauser,
            });
            if (list != null) PlaceMechs(buildings, list);
            return new MechClusterSketch(buildings, list, startDormant);
        }

        /// <summary>
        /// Comme vanilla (case libre du rectangle occupé, sinon anneaux de plus en plus larges sans collision), mais avec une
        /// réserve de cases tirée au hasard et retirée au fur et à mesure : O(cases + mécas) au lieu de O(mécas² × cases).
        /// </summary>
        private static void PlaceMechs(Sketch sketch, List<MechClusterSketch.Mech> mechs)
        {
            CellRect rect = sketch.OccupiedRect;
            var pool = new List<IntVec3>(Mathf.Max(16, rect.Area));
            foreach (IntVec3 c in rect) if (!sketch.ThingsAt(c).Any()) pool.Add(c);   // même test que vanilla
            int rings = 0;
            for (int i = 0; i < mechs.Count; i++)
            {
                MechClusterSketch.Mech m = mechs[i];
                while (pool.Count == 0 && rings < 200)
                {
                    rect = rect.ExpandedBy(1);
                    rings++;
                    foreach (IntVec3 c in rect.EdgeCells)
                        if (!sketch.WouldCollide(m.kindDef.race, c, Rot4.North)) pool.Add(c);
                }
                if (pool.Count == 0) { m.position = rect.RandomCell; mechs[i] = m; continue; }
                int idx = Rand.Range(0, pool.Count);
                m.position = pool[idx];
                pool[idx] = pool[pool.Count - 1];
                pool.RemoveAt(pool.Count - 1);
                mechs[i] = m;
            }
        }
    }

    /// <summary>
    /// Taille de la grappe (ResolveSketch) : calculée ici comme vanilla (courbe × 0,8-2 par côté), prolongée au-delà de
    /// 5 000 pts, plafonnée à MaxSide après le facteur aléatoire, puis au plus grand carré constructible de la carte.
    /// </summary>
    [HarmonyPatch(typeof(MechClusterGenerator), nameof(MechClusterGenerator.ResolveSketch))]
    public static class Patch_MechClusterSize
    {
        private const float VanillaMaxSizePoints = 5000f;   // dernier point de PointsToSizeCurve (côté 25)

        private static readonly SimpleCurve VanillaCurve =
            (SimpleCurve)AccessTools.Field(typeof(MechClusterGenerator), "PointsToSizeCurve").GetValue(null);
        private static readonly FloatRange RandomFactor =
            (FloatRange)AccessTools.Field(typeof(MechClusterGenerator), "SizeRandomFactorRange").GetValue(null);

        public static void Prefix(ref SketchResolveParams parms)
        {
            if (parms.mechClusterSize.HasValue || !parms.points.HasValue) return;
            float size = Size(parms.points.Value);
            int x = Mathf.Min(GenMath.RoundRandom(size * RandomFactor.RandomInRange), ClusterSplit.MaxSide);
            int z = Mathf.Min(GenMath.RoundRandom(size * RandomFactor.RandomInRange), ClusterSplit.MaxSide);
            if (parms.mechClusterForMap != null)
            {
                int s = BuildableSquare.MaxSide(parms.mechClusterForMap);
                x = Mathf.Min(x, s);
                z = Mathf.Min(z, s);
            }
            parms.mechClusterSize = new IntVec2(x, z);
        }

        /// <summary>Vanilla jusqu'à 5 000 pts (côté 25), puis surface ∝ points de bâtiments ramenés au prix des tourelles.</summary>
        public static float Size(float points)
        {
            float vanilla = VanillaCurve.Evaluate(points);
            if (!HRSettings.UncapPoints || points <= VanillaMaxSizePoints) return vanilla;
            float baseSize = VanillaCurve.Points[VanillaCurve.PointsCount - 1].y;
            return Mathf.Max(vanilla, baseSize * Mathf.Sqrt(points / VanillaMaxSizePoints / Mathf.Max(1f, HRSettings.ClusterTurretCostFactor)));
        }
    }

    /// <summary>
    /// Côté du plus grand carré de cases praticables à affordance lourde (même prédicat que vanilla), en O(cases) par
    /// programmation dynamique. Mis en cache pour la carte et le tick courants (plusieurs grappes d'un même event).
    /// </summary>
    public static class BuildableSquare
    {
        private static readonly System.WeakReference<Map> lastMap = new System.WeakReference<Map>(null);
        private static int lastTick = -1, lastSide;
        private static int[] prev, cur;

        public static int MaxSide(Map map)
        {
            int tick = Find.TickManager?.TicksGame ?? -1;
            if (tick == lastTick && lastMap.TryGetTarget(out Map m) && m == map) return lastSide;
            int w = map.Size.x, h = map.Size.z, best = 0;
            if (prev == null || prev.Length < w) { prev = new int[w]; cur = new int[w]; }
            Array.Clear(prev, 0, w);
            for (int z = 0; z < h; z++)
            {
                for (int x = 0; x < w; x++)
                {
                    var c = new IntVec3(x, 0, z);
                    if (c.Impassable(map) || !c.GetAffordances(map).Contains(TerrainAffordanceDefOf.Heavy)) { cur[x] = 0; continue; }
                    int v = x == 0 || z == 0 ? 1 : 1 + Mathf.Min(prev[x], Mathf.Min(cur[x - 1], prev[x - 1]));
                    cur[x] = v;
                    if (v > best) best = v;
                }
                (prev, cur) = (cur, prev);
            }
            lastMap.SetTarget(map);
            lastTick = tick;
            lastSide = best;
            return best;
        }
    }

    /// <summary>
    /// FindClusterPosition remplacé : même recherche que vanilla (20 essais près de la colonie avec la chance donnée, puis
    /// maxTries positions de siège), score identique mais « trop près de la colonie » lu dans une grille précalculée, et zones
    /// des grappes actives exclues. Différence assumée : le meilleur candidat retenu est la position évaluée (vanilla gardait
    /// la case de départ aléatoire, non évaluée).
    /// </summary>
    [HarmonyPatch(typeof(MechClusterUtility), nameof(MechClusterUtility.FindClusterPosition))]
    public static class Patch_FindClusterPosition
    {
        private const float MaxScore = 100f;

        private static readonly AccessTools.FieldRef<LordJob_MechanoidDefendBase, IntVec3> DefSpot =
            AccessTools.FieldRefAccess<LordJob_MechanoidDefendBase, IntVec3>("defSpot");
        private static readonly AccessTools.FieldRef<LordJob_MechanoidDefendBase, float> DefendRadius =
            AccessTools.FieldRefAccess<LordJob_MechanoidDefendBase, float>("defendRadius");
        private static readonly AccessTools.FieldRef<LordJob_MechanoidDefendBase, bool> IsMechCluster =
            AccessTools.FieldRefAccess<LordJob_MechanoidDefendBase, bool>("isMechCluster");
        private static readonly AccessTools.FieldRef<LordJob_MechanoidDefendBase, bool> Defeated =
            AccessTools.FieldRefAccess<LordJob_MechanoidDefendBase, bool>("mechClusterDefeated");

        private static bool[] nearColony;
        private static readonly List<Vector3> zones = new List<Vector3>();   // x, z, rayon

        public static bool Prefix(Map map, MechClusterSketch sketch, int maxTries, float spawnCloseToColonyChance, ref IntVec3 __result)
        {
            if (map == null || sketch?.buildingsSketch == null) return true;
            __result = Find(map, sketch, maxTries, spawnCloseToColonyChance, ClusterSplit.Strict);
            return false;
        }

        private static IntVec3 Find(Map map, MechClusterSketch sketch, int maxTries, float closeChance, bool strict)
        {
            Prepare(map, sketch, out Vector3 offset, out float radius);
            IntVec3 best = IntVec3.Invalid;
            float bestScore = float.MinValue;
            if (Rand.Chance(closeChance))
            {
                for (int i = 0; i < 20; i++)
                {
                    if (!DropCellFinder.TryFindRaidDropCenterClose(out IntVec3 spot, map, canRoofPunch: true, allowIndoors: true, closeWalk: false, 40)) break;
                    float s = Score(spot, map, sketch, offset, radius);
                    if (s >= MaxScore || Mathf.Approximately(s, MaxScore)) return spot;
                    if (s > bestScore) { best = spot; bestScore = s; }
                }
            }
            for (int j = 0; j < maxTries; j++)
            {
                IntVec3 start = CellFinderLoose.RandomCellWith(x => x.Standable(map), map);
                if (!start.IsValid) continue;
                IntVec3 pos = RCellFinder.FindSiegePositionFrom(start, map, allowRoofed: false, errorOnFail: false);
                if (!pos.IsValid) continue;
                float s = Score(pos, map, sketch, offset, radius);
                if (s >= MaxScore || Mathf.Approximately(s, MaxScore)) return pos;
                if (s > bestScore) { best = pos; bestScore = s; }
            }
            if (strict) return bestScore > -MaxScore ? best : IntVec3.Invalid;
            return best.IsValid ? best : CellFinder.RandomCell(map);
        }

        /// <summary>Grille « à 5 cases ou moins d'un bâtiment / colon » et zones des grappes actives de la carte.</summary>
        private static void Prepare(Map map, MechClusterSketch sketch, out Vector3 offset, out float radius)
        {
            int cells = map.cellIndices.NumGridCells;
            if (nearColony == null || nearColony.Length < cells) nearColony = new bool[cells];
            else Array.Clear(nearColony, 0, cells);
            int n = GenRadial.NumCellsInRadius(5f);
            void Mark(IntVec3 center)
            {
                for (int i = 0; i < n; i++)
                {
                    IntVec3 c = center + GenRadial.RadialPattern[i];
                    if (c.InBounds(map)) nearColony[map.cellIndices.CellToIndex(c)] = true;
                }
            }
            foreach (Building b in map.listerBuildings.allBuildingsColonist) Mark(b.Position);
            foreach (Pawn p in map.mapPawns.FreeColonistsAndPrisonersSpawned) Mark(p.Position);

            CellRect r = sketch.buildingsSketch.OccupiedRect;
            offset = r.CenterVector3;
            radius = Mathf.Sqrt(r.Width * r.Width + r.Height * r.Height) / 2f + 6f;   // comme le defendRadius vanilla
            zones.Clear();
            foreach (Lord lord in map.lordManager.lords)
            {
                if (lord?.LordJob is not LordJob_MechanoidDefendBase job || !IsMechCluster(job) || Defeated(job)) continue;
                IntVec3 spot = DefSpot(job);
                zones.Add(new Vector3(spot.x, spot.z, DefendRadius(job)));
            }
        }

        private static float Score(IntVec3 center, Map map, MechClusterSketch sketch, Vector3 offset, float radius)
        {
            Sketch b = sketch.buildingsSketch;
            if (b.AnyThingOutOfBounds(map, center)) return -100f;
            List<MechClusterSketch.Mech> pawns = sketch.pawns;
            if (pawns != null)
                for (int i = 0; i < pawns.Count; i++)
                    if (!(pawns[i].position + center).InBounds(map)) return -100f;
            float cx = center.x + offset.x, cz = center.z + offset.z;
            for (int i = 0; i < zones.Count; i++)
            {
                float dx = zones[i].x - cx, dz = zones[i].y - cz, min = zones[i].z + radius;
                if (dx * dx + dz * dz < min * min) return -100f;   // chevauche une grappe active
            }
            int blocked = 0, fogged = 0, roofed = 0, indoors = 0;
            bool tooClose = false;
            List<SketchEntity> entities = b.Entities;
            for (int i = 0; i < entities.Count; i++)
            {
                if (entities[i].IsSpawningBlocked(center, map)) blocked++;
                if (!Check(entities[i].pos + center)) return -100f;
            }
            if (pawns != null)
                for (int i = 0; i < pawns.Count; i++)
                {
                    IntVec3 c = pawns[i].position + center;
                    if (!c.Standable(map)) blocked++;
                    if (!Check(c)) return -100f;
                }
            int total = entities.Count + (pawns?.Count ?? 0);
            if (total == 0) return MaxScore;
            float f1 = (float)blocked / total, f2 = (float)fogged / total, a = (float)roofed / total, bb = (float)indoors / total;
            return MaxScore * (1f - f1) * (1f - Mathf.Max(a, bb)) * (1f - f2) * (tooClose ? 0.5f : 1f);

            bool Check(IntVec3 x)
            {
                if (!x.InBounds(map)) return false;
                if (x.Fogged(map)) fogged++;
                RoofDef roof = x.GetRoof(map);
                if (roof == RoofDefOf.RoofRockThick) return false;
                if (roof != null) roofed++;
                Room room = x.GetRoom(map);
                if (room != null && !room.PsychologicallyOutdoors) indoors++;
                List<Thing> things = x.GetThingList(map);
                for (int k = 0; k < things.Count; k++) if (things[k].def.preventSkyfallersLandingOn) return false;
                if (!tooClose && nearColony[map.cellIndices.CellToIndex(x)]) tooClose = true;
                return true;
            }
        }
    }
}
