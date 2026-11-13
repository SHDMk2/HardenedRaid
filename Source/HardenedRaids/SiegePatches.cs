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
    [HarmonyPatch(typeof(SiegeBlueprintPlacer), nameof(SiegeBlueprintPlacer.PlaceBlueprints))]
    public static class Patch_SiegeBlueprints
    {
        private static readonly MethodInfo findArtySpot = AccessTools.Method(typeof(SiegeBlueprintPlacer), "FindArtySpot");
        private static readonly MethodInfo canPlaceAt = AccessTools.Method(typeof(SiegeBlueprintPlacer), "CanPlaceBlueprintAt");
        private static readonly MethodInfo clearHostilePlans = AccessTools.Method(typeof(SiegeBlueprintPlacer), "ClearHostilePlansAt");

        public static void Postfix(IntVec3 placeCenter, Map map, Faction placeFaction, float points, ref IEnumerable<Blueprint_Build> __result)
        {
            if (!HRSettings.SiegeUpgrade || placeFaction == null || findArtySpot == null || canPlaceAt == null) return;
            __result = Extend(__result, placeCenter, map, placeFaction, points);
        }

        private static IEnumerable<Blueprint_Build> Extend(IEnumerable<Blueprint_Build> vanilla, IntVec3 center, Map map, Faction faction, float blueprintPoints)
        {
            var pieces = new List<Blueprint_Build>();
            foreach (Blueprint_Build b in vanilla)   // vanilla : seulement l'artillerie (sa couverture est désactivée plus bas)
            {
                if (b.def.entityDefToBuild is ThingDef td && td.building?.turretGunDef != null) pieces.Add(b);
                yield return b;
            }

            // blueprintPoints = points du raid × 0.2-0.3 (RaidStrategyWorker_Siege / équipe d'appui)
            float x = Tiers.TierX(faction.def, blueprintPoints * 4f);
            IntVec3 target = ThreatTarget(map, center);
            Rot4 facing = Rot4.FromAngleFlat((target - center).AngleFlat);
            var extras = new List<Blueprint_Build>();
            try
            {
                if (x >= 2f)
                {
                    PlaceArtillery(extras, map, faction, x, pieces.Count(b => IsArtillery((ThingDef)b.def.entityDefToBuild)), facing);
                    PlaceTurrets(extras, map, faction, x, facing);
                }
                pieces.AddRange(extras);
                PlaceCover(extras, map, faction, center, target, x, pieces);
            }
            catch (Exception e)
            {
                Log.Warning("[Hardened Raids] siege camp: " + e);
            }
            HRLog.Msg($"siège {faction.Name} (palier {x:F1}) vers {target} : +{extras.Count} plans ({string.Join(", ", extras.GroupBy(b => b.def.entityDefToBuild.label).Select(g => g.Count() + "× " + g.Key))})");
            foreach (Blueprint_Build b in extras) yield return b;
        }

        // ------------------------------------------------------------ catalogues
        public static bool IsArtillery(ThingDef d) => d?.building?.buildingTags != null && d.building.buildingTags.Contains("Artillery_BaseDestroyer");

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
        private static bool TechOk(ThingDef d, Faction f)
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

        private static List<ThingDef> artilleryDefs, turretDefs, coverDefs;
        public static int MaxConstructionSkill { get; private set; }

        public static void EnsureCatalog()
        {
            if (artilleryDefs != null) return;
            artilleryDefs = DefDatabase<ThingDef>.AllDefs.Where(d => IsArtillery(d) && d.BuildableByPlayer && !NeedsPower(d)).ToList();
            // tourelles : servies (autocanon, flak, mitrailleuse...) ou automatiques, sans électricité, au plus 5×5
            turretDefs = DefDatabase<ThingDef>.AllDefs.Where(d =>
                d.building?.turretGunDef != null && !IsArtillery(d) && d.BuildableByPlayer && !NeedsPower(d)
                && d.size.x <= 5 && d.size.z <= 5 && GunHarms(d.building.turretGunDef)
                && (d.building.buildingTags == null || !d.building.buildingTags.Any(t => t.StartsWith("Artillery")))).ToList();
            // tranchées / sacs de sable des mods (1×1)
            coverDefs = DefDatabase<ThingDef>.AllDefs.Where(d =>
                d.building != null && d.BuildableByPlayer && d.size == IntVec2.One && d.fillPercent >= 0.4f && d.passability != Traversability.Impassable
                && (d.defName.IndexOf("Trench", StringComparison.OrdinalIgnoreCase) >= 0 || d.defName.IndexOf("Sandbag", StringComparison.OrdinalIgnoreCase) >= 0)).ToList();
            MaxConstructionSkill = Mathf.Min(20, artilleryDefs.Concat(turretDefs).Concat(coverDefs).Select(d => d.constructionSkillPrerequisite).DefaultIfEmpty(0).Max());
            HRLog.Msg($"siège : {artilleryDefs.Count} artilleries, {turretDefs.Count} tourelles ({turretDefs.Count(d => d.HasComp(typeof(CompMannable)))} servies), "
                      + $"{coverDefs.Count} couvertures. Tourelles : {string.Join(", ", turretDefs.Select(d => d.defName))}");
        }

        /// <summary>Tourelles autorisées pour la faction, au plus maxSize×maxSize.</summary>
        public static List<ThingDef> TurretDefsFor(Faction f, int maxSize)
        {
            EnsureCatalog();
            return turretDefs.Where(d => TechOk(d, f) && d.size.x <= maxSize && d.size.z <= maxSize).ToList();
        }

        private static ThingDef StuffFor(ThingDef d) => d.MadeFromStuff ? (GenStuff.AllowedStuffsFor(d).Contains(ThingDefOf.Steel) ? ThingDefOf.Steel : GenStuff.DefaultStuffFor(d)) : null;

        /// <summary>Plus le palier est haut, plus on favorise le matériel cher (gros calibres).</summary>
        public static ThingDef PickWeighted(List<ThingDef> defs, float x)
        {
            float exp = Mathf.Lerp(0f, 1.5f, Mathf.InverseLerp(2f, 6f, x));
            return defs.TryRandomElementByWeight(d => Mathf.Pow(Mathf.Max(50f, d.BaseMarketValue), exp), out ThingDef r) ? r : null;
        }

        private static bool TryPlace(List<Blueprint_Build> output, ThingDef def, Map map, Faction faction, Rot4 rot)
        {
            if (!def.rotatable) rot = Rot4.North;
            var spot = (IntVec3)findArtySpot.Invoke(null, new object[] { def, rot, map });
            if (!spot.IsValid) return false;
            ThingDef stuff = StuffFor(def);
            clearHostilePlans?.Invoke(null, new object[] { spot, rot, def, stuff ?? ThingDefOf.Steel, map });
            output.Add(GenConstruct.PlaceBlueprintForBuild(def, spot, map, rot, faction, stuff));
            return true;
        }

        /// <summary>La cible du siège : le bâtiment du joueur le plus proche du camp.</summary>
        public static IntVec3 ThreatTarget(Map map, IntVec3 from)
        {
            Building best = null;
            float bestD = float.MaxValue;
            foreach (Building b in map.listerBuildings.allBuildingsColonist)
            {
                float d = (b.Position - from).LengthHorizontalSquared;
                if (d < bestD) { bestD = d; best = b; }
            }
            return best?.Position ?? map.Center;
        }

        // ------------------------------------------------------------ placement
        private static void PlaceArtillery(List<Blueprint_Build> output, Map map, Faction faction, float x, int already, Rot4 facing)
        {
            EnsureCatalog();
            List<ThingDef> defs = artilleryDefs.Where(d => TechOk(d, faction)).ToList();
            if (defs.Count == 0) return;
            int target = Mathf.Min(HRSettings.MaxSiegeArtillery, 1 + Mathf.RoundToInt(x * 1.2f));
            for (int i = already; i < target; i++)
            {
                ThingDef def = PickWeighted(defs, x);
                if (def == null || !TryPlace(output, def, map, faction, facing)) break;
            }
        }

        private static void PlaceTurrets(List<Blueprint_Build> output, Map map, Faction faction, float x, Rot4 facing)
        {
            EnsureCatalog();
            List<ThingDef> kit = ChooseKit(turretDefs.Where(d => TechOk(d, faction)).ToList(), x);
            if (kit.Count == 0) return;
            HRLog.Msg($"siège : dotation {string.Join(" + ", kit.Select(d => d.label))}.");
            int n = Mathf.Clamp(Mathf.RoundToInt((x - 1.5f) * 1.5f), 1, HRSettings.MaxSiegeTurrets);
            for (int i = 0; i < n; i++)
            {
                ThingDef def = kit[i % kit.Count];
                if (def == null || !TryPlace(output, def, map, faction, facing)) break;
            }
        }

        /// <summary>
        /// Lignes de couverture perpendiculaires à la direction de la base, devant le camp (plusieurs rangées avec
        /// des passages), plus un parapet devant chaque pièce d'artillerie / tourelle.
        /// </summary>
        private static void PlaceCover(List<Blueprint_Build> output, Map map, Faction faction, IntVec3 center, IntVec3 target, float x, List<Blueprint_Build> pieces)
        {
            EnsureCatalog();
            List<ThingDef> defs = coverDefs.Where(d => TechOk(d, faction)).ToList();
            ThingDef cover = defs.Count > 0 ? defs.RandomElement() : ThingDefOf.Sandbags;   // une seule sorte par camp
            ThingDef stuff = cover == ThingDefOf.Sandbags ? ThingDefOf.Cloth : StuffFor(cover);

            Vector2 dir = new Vector2(target.x - center.x, target.z - center.z);
            dir = dir.sqrMagnitude < 1f ? Vector2.right : dir.normalized;
            Vector2 perp = new Vector2(-dir.y, dir.x);
            var done = new HashSet<IntVec3>();

            void Line(Vector2 mid, int half, int gapEvery)
            {
                for (int t = -half; t <= half; t++)
                {
                    if (gapEvery > 0 && t != 0 && t % gapEvery == 0) continue; // passages pour sortir
                    Vector2 p = mid + perp * t;
                    var c = new IntVec3(Mathf.RoundToInt(p.x), 0, Mathf.RoundToInt(p.y));
                    if (!done.Add(c) || !c.InBounds(map)) continue;
                    if (!(bool)canPlaceAt.Invoke(null, new object[] { c, Rot4.North, cover, map, stuff })) continue;
                    output.Add(GenConstruct.PlaceBlueprintForBuild(cover, c, map, Rot4.North, faction, stuff));
                }
            }

            var origin = new Vector2(center.x, center.z);
            // rangées du camp, face à la base
            int rows = Mathf.Clamp(1 + Mathf.RoundToInt(x / 2f), 1, 4);
            for (int r = 0; r < rows; r++)
                Line(origin + dir * (7f + 3f * r), 5 + r + Mathf.RoundToInt(x / 2f), 6);
            // parapet devant chaque pièce
            foreach (Blueprint_Build b in pieces)
            {
                ThingDef d = (ThingDef)b.def.entityDefToBuild;
                float s = Mathf.Max(d.size.x, d.size.z);
                Vector2 front = new Vector2(b.Position.x, b.Position.z) + dir * (s / 2f + 1.5f);
                Line(front, Mathf.CeilToInt(s / 2f) + 1, 0);
            }
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

    /// <summary>Les bâtisseurs du siège ont le niveau de construction requis par les tourelles des mods.</summary>
    [HarmonyPatch(typeof(LordToil_Siege), "SetAsBuilder")]
    public static class Patch_SiegeBuilderSkill
    {
        public static void Postfix(Pawn p)
        {
            if (!HRSettings.SiegeUpgrade || p?.skills == null) return;
            Patch_SiegeBlueprints.EnsureCatalog();
            int lvl = Patch_SiegeBlueprints.MaxConstructionSkill;
            if (lvl > 0) p.skills.GetSkill(SkillDefOf.Construction)?.EnsureMinLevelWithMargin(lvl);
        }
    }
}
