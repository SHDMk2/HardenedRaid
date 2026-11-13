using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using RimWorld;
using UnityEngine;
using Verse;

namespace HardenedRaids
{
    public class GroupPlan
    {
        public string name;
        public int tierIndex;
        public TierDef tier;
        public int count;
        public Loadout loadout;
        public float price;    // prix moyen prévu d'un raider du groupe (points)
    }

    /// <summary>Modèle C : une troupe au palier bas + un noyau au palier haut, proportion continue.</summary>
    public class RaidPlan
    {
        public FactionDef faction;
        public float points;
        public float tierX;
        public int total;      // raiders sur l'ensemble des vagues
        public int waves;
        public int thisWave;   // raiders de cette vague
        public GroupPlan core; // peut être null
        public GroupPlan troop;
        public int ncos, officers; // grades de cette vague
        public ArmorGate gate;

        public static RaidPlan Make(FactionDef f, float points, float? xOverride, int? countOverride, bool needBreacher, System.Random rng)
        {
            float x = xOverride ?? Tiers.TierX(f, points);
            if (x < 1f) return null;
            TierDef[] ladder = Tiers.LadderFor(f);
            int lo = Mathf.Clamp(Mathf.FloorToInt(x), 1, ladder.Length - 1);
            int hi = Mathf.Min(lo + 1, ladder.Length - 1);
            float frac = lo == hi ? 0f : Mathf.Clamp01(x - lo);

            // dotations d'abord : le prix d'un raider dépend de ce qu'il porte
            ArmorGate gate = ArmorGate.For(points);
            Loadout loLoadout = Loadouts.BuildLoadout(f, ladder[lo], needBreacher, rng, gate);
            Loadout hiLoadout = lo == hi ? loLoadout : Loadouts.BuildLoadout(f, ladder[hi], needBreacher, rng, gate);
            float loPrice = Pricing.ExpectedPrice(f, ladder[lo], lo, loLoadout);
            float hiPrice = lo == hi ? loPrice : Pricing.ExpectedPrice(f, ladder[hi], hi, hiLoadout);
            float avg = Mathf.Max(1f, (1f - frac) * loPrice + frac * hiPrice);

            var p = new RaidPlan { faction = f, points = points, tierX = x, gate = gate };
            if (countOverride.HasValue)
            {
                p.total = p.thisWave = Mathf.Max(1, countOverride.Value);
                p.waves = 1;
            }
            else
            {
                p.total = Mathf.Max(1, Mathf.FloorToInt(points / avg));
                int cap = Tiers.WaveCapFor(f);
                // un reste de moins de 10 % est absorbé par la vague, pas de mini-vague inutile
                p.waves = p.total > cap * 1.1f ? Mathf.CeilToInt(p.total / (float)cap) : 1;
                p.thisWave = Mathf.CeilToInt(p.total / (float)p.waves);
            }

            int k = Mathf.RoundToInt(p.thisWave * frac);
            if (k > 0)
                p.core = new GroupPlan { name = "noyau", tierIndex = hi, tier = ladder[hi], count = k, loadout = hiLoadout, price = hiPrice };
            p.troop = new GroupPlan { name = "troupe", tierIndex = lo, tier = ladder[lo], count = p.thisWave - k, loadout = loLoadout, price = loPrice };
            p.ncos = Pricing.NcoCount(p.thisWave);
            p.officers = Pricing.OfficerCount(p.thisWave);
            return p;
        }

        public string Describe()
        {
            var sb = new StringBuilder();
            sb.Append($"{faction.label} @ {points:F0} pts : {total} raiders (palier {tierX:F2})");
            if (waves > 1) sb.Append($" en {waves} vagues de ~{thisWave}");
            sb.Append($", par vague : {ncos} sous-officier(s), {officers} officier(s)");
            if (gate != null) sb.Append($", armure max {gate.cap * ArmorGate.MarineSharp:F1} (marine {ArmorGate.MarineSharp:F1}), tech colonie {gate.colonyTech}");
            sb.AppendLine();
            foreach (GroupPlan g in new[] { core, troop })
            {
                if (g == null || g.count <= 0) continue;
                sb.AppendLine($"   [{g.name}] {g.count}× {g.tier.label} à ~{g.price:F0} pts — skill {g.tier.combatSkill.min}-{g.tier.combatSkill.max}, implants {g.tier.implantCount.min}-{g.tier.implantCount.max}");
                sb.AppendLine($"      {g.loadout.Describe()}");
            }
            return sb.ToString().TrimEnd();
        }
    }

    /// <summary>Ce qu'on est en train de générer pour un raider précis.</summary>
    public class Slot
    {
        public PawnKindDef kind;
        public XenotypeDef xenotype;
        public Faction faction;
        public int tierIndex;
        public TierDef tier;
        public Loadout loadout;
        public Role role;
        public Rank rank;
        public List<ThingDef> implants;
        public ThingDef brain;
        public System.Random rng;
    }

    public static class GenContext
    {
        public static Slot Current;

        public static Slot For(Pawn p)
        {
            Slot s = Current;
            if (s == null || p == null || p.kindDef != s.kind || p.Faction != s.faction) return null;
            return s;
        }
    }

    /// <summary>Informations sur le raid en cours d'exécution (null hors raid).</summary>
    public static class RaidContext
    {
        public static IncidentParms Parms;
        public static bool DebugTest;
        public static float PreAdjustPoints = -1f;
        public static WaveEntry PendingWave;
        public static List<ThingDef> PendingVehicles; // véhicules achetés pour ce raid, apparus après l'arrivée
    }

    public static class RaidGenerator
    {
        public static bool Applies(PawnGroupMakerParms parms)
        {
            if (!HRSettings.EnableTiers || RaidContext.Parms == null) return false;
            Faction f = parms.faction;
            if (f == null || f.IsPlayer || !f.def.humanlikeFaction || !f.HostileTo(Faction.OfPlayer)) return false;
            if (parms.groupKind != PawnGroupKindDefOf.Combat) return false;
            return true;
        }

        private static bool StrategyNeedsBreachers(RaidStrategyDef s, out Func<PawnKindDef, bool> isRequired)
        {
            isRequired = null;
            if (s?.Worker is RaidStrategyWorker_ImmediateAttackBreaching) { isRequired = k => k.isGoodBreacher; return true; }
            if (s?.Worker is RaidStrategyWorker_ImmediateAttackSappers) { isRequired = k => k.canBeSapper; return true; }
            return false;
        }

        public static bool TryGenerate(PawnGroupMakerParms parms, PawnGroupMaker groupMaker, List<Pawn> outPawns)
        {
            WaveEntry wave = RaidContext.PendingWave;
            bool needBreacher = StrategyNeedsBreachers(parms.raidStrategy, out Func<PawnKindDef, bool> isRequired);
            var rng = new System.Random(Rand.Int);
            float tierX = wave?.tierX ?? Tiers.TierX(parms.faction.def, parms.points);
            if (tierX < 1f) return false;

            // ---- véhicules (partie 2) : achetés sur le budget du raid, avant l'infanterie
            float infantryPoints = parms.points;
            List<ThingDef> vehicles = null;
            if (HRSettings.EnableVehicles && VehicleGate.Active && !RaidContext.DebugTest && VehicleGate.ArrivalOk(RaidContext.Parms?.raidArrivalMode)
                && parms.faction.def.techLevel >= TechLevel.Industrial)
            {
                try
                {
                    vehicles = VehicleCompat.Plan(parms.faction, parms.points, tierX, rng, out float vCost);
                    if (wave == null) infantryPoints = Mathf.Max(parms.points * 0.5f, parms.points - vCost);
                }
                catch (Exception e)
                {
                    Log.Warning("[Hardened Raids] choix des véhicules : " + e.Message);
                    vehicles = null;
                }
            }

            RaidPlan plan = RaidPlan.Make(parms.faction.def, infantryPoints, tierX, wave?.count, needBreacher, rng);
            if (plan == null) return false;

            // ---- choix des pawnkinds (on respecte les règles vanilla : chef unique, pawns requis par la stratégie, xénotypes)
            var chosen = new List<PawnGenOptionWithXenotype>();
            bool leaderChosen = false;
            for (int i = 0; i < plan.thisWave; i++)
            {
                List<PawnGenOptionWithXenotype> opts = PawnGroupMakerUtility.GetOptions(parms, parms.faction.def, groupMaker.options, parms.points, 1e9f, 1e9f, chosen, leaderChosen)
                    .Where(o => o.Option.kind.isFighter && o.Option.kind.RaceProps.Humanlike).ToList();
                if (!opts.TryRandomElementByWeight(o => o.SelectionWeight, out PawnGenOptionWithXenotype pick)) break;
                chosen.Add(pick);
                if (pick.Option.kind.factionLeader) leaderChosen = true;
            }
            if (chosen.Count == 0) return false;

            // ---- répartition noyau / troupe et rôles
            var slots = new List<Slot>();
            List<PawnGenOptionWithXenotype> order = chosen.InRandomOrder().ToList();
            // le chef va dans le noyau (ou la troupe s'il n'y a pas de noyau)
            order = order.OrderByDescending(o => o.Option.kind.factionLeader).ToList();
            int coreCount = plan.core?.count ?? 0;
            for (int i = 0; i < order.Count; i++)
            {
                GroupPlan g = i < coreCount ? plan.core : plan.troop;
                PawnKindDef kind = order[i].Option.kind;
                TierDef[] ladder = Tiers.LadderFor(parms.faction.def);
                int ti = kind.factionLeader ? Mathf.Min(g.tierIndex + 1, ladder.Length - 1) : g.tierIndex;
                slots.Add(new Slot
                {
                    kind = kind, xenotype = order[i].Xenotype, faction = parms.faction,
                    tierIndex = ti, tier = ladder[ti], loadout = g.loadout, role = Role.Main, rank = Rank.Soldier, rng = rng,
                });
            }
            AssignRoles(slots.Where(s => s.loadout == plan.core?.loadout).ToList(), isRequired);
            AssignRoles(slots.Where(s => s.loadout == plan.troop.loadout).ToList(), isRequired);

            // ---- grades (chef d'abord, puis noyau) : 1 officier / 100, 1 sous-officier / 10 ; implants tirés ici pour le prix
            int nOff = Pricing.OfficerCount(slots.Count), nNco = Pricing.NcoCount(slots.Count);
            float spent = 0f;
            for (int i = 0; i < slots.Count; i++)
            {
                Slot s = slots[i];
                s.rank = i < nOff ? Rank.Officer : i < nOff + nNco ? Rank.Nco : Rank.Soldier;
                s.implants = Loadouts.PickImplants(parms.faction.def, s.tier, rng);
                s.brain = Tiers.IsLowTech(parms.faction.def) ? null : Loadouts.PickCyberbrain(s.tier.cyberbrainRank);
                spent += Pricing.SlotPrice(s);
            }

            // ---- vagues suivantes
            if (plan.waves > 1 && wave == null && !RaidContext.DebugTest)
                WaveScheduler.Schedule(plan, RaidContext.Parms);

            // ---- génération
            bool allowFood = parms.raidStrategy == null || parms.raidStrategy.pawnsCanBringFood || (parms.faction != null && !parms.faction.HostileTo(Faction.OfPlayer));
            Predicate<Pawn> validator = parms.raidStrategy != null ? (Pawn p) => parms.raidStrategy.Worker.CanUsePawn(parms.points, p, outPawns) : null;
            bool downedDone = false;
            foreach (Slot s in slots)
            {
                Pawn pawn = GenerateOne(s, parms, allowFood, validator);
                if (pawn == null) continue;
                if (parms.forceOneDowned && !downedDone)
                {
                    pawn.health.forceDowned = true;
                    if (pawn.guest != null) pawn.guest.Recruitable = true;
                    pawn.mindState.canFleeIndividual = false;
                    downedDone = true;
                }
                outPawns.Add(pawn);
            }

            if (vehicles != null && vehicles.Count > 0 && outPawns.Count > 0) RaidContext.PendingVehicles = vehicles;
            HRLog.Msg((wave != null ? $"vague {wave.index}/{wave.totalWaves} — " : "") + plan.Describe() +
                      $"\n   stratégie : {parms.raidStrategy?.defName ?? "?"}, générés : {outPawns.Count}, coût réel ≈ {spent:F0} / {infantryPoints:F0} pts" +
                      (vehicles != null && vehicles.Count > 0 ? $", véhicules : {string.Join(", ", vehicles.Select(v => v.label))}" : ""));
            return outPawns.Count > 0;
        }

        private static void AssignRoles(List<Slot> group, Func<PawnKindDef, bool> isRequired)
        {
            if (group.Count == 0) return;
            TierDef t = group[0].tier;
            // les pawns exigés par une stratégie de brèche/sape portent une arme destructrice
            if (isRequired != null)
                foreach (Slot s in group.Where(s => isRequired(s.kind))) s.role = Role.Breacher;

            void Fill(Role r, float share)
            {
                if (share <= 0f) return;
                int n = Mathf.RoundToInt(group.Count * share);
                if (n == 0 && group.Count >= 6) n = 1;
                n -= group.Count(s => s.role == r);
                foreach (Slot s in group.Where(s => s.role == Role.Main && !s.kind.factionLeader).Take(Mathf.Max(0, n))) s.role = r;
            }
            Fill(Role.Breacher, t.breacherShare);
            Fill(Role.Heavy, t.heavyShare);
            Fill(Role.Sniper, t.sniperShare);
            Fill(Role.Melee, t.meleeShare);
        }

        private static Pawn GenerateOne(Slot s, PawnGroupMakerParms parms, bool allowFood, Predicate<Pawn> validator)
        {
            FloatRange? age = null;
            if (s.tier.healthy && parms.raidAgeRestriction == null && s.kind.race?.race != null && s.kind.race.race.lifeExpectancy is > 50f and < 120f)
            {
                float lo = Mathf.Max(s.tier.ageRange.min, s.kind.minGenerationAge);
                float hi = Mathf.Min(s.tier.ageRange.max, s.kind.maxGenerationAge);
                if (hi > lo) age = new FloatRange(lo, hi);
            }
            var request = new PawnGenerationRequest(s.kind, parms.faction, PawnGenerationContext.NonPlayer, tile: parms.tile,
                allowDowned: parms.faction.deactivated, canGeneratePawnRelations: true, mustBeCapableOfViolence: true,
                allowPregnant: !s.tier.healthy, allowFood: allowFood, allowAddictions: !s.tier.healthy, inhabitant: parms.inhabitants,
                validatorPostGear: validator, fixedIdeo: parms.ideo, forcedXenotype: s.xenotype, biologicalAgeRange: age);
            if (parms.raidAgeRestriction != null && parms.raidAgeRestriction.Worker.ShouldApplyToKind(s.kind))
            {
                request.BiologicalAgeRange = parms.raidAgeRestriction.ageRange;
                request.AllowedDevelopmentalStages = parms.raidAgeRestriction.developmentStage;
            }
            if (s.kind.pawnGroupDevelopmentStage.HasValue) request.AllowedDevelopmentalStages = s.kind.pawnGroupDevelopmentStage.Value;
            if (!Find.Storyteller.difficulty.ChildRaidersAllowed) request.AllowedDevelopmentalStages = DevelopmentalStage.Adult;

            Pawn pawn;
            GenContext.Current = s;
            try
            {
                pawn = PawnGenerator.GeneratePawn(request);
            }
            finally
            {
                GenContext.Current = null;
            }
            if (pawn == null) return null;
            try
            {
                PawnTuning.ApplySkills(pawn, s);
                PawnTuning.Mark(pawn, s.tierIndex);
                PawnTuning.ApplyHarshDeath(pawn, s);
            }
            catch (Exception e)
            {
                Log.Warning("[Hardened Raids] post-traitement de " + pawn + " : " + e.Message);
            }
            return pawn;
        }
    }
}
