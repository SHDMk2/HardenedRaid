using System;
using System.Collections.Generic;
using System.Linq;
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
        public static bool Prefix(float points, RaidStrategyDef raidStrategy, Faction faction, PawnGroupKindDef groupKind, ref float __result)
        {
            RaidFrame f = RaidContext.Current;
            if (f?.Parms == null || f.PreAdjustPoints >= 0f) return true;
            f.PreAdjustPoints = points;
            // vague différée : ses points sont déjà ajustés (part exacte du raid d'origine, même si la stratégie a changé)
            WaveEntry w = f.Wave;
            if (w == null || w.adjustedPoints <= 0f) return true;
            float min = raidStrategy?.Worker != null ? raidStrategy.Worker.MinimumPoints(faction, groupKind) * 1.05f : 0f;
            __result = Math.Max(w.adjustedPoints, min);
            return false;
        }
    }

    /// <summary>Vague différée : même restriction d'âge que le raid d'origine (pas de nouveau tirage).</summary>
    [HarmonyPatch(typeof(IncidentWorker_RaidEnemy), nameof(IncidentWorker_RaidEnemy.ResolveRaidAgeRestriction))]
    public static class Patch_WaveAgeRestriction
    {
        public static bool Prefix() => RaidContext.PendingWave?.raidParms == null;
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
                __state = VanillaWaveCap.Arm(parms, main ? frame : null,
                                             Tiers.WaveCapFor(parms.faction.def) * (main ? 1 : RaidGenerator.SingleWaveCapFactor(parms)));
                return true;
            }
            // non-humains hostiles d'un raid (mécanoïdes…) : plafond low-tech par vague, le reste en vagues (A12 / D5) ;
            // humains d'un raid sans paliers (EnableTiers décoché) : même plafond par vague que les paliers (D3)
            if (main && parms.faction != null && !parms.faction.IsPlayer && parms.faction.HostileTo(Faction.OfPlayer))
            {
                if (!parms.faction.def.humanlikeFaction)
                    __state = VanillaWaveCap.Arm(parms, frame, VanillaWaveCap.NonHumanCap);
                else if (!HRSettings.EnableTiers)
                    __state = VanillaWaveCap.Arm(parms, frame, Tiers.WaveCapFor(parms.faction.def));
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
        public static void Prefix(PawnGroupMakerParms parms, PawnGroupMaker groupMaker, out bool __state)
        {
            // raids et tout groupe mécanoïde hors raid (vaisseau écrasé, mécas endormis, quêtes…)
            __state = false;
            if (parms?.groupKind != PawnGroupKindDefOf.Combat) return;
            // vague différée : plafond d'armure du raid entier, pas de ses seuls points (A5)
            float gatePoints = RaidContext.PendingWave?.gatePoints ?? -1f;
            if (gatePoints > 0f && parms.faction != null && !parms.faction.def.humanlikeFaction && groupMaker?.options != null
                && parms.faction == RaidContext.Parms?.faction && MechPricing.HostileToPlayer(parms.faction))
                __state = MechPricing.Push(groupMaker.options.Select(o => o.kind), gatePoints);
            else
                __state = MechPricing.Apply(parms, groupMaker);
        }

        // ne ferme que la portée ouverte par ce prefix (imbrication sûre)
        public static void Finalizer(bool __state)
        {
            if (__state) MechPricing.Pop();
        }
    }

    // ---------------------------------------------------------------- santé + implants (avant l'équipement)
    [HarmonyPatch(typeof(PawnGenerator), "GenerateInitialHediffs")]
    public static class Patch_GenerateInitialHediffs
    {
        public static void Postfix(Pawn pawn)
        {
            Slot s = GenContext.For(pawn);
            if (s == null) return;
            s.freshPawn = pawn;   // pawn neuf (un pawn du monde rhabillé ne passe jamais ici)
            if (!s.tier.healthy) return;
            try
            {
                s.installed = null;
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
        public static bool Prefix(Pawn pawn, PawnGenerationRequest request)
        {
            Slot s = GenContext.For(pawn);
            if (s == null || !s.tier.healthy) return true;
            if (!pawn.RaceProps.ToolUser || !pawn.RaceProps.IsFlesh) return true;
            try
            {
                return !PawnTuning.WearLoadout(pawn, s, request);
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
            if (s.sapperRaid) PawnTuning.EnsureSapperSkill(pawn, s);
            try { PawnTuning.DropBlockingApparel(pawn); }
            catch (Exception e) { Log.Warning("[Hardened Raids] shield/weapon check of " + pawn + " : " + e.Message); }
        }
    }
}
