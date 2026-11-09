using UnityEngine;
using Verse;

namespace HardenedRaids
{
    public class HRSettings : ModSettings
    {
        // --- Points ---
        public static bool UncapPoints = true;          // supprime le plafond vanilla de 10 000
        public static bool ExtendWealthCurves = true;   // prolonge les courbes richesse -> points au-delà de 1M

        // --- Génération ---
        public static bool EnableTiers = true;
        public static float CostFactor = 1f;            // multiplie le coût de chaque palier (>1 = moins de raiders, <1 = plus)
        public static float TierSpeed = 1f;             // >1 = on atteint les hauts paliers avec moins de points
        public static bool AddCyberbrains = true;       // GiTS Cyberbrains (si présent)

        // --- Vagues ---
        public static int CapHighTech = 100;
        public static int CapLowTech = 300;
        public static float WaveDelayHoursMin = 2f;
        public static float WaveDelayHoursMax = 6f;

        // --- IA ---
        public static bool SmarterStrategies = true;
        public static bool VeteransAvoidTurrets = true;

        public static bool VerboseLog = true;

        public override void ExposeData()
        {
            base.ExposeData();
            Scribe_Values.Look(ref UncapPoints, "UncapPoints", true);
            Scribe_Values.Look(ref ExtendWealthCurves, "ExtendWealthCurves", true);
            Scribe_Values.Look(ref EnableTiers, "EnableTiers", true);
            Scribe_Values.Look(ref CostFactor, "CostFactor", 1f);
            Scribe_Values.Look(ref TierSpeed, "TierSpeed", 1f);
            Scribe_Values.Look(ref AddCyberbrains, "AddCyberbrains", true);
            Scribe_Values.Look(ref CapHighTech, "CapHighTech", 100);
            Scribe_Values.Look(ref CapLowTech, "CapLowTech", 300);
            Scribe_Values.Look(ref WaveDelayHoursMin, "WaveDelayHoursMin", 2f);
            Scribe_Values.Look(ref WaveDelayHoursMax, "WaveDelayHoursMax", 6f);
            Scribe_Values.Look(ref SmarterStrategies, "SmarterStrategies", true);
            Scribe_Values.Look(ref VeteransAvoidTurrets, "VeteransAvoidTurrets", true);
            Scribe_Values.Look(ref VerboseLog, "VerboseLog", true);
        }

        public static void DoWindow(Rect rect)
        {
            var l = new Listing_Standard();
            l.Begin(rect);

            l.Label("<b>Points de raid</b>");
            l.CheckboxLabeled("Supprimer le plafond de 10 000 points", ref UncapPoints);
            bool before = ExtendWealthCurves;
            l.CheckboxLabeled("Prolonger les courbes richesse → points au-delà de 1M", ref ExtendWealthCurves,
                "Vanilla arrête de compter la richesse après 1 000 000. Activé : la courbe continue avec la même pente.");
            if (before != ExtendWealthCurves) PointsPatches.ApplyCurves();
            if (Current.ProgramState == ProgramState.Playing && Find.CurrentMap != null)
            {
                l.Label($"Points actuels sur cette carte : {RimWorld.StorytellerUtility.DefaultThreatPointsNow(Find.CurrentMap):F0}");
            }
            l.GapLine();

            l.Label("<b>Génération par paliers</b>");
            l.CheckboxLabeled("Activer les paliers d'équipement", ref EnableTiers);
            CostFactor = l.SliderLabeled($"Coût des raiders équipés : ×{CostFactor:F2}", CostFactor, 0.5f, 2f, 0.5f,
                "Prix en points de l'équipement. Plus haut = moins de raiders mais aussi bien équipés.");
            CostFactor = Mathf.Round(CostFactor * 20f) / 20f;
            TierSpeed = l.SliderLabeled($"Vitesse de montée en palier : ×{TierSpeed:F2}", TierSpeed, 0.5f, 3f, 0.5f,
                "Plus haut = les raiders atteignent les paliers élevés avec moins de points.");
            TierSpeed = Mathf.Round(TierSpeed * 20f) / 20f;
            l.CheckboxLabeled("Cyberbrains GiTS pour les vétérans (si le mod est actif)", ref AddCyberbrains);
            l.GapLine();

            l.Label("<b>Vagues</b>");
            CapHighTech = (int)l.SliderLabeled($"Raiders max par vague (industriel+) : {CapHighTech}", CapHighTech, 20, 300, 0.5f);
            CapLowTech = (int)l.SliderLabeled($"Raiders max par vague (néolithique/médiéval) : {CapLowTech}", CapLowTech, 50, 600, 0.5f);
            WaveDelayHoursMin = Mathf.Round(l.SliderLabeled($"Délai min entre vagues : {WaveDelayHoursMin:F1} h", WaveDelayHoursMin, 0.5f, 24f, 0.5f) * 2f) / 2f;
            WaveDelayHoursMax = Mathf.Round(l.SliderLabeled($"Délai max entre vagues : {WaveDelayHoursMax:F1} h", WaveDelayHoursMax, 0.5f, 24f, 0.5f) * 2f) / 2f;
            if (WaveDelayHoursMax < WaveDelayHoursMin) WaveDelayHoursMax = WaveDelayHoursMin;
            l.GapLine();

            l.Label("<b>IA</b>");
            l.CheckboxLabeled("Stratégies plus intelligentes selon le palier (smart, brèche, sape)", ref SmarterStrategies);
            l.CheckboxLabeled("Les vétérans (T2+) évitent les zones couvertes par les tourelles", ref VeteransAvoidTurrets);
            l.GapLine();
            l.CheckboxLabeled("Journal détaillé (log)", ref VerboseLog);

            l.End();
        }
    }

    public class HardenedRaidsMod : Mod
    {
        public static HardenedRaidsMod Instance;

        public HardenedRaidsMod(ModContentPack content) : base(content)
        {
            Instance = this;
            GetSettings<HRSettings>();
            new HarmonyLib.Harmony("shdmk2.hardenedraids").PatchAll();
            Log.Message("[Hardened Raids] chargé.");
        }

        public override string SettingsCategory() => "Hardened Raids";

        public override void DoSettingsWindowContents(Rect inRect) => HRSettings.DoWindow(inRect);
    }

    [StaticConstructorOnStartup]
    public static class HRStartup
    {
        static HRStartup()
        {
            PointsPatches.ApplyCurves();
            Loadouts.BuildCaches();
        }
    }

    public static class HRLog
    {
        public static void Msg(string s)
        {
            if (HRSettings.VerboseLog) Log.Message("[Hardened Raids] " + s);
        }
    }
}
