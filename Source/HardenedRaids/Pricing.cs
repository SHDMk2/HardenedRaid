using System;
using System.Collections.Generic;
using System.Linq;
using RimWorld;
using UnityEngine;
using Verse;

namespace HardenedRaids
{
    public enum Rank { Soldier, Nco, Officer }

    /// <summary>Qualité de l'équipement : gaussienne tronquée par grade, centre qui monte légèrement avec le palier.</summary>
    public static class QualityModel
    {
        public const float Sigma = 0.9f;
        public const float TierShift = 0.5f; // centre décalé de -0,5 (premier palier) à +0,5 cran (dernier palier)

        /// <summary>Facteur de valeur marchande vanilla par qualité, sans le plafond de gain (Awful → Legendary).</summary>
        public static readonly float[] ValueFactor = { 0.5f, 0.75f, 1f, 1.25f, 1.5f, 2.5f, 5f };

        public static void Params(Rank r, out float mu, out int lo, out int hi)
        {
            switch (r)
            {
                case Rank.Officer: mu = 4f; lo = (int)QualityCategory.Normal; hi = (int)QualityCategory.Legendary; break;
                case Rank.Nco: mu = 3f; lo = (int)QualityCategory.Poor; hi = (int)QualityCategory.Masterwork; break;
                default: mu = 2f; lo = (int)QualityCategory.Awful; hi = (int)QualityCategory.Excellent; break;
            }
        }

        public static float Shift(int tierIndex, int ladderLength)
        {
            float t = ladderLength > 2 ? Mathf.Clamp01((tierIndex - 1f) / (ladderLength - 2f)) : 0.5f;
            return Mathf.Lerp(-TierShift, TierShift, t);
        }

        /// <summary>Probabilité de chaque qualité (7 cases), nulle hors des bornes du grade, renormalisée (troncature, pas d'empilement aux bornes).</summary>
        public static float[] Dist(Rank r, int tierIndex, int ladderLength)
        {
            Params(r, out float mu, out int lo, out int hi);
            mu += Shift(tierIndex, ladderLength);
            var p = new float[7];
            float sum = 0f;
            for (int q = lo; q <= hi; q++)
            {
                p[q] = Phi((q + 0.5f - mu) / Sigma) - Phi((q - 0.5f - mu) / Sigma);
                sum += p[q];
            }
            for (int q = 0; q < 7; q++) p[q] = sum > 0f ? p[q] / sum : (q == 2 ? 1f : 0f);
            return p;
        }

        public static QualityCategory Sample(Rank r, int tierIndex, int ladderLength, System.Random rng)
        {
            float[] p = Dist(r, tierIndex, ladderLength);
            double x = rng.NextDouble();
            for (int q = 0; q < 7; q++)
            {
                x -= p[q];
                if (x <= 0) return (QualityCategory)q;
            }
            Params(r, out _, out _, out int hi);
            return (QualityCategory)hi;
        }

        public static float ExpectedValueFactor(Rank r, int tierIndex, int ladderLength)
        {
            float[] p = Dist(r, tierIndex, ladderLength);
            float e = 0f;
            for (int q = 0; q < 7; q++) e += p[q] * ValueFactor[q];
            return e;
        }

        // Abramowitz-Stegun 7.1.26
        private static float Phi(float x)
        {
            double z = Math.Abs(x) / Math.Sqrt(2.0);
            double t = 1.0 / (1.0 + 0.3275911 * z);
            double erf = 1.0 - (((((1.061405429 * t - 1.453152027) * t) + 1.421413741) * t - 0.284496736) * t + 0.254829592) * t * Math.Exp(-z * z);
            return (float)(0.5 * (1.0 + (x >= 0 ? erf : -erf)));
        }
    }

    /// <summary>Prix d'un raider = corps (palier) + valeur de tout ce qu'il porte / taux $ par point.</summary>
    public static class Pricing
    {
        // Grades : 1 sous-officier pour 10 raiders, 1 officier pour 100 (arrondi inférieur)
        public static int NcoCount(int n) => n / 10;
        public static int OfficerCount(int n) => n / 100;

        public static float BodyCost(FactionDef f, TierDef tier) => Tiers.BaseCostFor(f) * tier.bodyMult;

        public static float Points(float body, float dollars) => (body + dollars / Mathf.Max(1f, HRSettings.ValueDivisor)) * HRSettings.CostFactor;

        public static float GearValue(Loadout lo, Role role)
        {
            float v = lo.ApparelFor(role).Sum(p => p.Price);
            ThingStuffPair? w = lo.WeaponFor(role);
            if (w.HasValue) v += w.Value.Price;
            return v;
        }

        /// <summary>Valeur moyenne de l'équipement (qualité Normale) sur la répartition des rôles du palier.</summary>
        public static float AverageGearValue(Loadout lo, TierDef tier)
        {
            float heavy = lo.weapons.ContainsKey(Role.Heavy) ? tier.heavyShare : 0f;
            float sniper = lo.weapons.ContainsKey(Role.Sniper) ? tier.sniperShare : 0f;
            float breach = lo.weapons.ContainsKey(Role.Breacher) ? tier.breacherShare : 0f;
            float melee = tier.meleeShare;
            float main = Mathf.Max(0f, 1f - heavy - sniper - breach - melee);
            return main * GearValue(lo, Role.Main) + heavy * GearValue(lo, Role.Heavy) + sniper * GearValue(lo, Role.Sniper)
                   + breach * GearValue(lo, Role.Breacher) + melee * GearValue(lo, Role.Melee);
        }

        /// <summary>Prix moyen d'un raider du groupe (sert à calculer l'effectif avant de générer).</summary>
        public static float ExpectedPrice(FactionDef f, TierDef tier, int tierIndex, Loadout lo)
        {
            int len = Tiers.LadderFor(f).Length;
            float qf = 0.89f * QualityModel.ExpectedValueFactor(Rank.Soldier, tierIndex, len)
                       + 0.10f * QualityModel.ExpectedValueFactor(Rank.Nco, tierIndex, len)
                       + 0.01f * QualityModel.ExpectedValueFactor(Rank.Officer, tierIndex, len);
            float dollars = AverageGearValue(lo, tier) * qf + Loadouts.ExpectedImplantValue(f, tier);
            ThingDef brain = Tiers.IsLowTech(f) ? null : Loadouts.PickCyberbrain(tier.cyberbrainRank);
            if (brain != null) dollars += brain.BaseMarketValue;
            return Points(BodyCost(f, tier), dollars);
        }

        /// <summary>Prix exact d'un raider une fois son grade, son rôle et ses implants connus (qualité : espérance du grade).</summary>
        public static float SlotPrice(Slot s)
        {
            int len = Tiers.LadderFor(s.faction.def).Length;
            float dollars = GearValue(s.loadout, s.role) * QualityModel.ExpectedValueFactor(s.rank, s.tierIndex, len);
            if (s.implants != null) dollars += s.implants.Sum(d => d.BaseMarketValue);
            if (s.brain != null) dollars += s.brain.BaseMarketValue;
            return Points(BodyCost(s.faction.def, s.tier), dollars);
        }
    }

    /// <summary>Blocage des armures (et mécanoïdes blindés) trop solides trop tôt : selon les points du raid et la recherche de la colonie.</summary>
    public class ArmorGate
    {
        public float points;
        public TechLevel colonyTech;
        public float cap;   // protection perçante max, en fraction de l'armure de marine

        // fraction de l'armure de marine autorisée selon les points (CE : marine = 20 mm ; vanilla : 1,06)
        public static readonly SimpleCurve CapByPoints = new SimpleCurve
        {
            new CurvePoint(0f, 0.55f),
            new CurvePoint(3000f, 0.8f),
            new CurvePoint(6000f, 1.0f),
            new CurvePoint(12000f, 1.4f),
            new CurvePoint(20000f, 100f),
        };

        public const float ArmorPieceRatio = 0.25f;   // en dessous : vêtement, pas d'armure → pas de blocage technologique

        private static float marineSharp = -1f;
        public static float MarineSharp
        {
            get
            {
                if (marineSharp < 0f)
                {
                    ThingDef m = DefDatabase<ThingDef>.GetNamedSilentFail("Apparel_PowerArmor");
                    marineSharp = m != null ? m.GetStatValueAbstract(StatDefOf.ArmorRating_Sharp) : 0f;
                    if (marineSharp <= 0f) marineSharp = 1f;
                }
                return marineSharp;
            }
        }

        public static ArmorGate For(float points)
        {
            if (!HRSettings.ArmorGate) return null;
            return new ArmorGate { points = points, colonyTech = ColonyTech(), cap = CapByPoints.Evaluate(points) };
        }

        public static float Ratio(ThingDef d, ThingDef stuff) => d.GetStatValueAbstract(StatDefOf.ArmorRating_Sharp, stuff) / MarineSharp;

        public bool Allows(ThingStuffPair p)
        {
            float r = Ratio(p.thing, p.stuff);
            if (r > cap) return false;
            // armure spacer/ultra seulement si la colonie a elle-même atteint ce niveau de recherche
            if (r >= ArmorPieceRatio && p.thing.techLevel > (TechLevel)Math.Max((int)colonyTech, (int)TechLevel.Industrial)) return false;
            return true;
        }

        public bool AllowsMech(ThingDef race)
        {
            if (Ratio(race, null) > cap) return false;
            return colonyTech >= TechLevel.Industrial;
        }

        /// <summary>Plus haut niveau technologique dont la colonie a fini au moins un projet de recherche.</summary>
        public static TechLevel ColonyTech()
        {
            TechLevel best = Faction.OfPlayer?.def?.techLevel ?? TechLevel.Neolithic;
            if (Find.ResearchManager == null) return best;
            foreach (ResearchProjectDef r in DefDatabase<ResearchProjectDef>.AllDefsListForReading)
                if (r.techLevel > best && r.IsFinished) best = r.techLevel;
            return best;
        }
    }

    /// <summary>Mécanoïdes blindés : prix multiplié et blocage C, appliqués pendant la génération du groupe.</summary>
    public static class MechPricing
    {
        public const float HeavyRatio = 0.75f;   // ≥ 75 % de l'armure de marine (CE : 15 mm)
        private static readonly Dictionary<PawnKindDef, float> saved = new Dictionary<PawnKindDef, float>();

        public static bool IsHeavy(PawnKindDef k) =>
            k?.race != null && k.RaceProps.IsMechanoid && ArmorGate.Ratio(k.race, null) >= HeavyRatio;

        public static void Apply(PawnGroupMakerParms parms, PawnGroupMaker groupMaker)
        {
            Restore();
            if (parms?.faction == null || parms.faction.def.humanlikeFaction || groupMaker?.options == null) return;
            ArmorGate gate = ArmorGate.For(parms.points);
            foreach (PawnKindDef k in groupMaker.options.Select(o => o.kind).Distinct())
            {
                if (!IsHeavy(k)) continue;
                saved[k] = k.combatPower;
                if (gate != null && !gate.AllowsMech(k.race)) k.combatPower = 1e6f;      // hors de prix = jamais choisi
                else k.combatPower *= HRSettings.HeavyMechCostFactor;
            }
            if (saved.Count > 0)
                HRLog.Msg($"mécanoïdes blindés ({parms.points:F0} pts) : " + string.Join(", ", saved.Keys.Select(k => k.combatPower >= 1e6f ? $"{k.defName} bloqué" : $"{k.defName} {k.combatPower:F0} pts")));
        }

        public static void Restore()
        {
            foreach (KeyValuePair<PawnKindDef, float> kv in saved) kv.Key.combatPower = kv.Value;
            saved.Clear();
        }
    }
}
