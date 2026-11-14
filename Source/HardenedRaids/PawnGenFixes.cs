using System;
using System.Collections.Generic;
using HarmonyLib;
using RimWorld;
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
