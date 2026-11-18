using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using HarmonyLib;
using RimWorld;
using UnityEngine;
using Verse;

namespace HardenedRaids
{
    /// <summary>
    /// Sièges (et camps des équipes d'appui) : artillerie des mods, tourelles servies ou autonomes (Fortifications,
    /// VFE Security...), et lignes de couverture tournées vers la base au lieu des sacs de sable aléatoires vanilla.
    /// Tout passe par SiegeBlueprintPlacer.PlaceBlueprints : vanilla livre les matériaux par capsule ;
    /// les munitions des tourelles sont fournies par <see cref="MapComponent_HostileTurretSupply"/>.
    /// </summary>
    /// <summary>
    /// Palier du camp de siège = palier forcé du raid (A5), pas les points du lord. Retenu par lord de siège (table
    /// faible, non sauvegardée) et lu pendant LordToil_Siege.Init, où les plans sont posés, avec l'effectif du
    /// groupe et le type de camp (siège ou équipe d'appui).
    /// </summary>
    public static class SiegeTier
    {
        private class Box { public float x; }
        private static readonly System.Runtime.CompilerServices.ConditionalWeakTable<Verse.AI.Group.LordJob, Box> byJob =
            new System.Runtime.CompilerServices.ConditionalWeakTable<Verse.AI.Group.LordJob, Box>();

        /// <summary>Palier du siège dont on pose le camp, -1 sinon.</summary>
        public static float Current = -1f;

        /// <summary>Effectif du groupe dont on pose le camp (0 hors de LordToil_Siege.Init).</summary>
        public static int Raiders;

        /// <summary>Camp d'une équipe d'appui (pendant LordToil_Siege.Init).</summary>
        public static bool Support;

        /// <summary>Toil de siège dont on pose le camp (pendant LordToil_Siege.Init).</summary>
        public static LordToil_Siege Toil;

        /// <summary>À appeler par qui crée un LordJob_Siege dont le palier est connu (raid, équipe d'appui).</summary>
        public static void Register(Verse.AI.Group.LordJob job, float tierX)
        {
            if (job == null || tierX <= 0f) return;
            byJob.Remove(job);
            byJob.Add(job, new Box { x = tierX });
        }

        public static float For(Verse.AI.Group.LordJob job) => job != null && byJob.TryGetValue(job, out Box b) ? b.x : -1f;
    }

    [HarmonyPatch(typeof(RaidStrategyWorker_Siege), "MakeLordJob")]
    public static class Patch_SiegeLordTier
    {
        public static void Postfix(IncidentParms parms, Verse.AI.Group.LordJob __result)
        {
            if (__result is LordJob_Siege) SiegeTier.Register(__result, RaidContext.TierXFor(parms));
        }
    }

    [HarmonyPatch(typeof(LordToil_Siege), nameof(LordToil_Siege.Init))]
    public static class Patch_SiegeInitTier
    {
        public static void Prefix(LordToil_Siege __instance)
        {
            Verse.AI.Group.Lord l = __instance.lord;
            SiegeTier.Current = SiegeTier.For(l?.LordJob);
            SiegeTier.Raiders = l?.ownedPawns.Count ?? 0;
            SiegeTier.Support = l?.Map != null && MapComponent_HostileTurretSupply.For(l.Map)?.IsSupportLord(l) == true;
            SiegeTier.Toil = __instance;
        }

        public static void Finalizer()
        {
            SiegeTier.Current = -1f;
            SiegeTier.Raiders = 0;
            SiegeTier.Support = false;
            SiegeTier.Toil = null;
        }
    }

    [HarmonyPatch(typeof(SiegeBlueprintPlacer), nameof(SiegeBlueprintPlacer.PlaceBlueprints))]
    public static class Patch_SiegeBlueprints
    {
        public static void Postfix(IntVec3 placeCenter, Map map, Faction placeFaction, float points, ref IEnumerable<Blueprint_Build> __result)
        {
            if (!HRSettings.SiegeUpgrade || placeFaction == null) return;
            __result = Extend(__result, placeCenter, map, placeFaction, points);
        }

        /// <summary>
        /// Palier 2+ : plan du camp (<see cref="SiegeCamp.PlacePieces"/>) à la place de l'artillerie vanilla posée au
        /// hasard. La couverture vient plus tard, quand les pièces sont construites (<see cref="Patch_SiegeCoverLater"/>).
        /// </summary>
        private static IEnumerable<Blueprint_Build> Extend(IEnumerable<Blueprint_Build> vanilla, IntVec3 center, Map map, Faction faction, float blueprintPoints)
        {
            List<Blueprint_Build> vanillaArty = vanilla.ToList();   // vanilla : seulement l'artillerie (sa couverture est désactivée plus bas)
            // palier forcé du raid (SiegeTier) ; à défaut (camp posé après un rechargement…), estimé depuis
            // blueprintPoints = points du raid × 0.2-0.3 (RaidStrategyWorker_Siege / équipe d'appui)
            float x = SiegeTier.Current >= 0f ? SiegeTier.Current : Tiers.TierX(faction.def, blueprintPoints * 4f);
            List<Blueprint_Build> plan = null;
            if (x >= 2f)
            {
                try
                {
                    int raiders = SiegeTier.Raiders > 0 ? SiegeTier.Raiders : 10;
                    plan = SiegeCamp.PlacePieces(map, faction, center, x, raiders, SiegeTier.Support, vanillaArty, ThreatTarget(map, center));
                }
                catch (Exception e)
                {
                    Log.Warning("[Hardened Raids] siege camp: " + e);
                }
            }
            else HRLog.Msg($"siège {faction.Name} (palier {x:F1}) : artillerie vanilla ({vanillaArty.Count} pièce(s)).");
            foreach (Blueprint_Build b in plan ?? vanillaArty)
                if (!b.Destroyed) yield return b;
        }

        // ------------------------------------------------------------ catalogues
        /// <summary>
        /// Pièce d'artillerie (tir indirect) : tag de bâtiment Artillery / Artillery_BaseDestroyer, tag d'arme
        /// Artillery / Artillery_BaseDestroyer, ou arme à tir courbe (pas de ligne de vue et portée minimale ≥ 14,
        /// ou verbe « mortier » de CE). Couvre le mortier vanilla, VFES_Turret_Artillery, le mortier portable et la
        /// bombarde de CE Armory, la catapulte VFES, et sous CE les FT_TurretHexMortar / FT_Turret_Mortar /
        /// FTC_Katyusha_Turret, dont les armes CE n'ont pas de tag d'artillerie ; Prince et Empero ont le tag d'arme.
        /// </summary>
        public static bool IsArtillery(ThingDef d)
        {
            if (d?.building == null) return false;
            List<string> bt = d.building.buildingTags;
            if (bt != null && (bt.Contains("Artillery_BaseDestroyer") || bt.Contains("Artillery"))) return true;
            ThingDef gun = d.building.turretGunDef;
            if (gun == null) return false;
            if (gun.weaponTags != null && (gun.weaponTags.Contains("Artillery_BaseDestroyer") || gun.weaponTags.Contains("Artillery"))) return true;
            VerbProperties v = gun.Verbs?.FirstOrDefault();
            if (v == null) return false;
            return (!v.requireLineOfSight && v.minRange >= 14f) || (v.verbClass != null && v.verbClass.Name.Contains("Mortar"));
        }

        /// <summary>Pièce qui compte pour le trigger vanilla NoArtillery (tag de bâtiment exact « Artillery »).</summary>
        public static bool HasArtilleryTag(ThingDef d) => d?.building?.buildingTags != null && d.building.buildingTags.Contains("Artillery");

        /// <summary>Portée minimale de l'arme de la pièce (0 sans arme).</summary>
        public static float MinRange(ThingDef d) => d?.building?.turretGunDef?.Verbs?.FirstOrDefault()?.minRange ?? 0f;

        /// <summary>Projecteurs / désignateurs (FTC_SpotterLight, VFES_Turret_Searchlight…) : ils ne blessent personne.</summary>
        private static bool IsSpotter(ThingDef d)
        {
            if (d.defName.IndexOf("Spotter", StringComparison.OrdinalIgnoreCase) >= 0
                || d.defName.IndexOf("Searchlight", StringComparison.OrdinalIgnoreCase) >= 0) return true;
            List<VerbProperties> verbs = d.building?.turretGunDef?.Verbs;
            return verbs != null && verbs.Any(v => v.verbClass != null
                                                 && (v.verbClass.Name.Contains("MarkForArtillery") || v.verbClass.Name.Contains("Dazzle")));
        }

        private static readonly System.Text.RegularExpressions.Regex AAName =
            new System.Text.RegularExpressions.Regex("(^|_|[a-z])AA($|_|[A-Z])|AntiAir|Flak");

        /// <summary>
        /// Tourelles anti-aériennes (FT_TurretAA, FT_TurretQuadAA, FT_TurretHeavyFlak, FTC_TurretTwinHeavyFlak,
        /// Turret_Flak de CE, VFES_Turret_AntiAir) : aucun marqueur commun dans les defs, on lit le nom et le label.
        /// </summary>
        private static bool IsAntiAir(ThingDef d)
        {
            ThingDef gun = d.building?.turretGunDef;
            if (AAName.IsMatch(d.defName) || (gun != null && AAName.IsMatch(gun.defName))) return true;
            string label = (d.label ?? "") + " " + (gun?.label ?? "");
            return label.IndexOf("anti-air", StringComparison.OrdinalIgnoreCase) >= 0
                   || label.IndexOf("antiair", StringComparison.OrdinalIgnoreCase) >= 0
                   || label.IndexOf("flak", StringComparison.OrdinalIgnoreCase) >= 0;
        }

        /// <summary>Niveau réel d'une pièce : le sien, ou celui de ses recherches, ou celui de son arme.</summary>
        public static TechLevel EffectiveTech(ThingDef d)
        {
            TechLevel t = d.techLevel;
            if (d.researchPrerequisites != null)
                foreach (ResearchProjectDef r in d.researchPrerequisites) if (r != null && r.techLevel > t) t = r.techLevel;
            ThingDef gun = d.building?.turretGunDef;
            if (gun != null && gun.techLevel > t) t = gun.techLevel;
            return t;
        }

        /// <summary>
        /// Pas au-dessus du niveau de la faction ; et une armée industrielle ou plus n'aligne pas de baliste ni de
        /// pierrier : plancher industriel.
        /// </summary>
        public static bool TechOk(ThingDef d, Faction f)
        {
            TechLevel t = EffectiveTech(d);
            if (t == TechLevel.Undefined) return true;
            if (t > f.def.techLevel) return false;
            return f.def.techLevel < TechLevel.Industrial || t >= TechLevel.Industrial;
        }

        /// <summary>
        /// Règle d'uniformité : un raid aligne au plus deux modèles, une pièce légère (moitié la moins chère du
        /// catalogue) et une lourde (l'autre moitié). Index 0 = légère, 1 = lourde.
        /// </summary>
        public static List<ThingDef> ChooseKit(List<ThingDef> defs, float x)
        {
            var kit = new List<ThingDef>();
            if (defs == null || defs.Count == 0) return kit;
            List<ThingDef> sorted = defs.OrderBy(d => d.BaseMarketValue).ToList();
            int half = sorted.Count / 2;
            ThingDef light = PickWeighted(sorted.Take(Mathf.Max(1, half)).ToList(), x);
            ThingDef heavy = PickWeighted(sorted.Skip(half).ToList(), x);
            if (light != null) kit.Add(light);
            if (heavy != null && heavy != light) kit.Add(heavy);
            return kit;
        }

        private static bool NeedsPower(ThingDef d) =>
            d.comps != null && d.comps.Any(c => c is CompProperties_Power p && p.PowerConsumption > 0f);

        private static bool GunHarms(ThingDef gun) =>
            gun != null && ((gun.Verbs != null && gun.Verbs.Any(v => v.defaultProjectile != null))
                            || (gun.comps != null && gun.comps.Any(c => c.GetType().Name == "CompProperties_AmmoUser")));

        private static List<ThingDef> artilleryDefs, turretDefs, coverDefs, parapetDefs;

        private static bool Contains(string s, string part) => s.IndexOf(part, StringComparison.OrdinalIgnoreCase) >= 0;

        public static void EnsureCatalog()
        {
            if (artilleryDefs != null) return;
            artilleryDefs = DefDatabase<ThingDef>.AllDefs.Where(d =>
                d.building?.turretGunDef != null && IsArtillery(d) && d.BuildableByPlayer && !NeedsPower(d)
                && !TurretManning.RequiresOccupant(d) && !IsSpotter(d)).ToList();
            // tourelles : servies (autocanon, mitrailleuse...) ou automatiques, sans électricité, au plus 5×5 ;
            // ni artillerie, ni anti-aérien, ni projecteur
            var antiAir = new List<ThingDef>();
            turretDefs = DefDatabase<ThingDef>.AllDefs.Where(d =>
            {
                if (d.building?.turretGunDef == null || IsArtillery(d) || !d.BuildableByPlayer || NeedsPower(d)) return false;
                if (d.size.x > 5 || d.size.z > 5 || !GunHarms(d.building.turretGunDef) || IsSpotter(d)) return false;
                if (TurretManning.RequiresOccupant(d)) return false;   // bunkers à occupant : l'IA n'y entre pas, ils ne tireraient jamais
                if (IsAntiAir(d)) { antiAir.Add(d); return false; }
                return true;
            }).ToList();
            // sacs de sable (1×1, et 1×2 / 2×2 de Fortifications Citadel) ; les tranchées (fillPercent 0, 5 000 à
            // 7 500 de travail par case) ne couvrent pas et sont exclues
            coverDefs = DefDatabase<ThingDef>.AllDefs.Where(d =>
                d.building != null && d.BuildableByPlayer && d.size.x <= 2 && d.size.z <= 2 && d.fillPercent >= 0.4f
                && d.passability != Traversability.Impassable && Contains(d.defName, "Sandbag")).ToList();
            // parapets des pièces d'artillerie (tir courbe : un mur devant ne gêne pas) : murs de sacs et embrasures 1×1
            parapetDefs = DefDatabase<ThingDef>.AllDefs.Where(d =>
                d.building != null && d.BuildableByPlayer && d.size == IntVec2.One && d.fillPercent >= 0.4f
                && ((Contains(d.defName, "Embrasure") && !Contains(d.defName, "Corner"))
                    || (Contains(d.defName, "Sandbag") && Contains(d.defName, "Wall")))).ToList();
            coverDefs.RemoveAll(parapetDefs.Contains);
            HRLog.Msg($"siège : catalogue — {artilleryDefs.Count} artilleries ({string.Join(", ", artilleryDefs.Select(d => d.defName))}), "
                      + $"{turretDefs.Count} tourelles ({turretDefs.Count(d => d.HasComp<CompMannable>())} servies : {string.Join(", ", turretDefs.Select(d => d.defName))}), "
                      + $"anti-aériens écartés ({string.Join(", ", antiAir.Select(d => d.defName))}), "
                      + $"couvertures ({string.Join(", ", coverDefs.Select(d => d.defName))}), parapets ({string.Join(", ", parapetDefs.Select(d => d.defName))}).");
        }

        /// <summary>
        /// Parapets autorisés pour la faction : une armée industrielle ou plus ne monte pas de palissade en bois
        /// (FT_Palisade_Embrasures : niveau non renseigné, seul le matériau la trahit).
        /// </summary>
        public static List<ThingDef> ParapetDefsFor(Faction f)
        {
            EnsureCatalog();
            return parapetDefs.Where(d => TechOk(d, f)
                && !(f.def.techLevel >= TechLevel.Industrial && d.stuffCategories != null && d.stuffCategories.Count > 0
                     && d.stuffCategories.All(c => c == StuffCategoryDefOf.Woody))).ToList();
        }

        /// <summary>Sac de sable ou parapet du catalogue (couverture d'un camp).</summary>
        public static bool IsCatalogCover(ThingDef d)
        {
            EnsureCatalog();
            return coverDefs.Contains(d) || parapetDefs.Contains(d);
        }

        public static List<ThingDef> CoverDefsFor(Faction f)
        {
            EnsureCatalog();
            return coverDefs.Where(d => TechOk(d, f)).ToList();
        }

        public static List<ThingDef> ArtilleryDefsFor(Faction f)
        {
            EnsureCatalog();
            return artilleryDefs.Where(d => TechOk(d, f)).ToList();
        }

        /// <summary>Tourelles autorisées pour la faction, au plus maxSize×maxSize.</summary>
        public static List<ThingDef> TurretDefsFor(Faction f, int maxSize)
        {
            EnsureCatalog();
            return turretDefs.Where(d => TechOk(d, f) && d.size.x <= maxSize && d.size.z <= maxSize).ToList();
        }

        public static ThingDef StuffFor(ThingDef d) => d.MadeFromStuff ? (GenStuff.AllowedStuffsFor(d).Contains(ThingDefOf.Steel) ? ThingDefOf.Steel : GenStuff.DefaultStuffFor(d)) : null;

        /// <summary>Plus le palier est haut, plus on favorise le matériel cher (gros calibres).</summary>
        public static ThingDef PickWeighted(List<ThingDef> defs, float x)
        {
            float exp = Mathf.Lerp(0f, 1.5f, Mathf.InverseLerp(2f, 6f, x));
            return defs.TryRandomElementByWeight(d => Mathf.Pow(Mathf.Max(50f, d.BaseMarketValue), exp), out ThingDef r) ? r : null;
        }

        /// <summary>Case du servant d'un plan de pièce (Invalid si la pièce n'en a pas).</summary>
        public static IntVec3 GunnerCell(Blueprint_Build b, Map map) =>
            b.def.entityDefToBuild is ThingDef d && d.hasInteractionCell ? ThingUtility.InteractionCellWhenAt(d, b.Position, b.Rotation, map) : IntVec3.Invalid;

        /// <summary>
        /// La cible du siège : le bâtiment de combat du joueur le plus proche (tourelles, bâtiments que vanilla
        /// traite comme cibles), pas un câble ou un mur isolé ; à défaut, le barycentre des colons et de la zone Home.
        /// </summary>
        public static IntVec3 ThreatTarget(Map map, IntVec3 from)
        {
            Building best = null;
            float bestD = float.MaxValue;
            foreach (Building b in map.listerBuildings.allBuildingsColonistCombatTargets)
            {
                if (b == null || !b.Spawned) continue;
                float d = (b.Position - from).LengthHorizontalSquared;
                if (d < bestD) { bestD = d; best = b; }
            }
            if (best != null) return best.Position;
            var parts = new List<Vector2>();
            List<Pawn> colonists = map.mapPawns.FreeColonistsSpawned;
            if (colonists.Count > 0) parts.Add(new Vector2((float)colonists.Average(p => p.Position.x), (float)colonists.Average(p => p.Position.z)));
            Area home = map.areaManager?.Home;
            if (home != null && home.TrueCount > 0)
            {
                long sx = 0, sz = 0;
                int n = 0;
                foreach (IntVec3 c in home.ActiveCells) { sx += c.x; sz += c.z; n++; }
                if (n > 0) parts.Add(new Vector2(sx / (float)n, sz / (float)n));
            }
            if (parts.Count == 0) return map.Center;
            Vector2 m = new Vector2(parts.Average(v => v.x), parts.Average(v => v.y));
            return new IntVec3(Mathf.RoundToInt(m.x), 0, Mathf.RoundToInt(m.y)).ClampInsideMap(map);
        }

    }

    /// <summary>La couverture vanilla (aléatoire) est remplacée par la nôtre, orientée vers la base.</summary>
    [HarmonyPatch(typeof(SiegeBlueprintPlacer), "PlaceCoverBlueprints")]
    public static class Patch_SiegeVanillaCover
    {
        public static bool Prefix(ref IEnumerable<Blueprint_Build> __result)
        {
            if (!HRSettings.SiegeUpgrade) return true;
            __result = Enumerable.Empty<Blueprint_Build>();
            return false;
        }
    }

    /// <summary>
    /// Les bâtisseurs du siège ont le niveau de construction requis par ce que le camp construit vraiment (plans et
    /// chantiers du lord), pas par tout le catalogue des mods. Déduit de la carte à chaque appel, rien n'est retenu.
    /// </summary>
    [HarmonyPatch(typeof(LordToil_Siege), "SetAsBuilder")]
    public static class Patch_SiegeBuilderSkill
    {
        public static void Postfix(LordToil_Siege __instance, Pawn p)
        {
            if (!HRSettings.SiegeUpgrade || p?.skills == null || __instance?.data is not LordToilData_Siege data) return;
            int lvl = 0;
            foreach (Blueprint b in data.blueprints)
                if (b != null && !b.Destroyed && b.def.entityDefToBuild is ThingDef d) lvl = Mathf.Max(lvl, d.constructionSkillPrerequisite);
            Map map = __instance.Map;
            Faction f = __instance.lord?.faction;
            if (map != null && f != null)
                foreach (Thing t in map.listerThings.ThingsInGroup(ThingRequestGroup.BuildingFrame))
                    if (t.Faction == f && t.Position.InHorDistOf(data.siegeCenter, 25f) && t.def.entityDefToBuild is ThingDef fd)
                        lvl = Mathf.Max(lvl, fd.constructionSkillPrerequisite);
            lvl = Mathf.Min(20, lvl);
            if (lvl > 0) p.skills.GetSkill(SkillDefOf.Construction)?.EnsureMinLevelWithMargin(lvl);
        }
    }
}
