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
                "Multiplie le prix de chaque raider. Plus haut = moins de raiders mais aussi bien équipés.");
            CostFactor = Mathf.Round(CostFactor * 20f) / 20f;
            TierSpeed = l.SliderLabeled($"Vitesse de montée en palier : ×{TierSpeed:F2}", TierSpeed, 0.5f, 3f, 0.5f,
                "Plus haut = les raiders atteignent les paliers élevés avec moins de points.");
            TierSpeed = Mathf.Round(TierSpeed * 20f) / 20f;
            l.CheckboxLabeled("Cyberbrains GiTS pour les vétérans (si le mod est actif)", ref AddCyberbrains);
            ValueDivisor = Mathf.Round(l.SliderLabeled($"Équipement : {ValueDivisor:F0} $ par point", ValueDivisor, 15f, 100f, 0.5f,
                "Prix d'un raider = corps + valeur de son équipement (qualité, implants, cyberbrain compris) ÷ ce taux. 40 ≈ vanilla. Plus bas = équipement plus cher = moins de raiders."));
            l.CheckboxLabeled("Limiter les armures solides selon les points et la recherche de la colonie", ref ArmorGate,
                "Armures et mécanoïdes blindés plafonnés par les points du raid (marine vers 6 000 pts), et pas d'armure spacer tant que la colonie n'a fini aucune recherche spacer.");
            HarshDeathShare = Mathf.Round(l.SliderLabeled($"Raiders biocodés + acidificateur : {HarshDeathShare:P0}", HarshDeathShare, 0f, 1f, 0.5f,
                "Industriel et au-delà. Arme biocodée et acidificateur (l'équipement fond à la mort). Sans effet sur le prix.") * 20f) / 20f;
            HeavyMechCostFactor = Mathf.Round(l.SliderLabeled($"Prix des mécanoïdes blindés : ×{HeavyMechCostFactor:F2}", HeavyMechCostFactor, 1f, 4f, 0.5f,
                "Centipède, Termite, Diabolus, Warqueen… (protection ≥ 75 % de l'armure de marine).") * 4f) / 4f;
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
            VehicleValueFactor = Mathf.Round(l.SliderLabeled($"Prix des blindés : valeur ÷ {ValueDivisor:F0} × {VehicleValueFactor:F1}", VehicleValueFactor, 1f, 15f, 0.5f,
                "Même taux $/pt que l'équipement des raiders, multiplié pour le blindage. Plancher 400 pts.") * 2f) / 2f;
            RocketVehicleFactor = Mathf.Round(l.SliderLabeled($"Véhicules à roquettes en essaim : ×{RocketVehicleFactor:F0}", RocketVehicleFactor, 1f, 20f, 0.5f,
                "Scytheman, Burya : tir par-dessus les défenses, saturation anti-missile."));
            ArtilleryVehicleFactor = Mathf.Round(l.SliderLabeled($"Véhicules d'artillerie : ×{ArtilleryVehicleFactor:F0}", ArtilleryVehicleFactor, 1f, 20f, 0.5f,
                "Udar : tir indirect par-dessus les murs."));
            MaxVehicles = (int)l.SliderLabeled($"Blindés max par vague : {MaxVehicles}", MaxVehicles, 1, 15, 0.5f);
            TransportPointShare = Mathf.Round(l.SliderLabeled($"Part des points en transports : {TransportPointShare:P0}", TransportPointShare, 0f, 0.3f, 0.5f,
                "Camions et IFV pour l'infanterie. Petits paliers : voitures bon marché ; fin de partie : IFV. Ce qui ne rentre pas marche.") * 20f) / 20f;
            TransportValueFactor = Mathf.Round(l.SliderLabeled($"Prix des camions : valeur ÷ {ValueDivisor:F0} × {TransportValueFactor:F1}", TransportValueFactor, 0.5f, 5f, 0.5f,
                "Les IFV (Tango, Badger, Paladin) sont au prix des blindés.") * 2f) / 2f;
            MaxTransports = (int)l.SliderLabeled($"Transports max par vague : {MaxTransports}", MaxTransports, 0, 40, 0.5f);
            l.CheckboxLabeled("Raid smart : tourelles déchargées devant les camions", ref TruckTurrets);
            MaxTruckTurrets = (int)l.SliderLabeled($"Tourelles de camion max : {MaxTruckTurrets}", MaxTruckTurrets, 0, 10, 0.5f);
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
            var harmony = new HarmonyLib.Harmony("shdmk2.hardenedraids");
            harmony.PatchAll();
            Patch_SpawnNearVehicle.Apply(harmony);
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
