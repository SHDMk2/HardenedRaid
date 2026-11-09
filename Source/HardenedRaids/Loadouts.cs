using System;
using System.Collections.Generic;
using System.Linq;
using RimWorld;
using Verse;

namespace HardenedRaids
{
    public enum Role { Main, Heavy, Sniper, Breacher, Melee }

    /// <summary>Tables d'objets auxquelles une faction a droit (union des tags de ses pawnkinds).</summary>
    public class FactionTables
    {
        public HashSet<string> apparelTags = new HashSet<string>();
        public HashSet<string> weaponTags = new HashSet<string>();
        public HashSet<string> implantTags = new HashSet<string>();
    }

    /// <summary>Dotation uniforme d'un groupe.</summary>
    public class Loadout
    {
        public List<ThingStuffPair> apparel = new List<ThingStuffPair>();
        public Dictionary<Role, ThingStuffPair?> weapons = new Dictionary<Role, ThingStuffPair?>();

        public ThingStuffPair? WeaponFor(Role r)
        {
            if (weapons.TryGetValue(r, out var w) && w.HasValue) return w;
            if (weapons.TryGetValue(Role.Main, out var m) && m.HasValue) return m;
            if (weapons.TryGetValue(Role.Melee, out var me) && me.HasValue) return me;
            return null;
        }

        public string Describe()
        {
            string ap = apparel.Count == 0 ? "(vanilla)" : string.Join(", ", apparel.Select(Label));
            var parts = new List<string>();
            foreach (Role r in Enum.GetValues(typeof(Role)))
                if (weapons.TryGetValue(r, out var w) && w.HasValue) parts.Add($"{r}={Label(w.Value)}");
            return $"armure: {ap} | armes: {string.Join(", ", parts)}";
        }

        public static string Label(ThingStuffPair p) => p.stuff != null ? $"{p.thing.label} ({p.stuff.label})" : p.thing.label;
    }

    public static class Loadouts
    {
        private static List<ThingStuffPair> apparelPairs;
        private static List<ThingStuffPair> weaponPairs;
        private static List<ThingDef> implants;
        private static List<ThingDef> cyberbrains;
        private static readonly Dictionary<FactionDef, FactionTables> tables = new Dictionary<FactionDef, FactionTables>();
        private static StatDef carryBulk;

        public static void BuildCaches()
        {
            try
            {
                carryBulk = DefDatabase<StatDef>.GetNamedSilentFail("CarryBulk"); // Combat Extended
                apparelPairs = ThingStuffPair.AllWith(td => td.IsApparel)
                    .Where(p => p.Commonality > 0f && (p.stuff == null || p.stuff.stuffProps.allowedInStuffGeneration) && ApparelAllowed(p.thing))
                    .ToList();
                weaponPairs = ThingStuffPair.AllWith(td => td.equipmentType == EquipmentType.Primary && !td.weaponTags.NullOrEmpty())
                    .Where(p => p.Commonality > 0f && (p.stuff == null || p.stuff.stuffProps.allowedInStuffGeneration) && WeaponAllowed(p.thing))
                    .ToList();
                cyberbrains = DefDatabase<ThingDef>.AllDefs
                    .Where(d => d.defName.StartsWith("gits") && d.defName.EndsWith("Cyberbrain") && d.category == ThingCategory.Item)
                    .OrderBy(d => d.BaseMarketValue).ToList();
                implants = DefDatabase<ThingDef>.AllDefs
                    .Where(d => d.isTechHediff && d.techHediffsTags != null && !cyberbrains.Contains(d) && ImplantCombatScore(d) > 0f)
                    .ToList();
                HRLog.Msg($"catalogue : {apparelPairs.Count} vêtements, {weaponPairs.Count} armes, {implants.Count} implants de combat, {cyberbrains.Count} cyberbrains GiTS.");
            }
            catch (Exception e)
            {
                Log.Error("[Hardened Raids] construction du catalogue impossible : " + e);
            }
        }

        // ---------------------------------------------------------------- tables par faction
        public static FactionTables TablesFor(FactionDef f)
        {
            if (tables.TryGetValue(f, out var t)) return t;
            t = new FactionTables();
            IEnumerable<PawnKindDef> kinds = (f.pawnGroupMakers ?? new List<PawnGroupMaker>())
                .SelectMany(g => (g.options ?? new List<PawnGenOption>()).Concat(g.guards ?? new List<PawnGenOption>()).Select(o => o.kind));
            if (f.basicMemberKind != null) kinds = kinds.Append(f.basicMemberKind);
            foreach (PawnKindDef k in kinds.Distinct())
            {
                if (k.RaceProps == null || !k.RaceProps.Humanlike) continue;
                if (k.apparelTags != null) t.apparelTags.AddRange(k.apparelTags);
                if (k.apparelRequired != null) foreach (ThingDef d in k.apparelRequired) if (d.apparel?.tags != null) t.apparelTags.AddRange(d.apparel.tags);
                if (k.weaponTags != null) t.weaponTags.AddRange(k.weaponTags);
                if (k.techHediffsTags != null) t.implantTags.AddRange(k.techHediffsTags);
            }
            tables[f] = t;
            return t;
        }

        // ---------------------------------------------------------------- filtres
        private static bool ApparelAllowed(ThingDef d)
        {
            if (d.apparel == null) return false;
            if (!d.apparel.developmentalStageFilter.Has(DevelopmentalStage.Adult)) return false;
            if (!d.apparel.CorrectGenderForWearing(Gender.Male) || !d.apparel.CorrectGenderForWearing(Gender.Female)) return false;
            if (d.HasComp(typeof(CompShield))) return false;                       // ceintures bouclier : incompatibles avec le tir
            if (d.thingClass != null && d.thingClass.Name.Contains("Shield")) return false; // boucliers CE
            return true;
        }

        private static bool WeaponAllowed(ThingDef d)
        {
            if (d.HasComp(typeof(CompBladelinkWeapon))) return false;              // armes persona
            if (d.generateAllowChance <= 0f) return false;
            return true;
        }

        private static bool Intersects(List<string> a, HashSet<string> b) => a != null && a.Any(b.Contains);

        public static bool IsDestructive(ThingDef d) =>
            (d.Verbs != null && d.Verbs.Any(v => v.ai_IsBuildingDestroyer)) || (d.weaponTags != null && d.weaponTags.Any(t => t.Contains("Destructive")));

        private static bool IsHeavy(ThingDef d) =>
            (d.weaponClasses != null && d.weaponClasses.Any(c => c.defName == "RangedHeavy")) || (d.weaponTags != null && d.weaponTags.Contains("GunHeavy"));

        private static bool IsSniper(ThingDef d) => d.weaponTags != null && d.weaponTags.Any(t => t.Contains("Sniper"));

        private static bool IsGrenadeOrSingleUse(ThingDef d) =>
            d.weaponTags != null && d.weaponTags.Any(t => t.Contains("Grenade") || t == "GunSingleUse");

        // ---------------------------------------------------------------- armure
        private static float ApparelScore(ThingStuffPair p)
        {
            float cov = p.thing.apparel.HumanBodyCoverage;
            float sharp = p.thing.GetStatValueAbstract(StatDefOf.ArmorRating_Sharp, p.stuff);
            float blunt = p.thing.GetStatValueAbstract(StatDefOf.ArmorRating_Blunt, p.stuff);
            return (sharp + 0.5f * blunt) * cov * 10f + cov * 0.3f;
        }

        private static float CarryScore(ThingStuffPair p)
        {
            if (carryBulk == null || p.thing.equippedStatOffsets == null) return 0f;
            return p.thing.equippedStatOffsets.GetStatOffsetFromList(carryBulk);
        }

        public static List<ThingStuffPair> BuildApparelSet(FactionTables t, float budget, Random rng)
        {
            var set = new List<ThingStuffPair>();
            if (apparelPairs == null) return set;
            List<ThingStuffPair> cands = apparelPairs.Where(p => p.Price <= budget && Intersects(p.thing.apparel.tags, t.apparelTags)).ToList();
            if (cands.Count == 0) return set;
            float left = budget;

            // 1. Combat Extended : un équipement de portage (gilet tactique / sac) pour les munitions
            if (carryBulk != null)
            {
                ThingStuffPair? carry = cands.Where(p => CarryScore(p) > 0f && p.Price <= budget * 0.25f)
                    .OrderByDescending(CarryScore).Cast<ThingStuffPair?>().FirstOrDefault();
                if (carry.HasValue) { set.Add(carry.Value); left -= carry.Value.Price; }
            }

            // 2. Glouton : la meilleure protection d'abord, en restant compatible avec ce qui est déjà choisi
            foreach (ThingStuffPair p in cands.Where(p => !p.thing.apparel.layers.Contains(ApparelLayerDefOf.Belt))
                         .OrderByDescending(p => ApparelScore(p) * (0.95f + 0.1f * (float)rng.NextDouble())))
            {
                if (p.Price > left) continue;
                if (set.Any(s => s.thing == p.thing || !ApparelUtility.CanWearTogether(s.thing, p.thing, BodyDefOf.Human))) continue;
                set.Add(p);
                left -= p.Price;
            }
            return set;
        }

        /// <summary>Couche chaude compatible avec la dotation (parka...) si le raider risque l'hypothermie.</summary>
        public static ThingStuffPair? BestWarmLayer(FactionTables t, Pawn pawn, float neededInsulation)
        {
            if (apparelPairs == null) return null;
            return apparelPairs
                .Where(p => Intersects(p.thing.apparel.tags, t.apparelTags) && p.InsulationCold > 0f
                            && p.thing.apparel.PawnCanWear(pawn) && pawn.apparel.CanWearWithoutDroppingAnything(p.thing))
                .OrderByDescending(p => System.Math.Min(p.InsulationCold, neededInsulation) - p.Price * 0.0001f)
                .Cast<ThingStuffPair?>().FirstOrDefault();
        }

        // ---------------------------------------------------------------- armes
        private static List<ThingStuffPair> WeaponCands(FactionTables t, float budget, Func<ThingDef, bool> filter, bool ignoreTags = false) =>
            weaponPairs == null ? new List<ThingStuffPair>() :
            weaponPairs.Where(p => p.Price <= budget && filter(p.thing) && (ignoreTags || Intersects(p.thing.weaponTags, t.weaponTags))
                                   && !PawnWeaponGenerator.IsDerpWeapon(p.thing, p.stuff)).ToList();

        /// <summary>Parmi les 3 meilleures (par valeur), une au hasard pondéré : variété d'un raid à l'autre, uniformité dans le raid.</summary>
        private static ThingStuffPair? PickTop(List<ThingStuffPair> cands, Random rng)
        {
            if (cands.Count == 0) return null;
            // une seule matière par arme : la plus chère
            List<ThingStuffPair> best = cands.GroupBy(p => p.thing).Select(g => g.OrderByDescending(p => p.Price).First())
                .OrderByDescending(p => p.Price).Take(3).ToList();
            float[] w = { 3f, 2f, 1f };
            float total = 0f;
            for (int i = 0; i < best.Count; i++) total += w[i];
            double r = rng.NextDouble() * total;
            for (int i = 0; i < best.Count; i++)
            {
                r -= w[i];
                if (r <= 0) return best[i];
            }
            return best[0];
        }

        public static Loadout BuildLoadout(FactionDef f, TierDef tier, bool needBreacher, Random rng)
        {
            FactionTables t = TablesFor(f);
            var lo = new Loadout();
            lo.apparel = BuildApparelSet(t, tier.apparelBudget, rng);
            float wb = tier.weaponBudget;
            lo.weapons[Role.Main] = PickTop(WeaponCands(t, wb, d => d.IsRangedWeapon && !IsDestructive(d) && !IsHeavy(d) && !IsSniper(d) && !IsGrenadeOrSingleUse(d)), rng);
            lo.weapons[Role.Melee] = PickTop(WeaponCands(t, wb, d => d.IsMeleeWeapon && !IsDestructive(d)), rng);
            if (tier.heavyShare > 0f) lo.weapons[Role.Heavy] = PickTop(WeaponCands(t, wb, d => d.IsRangedWeapon && IsHeavy(d) && !IsDestructive(d)), rng);
            if (tier.sniperShare > 0f) lo.weapons[Role.Sniper] = PickTop(WeaponCands(t, wb, d => d.IsRangedWeapon && IsSniper(d)), rng);
            if (tier.breacherShare > 0f || needBreacher)
            {
                ThingStuffPair? br = PickTop(WeaponCands(t, wb, IsDestructive), rng);
                // Raid de brèche/sape : sans arme destructrice la stratégie échoue -> on ignore les tags en dernier recours
                if (!br.HasValue && needBreacher)
                    br = PickTop(WeaponCands(t, Math.Max(wb, 1500f), d => IsDestructive(d) && (d.techLevel <= f.techLevel || d.techLevel == TechLevel.Undefined), ignoreTags: true), rng);
                lo.weapons[Role.Breacher] = br;
            }
            if (!lo.weapons[Role.Main].HasValue)
            {
                // faction sans arme à distance dans ses tables : on reste au corps à corps
                lo.weapons[Role.Main] = lo.weapons[Role.Melee];
            }
            return lo;
        }

        // ---------------------------------------------------------------- implants
        private static HediffDef HediffFor(ThingDef part)
        {
            RecipeDef r = DefDatabase<RecipeDef>.AllDefs.FirstOrDefault(x => x.addsHediff != null && x.IsIngredient(part));
            if (r != null) return r.addsHediff;
            return part.GetCompProperties<CompProperties_UseEffectInstallImplant>()?.hediffDef;
        }

        public static float ImplantCombatScore(ThingDef part)
        {
            HediffDef h = HediffFor(part);
            if (h == null) return 0f;
            float s = 0f;
            if (h.addedPartProps != null) s += h.addedPartProps.partEfficiency - 1f;
            HediffStage st = h.stages?.FirstOrDefault();
            if (st != null)
            {
                if (st.capMods != null)
                {
                    foreach (PawnCapacityModifier cm in st.capMods)
                    {
                        if (cm.offset < -0.001f || cm.postFactor < 0.999f) return 0f; // rien qui affaiblit
                        s += cm.offset + (cm.postFactor - 1f);
                    }
                }
                if (st.painFactor < 1f) s += 0.2f;
            }
            return s;
        }

        public static List<ThingDef> PickImplants(FactionDef f, TierDef tier, Random rng)
        {
            var res = new List<ThingDef>();
            if (implants == null || tier.implantCount.max <= 0) return res;
            FactionTables t = TablesFor(f);
            int n = rng.Next(tier.implantCount.min, tier.implantCount.max + 1);
            List<ThingDef> cands = implants.Where(d => d.BaseMarketValue <= tier.implantBudget && Intersects(d.techHediffsTags, t.implantTags)).ToList();
            for (int i = 0; i < n && cands.Count > 0; i++)
            {
                // pondéré par la valeur : les meilleurs implants sortent plus souvent
                float total = cands.Sum(d => d.BaseMarketValue);
                double r = rng.NextDouble() * total;
                ThingDef pick = cands[cands.Count - 1];
                foreach (ThingDef d in cands)
                {
                    r -= d.BaseMarketValue;
                    if (r <= 0) { pick = d; break; }
                }
                res.Add(pick);
                cands.Remove(pick);
            }
            return res;
        }

        public static ThingDef PickCyberbrain(int rank)
        {
            if (!HRSettings.AddCyberbrains || rank <= 0 || cyberbrains == null || cyberbrains.Count == 0) return null;
            int i = rank switch
            {
                1 => 0,
                2 => cyberbrains.Count / 2,
                _ => cyberbrains.Count - 1,
            };
            return cyberbrains[i];
        }
    }
}
