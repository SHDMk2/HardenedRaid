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
    /// faible, non sauvegardée) et lu pendant LordToil_Siege.Init, où les plans sont posés.
    /// </summary>
    public static class SiegeTier
    {
        private class Box { public float x; }
        private static readonly System.Runtime.CompilerServices.ConditionalWeakTable<Verse.AI.Group.LordJob, Box> byJob =
            new System.Runtime.CompilerServices.ConditionalWeakTable<Verse.AI.Group.LordJob, Box>();

        /// <summary>Palier du siège dont on pose le camp, -1 sinon.</summary>
        public static float Current = -1f;

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
        public static void Prefix(LordToil_Siege __instance) => SiegeTier.Current = SiegeTier.For(__instance.lord?.LordJob);
        public static void Finalizer() => SiegeTier.Current = -1f;
    }

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

            // palier forcé du raid (SiegeTier) ; à défaut (camp posé après un rechargement…), estimé depuis
            // blueprintPoints = points du raid × 0.2-0.3 (RaidStrategyWorker_Siege / équipe d'appui)
            float x = SiegeTier.Current >= 0f ? SiegeTier.Current : Tiers.TierX(faction.def, blueprintPoints * 4f);
            IntVec3 target = ThreatTarget(map, center);
            Rot4 facing = Rot4.FromAngleFlat((target - center).AngleFlat);
            var extras = new List<Blueprint_Build>();
            try
            {
                if (x >= 2f)
                {
                    PlaceArtillery(extras, pieces, map, faction, x, pieces.Count(b => IsArtillery((ThingDef)b.def.entityDefToBuild)), facing);
                    PlaceTurrets(extras, pieces, map, faction, x, facing);
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
                      + $"{turretDefs.Count} tourelles ({turretDefs.Count(d => d.HasComp(typeof(CompMannable)))} servies : {string.Join(", ", turretDefs.Select(d => d.defName))}), "
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

        private static ThingDef StuffFor(ThingDef d) => d.MadeFromStuff ? (GenStuff.AllowedStuffsFor(d).Contains(ThingDefOf.Steel) ? ThingDefOf.Steel : GenStuff.DefaultStuffFor(d)) : null;

        /// <summary>Plus le palier est haut, plus on favorise le matériel cher (gros calibres).</summary>
        public static ThingDef PickWeighted(List<ThingDef> defs, float x)
        {
            float exp = Mathf.Lerp(0f, 1.5f, Mathf.InverseLerp(2f, 6f, x));
            return defs.TryRandomElementByWeight(d => Mathf.Pow(Mathf.Max(50f, d.BaseMarketValue), exp), out ThingDef r) ? r : null;
        }

        private static bool TryPlace(List<Blueprint_Build> output, List<Blueprint_Build> others, ThingDef def, Map map, Faction faction, Rot4 rot)
        {
            if (!def.rotatable) rot = Rot4.North;
            // FindArtySpot vanilla ignore les cases de servant : on retire au sort tant que la case du servant est
            // bouchée ou que la pièce recouvre celle d'une autre
            IntVec3 spot = IntVec3.Invalid;
            for (int i = 0; i < 12 && !spot.IsValid; i++)
            {
                var c = (IntVec3)findArtySpot.Invoke(null, new object[] { def, rot, map });
                if (!c.IsValid) return false;
                if (InteractionOk(def, c, rot, map, others.Concat(output))) spot = c;
            }
            if (!spot.IsValid) return false;
            ThingDef stuff = StuffFor(def);
            clearHostilePlans?.Invoke(null, new object[] { spot, rot, def, stuff ?? ThingDefOf.Steel, map });
            output.Add(GenConstruct.PlaceBlueprintForBuild(def, spot, map, rot, faction, stuff));
            return true;
        }

        private static CellRect Body(Blueprint_Build b) => GenAdj.OccupiedRect(b.Position, b.Rotation, ((ThingDef)b.def.entityDefToBuild).size);

        /// <summary>Case du servant d'un plan de pièce (Invalid si la pièce n'en a pas).</summary>
        public static IntVec3 GunnerCell(Blueprint_Build b, Map map) =>
            b.def.entityDefToBuild is ThingDef d && d.hasInteractionCell ? ThingUtility.InteractionCellWhenAt(d, b.Position, b.Rotation, map) : IntVec3.Invalid;

        /// <summary>Case du servant praticable, hors des autres pièces ; la pièce ne recouvre pas le poste d'une autre.</summary>
        private static bool InteractionOk(ThingDef def, IntVec3 at, Rot4 rot, Map map, IEnumerable<Blueprint_Build> others)
        {
            CellRect mine = GenAdj.OccupiedRect(at, rot, def.size);
            IntVec3 ic = def.hasInteractionCell ? ThingUtility.InteractionCellWhenAt(def, at, rot, map) : IntVec3.Invalid;
            if (ic.IsValid)
            {
                if (!ic.InBounds(map) || !ic.Walkable(map)) return false;
                Building e = ic.GetEdifice(map);
                if (e != null && !mine.Contains(ic) && e.def.passability != Traversability.Standable) return false;
            }
            foreach (Blueprint_Build o in others)
            {
                if (o == null || o.Destroyed || o.def.entityDefToBuild is not ThingDef) continue;
                if (ic.IsValid && Body(o).Contains(ic)) return false;
                IntVec3 oic = GunnerCell(o, map);
                if (oic.IsValid && mine.Contains(oic)) return false;
            }
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
        private static void PlaceArtillery(List<Blueprint_Build> output, List<Blueprint_Build> others, Map map, Faction faction, float x, int already, Rot4 facing)
        {
            List<ThingDef> defs = ArtilleryDefsFor(faction);
            if (defs.Count == 0) return;
            int target = Mathf.Min(HRSettings.MaxSiegeArtillery, 1 + Mathf.RoundToInt(x * 1.2f));
            // NoArtillery vanilla : sans aucune pièce au tag de bâtiment « Artillery », l'assaut part dès la fin du chantier
            bool tagged = others.Concat(output).Any(b => HasArtilleryTag(b.def.entityDefToBuild as ThingDef));
            for (int i = already; i < target; i++)
            {
                List<ThingDef> pool = tagged ? defs : defs.Where(HasArtilleryTag).ToList();
                if (pool.Count == 0) pool = defs;
                ThingDef def = PickWeighted(pool, x);
                if (def == null) break;
                if (!TryPlace(output, others, def, map, faction, facing))
                {
                    // pièce trop grosse pour la place restante : la plus petite du catalogue avant d'abandonner
                    ThingDef small = pool.Where(d => d != def).OrderBy(d => d.size.Area).FirstOrDefault();
                    if (small == null || small.size.Area >= def.size.Area || !TryPlace(output, others, small, map, faction, facing)) break;
                    def = small;
                }
                tagged |= HasArtilleryTag(def);
            }
        }

        private static void PlaceTurrets(List<Blueprint_Build> output, List<Blueprint_Build> others, Map map, Faction faction, float x, Rot4 facing)
        {
            EnsureCatalog();
            List<ThingDef> kit = ChooseKit(turretDefs.Where(d => TechOk(d, faction)).ToList(), x);
            if (kit.Count == 0) return;
            HRLog.Msg($"siège : dotation {string.Join(" + ", kit.Select(d => d.label))}.");
            int n = Mathf.Clamp(Mathf.RoundToInt((x - 1.5f) * 1.5f), 1, HRSettings.MaxSiegeTurrets);
            for (int i = 0; i < n; i++)
            {
                ThingDef def = kit[i % kit.Count];
                if (def == null) break;
                if (TryPlace(output, others, def, map, faction, facing)) continue;
                // la lourde ne trouve pas de place : la légère (kit[0]) avant d'abandonner
                if (def == kit[0] || !TryPlace(output, others, kit[0], map, faction, facing)) break;
            }
        }

        /// <summary>
        /// Lignes de couverture perpendiculaires à la direction de la base, devant le camp (plusieurs rangées avec
        /// des passages), plus un parapet devant chaque pièce d'artillerie / tourelle.
        /// </summary>
        private static void PlaceCover(List<Blueprint_Build> output, Map map, Faction faction, IntVec3 center, IntVec3 target, float x, List<Blueprint_Build> pieces)
        {
            List<ThingDef> defs = CoverDefsFor(faction);
            ThingDef cover = defs.Count > 0 ? defs.RandomElement() : ThingDefOf.Sandbags;   // une seule sorte par camp
            ThingDef stuff = cover == ThingDefOf.Sandbags ? ThingDefOf.Cloth : StuffFor(cover);
            List<ThingDef> walls = ParapetDefsFor(faction);
            ThingDef wall = walls.Count > 0 ? walls.RandomElement() : null;   // parapet des pièces d'artillerie
            ThingDef wallStuff = wall != null ? StuffFor(wall) : null;

            Vector2 dir = new Vector2(target.x - center.x, target.z - center.z);
            dir = dir.sqrMagnitude < 1f ? Vector2.right : dir.normalized;
            Vector2 perp = new Vector2(-dir.y, dir.x);
            var done = new HashSet<IntVec3>();
            // CanPlaceBlueprintAt vanilla ignore les cases de servant : jamais de couverture sur le poste d'une pièce
            foreach (Blueprint_Build b in pieces)
            {
                IntVec3 ic = GunnerCell(b, map);
                if (ic.IsValid) done.Add(ic);
            }

            // sacs 1×2 : le grand côté le long de la ligne (rotation tournée vers la base)
            Rot4 rot = cover.rotatable ? Rot4.FromAngleFlat(new Vector3(dir.x, 0f, dir.y).AngleFlat()) : Rot4.North;

            void Line(Vector2 mid, int half, int gapEvery, ThingDef def, ThingDef st)
            {
                Rot4 r = def.rotatable ? rot : Rot4.North;
                for (int t = -half; t <= half; t++)
                {
                    if (gapEvery > 0 && t != 0 && t % gapEvery == 0) continue; // passages pour sortir
                    Vector2 p = mid + perp * t;
                    var c = new IntVec3(Mathf.RoundToInt(p.x), 0, Mathf.RoundToInt(p.y));
                    if (!c.InBounds(map)) continue;
                    CellRect body = GenAdj.OccupiedRect(c, r, def.size);
                    if (!body.InBounds(map) || body.Cells.Any(done.Contains)) continue;
                    if (!(bool)canPlaceAt.Invoke(null, new object[] { c, r, def, map, st })) continue;
                    foreach (IntVec3 bc in body) done.Add(bc);
                    output.Add(GenConstruct.PlaceBlueprintForBuild(def, c, map, r, faction, st));
                }
            }

            var origin = new Vector2(center.x, center.z);
            // rangées du camp, face à la base
            int rows = Mathf.Clamp(1 + Mathf.RoundToInt(x / 2f), 1, 4);
            for (int r = 0; r < rows; r++)
                Line(origin + dir * (7f + 3f * r), 5 + r + Mathf.RoundToInt(x / 2f), 6, cover, stuff);
            // parapet devant chaque pièce : mur de sacs / embrasure devant l'artillerie, sacs devant les tourelles
            foreach (Blueprint_Build b in pieces)
            {
                ThingDef d = (ThingDef)b.def.entityDefToBuild;
                float s = Mathf.Max(d.size.x, d.size.z);
                Vector2 front = new Vector2(b.Position.x, b.Position.z) + dir * (s / 2f + 1.5f);
                bool arty = wall != null && IsArtillery(d);
                Line(front, Mathf.CeilToInt(s / 2f) + 1, 0, arty ? wall : cover, arty ? wallStuff : stuff);
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
