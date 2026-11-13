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
        public static float ValueDivisor = 40f;         // $ d'équipement par point (calé sur vanilla)
        public static bool ArmorGate = true;            // armures et mécanoïdes blindés limités par les points et la recherche de la colonie
        public static float HarshDeathShare = 0.30f;    // part des raiders industriels+ biocodés avec acidificateur
        public static float HeavyMechCostFactor = 2f;   // prix des mécanoïdes blindés (≥ 75 % de l'armure de marine)

        // --- Vagues ---
        public static int CapHighTech = 100;
        public static int CapLowTech = 300;
        public static float WaveDelayHoursMin = 2f;
        public static float WaveDelayHoursMax = 6f;

        // --- IA ---
        public static bool SmarterStrategies = true;
        public static bool VeteransAvoidTurrets = true;
        public static bool IgnoreDeadTurrets = true;

        // --- Partie 2 : sièges et véhicules ---
        public static bool SiegeUpgrade = true;
        public static int MaxSiegeArtillery = 8;
        public static int MaxSiegeTurrets = 6;
        public static bool EnableVehicles = true;
        public static float VehiclePointShare = 0.25f; // part max des points dépensée en véhicules
        public static float VehicleMinTier = 3f;
        public static float VehicleValueFactor = 5f;      // blindé : valeur ÷ taux $/pt × ce facteur
        public static float RocketVehicleFactor = 10f;    // roquettes en essaim (Scytheman, Burya)
        public static float ArtilleryVehicleFactor = 5f;  // artillerie (Udar)
        public static float TransportPointShare = 0.10f;  // part max des points dépensée en transports (camions, IFV)
        public static float TransportValueFactor = 1f;    // camion : valeur ÷ taux $/pt × ce facteur
        public static int MaxVehicles = 6;
        public static int MaxTransports = 20;
        public static bool TruckTurrets = true;          // raid smart : tourelles déchargées des camions
        public static int MaxTruckTurrets = 4;
        public static bool EnableSupportTeams = true;
        public static float SupportMinTier = 3f;
        public static float SupportShare = 0.12f;  // part des raiders qui montent le camp d'appui

        public static bool VerboseLog = false;
        public static HRLanguage Language = HRLanguage.English;

        public override void ExposeData()
        {
            base.ExposeData();
            Scribe_Values.Look(ref UncapPoints, "UncapPoints", true);
            Scribe_Values.Look(ref ExtendWealthCurves, "ExtendWealthCurves", true);
            Scribe_Values.Look(ref EnableTiers, "EnableTiers", true);
            Scribe_Values.Look(ref CostFactor, "CostFactor", 1f);
            Scribe_Values.Look(ref TierSpeed, "TierSpeed", 1f);
            Scribe_Values.Look(ref AddCyberbrains, "AddCyberbrains", true);
            Scribe_Values.Look(ref ValueDivisor, "ValueDivisor", 40f);
            Scribe_Values.Look(ref ArmorGate, "ArmorGate", true);
            Scribe_Values.Look(ref HarshDeathShare, "HarshDeathShare", 0.30f);
            Scribe_Values.Look(ref HeavyMechCostFactor, "HeavyMechCostFactor", 2f);
            Scribe_Values.Look(ref CapHighTech, "CapHighTech", 100);
            Scribe_Values.Look(ref CapLowTech, "CapLowTech", 300);
            Scribe_Values.Look(ref WaveDelayHoursMin, "WaveDelayHoursMin", 2f);
            Scribe_Values.Look(ref WaveDelayHoursMax, "WaveDelayHoursMax", 6f);
            Scribe_Values.Look(ref SmarterStrategies, "SmarterStrategies", true);
            Scribe_Values.Look(ref VeteransAvoidTurrets, "VeteransAvoidTurrets", true);
            Scribe_Values.Look(ref IgnoreDeadTurrets, "IgnoreDeadTurrets", true);
            Scribe_Values.Look(ref SiegeUpgrade, "SiegeUpgrade", true);
            Scribe_Values.Look(ref MaxSiegeArtillery, "MaxSiegeArtillery", 8);
            Scribe_Values.Look(ref MaxSiegeTurrets, "MaxSiegeTurrets", 6);
            Scribe_Values.Look(ref EnableVehicles, "EnableVehicles", true);
            Scribe_Values.Look(ref VehiclePointShare, "VehiclePointShare", 0.25f);
            Scribe_Values.Look(ref VehicleMinTier, "VehicleMinTier", 3f);
            Scribe_Values.Look(ref VehicleValueFactor, "VehicleValueFactor", 5f);
            Scribe_Values.Look(ref RocketVehicleFactor, "RocketVehicleFactor", 10f);
            Scribe_Values.Look(ref ArtilleryVehicleFactor, "ArtilleryVehicleFactor", 5f);
            Scribe_Values.Look(ref TransportPointShare, "TransportPointShare", 0.10f);
            Scribe_Values.Look(ref TransportValueFactor, "TransportValueFactor", 1f);
            Scribe_Values.Look(ref MaxVehicles, "MaxVehicles", 6);
            Scribe_Values.Look(ref MaxTransports, "MaxTransports", 20);
            Scribe_Values.Look(ref TruckTurrets, "TruckTurrets", true);
            Scribe_Values.Look(ref MaxTruckTurrets, "MaxTruckTurrets", 4);
            Scribe_Values.Look(ref EnableSupportTeams, "EnableSupportTeams", true);
            Scribe_Values.Look(ref SupportMinTier, "SupportMinTier", 3f);
            Scribe_Values.Look(ref SupportShare, "SupportShare", 0.12f);
            Scribe_Values.Look(ref VerboseLog, "VerboseLog", false);
            Scribe_Values.Look(ref Language, "Language", HRLanguage.English);
        }

        public static void DoWindow(Rect rect)
        {
            var l = new Listing_Standard { ColumnWidth = rect.width / 2f - 17f };
            l.Begin(rect);

            Rect langRow = l.GetRect(28f);
            Widgets.Label(langRow.LeftHalf(), Strings.T("Language"));
            if (Widgets.ButtonText(langRow.RightHalf().LeftPartPixels(140f), Strings.LanguageName(Language)))
            {
                var options = new System.Collections.Generic.List<FloatMenuOption>();
                foreach (HRLanguage lang in System.Enum.GetValues(typeof(HRLanguage)))
                {
                    HRLanguage x = lang;
                    options.Add(new FloatMenuOption(Strings.LanguageName(x), () => Language = x));
                }
                Find.WindowStack.Add(new FloatMenu(options));
            }
            l.GapLine();

            l.Label(Strings.T("SecPoints"));
            l.CheckboxLabeled(Strings.T("UncapPoints"), ref UncapPoints);
            bool before = ExtendWealthCurves;
            l.CheckboxLabeled(Strings.T("ExtendCurves"), ref ExtendWealthCurves, Strings.T("ExtendCurvesTip"));
            if (before != ExtendWealthCurves) PointsPatches.ApplyCurves();
            if (Current.ProgramState == ProgramState.Playing && Find.CurrentMap != null)
                l.Label(Strings.T("PointsNow", RimWorld.StorytellerUtility.DefaultThreatPointsNow(Find.CurrentMap).ToString("F0")));
            l.GapLine();

            l.Label(Strings.T("SecTiers"));
            l.CheckboxLabeled(Strings.T("EnableTiers"), ref EnableTiers);
            CostFactor = l.SliderLabeled(Strings.T("CostFactor", CostFactor.ToString("F2")), CostFactor, 0.5f, 2f, 0.5f, Strings.T("CostFactorTip"));
            CostFactor = Mathf.Round(CostFactor * 20f) / 20f;
            TierSpeed = l.SliderLabeled(Strings.T("TierSpeed", TierSpeed.ToString("F2")), TierSpeed, 0.5f, 3f, 0.5f, Strings.T("TierSpeedTip"));
            TierSpeed = Mathf.Round(TierSpeed * 20f) / 20f;
            l.CheckboxLabeled(Strings.T("Cyberbrains"), ref AddCyberbrains);
            ValueDivisor = Mathf.Round(l.SliderLabeled(Strings.T("ValueDivisor", ValueDivisor.ToString("F0")), ValueDivisor, 15f, 100f, 0.5f, Strings.T("ValueDivisorTip")));
            l.CheckboxLabeled(Strings.T("ArmorGate"), ref ArmorGate, Strings.T("ArmorGateTip"));
            HarshDeathShare = Mathf.Round(l.SliderLabeled(Strings.T("HarshDeath", HarshDeathShare.ToString("P0")), HarshDeathShare, 0f, 1f, 0.5f,
                Strings.T("HarshDeathTip")) * 20f) / 20f;
            HeavyMechCostFactor = Mathf.Round(l.SliderLabeled(Strings.T("HeavyMech", HeavyMechCostFactor.ToString("F2")), HeavyMechCostFactor, 1f, 4f, 0.5f,
                Strings.T("HeavyMechTip")) * 4f) / 4f;
            l.GapLine();

            l.Label(Strings.T("SecWaves"));
            CapHighTech = (int)l.SliderLabeled(Strings.T("CapHigh", CapHighTech), CapHighTech, 20, 300, 0.5f);
            CapLowTech = (int)l.SliderLabeled(Strings.T("CapLow", CapLowTech), CapLowTech, 50, 600, 0.5f);
            WaveDelayHoursMin = Mathf.Round(l.SliderLabeled(Strings.T("WaveMin", WaveDelayHoursMin.ToString("F1")), WaveDelayHoursMin, 0.5f, 24f, 0.5f) * 2f) / 2f;
            WaveDelayHoursMax = Mathf.Round(l.SliderLabeled(Strings.T("WaveMax", WaveDelayHoursMax.ToString("F1")), WaveDelayHoursMax, 0.5f, 24f, 0.5f) * 2f) / 2f;
            if (WaveDelayHoursMax < WaveDelayHoursMin) WaveDelayHoursMax = WaveDelayHoursMin;
            l.GapLine();
            l.CheckboxLabeled(Strings.T("VerboseLog"), ref VerboseLog);

            l.NewColumn();
            l.Label(Strings.T("SecAI"));
            l.CheckboxLabeled(Strings.T("Smarter"), ref SmarterStrategies);
            l.CheckboxLabeled(Strings.T("AvoidTurrets"), ref VeteransAvoidTurrets);
            l.CheckboxLabeled(Strings.T("DeadTurrets"), ref IgnoreDeadTurrets);
            l.GapLine();

            l.Label(Strings.T("SecSiege"));
            l.CheckboxLabeled(Strings.T("SiegeUpgrade"), ref SiegeUpgrade);
            MaxSiegeArtillery = (int)l.SliderLabeled(Strings.T("SiegeArtillery", MaxSiegeArtillery), MaxSiegeArtillery, 2, 16, 0.5f);
            MaxSiegeTurrets = (int)l.SliderLabeled(Strings.T("SiegeTurrets", MaxSiegeTurrets), MaxSiegeTurrets, 0, 12, 0.5f);
            l.CheckboxLabeled(Strings.T("SupportTeams"), ref EnableSupportTeams, Strings.T("SupportTeamsTip"));
            SupportMinTier = Mathf.Round(l.SliderLabeled(Strings.T("SupportMinTier", SupportMinTier.ToString("F0")), SupportMinTier, 1f, 6f, 0.5f));
            SupportShare = Mathf.Round(l.SliderLabeled(Strings.T("SupportShare", SupportShare.ToString("P0")), SupportShare, 0.05f, 0.4f, 0.5f) * 20f) / 20f;
            l.CheckboxLabeled(Strings.T("Vehicles"), ref EnableVehicles, Strings.T("VehiclesTip"));
            VehiclePointShare = Mathf.Round(l.SliderLabeled(Strings.T("VehicleShare", VehiclePointShare.ToString("P0")), VehiclePointShare, 0.05f, 0.5f, 0.5f) * 20f) / 20f;
            VehicleMinTier = Mathf.Round(l.SliderLabeled(Strings.T("VehicleMinTier", VehicleMinTier.ToString("F0")), VehicleMinTier, 1f, 6f, 0.5f));
            VehicleValueFactor = Mathf.Round(l.SliderLabeled(Strings.T("VehicleValue", ValueDivisor.ToString("F0"), VehicleValueFactor.ToString("F1")), VehicleValueFactor, 1f, 15f, 0.5f,
                Strings.T("VehicleValueTip")) * 2f) / 2f;
            RocketVehicleFactor = Mathf.Round(l.SliderLabeled(Strings.T("RocketFactor", RocketVehicleFactor.ToString("F0")), RocketVehicleFactor, 1f, 20f, 0.5f, Strings.T("RocketFactorTip")));
            ArtilleryVehicleFactor = Mathf.Round(l.SliderLabeled(Strings.T("ArtilleryFactor", ArtilleryVehicleFactor.ToString("F0")), ArtilleryVehicleFactor, 1f, 20f, 0.5f, Strings.T("ArtilleryFactorTip")));
            MaxVehicles = (int)l.SliderLabeled(Strings.T("MaxVehicles", MaxVehicles), MaxVehicles, 1, 15, 0.5f);
            TransportPointShare = Mathf.Round(l.SliderLabeled(Strings.T("TransportShare", TransportPointShare.ToString("P0")), TransportPointShare, 0f, 0.3f, 0.5f,
                Strings.T("TransportShareTip")) * 20f) / 20f;
            TransportValueFactor = Mathf.Round(l.SliderLabeled(Strings.T("TransportValue", ValueDivisor.ToString("F0"), TransportValueFactor.ToString("F1")), TransportValueFactor, 0.5f, 5f, 0.5f,
                Strings.T("TransportValueTip")) * 2f) / 2f;
            MaxTransports = (int)l.SliderLabeled(Strings.T("MaxTransports", MaxTransports), MaxTransports, 0, 40, 0.5f);
            l.CheckboxLabeled(Strings.T("TruckTurrets"), ref TruckTurrets);
            MaxTruckTurrets = (int)l.SliderLabeled(Strings.T("MaxTruckTurrets", MaxTruckTurrets), MaxTruckTurrets, 0, 10, 0.5f);

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
            var harmony = new HarmonyLib.Harmony("shdmk2.hardenedraids");
            harmony.PatchAll();
            Patch_SpawnNearVehicle.Apply(harmony);
            Log.Message("[Hardened Raids] loaded.");
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
            try { CECompat.TuneAmmoChances(); } catch (System.Exception e) { Log.Warning("[Hardened Raids] CE ammo: " + e.Message); }
            Loadouts.BuildCaches();
            try { Patch_SiegeBlueprints.EnsureCatalog(); } catch (System.Exception e) { Log.Warning("[Hardened Raids] siege catalog: " + e.Message); }
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
