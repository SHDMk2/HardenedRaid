using System.Collections.Generic;

namespace HardenedRaids
{
    public enum HRLanguage { English, French }

    /// <summary>
    /// Player-facing strings (settings, messages), chosen by the mod's own language setting (independent of the
    /// game language). English by default.
    /// </summary>
    public static class Strings
    {
        private static readonly Dictionary<string, string> en = new Dictionary<string, string>
        {
            ["Language"] = "Language",
            ["VerboseLog"] = "Detailed log (debug)",

            ["SecPoints"] = "<b>Raid points</b>",
            ["UncapPoints"] = "Remove the 10,000 points cap",
            ["ExtendCurves"] = "Extend wealth → points curves beyond 1M",
            ["ExtendCurvesTip"] = "Vanilla stops counting wealth after 1,000,000. Enabled: the curve keeps the same slope.",
            ["PointsNow"] = "Current points on this map: {0}",

            ["SecTiers"] = "<b>Tiered generation</b>",
            ["EnableTiers"] = "Enable equipment tiers",
            ["CostFactor"] = "Cost of equipped raiders: ×{0}",
            ["CostFactorTip"] = "Multiplies each raider's price. Higher = fewer raiders, equally well equipped.",
            ["TierSpeed"] = "Tier progression speed: ×{0}",
            ["TierSpeedTip"] = "Higher = raiders reach high tiers with fewer points.",
            ["Cyberbrains"] = "GiTS cyberbrains for veterans (if the mod is active)",
            ["ValueDivisor"] = "Equipment: ${0} per point",
            ["ValueDivisorTip"] = "Raider price = body + value of their gear (quality, implants, cyberbrain included) ÷ this rate. 40 ≈ vanilla. Lower = gear costs more = fewer raiders.",
            ["ArmorGate"] = "Limit strong armor by raid points and colony research",
            ["ArmorGateTip"] = "Armor and armored mechanoids capped by raid points (marine armor around 6,000 pts), and no spacer armor until the colony has finished a spacer research project.",
            ["HarshDeath"] = "Biocoded raiders + death acidifier: {0}",
            ["HarshDeathTip"] = "Industrial and above. Biocoded weapon and death acidifier (gear melts on death). No effect on price.",
            ["HeavyMech"] = "Armored mechanoid price: ×{0}",
            ["HeavyMechTip"] = "Centipede, Termite, Diabolus, War Queen… (protection ≥ 75% of marine armor).",

            ["SecWaves"] = "<b>Waves</b>",
            ["CapHigh"] = "Max raiders per wave (industrial+): {0}",
            ["CapLow"] = "Max raiders per wave (neolithic/medieval): {0}",
            ["WaveMin"] = "Min delay between waves: {0} h",
            ["WaveMax"] = "Max delay between waves: {0} h",

            ["SecAI"] = "<b>AI</b>",
            ["Smarter"] = "Smarter strategies at high tiers (smart, breach, sappers)",
            ["AvoidTurrets"] = "Veterans (T2+) avoid turret fields of fire",
            ["DeadTurrets"] = "AI ignores out-of-service turrets (no power, empty magazine, no fuel)",

            ["SecSiege"] = "<b>Sieges and vehicles</b>",
            ["SiegeUpgrade"] = "Reinforced siege camps (artillery, modded turrets, trenches facing the base)",
            ["SiegeArtillery"] = "Max artillery pieces per siege: {0}",
            ["SiegeTurrets"] = "Max turrets per siege: {0}",
            ["SupportTeams"] = "Support teams during assaults (forward camp with turrets)",
            ["SupportTeamsTip"] = "Some industrial+ raiders arriving from the map edge set up a firing camp (like a siege) while the others attack.",
            ["SupportMinTier"] = "Minimum tier for support teams: {0}",
            ["SupportShare"] = "Share of raiders in support: {0}",
            ["Vehicles"] = "Enemy combat vehicles (Vehicle Framework, experimental)",
            ["VehiclesTip"] = "Industrial+ raiders walking in from the map edge can bring VVE armored vehicles.",
            ["VehicleShare"] = "Share of points spent on vehicles: {0}",
            ["VehicleMinTier"] = "Minimum tier for vehicles: {0}",
            ["VehicleValue"] = "Armored vehicle price: value ÷ {0} × {1}",
            ["VehicleValueTip"] = "Same $/point rate as raider gear, multiplied for the armor. Minimum 400 pts.",
            ["RocketFactor"] = "Rocket swarm vehicles: ×{0}",
            ["RocketFactorTip"] = "Scytheman, Burya: fire over defenses, saturate anti-missile systems.",
            ["ArtilleryFactor"] = "Artillery vehicles: ×{0}",
            ["ArtilleryFactorTip"] = "Udar: indirect fire over walls.",
            ["MaxVehicles"] = "Max armored vehicles per wave: {0}",
            ["TransportShare"] = "Share of points spent on transports: {0}",
            ["TransportShareTip"] = "Trucks and IFVs for the infantry. Low tiers: cheap cars; late game: IFVs. Whoever doesn't fit walks.",
            ["TransportValue"] = "Truck price: value ÷ {0} × {1}",
            ["TransportValueTip"] = "IFVs (Tango, Badger, Paladin) are priced like armored vehicles.",
            ["MaxTransports"] = "Max transports per wave: {0}",
            ["TruckTurrets"] = "Smart raids: turrets unloaded in front of the trucks",
            ["MaxTruckTurrets"] = "Max truck turrets: {0}",

            ["MsgWaves"] = "Hardened Raids: {0} attacks in {1} waves ({2} raiders in total). Next wave in {3}.",
            ["MsgSupportTeam"] = "A support team from {0} ({1} men) is setting up firing positions.",
            ["MsgTruckTurret"] = "{0} is unloading a {1} in front of its trucks.",
        };

        private static readonly Dictionary<string, string> fr = new Dictionary<string, string>
        {
            ["Language"] = "Langue",
            ["VerboseLog"] = "Journal détaillé (log)",

            ["SecPoints"] = "<b>Points de raid</b>",
            ["UncapPoints"] = "Supprimer le plafond de 10 000 points",
            ["ExtendCurves"] = "Prolonger les courbes richesse → points au-delà de 1M",
            ["ExtendCurvesTip"] = "Vanilla arrête de compter la richesse après 1 000 000. Activé : la courbe continue avec la même pente.",
            ["PointsNow"] = "Points actuels sur cette carte : {0}",

            ["SecTiers"] = "<b>Génération par paliers</b>",
            ["EnableTiers"] = "Activer les paliers d'équipement",
            ["CostFactor"] = "Coût des raiders équipés : ×{0}",
            ["CostFactorTip"] = "Multiplie le prix de chaque raider. Plus haut = moins de raiders mais aussi bien équipés.",
            ["TierSpeed"] = "Vitesse de montée en palier : ×{0}",
            ["TierSpeedTip"] = "Plus haut = les raiders atteignent les paliers élevés avec moins de points.",
            ["Cyberbrains"] = "Cyberbrains GiTS pour les vétérans (si le mod est actif)",
            ["ValueDivisor"] = "Équipement : {0} $ par point",
            ["ValueDivisorTip"] = "Prix d'un raider = corps + valeur de son équipement (qualité, implants, cyberbrain compris) ÷ ce taux. 40 ≈ vanilla. Plus bas = équipement plus cher = moins de raiders.",
            ["ArmorGate"] = "Limiter les armures solides selon les points et la recherche de la colonie",
            ["ArmorGateTip"] = "Armures et mécanoïdes blindés plafonnés par les points du raid (marine vers 6 000 pts), et pas d'armure spacer tant que la colonie n'a fini aucune recherche spacer.",
            ["HarshDeath"] = "Raiders biocodés + acidificateur : {0}",
            ["HarshDeathTip"] = "Industriel et au-delà. Arme biocodée et acidificateur (l'équipement fond à la mort). Sans effet sur le prix.",
            ["HeavyMech"] = "Prix des mécanoïdes blindés : ×{0}",
            ["HeavyMechTip"] = "Centipède, Termite, Diabolus, Warqueen… (protection ≥ 75 % de l'armure de marine).",

            ["SecWaves"] = "<b>Vagues</b>",
            ["CapHigh"] = "Raiders max par vague (industriel+) : {0}",
            ["CapLow"] = "Raiders max par vague (néolithique/médiéval) : {0}",
            ["WaveMin"] = "Délai min entre vagues : {0} h",
            ["WaveMax"] = "Délai max entre vagues : {0} h",

            ["SecAI"] = "<b>IA</b>",
            ["Smarter"] = "Stratégies plus intelligentes selon le palier (smart, brèche, sape)",
            ["AvoidTurrets"] = "Les vétérans (T2+) évitent les zones couvertes par les tourelles",
            ["DeadTurrets"] = "L'IA ignore les tourelles hors service (sans courant, chargeur vide, sans carburant)",

            ["SecSiege"] = "<b>Sièges et véhicules</b>",
            ["SiegeUpgrade"] = "Camps de siège renforcés (artillerie, tourelles des mods, tranchées face à la base)",
            ["SiegeArtillery"] = "Pièces d'artillerie max par siège : {0}",
            ["SiegeTurrets"] = "Tourelles max par siège : {0}",
            ["SupportTeams"] = "Équipes d'appui pendant les assauts (camp avancé avec tourelles)",
            ["SupportTeamsTip"] = "Une partie des raiders industriels+ arrivant par le bord installe un camp de tir (comme un siège) pendant que les autres attaquent.",
            ["SupportMinTier"] = "Palier minimum des équipes d'appui : {0}",
            ["SupportShare"] = "Part des raiders en appui : {0}",
            ["Vehicles"] = "Véhicules de combat ennemis (Vehicle Framework, expérimental)",
            ["VehiclesTip"] = "Les raiders industriels+ arrivant à pied par le bord de la carte peuvent amener des blindés VVE.",
            ["VehicleShare"] = "Part des points en véhicules : {0}",
            ["VehicleMinTier"] = "Palier minimum pour les véhicules : {0}",
            ["VehicleValue"] = "Prix des blindés : valeur ÷ {0} × {1}",
            ["VehicleValueTip"] = "Même taux $/pt que l'équipement des raiders, multiplié pour le blindage. Plancher 400 pts.",
            ["RocketFactor"] = "Véhicules à roquettes en essaim : ×{0}",
            ["RocketFactorTip"] = "Scytheman, Burya : tir par-dessus les défenses, saturation anti-missile.",
            ["ArtilleryFactor"] = "Véhicules d'artillerie : ×{0}",
            ["ArtilleryFactorTip"] = "Udar : tir indirect par-dessus les murs.",
            ["MaxVehicles"] = "Blindés max par vague : {0}",
            ["TransportShare"] = "Part des points en transports : {0}",
            ["TransportShareTip"] = "Camions et IFV pour l'infanterie. Petits paliers : voitures bon marché ; fin de partie : IFV. Ce qui ne rentre pas marche.",
            ["TransportValue"] = "Prix des camions : valeur ÷ {0} × {1}",
            ["TransportValueTip"] = "Les IFV (Tango, Badger, Paladin) sont au prix des blindés.",
            ["MaxTransports"] = "Transports max par vague : {0}",
            ["TruckTurrets"] = "Raid smart : tourelles déchargées devant les camions",
            ["MaxTruckTurrets"] = "Tourelles de camion max : {0}",

            ["MsgWaves"] = "Hardened Raids : {0} attaque en {1} vagues ({2} raiders au total). Prochaine vague dans {3}.",
            ["MsgSupportTeam"] = "Une équipe d'appui de {0} ({1} hommes) installe des positions de tir.",
            ["MsgTruckTurret"] = "{0} décharge un(e) {1} devant ses camions.",
        };

        public static string LanguageName(HRLanguage lang) => lang == HRLanguage.French ? "Français" : "English";

        public static string T(string key, params object[] args)
        {
            var table = HRSettings.Language == HRLanguage.French ? fr : en;
            if (!table.TryGetValue(key, out string s) && !en.TryGetValue(key, out s))
                s = key;
            return args.Length == 0 ? s : string.Format(s, args);
        }
    }
}
