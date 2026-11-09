using System;
using System.Collections.Generic;
using HarmonyLib;
using RimWorld;
using Verse;

namespace HardenedRaids
{
    // ---------------------------------------------------------------- contexte du raid
    [HarmonyPatch(typeof(IncidentWorker_Raid), nameof(IncidentWorker_Raid.TryGenerateRaidInfo))]
    public static class Patch_TryGenerateRaidInfo
    {
        public static void Prefix(IncidentParms parms, bool debugTest, out bool __state)
        {
            __state = RaidContext.Parms == null;      // raid imbriqué : on ne touche pas au contexte externe
            if (!__state) return;
            RaidContext.Parms = parms;
            RaidContext.DebugTest = debugTest;
            RaidContext.PreAdjustPoints = -1f;
            RaidContext.PendingVehicles = null;
        }

        public static void Finalizer(bool __state)
        {
            if (!__state) return;
            RaidContext.Parms = null;
            RaidContext.DebugTest = false;
            RaidContext.PreAdjustPoints = -1f;
            RaidContext.PendingVehicles = null;
        }
    }

    [HarmonyPatch(typeof(IncidentWorker_Raid), nameof(IncidentWorker_Raid.AdjustedRaidPoints))]
    public static class Patch_AdjustedRaidPoints
    {
        public static void Prefix(float points)
        {
            if (RaidContext.Parms != null && RaidContext.PreAdjustPoints < 0f) RaidContext.PreAdjustPoints = points;
        }
    }

    // ---------------------------------------------------------------- génération du groupe
    [HarmonyPatch(typeof(PawnGroupKindWorker_Normal), "GeneratePawns",
        new[] { typeof(PawnGroupMakerParms), typeof(PawnGroupMaker), typeof(List<Pawn>), typeof(bool) })]
    public static class Patch_GeneratePawns
    {
        public static bool Prefix(PawnGroupMakerParms parms, PawnGroupMaker groupMaker, List<Pawn> outPawns)
        {
            if (!RaidGenerator.Applies(parms)) return true;
            try
            {
                if (RaidGenerator.TryGenerate(parms, groupMaker, outPawns)) return false;
            }
            catch (Exception e)
            {
                Log.Error("[Hardened Raids] génération du raid échouée, retour à vanilla : " + e);
                foreach (Pawn p in outPawns) if (!p.Destroyed) p.Destroy();
                outPawns.Clear();
                GenContext.Current = null;
            }
            return true;
        }
    }

    // ---------------------------------------------------------------- santé + implants (avant l'équipement)
    [HarmonyPatch(typeof(PawnGenerator), "GenerateInitialHediffs")]
    public static class Patch_GenerateInitialHediffs
    {
        public static void Postfix(Pawn pawn)
        {
            Slot s = GenContext.For(pawn);
            if (s == null || !s.tier.healthy) return;
            try
            {
                PawnTuning.CleanHealth(pawn);
                PawnTuning.InstallImplants(pawn, s);
            }
            catch (Exception e)
            {
                Log.Warning("[Hardened Raids] santé/implants de " + pawn + " : " + e.Message);
            }
        }
    }

    // ---------------------------------------------------------------- armure uniforme
    [HarmonyPatch(typeof(PawnApparelGenerator), nameof(PawnApparelGenerator.GenerateStartingApparelFor))]
    public static class Patch_GenerateApparel
    {
        public static bool Prefix(Pawn pawn)
        {
            Slot s = GenContext.For(pawn);
            if (s == null || !s.tier.healthy) return true;
            if (!pawn.RaceProps.ToolUser || !pawn.RaceProps.IsFlesh) return true;
            try
            {
                return !PawnTuning.WearLoadout(pawn, s);
            }
            catch (Exception e)
            {
                Log.Warning("[Hardened Raids] armure de " + pawn + " : " + e.Message);
                return true;
            }
        }
    }

    // ---------------------------------------------------------------- arme uniforme (CE ajoute ses munitions dans son postfix)
    [HarmonyPatch(typeof(PawnWeaponGenerator), nameof(PawnWeaponGenerator.TryGenerateWeaponFor))]
    public static class Patch_GenerateWeapon
    {
        public static bool Prefix(Pawn pawn)
        {
            Slot s = GenContext.For(pawn);
            if (s == null || !s.tier.healthy) return true;
            if (!pawn.RaceProps.ToolUser || !pawn.health.capacities.CapableOf(PawnCapacityDefOf.Manipulation) || pawn.WorkTagIsDisabled(WorkTags.Violent))
                return true;
            try
            {
                return !PawnTuning.EquipWeapon(pawn, s);
            }
            catch (Exception e)
            {
                Log.Warning("[Hardened Raids] arme de " + pawn + " : " + e.Message);
                return true;
            }
        }

        [HarmonyPriority(Priority.Last)]
        public static void Postfix(Pawn pawn)
        {
            Slot s = GenContext.For(pawn);
            if (s == null || !s.tier.healthy) return;
            CECompat.EnsureAmmo(pawn);
        }
    }
}
