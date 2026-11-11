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

        // --- Partie 2 : sièges et véhicules ---
        public static bool SiegeUpgrade = true;
        public static int MaxSiegeArtillery = 8;
        public static int MaxSiegeTurrets = 6;
        public static bool EnableVehicles = true;
        public static float VehiclePointShare = 0.25f; // part max des points dépensée en véhicules
        public static float VehicleMinTier = 3f;
        public static int MaxVehicles = 6;
        public static bool EnableSupportTeams = true;
        public static float SupportMinTier = 3f;
        public static float SupportShare = 0.12f;  // part des raiders qui montent le camp d'appui

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
            Scribe_Values.Look(ref SiegeUpgrade, "SiegeUpgrade", true);
            Scribe_Values.Look(ref MaxSiegeArtillery, "MaxSiegeArtillery", 8);
            Scribe_Values.Look(ref MaxSiegeTurrets, "MaxSiegeTurrets", 6);
            Scribe_Values.Look(ref EnableVehicles, "EnableVehicles", true);
            Scribe_Values.Look(ref VehiclePointShare, "VehiclePointShare", 0.25f);
            Scribe_Values.Look(ref VehicleMinTier, "VehicleMinTier", 3f);
            Scribe_Values.Look(ref MaxVehicles, "MaxVehicles", 6);
            Scribe_Values.Look(ref EnableSupportTeams, "EnableSupportTeams", true);
            Scribe_Values.Look(ref SupportMinTier, "SupportMinTier", 3f);
            Scribe_Values.Look(ref SupportShare, "SupportShare", 0.12f);
            Scribe_Values.Look(ref VerboseLog, "VerboseLog", true);
        }

        public static void DoWindow(Rect rect)
        {
            var l = new Listing_Standard { ColumnWidth = rect.width / 2f - 17f };
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

            l.NewColumn();
            l.Label("<b>IA</b>");
            l.CheckboxLabeled("Stratégies plus intelligentes selon le palier (smart, brèche, sape)", ref SmarterStrategies);
            l.CheckboxLabeled("Les vétérans (T2+) évitent les zones couvertes par les tourelles", ref VeteransAvoidTurrets);
            l.GapLine();

            l.Label("<b>Sièges et véhicules</b>");
            l.CheckboxLabeled("Camps de siège renforcés (artillerie, tourelles des mods, tranchées face à la base)", ref SiegeUpgrade);
            MaxSiegeArtillery = (int)l.SliderLabeled($"Pièces d'artillerie max par siège : {MaxSiegeArtillery}", MaxSiegeArtillery, 2, 16, 0.5f);
            MaxSiegeTurrets = (int)l.SliderLabeled($"Tourelles max par siège : {MaxSiegeTurrets}", MaxSiegeTurrets, 0, 12, 0.5f);
            l.CheckboxLabeled("Équipes d'appui pendant les assauts (camp avancé avec tourelles)", ref EnableSupportTeams,
                "Une partie des raiders industriels+ arrivant par le bord installe un camp de tir (comme un siège) pendant que les autres attaquent.");
            SupportMinTier = Mathf.Round(l.SliderLabeled($"Palier minimum des équipes d'appui : {SupportMinTier:F0}", SupportMinTier, 1f, 6f, 0.5f));
            SupportShare = Mathf.Round(l.SliderLabeled($"Part des raiders en appui : {SupportShare:P0}", SupportShare, 0.05f, 0.4f, 0.5f) * 20f) / 20f;
            l.CheckboxLabeled("Véhicules de combat ennemis (Vehicle Framework, expérimental)", ref EnableVehicles,
                "Les raiders industriels+ arrivant à pied par le bord de la carte peuvent amener des blindés VVE.");
            VehiclePointShare = Mathf.Round(l.SliderLabeled($"Part des points en véhicules : {VehiclePointShare:P0}", VehiclePointShare, 0.05f, 0.5f, 0.5f) * 20f) / 20f;
            VehicleMinTier = Mathf.Round(l.SliderLabeled($"Palier minimum pour les véhicules : {VehicleMinTier:F0}", VehicleMinTier, 1f, 6f, 0.5f));
            MaxVehicles = (int)l.SliderLabeled($"Véhicules max par vague : {MaxVehicles}", MaxVehicles, 1, 15, 0.5f);
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
            try { Patch_SiegeBlueprints.EnsureCatalog(); } catch (System.Exception e) { Log.Warning("[Hardened Raids] catalogue de siège : " + e.Message); }
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
