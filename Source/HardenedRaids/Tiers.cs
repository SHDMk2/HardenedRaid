using RimWorld;
using Verse;

namespace HardenedRaids
{
    /// <summary>Un palier d'équipement. Les valeurs d'argent sont des budgets en valeur marchande.</summary>
    public class TierDef
    {
        public string label;
        public float bodyMult;          // prix du « corps » (skills, santé) / prix de base ; l'équipement est payé à part, à sa valeur
        public bool healthy;            // retire vieilles blessures, maladies chroniques, addictions
        public FloatRange ageRange;     // âge biologique
        public IntRange combatSkill;    // niveau du skill de combat principal
        public float apparelBudget;
        public float weaponBudget;
        public IntRange implantCount;
        public float implantBudget;     // valeur max d'un implant
        public int cyberbrainRank;      // 0 = aucun, 1 = bas de gamme, 2 = milieu, 3 = meilleur (GiTS)
        public float heavyShare;        // part de mitrailleurs (LMG/minigun)
        public float sniperShare;
        public float breacherShare;     // armes "destructrices" (roquettes, grenades, hache de brèche)
        public float meleeShare;        // tribaux : guerriers au corps à corps
    }

    public static class Tiers
    {
        // Échelle industrielle et au-delà (pirates, Empire, outlanders...)
        public static readonly TierDef[] Industrial =
        {
            new TierDef { label = "T0 Vanilla", bodyMult = 1.00f },
            new TierDef { label = "T1 Sain", bodyMult = 1.00f, healthy = true, ageRange = new FloatRange(20, 45), combatSkill = new IntRange(6, 8),
                apparelBudget = 350, weaponBudget = 350, implantCount = new IntRange(0, 0) },
            new TierDef { label = "T2 Milice", bodyMult = 1.10f, healthy = true, ageRange = new FloatRange(20, 45), combatSkill = new IntRange(8, 10),
                apparelBudget = 800, weaponBudget = 600, implantCount = new IntRange(0, 1), implantBudget = 700,
                breacherShare = 0.08f },
            new TierDef { label = "T3 Soldat", bodyMult = 1.20f, healthy = true, ageRange = new FloatRange(20, 42), combatSkill = new IntRange(10, 12),
                apparelBudget = 1500, weaponBudget = 1000, implantCount = new IntRange(1, 2), implantBudget = 1400,
                heavyShare = 0.10f, breacherShare = 0.08f },
            new TierDef { label = "T4 Vétéran", bodyMult = 1.35f, healthy = true, ageRange = new FloatRange(22, 40), combatSkill = new IntRange(12, 14),
                apparelBudget = 3000, weaponBudget = 1800, implantCount = new IntRange(2, 4), implantBudget = 2200, cyberbrainRank = 1, heavyShare = 0.10f, sniperShare = 0.08f, breacherShare = 0.07f },
            new TierDef { label = "T5 Élite", bodyMult = 1.50f, healthy = true, ageRange = new FloatRange(22, 40), combatSkill = new IntRange(14, 16),
                apparelBudget = 6000, weaponBudget = 3500, implantCount = new IntRange(4, 6), implantBudget = 4000, cyberbrainRank = 2, heavyShare = 0.10f, sniperShare = 0.08f, breacherShare = 0.08f },
            new TierDef { label = "T6 Légende", bodyMult = 1.70f, healthy = true, ageRange = new FloatRange(22, 40), combatSkill = new IntRange(18, 20),
                apparelBudget = 1e9f, weaponBudget = 1e9f, implantCount = new IntRange(6, 8), implantBudget = 1e9f, cyberbrainRank = 3, heavyShare = 0.10f, sniperShare = 0.08f, breacherShare = 0.08f },
        };

        // Échelle néolithique / médiévale : pas d'implants, c'est le nombre et la qualité qui comptent
        public static readonly TierDef[] Neolithic =
        {
            new TierDef { label = "N0 Vanilla", bodyMult = 1.00f },
            new TierDef { label = "N1 Sain", bodyMult = 1.00f, healthy = true, ageRange = new FloatRange(16, 40), combatSkill = new IntRange(6, 8),
                apparelBudget = 200, weaponBudget = 200, meleeShare = 0.35f },
            new TierDef { label = "N2 Guerrier", bodyMult = 1.10f, healthy = true, ageRange = new FloatRange(16, 40), combatSkill = new IntRange(8, 10),
                apparelBudget = 400, weaponBudget = 300, meleeShare = 0.35f, breacherShare = 0.05f },
            new TierDef { label = "N3 Chasseur", bodyMult = 1.20f, healthy = true, ageRange = new FloatRange(18, 38), combatSkill = new IntRange(10, 12),
                apparelBudget = 800, weaponBudget = 550, meleeShare = 0.35f, breacherShare = 0.06f },
            new TierDef { label = "N4 Horde d'élite", bodyMult = 1.35f, healthy = true, ageRange = new FloatRange(18, 38), combatSkill = new IntRange(13, 15),
                apparelBudget = 1600, weaponBudget = 1000, meleeShare = 0.35f, breacherShare = 0.06f },
            new TierDef { label = "N5 Légende", bodyMult = 1.60f, healthy = true, ageRange = new FloatRange(18, 38), combatSkill = new IntRange(18, 20),
                apparelBudget = 1e9f, weaponBudget = 1e9f, meleeShare = 0.35f, breacherShare = 0.06f },
        };

        // Points (déjà ajustés par la stratégie) -> palier "continu". < 1 => vanilla.
        public static readonly SimpleCurve IndustrialTierCurve = new SimpleCurve
        {
            new CurvePoint(1000f, 1f),
            new CurvePoint(2500f, 2f),
            new CurvePoint(4500f, 3f),
            new CurvePoint(7500f, 4f),
            new CurvePoint(12000f, 5f),
            new CurvePoint(30000f, 6f),
        };

        public static readonly SimpleCurve NeolithicTierCurve = new SimpleCurve
        {
            new CurvePoint(1000f, 1f),
            new CurvePoint(2500f, 2f),
            new CurvePoint(5000f, 3f),
            new CurvePoint(9000f, 4f),
            new CurvePoint(25000f, 5f),
        };

        // Prix du corps, calé sur vanilla : combatPower ≈ 48 + valeur de l'équipement / 40 (pirates, mercenaires, outlanders)
        public const float IndustrialBaseCost = 45f;
        public const float NeolithicBaseCost = 38f;

        public static bool IsLowTech(FactionDef f) => f.techLevel <= TechLevel.Medieval;

        public static TierDef[] LadderFor(FactionDef f) => IsLowTech(f) ? Neolithic : Industrial;

        public static float BaseCostFor(FactionDef f) => IsLowTech(f) ? NeolithicBaseCost : IndustrialBaseCost;

        /// <summary>Palier continu (0 = vanilla) pour une faction et un nombre de points.</summary>
        public static float TierX(FactionDef f, float points)
        {
            if (points < 1000f) return 0f;
            SimpleCurve c = IsLowTech(f) ? NeolithicTierCurve : IndustrialTierCurve;
            float x = c.Evaluate(points * HRSettings.TierSpeed) ;
            return UnityEngine.Mathf.Clamp(x, 0f, LadderFor(f).Length - 1);
        }

        public static int WaveCapFor(FactionDef f) => IsLowTech(f) ? HRSettings.CapLowTech : HRSettings.CapHighTech;
    }
}
