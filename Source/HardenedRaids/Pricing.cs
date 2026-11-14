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
            ThingStuffPair? side = lo.SidearmFor(role);
            if (side.HasValue) v += side.Value.Price;
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

    /// <summary>
    /// Mécanoïdes blindés : prix multiplié et blocage C. Deux usages :
    /// - portée (Push / Pop) : combatPower des pawnkinds modifié le temps d'une génération (groupes, grappes, capsules) ;
    ///   pile de portées, sûre en cas d'imbrication (chaque Pop rend exactement ce que son Push a changé) ;
    /// - calcul sans modification (Cost / WeightFactor) pour les chemins qui choisissent eux-mêmes (mécas endormis, assembleurs).
    /// Seulement pour les factions hostiles au joueur : jamais les mécas du joueur (mécanitor) ni les alliés.
    /// </summary>
    public static class MechPricing
    {
        public const float HeavyRatio = 0.75f;   // ≥ 75 % de l'armure de marine (CE : 15 mm)
        public const float BlockedCost = 1e6f;   // hors de prix = jamais choisi

        private sealed class Frame
        {
            public float points;
            public readonly List<KeyValuePair<PawnKindDef, float>> prev = new List<KeyValuePair<PawnKindDef, float>>();
        }

        private static readonly Dictionary<PawnKindDef, bool> heavyCache = new Dictionary<PawnKindDef, bool>();
        private static readonly Dictionary<PawnKindDef, float> original = new Dictionary<PawnKindDef, float>();  // valeur vanilla tant qu'une portée est active
        private static readonly List<Frame> frames = new List<Frame>();

        public static bool Active => frames.Count > 0;

        public static bool IsHeavy(PawnKindDef k)
        {
            if (k?.race == null || !k.RaceProps.IsMechanoid) return false;
            if (!heavyCache.TryGetValue(k, out bool h))
            {
                h = ArmorGate.Ratio(k.race, null) >= HeavyRatio;
                heavyCache[k] = h;
            }
            return h;
        }

        public static bool HostileToPlayer(Faction f) => f != null && f != Faction.OfPlayer && Faction.OfPlayer != null && f.HostileTo(Faction.OfPlayer);

        /// <summary>combatPower vanilla (même pendant une portée active).</summary>
        public static float OriginalPower(PawnKindDef k) => original.TryGetValue(k, out float v) ? v : k.combatPower;

        /// <summary>Prix Hardened d'un pawnkind, sans rien modifier.</summary>
        public static float Cost(PawnKindDef k, ArmorGate gate)
        {
            float b = OriginalPower(k);
            if (!IsHeavy(k)) return b;
            if (gate != null && !gate.AllowsMech(k.race)) return BlockedCost;
            return b * HRSettings.HeavyMechCostFactor;
        }

        /// <summary>Facteur de poids pour un choix uniforme (assembleur, groupe aléatoire) : 1 / prix relatif, 0 si bloqué.</summary>
        public static float WeightFactor(PawnKindDef k, ArmorGate gate)
        {
            if (!IsHeavy(k)) return 1f;
            if (gate != null && !gate.AllowsMech(k.race)) return 0f;
            return 1f / Mathf.Max(1f, HRSettings.HeavyMechCostFactor);
        }

        /// <summary>
        /// Points de menace qui règlent le blocage hors raid : ceux de la portée active, sinon la carte si c'est une colonie,
        /// sinon le monde. Jamais pendant un chargement (monde incomplet) : -1 = ne pas appliquer.
        /// </summary>
        public static float ThreatPointsFor(Map map)
        {
            if (frames.Count > 0) return frames[frames.Count - 1].points;   // DefaultThreatPointsNow lit le combatPower des mécas de colonie
            if (Scribe.mode != LoadSaveMode.Inactive || Current.ProgramState != ProgramState.Playing) return -1f;
            try
            {
                IIncidentTarget t = map != null && map.IsPlayerHome ? map : (IIncidentTarget)Find.World;
                return t == null ? -1f : StorytellerUtility.DefaultThreatPointsNow(t);
            }
            catch (Exception e)
            {
                Log.WarningOnce("[Hardened Raids] mech pricing points: " + e.Message, 0x48524D50);
                return -1f;
            }
        }

        /// <summary>Groupe d'une faction non humaine hostile : portée de prix sur ses options.</summary>
        public static bool Apply(PawnGroupMakerParms parms, PawnGroupMaker groupMaker)
        {
            if (parms?.faction == null || parms.faction.def.humanlikeFaction || groupMaker?.options == null) return false;
            if (!HostileToPlayer(parms.faction)) return false;
            return Push(groupMaker.options.Select(o => o.kind), parms.points);
        }

        /// <summary>
        /// Ouvre une portée : prix / blocage des mécanoïdes blindés parmi ces pawnkinds. true = portée ouverte, à fermer par Pop()
        /// (dans un finalizer / finally). Ne lève jamais d'exception.
        /// </summary>
        public static bool Push(IEnumerable<PawnKindDef> kinds, float points)
        {
            if (kinds == null) return false;
            Frame fr = null;
            try
            {
                ArmorGate gate = null;
                bool gateDone = false;
                foreach (PawnKindDef k in kinds)
                {
                    if (!IsHeavy(k)) continue;
                    if (fr != null && fr.prev.Any(kv => kv.Key == k)) continue;
                    if (!gateDone) { gate = ArmorGate.For(points); gateDone = true; }
                    fr ??= new Frame { points = points };
                    if (!original.ContainsKey(k)) original[k] = k.combatPower;
                    fr.prev.Add(new KeyValuePair<PawnKindDef, float>(k, k.combatPower));
                    k.combatPower = Cost(k, gate);
                }
                if (fr == null) return false;
                frames.Add(fr);
            }
            catch (Exception e)
            {
                if (fr != null) Undo(fr);
                if (frames.Count == 0) RestoreAll();
                Log.Warning("[Hardened Raids] mechanoid pricing: " + e.Message);
                return false;
            }
            HRLog.Msg($"mécanoïdes blindés ({points:F0} pts) : " + string.Join(", ", fr.prev.Select(kv =>
                kv.Key.combatPower >= BlockedCost ? $"{kv.Key.defName} bloqué" : $"{kv.Key.defName} {kv.Key.combatPower:F0} pts")));
            return true;
        }

        /// <summary>Ferme la dernière portée ouverte.</summary>
        public static void Pop()
        {
            if (frames.Count == 0) return;
            Frame fr = frames[frames.Count - 1];
            frames.RemoveAt(frames.Count - 1);
            Undo(fr);
            if (frames.Count == 0) RestoreAll();
        }

        private static void Undo(Frame fr)
        {
            for (int i = fr.prev.Count - 1; i >= 0; i--) fr.prev[i].Key.combatPower = fr.prev[i].Value;
        }

        /// <summary>Filet de sécurité : remet toutes les valeurs vanilla et vide la pile.</summary>
        public static void RestoreAll()
        {
            foreach (KeyValuePair<PawnKindDef, float> kv in original) kv.Key.combatPower = kv.Value;
            original.Clear();
            frames.Clear();
        }
    }
}
