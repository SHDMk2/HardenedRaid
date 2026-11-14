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
        // pile de contextes : un raid déclenché pendant un autre a son propre cadre (Parms, points, vagues, véhicules…)
        public static void Prefix(IncidentParms parms, bool debugTest, out RaidFrame __state)
        {
            __state = RaidContext.Push(parms, debugTest);
        }

        // vagues suivantes : programmées seulement si le raid a réellement été généré (B12)
        public static void Postfix(bool debugTest, bool __result, RaidFrame __state)
        {
            if (!__result || debugTest || __state == null) return;
            try { WaveScheduler.Commit(__state); }
            catch (Exception e) { Log.Warning("[Hardened Raids] wave scheduling: " + e); }
        }

        public static void Finalizer(RaidFrame __state)
        {
            RaidContext.Pop(__state);
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
        public static bool Prefix(PawnGroupMakerParms parms, PawnGroupMaker groupMaker, List<Pawn> outPawns, out bool __state)
        {
            __state = false;
            // groupe principal du raid en cours (le premier groupe de la faction du raid) : seul à pouvoir faire des vagues
            RaidFrame frame = RaidContext.Current;
            bool main = frame != null && frame.TryClaimMainGroup(parms);
            if (RaidGenerator.Applies(parms))
            {
                try
                {
                    if (RaidGenerator.TryGenerate(parms, groupMaker, outPawns)) return false;
                }
                catch (Exception e)
                {
                    Log.Error("[Hardened Raids] raid generation failed, falling back to vanilla: " + e);
                    foreach (Pawn p in outPawns) if (!p.Destroyed) p.Destroy();
                    outPawns.Clear();
                    GenContext.Current = null;
                }
                // repli vanilla : rien de ce qui a été prévu (vagues, véhicules) ne vaut, mais le plafond par vague reste
                if (main) frame.CancelPlanned();
                __state = VanillaWaveCap.Arm(parms, main ? frame : null, Tiers.WaveCapFor(parms.faction.def));
                return true;
            }
            return true;
        }

        public static void Finalizer(bool __state)
        {
            if (__state) VanillaWaveCap.Disarm();
        }
    }

    // ---------------------------------------------------------------- mécanoïdes blindés : prix ×N et blocage selon points / tech colonie
    [HarmonyPatch(typeof(PawnGroupKindWorker_Normal), "GeneratePawns",
        new[] { typeof(PawnGroupMakerParms), typeof(PawnGroupMaker), typeof(List<Pawn>), typeof(bool) })]
    public static class Patch_GeneratePawns_Mechs
    {
        public static void Prefix(PawnGroupMakerParms parms, PawnGroupMaker groupMaker)
        {
            // raids et tout groupe mécanoïde hors raid (vaisseau écrasé, mécas endormis, quêtes…)
            if (parms?.groupKind != PawnGroupKindDefOf.Combat) return;
            try { MechPricing.Apply(parms, groupMaker); }
            catch (Exception e) { MechPricing.Restore(); Log.Warning("[Hardened Raids] mechanoid pricing: " + e.Message); }
        }

        public static void Finalizer() => MechPricing.Restore();
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
                Log.Warning("[Hardened Raids] health/implants of " + pawn + " : " + e.Message);
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
                Log.Warning("[Hardened Raids] armor of " + pawn + " : " + e.Message);
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
                Log.Warning("[Hardened Raids] weapon of " + pawn + " : " + e.Message);
                return true;
            }
        }

        [HarmonyPriority(Priority.Last)]
        public static void Postfix(Pawn pawn)
        {
            Slot s = GenContext.For(pawn);
            if (s == null || !s.tier.healthy) return;
            CECompat.EnsureAmmo(pawn);
            try { PawnTuning.DropBlockingApparel(pawn); }
            catch (Exception e) { Log.Warning("[Hardened Raids] shield/weapon check of " + pawn + " : " + e.Message); }
        }
    }
}
