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
        /// <summary>Dotation d'assaut (boucliers, armures à bouclier) pour les rôles brèche / corps à corps.</summary>
        public Dictionary<Role, List<ThingStuffPair>> roleApparel = new Dictionary<Role, List<ThingStuffPair>>();
        public Dictionary<Role, ThingStuffPair?> weapons = new Dictionary<Role, ThingStuffPair?>();

        public List<ThingStuffPair> ApparelFor(Role r) =>
            roleApparel.TryGetValue(r, out var l) && l != null && l.Count > 0 ? l : apparel;

        public ThingStuffPair? WeaponFor(Role r)
        {
            if (weapons.TryGetValue(r, out var w) && w.HasValue) return w;
            if (weapons.TryGetValue(Role.Main, out var m) && m.HasValue) return m;
            if (weapons.TryGetValue(Role.Melee, out var me) && me.HasValue) return me;
            return null;
        }

        /// <summary>Arme de secours d'un tireur (Simple Sidearms) : l'arme de corps à corps de la dotation.</summary>
        public ThingStuffPair? SidearmFor(Role r)
        {
            if (r == Role.Melee || !Loadouts.SidearmsActive) return null;
            if (!weapons.TryGetValue(Role.Melee, out var me) || !me.HasValue) return null;
            ThingStuffPair? prim = WeaponFor(r);
            if (prim.HasValue && prim.Value.thing == me.Value.thing) return null;
            return me;
        }

        public string Describe()
        {
            string ap = apparel.Count == 0 ? "(vanilla)" : string.Join(", ", apparel.Select(Label));
            var parts = new List<string>();
            foreach (Role r in Enum.GetValues(typeof(Role)))
                if (weapons.TryGetValue(r, out var w) && w.HasValue) parts.Add($"{r}={Label(w.Value)}");
            string assault = string.Join(" ; ", roleApparel.Where(kv => kv.Value != null && kv.Value.Count > 0)
                .Select(kv => $"{kv.Key}: {string.Join(", ", kv.Value.Select(Label))}"));
            return $"armure: {ap}" + (assault.Length > 0 ? $" | assaut: {assault}" : "") + $" | armes: {string.Join(", ", parts)}";
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
                Log.Error("[Hardened Raids] could not build the item catalog: " + e);
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
            return true;
        }

        // ---------------------------------------------------------------- boucliers
        /// <summary>Bouclier CE (Apparel_Shield) : seulement avec une arme à une main.</summary>
        public static bool IsCEShield(ThingDef d) => d.thingClass != null && d.thingClass.Name == "Apparel_Shield";

        /// <summary>Équipement « bouclier » : ceinture bouclier vanilla, bouclier CE, armure à bouclier VEF (siegebreaker...).
        /// Réservé aux rôles brèche / corps à corps.</summary>
        public static bool IsShieldGear(ThingDef d) =>
            IsCEShield(d) || (d.comps != null && d.comps.Any(c => c?.compClass != null && c.compClass.Name.Contains("Shield")));

        private static bool OneHanded(ThingDef w) => w.weaponTags != null && w.weaponTags.Contains("CE_OneHandedWeapon");

        /// <summary>L'objet empêche-t-il d'utiliser cette arme ? (à vérifier aussi au porté par Apparel.AllowVerbCast)</summary>
        public static bool BlocksWeapon(ThingDef ap, ThingDef weapon)
        {
            if (weapon == null) return false;
            if (IsCEShield(ap)) return !OneHanded(weapon);
            if (!weapon.IsRangedWeapon) return false;
            if (ap.IsShieldThatBlocksRanged) return true;
            if (ap.comps != null)
                foreach (CompProperties c in ap.comps)
                {
                    var f = c?.GetType().GetField("dontAllowRangedAttack"); // VEF CompProperties_ShieldBubble
                    if (f != null && f.FieldType == typeof(bool) && (bool)f.GetValue(c)) return true;
                }
            return false;
        }

        private static bool WeaponAllowed(ThingDef d)
        {
            if (d.HasComp(typeof(CompBladelinkWeapon))) return false;              // armes persona
            if (d.generateAllowChance <= 0f) return false;
            // armes antigrain (pilum, roquettes) : interdites, comme leurs munitions
            if (d.defName.IndexOf("antigrain", StringComparison.OrdinalIgnoreCase) >= 0) return false;
            if (d.Verbs != null && d.Verbs.Any(v => v.defaultProjectile != null && v.defaultProjectile.defName.IndexOf("antigrain", StringComparison.OrdinalIgnoreCase) >= 0)) return false;
            return true;
        }

        private static bool Intersects(List<string> a, HashSet<string> b) => a != null && a.Any(b.Contains);

        public static bool IsDestructive(ThingDef d) =>
            (d.Verbs != null && d.Verbs.Any(v => v.ai_IsBuildingDestroyer)) || (d.weaponTags != null && d.weaponTags.Any(t => t.Contains("Destructive")));

        private static bool IsHeavy(ThingDef d) =>
            (d.weaponClasses != null && d.weaponClasses.Any(c => c.defName == "RangedHeavy")) || (d.weaponTags != null && d.weaponTags.Contains("GunHeavy"));

        private static bool IsSniper(ThingDef d) => d.weaponTags != null && d.weaponTags.Any(t => t.Contains("Sniper"));

        /// <summary>Simple Sidearms chargé (composant mémoire des armes sur les humains).</summary>
        public static bool SidearmsActive => sidearmsActive ??= ThingDefOf.Human.comps.Any(c => c.compClass?.Name == "CompSidearmMemory");
        private static bool? sidearmsActive;

        public static bool IsGrenade(ThingDef d) => d.weaponTags != null && d.weaponTags.Any(t => t.Contains("Grenade"));

        public static bool IsGrenadeOrSingleUse(ThingDef d) =>
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

        private static readonly HashSet<string> GenericWords = new HashSet<string>
        {
            "armor", "armour", "helmet", "helm", "suit", "vest", "jacket", "set", "the", "of", "a", "heavy", "light", "combat", "gear", "hat", "mask", "pants"
        };

        private static HashSet<string> NameTokens(ThingDef d) =>
            new HashSet<string>((d.label ?? d.defName).ToLowerInvariant().Split(new[] { ' ', '-', '(', ')', ',' }, StringSplitOptions.RemoveEmptyEntries)
                .Where(w => w.Length > 2 && !GenericWords.Contains(w)));

        /// <summary>0 = rien en commun, 1 = même panoplie (tag "...Set" commun, ou même nom : marine armor / marine helmet).</summary>
        public static float SetMatch(ThingDef body, ThingDef helmet)
        {
            List<string> ta = body.apparel?.tags, tb = helmet.apparel?.tags;
            if (ta != null && tb != null && ta.Any(t => t.EndsWith("Set", StringComparison.OrdinalIgnoreCase) && tb.Contains(t))) return 1f;
            HashSet<string> a = NameTokens(body), b = NameTokens(helmet);
            if (a.Count == 0 || b.Count == 0) return 0f;
            int shared = a.Count(b.Contains);
            if (shared == 0) return 0f;
            return 0.5f + 0.5f * shared / Math.Max(a.Count, b.Count);   // "prestige marine" préfère "prestige marine helmet" à "marine helmet"
        }

        private static bool IsBodyArmor(ThingDef d) =>
            d.apparel.bodyPartGroups.Contains(BodyPartGroupDefOf.Torso) && (d.apparel.layers.Contains(ApparelLayerDefOf.Shell) || d.apparel.layers.Contains(ApparelLayerDefOf.Middle));

        private static bool IsHelmet(ThingDef d) =>
            d.apparel.layers.Contains(ApparelLayerDefOf.Overhead) &&
            (d.apparel.bodyPartGroups.Contains(BodyPartGroupDefOf.FullHead) || d.apparel.bodyPartGroups.Contains(BodyPartGroupDefOf.UpperHead));

        /// <param name="shields">false : aucun équipement bouclier ; true : autorisés s'ils ne gênent pas <paramref name="weapon"/>.</param>
        public static List<ThingStuffPair> BuildApparelSet(FactionTables t, float budget, Random rng, bool shields = false, ThingDef weapon = null, ArmorGate gate = null)
        {
            var set = new List<ThingStuffPair>();
            if (apparelPairs == null) return set;
            List<ThingStuffPair> cands = apparelPairs.Where(p => p.Price <= budget && Intersects(p.thing.apparel.tags, t.apparelTags) && (gate == null || gate.Allows(p))
                                                                 && (shields ? !BlocksWeapon(p.thing, weapon) : !IsShieldGear(p.thing))).ToList();
            if (cands.Count == 0) return set;
            float left = budget;
            var jitter = new Dictionary<ThingStuffPair, float>();
            float J(ThingStuffPair p)
            {
                if (!jitter.TryGetValue(p, out float j)) jitter[p] = j = 0.95f + 0.1f * (float)rng.NextDouble();
                return ApparelScore(p) * j;
            }
            bool Fits(ThingStuffPair p) => p.Price <= left && !set.Any(s => s.thing == p.thing || !ApparelUtility.CanWearTogether(s.thing, p.thing, BodyDefOf.Human));

            // 1. Combat Extended : un équipement de portage (gilet tactique / sac) pour les munitions
            if (carryBulk != null)
            {
                ThingStuffPair? carry = cands.Where(p => CarryScore(p) > 0f && p.Price <= budget * 0.25f && !IsShieldGear(p.thing))
                    .OrderByDescending(CarryScore).Cast<ThingStuffPair?>().FirstOrDefault();
                if (carry.HasValue) { set.Add(carry.Value); left -= carry.Value.Price; }
            }

            // 2. Armure + casque choisis ensemble : le casque de la même panoplie s'il existe dans les tables de la faction
            List<ThingStuffPair> helmets = cands.Where(p => IsHelmet(p.thing)).ToList();
            ThingStuffPair? bestBody = null, bestHelmet = null;
            float bestScore = 0f;
            foreach (ThingStuffPair b in cands.Where(p => IsBodyArmor(p.thing) && !p.thing.apparel.layers.Contains(ApparelLayerDefOf.Belt) && Fits(p))
                         .OrderByDescending(J).Take(20))
            {
                float rest = left - b.Price;
                List<ThingStuffPair> ok = helmets.Where(h => h.Price <= rest && h.thing != b.thing && ApparelUtility.CanWearTogether(b.thing, h.thing, BodyDefOf.Human)
                                                              && !set.Any(s => !ApparelUtility.CanWearTogether(s.thing, h.thing, BodyDefOf.Human))).ToList();
                ThingStuffPair? h = null;
                float match = 0f;
                if (ok.Count > 0)
                {
                    match = ok.Max(x => SetMatch(b.thing, x.thing));
                    float m = match;
                    h = (m > 0f ? ok.Where(x => SetMatch(b.thing, x.thing) >= m) : ok)
                        .OrderByDescending(x => J(x) + (b.stuff != null && x.stuff == b.stuff ? 0.01f : 0f)).First();
                }
                float score = (J(b) + (h.HasValue ? J(h.Value) : 0f)) * (1f + 0.15f * match) * (h.HasValue ? 1f : 0.8f);
                if (bestBody == null || score > bestScore) { bestBody = b; bestHelmet = h; bestScore = score; }
            }
            if (bestBody.HasValue)
            {
                set.Add(bestBody.Value); left -= bestBody.Value.Price;
                if (bestHelmet.HasValue) { set.Add(bestHelmet.Value); left -= bestHelmet.Value.Price; }
            }

            // 3. Glouton : le reste, meilleure protection d'abord, en restant compatible avec ce qui est déjà choisi
            foreach (ThingStuffPair p in cands.Where(p => !p.thing.apparel.layers.Contains(ApparelLayerDefOf.Belt) || (shields && IsShieldGear(p.thing)))
                         .OrderByDescending(p => J(p) + (shields && IsShieldGear(p.thing) ? 1000f : 0f)))
            {
                if (!Fits(p)) continue;
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

        public static Loadout BuildLoadout(FactionDef f, TierDef tier, bool needBreacher, Random rng, ArmorGate gate = null)
        {
            FactionTables t = TablesFor(f);
            var lo = new Loadout();
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

            // Armure : tireurs sans aucun bouclier ; brèche / corps à corps peuvent prendre boucliers et armures à bouclier
            lo.apparel = BuildApparelSet(t, tier.apparelBudget, rng, gate: gate);
            foreach (Role r in new[] { Role.Breacher, Role.Melee })
            {
                if (r == Role.Breacher && !lo.weapons.ContainsKey(Role.Breacher)) continue;
                if (r == Role.Melee && tier.meleeShare <= 0f) continue;
                ThingDef w = lo.WeaponFor(r)?.thing;
                List<ThingStuffPair> assault = BuildApparelSet(t, tier.apparelBudget, rng, shields: true, weapon: w, gate: gate);
                if (assault.Any(p => IsShieldGear(p.thing))) lo.roleApparel[r] = assault;
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

        private static List<ThingDef> ImplantCandidates(FactionDef f, TierDef tier)
        {
            FactionTables t = TablesFor(f);
            return implants.Where(d => d.BaseMarketValue <= tier.implantBudget && Intersects(d.techHediffsTags, t.implantTags)).ToList();
        }

        /// <summary>Valeur moyenne des implants d'un raider du palier (tirage pondéré par la valeur, sans remise : approximation).</summary>
        public static float ExpectedImplantValue(FactionDef f, TierDef tier)
        {
            if (implants == null || tier.implantCount.max <= 0 || Tiers.IsLowTech(f)) return 0f;
            List<ThingDef> cands = ImplantCandidates(f, tier);
            if (cands.Count == 0) return 0f;
            float sum = cands.Sum(d => d.BaseMarketValue), sq = cands.Sum(d => d.BaseMarketValue * d.BaseMarketValue);
            float avgCount = Math.Min((tier.implantCount.min + tier.implantCount.max) / 2f, cands.Count);
            return avgCount * (sum > 0f ? sq / sum : 0f);
        }

        public static List<ThingDef> PickImplants(FactionDef f, TierDef tier, Random rng)
        {
            var res = new List<ThingDef>();
            if (implants == null || tier.implantCount.max <= 0) return res;
            int n = rng.Next(tier.implantCount.min, tier.implantCount.max + 1);
            List<ThingDef> cands = ImplantCandidates(f, tier);
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
