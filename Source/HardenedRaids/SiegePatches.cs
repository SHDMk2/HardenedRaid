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
    /// Partie 2 — sièges : plus d'artillerie (y compris celle des mods : Fortifications, VFE Security, CE Armory...),
    /// tranchées/sandbags des mods en plus de la couverture vanilla, et tourelles autonomes autour du camp.
    /// Tout passe par SiegeBlueprintPlacer.PlaceBlueprints : vanilla livre automatiquement les matériaux
    /// et les obus (CE gère ses propres munitions d'artillerie).
    /// </summary>
    [HarmonyPatch(typeof(SiegeBlueprintPlacer), nameof(SiegeBlueprintPlacer.PlaceBlueprints))]
    public static class Patch_SiegeBlueprints
    {
        private static readonly MethodInfo findArtySpot = AccessTools.Method(typeof(SiegeBlueprintPlacer), "FindArtySpot");
        private static readonly MethodInfo findCoverRoot = AccessTools.Method(typeof(SiegeBlueprintPlacer), "FindCoverRoot");
        private static readonly MethodInfo makeCoverLine = AccessTools.Method(typeof(SiegeBlueprintPlacer), "MakeCoverLine");
        private static readonly MethodInfo clearHostilePlans = AccessTools.Method(typeof(SiegeBlueprintPlacer), "ClearHostilePlansAt");

        public static void Postfix(IntVec3 placeCenter, Map map, Faction placeFaction, float points, ref IEnumerable<Blueprint_Build> __result)
        {
            if (!HRSettings.SiegeUpgrade || placeFaction == null || findArtySpot == null) return;
            __result = Extend(__result, placeCenter, map, placeFaction, points);
        }

        private static IEnumerable<Blueprint_Build> Extend(IEnumerable<Blueprint_Build> vanilla, IntVec3 center, Map map, Faction faction, float blueprintPoints)
        {
            int vanillaArty = 0;
            foreach (Blueprint_Build b in vanilla)
            {
                if (IsArtillery(b.def.entityDefToBuild as ThingDef)) vanillaArty++;
                yield return b;
            }

            // blueprintPoints = points du raid × 0.2-0.3 (RaidStrategyWorker_Siege)
            float x = Tiers.TierX(faction.def, blueprintPoints * 4f);
            if (x < 2f) yield break;
            var extras = new List<Blueprint_Build>();
            try
            {
                PlaceCover(extras, map, faction, x);
                PlaceArtillery(extras, map, faction, x, vanillaArty);
                PlaceTurrets(extras, map, faction, x);
            }
            catch (Exception e)
            {
                Log.Warning("[Hardened Raids] camp de siège : " + e.Message);
            }
            HRLog.Msg($"siège {faction.Name} (palier {x:F1}) : +{extras.Count} plans ({string.Join(", ", extras.GroupBy(b => b.def.entityDefToBuild.label).Select(g => g.Count() + "× " + g.Key))})");
            foreach (Blueprint_Build b in extras) yield return b;
        }

        // ------------------------------------------------------------ catalogues
        private static bool IsArtillery(ThingDef d) => d?.building?.buildingTags != null && d.building.buildingTags.Contains("Artillery_BaseDestroyer");

        private static bool TechOk(ThingDef d, Faction f) => d.techLevel == TechLevel.Undefined || d.techLevel <= f.def.techLevel;

        private static bool NeedsPower(ThingDef d) =>
            d.comps != null && d.comps.Any(c => c is CompProperties_Power p && p.PowerConsumption > 0f);

        private static List<ThingDef> artilleryDefs, turretDefs, coverDefs;

        private static void EnsureCatalog()
        {
            if (artilleryDefs != null) return;
            artilleryDefs = DefDatabase<ThingDef>.AllDefs.Where(d => IsArtillery(d) && d.BuildableByPlayer && !NeedsPower(d)).ToList();
            // tourelles autonomes : tir automatique (non habitées), sans électricité ; munitions CE impossibles à fournir -> exclues
            turretDefs = DefDatabase<ThingDef>.AllDefs.Where(d =>
                d.building?.turretGunDef != null && !IsArtillery(d) && d.BuildableByPlayer && !NeedsPower(d)
                && !d.HasComp(typeof(CompMannable)) && d.size.x <= 3 && d.size.z <= 3
                && (d.building.turretGunDef.comps == null || !d.building.turretGunDef.comps.Any(c => c.GetType().Name == "CompProperties_AmmoUser"))
                && (d.building.buildingTags == null || !d.building.buildingTags.Any(t => t.StartsWith("Artillery")))).ToList();
            // tranchées / sacs de sable des mods (1×1)
            coverDefs = DefDatabase<ThingDef>.AllDefs.Where(d =>
                d.building != null && d.BuildableByPlayer && d.size == IntVec2.One && d.fillPercent >= 0.4f && d.passability != Traversability.Impassable
                && (d.defName.IndexOf("Trench", StringComparison.OrdinalIgnoreCase) >= 0 || d.defName.IndexOf("Sandbag", StringComparison.OrdinalIgnoreCase) >= 0)).ToList();
            HRLog.Msg($"siège : {artilleryDefs.Count} artilleries, {turretDefs.Count} tourelles autonomes, {coverDefs.Count} couvertures disponibles.");
        }

        private static ThingDef StuffFor(ThingDef d) => d.MadeFromStuff ? (GenStuff.AllowedStuffsFor(d).Contains(ThingDefOf.Steel) ? ThingDefOf.Steel : GenStuff.DefaultStuffFor(d)) : null;

        /// <summary>Plus le palier est haut, plus on favorise le matériel cher (gros calibres).</summary>
        private static ThingDef PickWeighted(List<ThingDef> defs, float x)
        {
            float exp = Mathf.Lerp(0f, 1.5f, Mathf.InverseLerp(2f, 6f, x));
            return defs.TryRandomElementByWeight(d => Mathf.Pow(Mathf.Max(50f, d.BaseMarketValue), exp), out ThingDef r) ? r : null;
        }

        private static bool TryPlace(List<Blueprint_Build> output, ThingDef def, Map map, Faction faction)
        {
            Rot4 rot = Rot4.Random;
            var spot = (IntVec3)findArtySpot.Invoke(null, new object[] { def, rot, map });
            if (!spot.IsValid) return false;
            ThingDef stuff = StuffFor(def);
            clearHostilePlans?.Invoke(null, new object[] { spot, rot, def, stuff ?? ThingDefOf.Steel, map });
            output.Add(GenConstruct.PlaceBlueprintForBuild(def, spot, map, rot, faction, stuff));
            return true;
        }

        // ------------------------------------------------------------ placement
        private static void PlaceArtillery(List<Blueprint_Build> output, Map map, Faction faction, float x, int already)
        {
            EnsureCatalog();
            List<ThingDef> defs = artilleryDefs.Where(d => TechOk(d, faction)).ToList();
            if (defs.Count == 0) return;
            int target = Mathf.Min(HRSettings.MaxSiegeArtillery, 1 + Mathf.RoundToInt(x * 1.2f));
            for (int i = already; i < target; i++)
            {
                ThingDef def = PickWeighted(defs, x);
                if (def == null || !TryPlace(output, def, map, faction)) break;
            }
        }

        private static void PlaceTurrets(List<Blueprint_Build> output, Map map, Faction faction, float x)
        {
            if (x < 3f) return;
            EnsureCatalog();
            List<ThingDef> defs = turretDefs.Where(d => TechOk(d, faction)).ToList();
            if (defs.Count == 0) return;
            int n = Mathf.Min(HRSettings.MaxSiegeTurrets, Mathf.RoundToInt((x - 2f) * 1.5f));
            for (int i = 0; i < n; i++)
            {
                ThingDef def = PickWeighted(defs, x);
                if (def == null || !TryPlace(output, def, map, faction)) break;
            }
        }

        private static void PlaceCover(List<Blueprint_Build> output, Map map, Faction faction, float x)
        {
            EnsureCatalog();
            if (findCoverRoot == null || makeCoverLine == null) return;
            List<ThingDef> defs = coverDefs.Where(d => TechOk(d, faction)).ToList();
            if (defs.Count == 0) defs = new List<ThingDef> { ThingDefOf.Sandbags };
            int lines = Mathf.RoundToInt(x * 1.5f);
            for (int i = 0; i < lines; i++)
            {
                ThingDef cover = defs.RandomElement();
                ThingDef stuff = cover == ThingDefOf.Sandbags ? ThingDefOf.Cloth : StuffFor(cover);
                var root = (IntVec3)findCoverRoot.Invoke(null, new object[] { map, cover, stuff });
                if (!root.IsValid) break;
                Rot4 dir = Rand.Bool ? Rot4.East : Rot4.North;
                var line = (IEnumerable<Blueprint_Build>)makeCoverLine.Invoke(null, new object[] { root, map, dir, Rand.RangeInclusive(3, 8), cover, stuff });
                output.AddRange(line.ToList());
            }
        }
    }
}
