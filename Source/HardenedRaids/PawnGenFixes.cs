using System;
using System.Collections.Generic;
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
