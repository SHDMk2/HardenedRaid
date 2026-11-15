using System;
using System.Collections.Generic;
using System.Linq;
using HarmonyLib;
using RimWorld;
using RimWorld.Planet;
using Verse;

namespace HardenedRaids
{
    // ---------------------------------------------------------------- lot « génération des pawns »

    /// <summary>
    /// Profondeur d'imbrication de la génération : pendant un raider, PawnRelationWorker_Sibling.GenerateParent (etc.)
    /// génère des proches du même pawnkind / faction ; seuls les pawns générés à la profondeur du raider reçoivent la dotation.
    /// </summary>
    [HarmonyPatch(typeof(PawnGenerator), nameof(PawnGenerator.GeneratePawn), new[] { typeof(PawnGenerationRequest) })]
    public static class Patch_GeneratePawnDepth
    {
        public static void Prefix(out bool __state)
        {
            GenContext.Depth++;
            __state = true;
        }

        // __state reste false si notre préfixe n'a pas tourné : pas de décrément orphelin
        public static void Finalizer(bool __state)
        {
            if (__state) GenContext.Depth--;
        }
    }
}

namespace HardenedRaids
{
    /// <summary>
    /// Groupes générés pour finir en cadavres : pas de génération Hardened (butin d'élite gratuit sur des corps).
    /// MapGenUtility.ScatterCorpses (colonie abandonnée) consomme l'itérateur de PawnGroupMakerUtility.GeneratePawns
    /// dans sa propre boucle, donc tout est généré entre notre préfixe et notre finaliseur.
    /// </summary>
    [HarmonyPatch(typeof(MapGenUtility), nameof(MapGenUtility.ScatterCorpses),
        new[] { typeof(Map), typeof(Faction), typeof(float), typeof(FloatRange) })]
    public static class Patch_ScatterCorpses_NonCombat
    {
        public static void Prefix(out bool __state) { GenContext.NonCombat++; __state = true; }
        public static void Finalizer(bool __state) { if (__state) GenContext.NonCombat--; }
    }

    /// <summary>Appel de détresse (Anomaly) : le groupe de la faction sert uniquement de cadavres tués par les chairbêtes.</summary>
    [HarmonyPatch(typeof(SitePartWorker_DistressCall_Fleshbeasts), nameof(SitePartWorker_DistressCall_Fleshbeasts.PostMapGenerate))]
    public static class Patch_DistressCall_NonCombat
    {
        public static void Prefix(out bool __state) { GenContext.NonCombat++; __state = true; }
        public static void Finalizer(bool __state) { if (__state) GenContext.NonCombat--; }
    }
}

namespace HardenedRaids
{
    /// <summary>
    /// Implants vanilla d'un raider Hardened : seuls les techHediffsRequired du pawnkind sont posés (et facturés), pas le tirage
    /// aléatoire de GenerateTechHediffsFor (gratuit). Seul appelant : PawnGenerator.GenerateInitialHediffs (pawns neufs).
    /// Nos implants de dotation sont posés après, dans le postfixe de GenerateInitialHediffs, et ne sont donc pas concernés.
    /// </summary>
    [HarmonyPatch(typeof(PawnTechHediffsGenerator), nameof(PawnTechHediffsGenerator.GenerateTechHediffsFor))]
    public static class Patch_GenerateTechHediffs
    {
        public static bool Prefix(Pawn pawn)
        {
            Slot s = GenContext.For(pawn);
            if (s == null || !s.tier.healthy) return true;
            try
            {
                PawnTuning.InstallRequiredTechHediffs(pawn, s);
            }
            catch (Exception e)
            {
                Log.Warning("[Hardened Raids] required implants of " + pawn + " : " + e.Message);
            }
            return false;
        }
    }
}

namespace HardenedRaids
{
    /// <summary>
    /// Aperçus (SitePartWorker_Outpost : nombre d'ennemis ; QuestNode_Raid / QuestGen_Threat : pawnkinds du raid ;
    /// SymbolResolver_Settlement : nombre de lits…) : reflètent la génération Hardened, sans générer de pawn.
    /// Avec une graine (parms.seed ou celle de la colonie en génération), l'aperçu et la génération réelle tirent la même chose ; résultat mis en cache (le libellé d'un site
    /// peut être demandé à chaque image), une heure de jeu au plus, vidé au changement de partie.
    /// </summary>
    [HarmonyPatch(typeof(PawnGroupKindWorker_Normal), nameof(PawnGroupKindWorker_Normal.GeneratePawnKindsExample))]
    public static class Patch_GeneratePawnKindsExample
    {
        private const int CacheTicks = 2500;
        private const int CacheMax = 64;
        private static readonly Dictionary<string, KeyValuePair<int, List<PawnKindDef>>> cache = new Dictionary<string, KeyValuePair<int, List<PawnKindDef>>>();
        private static Game cacheGame;

        public static bool Prefix(PawnGroupMakerParms parms, PawnGroupMaker groupMaker, ref IEnumerable<PawnKindDef> __result)
        {
            if (parms == null || groupMaker == null || !RaidGenerator.Applies(parms)) return true;
            try
            {
                string key = null;
                int now = Find.TickManager?.TicksGame ?? 0;
                // graine effective (parms.seed ou celle de la colonie en génération) ; la case fixe le facteur ×2 (DU2)
                int? seed = RaidGenerator.SeedFor(parms);
                if (seed.HasValue)
                {
                    if (cacheGame != Current.Game) { cache.Clear(); cacheGame = Current.Game; }
                    key = $"{parms.faction.loadID}|{parms.groupKind.defName}|{parms.points:R}|{seed.Value}|{parms.tile}|{parms.raidStrategy?.defName}|{parms.inhabitants}|{groupMaker.GetHashCode()}";
                    if (cache.TryGetValue(key, out var hit) && now - hit.Key < CacheTicks && now >= hit.Key)
                    {
                        __result = hit.Value;
                        return false;
                    }
                }
                List<PawnKindDef> kinds = RaidGenerator.ExampleKinds(parms, groupMaker);
                if (kinds == null) return true;
                if (key != null)
                {
                    if (cache.Count >= CacheMax) cache.Clear();
                    cache[key] = new KeyValuePair<int, List<PawnKindDef>>(now, kinds);
                }
                __result = kinds;
                return false;
            }
            catch (Exception e)
            {
                Log.Warning("[Hardened Raids] pawn kinds example: " + e.Message);
                return true;
            }
        }

        /// <summary>
        /// Paliers coupés : l'aperçu vanilla d'un groupe hors raid (lits d'une colonie, « N ennemis » d'un site) suit le
        /// même plafond que sa génération (VanillaWaveCap.OutOfRaidCap). Un aperçu de raid garde toutes ses vagues (DU3).
        /// </summary>
        public static void Postfix(PawnGroupMakerParms parms, ref IEnumerable<PawnKindDef> __result)
        {
            if (parms == null || __result == null || (parms.raidStrategy != null && parms.groupKind == PawnGroupKindDefOf.Combat)) return;
            try
            {
                int cap = VanillaWaveCap.OutOfRaidCap(parms);
                if (cap > 0) __result = __result.Take(cap);
            }
            catch (Exception e) { Log.Warning("[Hardened Raids] pawn kinds example cap: " + e.Message); }
        }
    }
}
