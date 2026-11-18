using System;
using System.Collections.Generic;
using System.Linq;
using HarmonyLib;
using RimWorld;
using UnityEngine;
using Verse;
using Verse.AI;
using Verse.AI.Group;

namespace HardenedRaids
{
    /// <summary>
    /// Repère du camp : centre = drapeau du siège, axe a vers la cible (direction cardinale la plus proche), axe l
    /// vers la droite. Tout le plan est aligné sur la grille : les sacs 1×2 / 2×2 se posent bout à bout.
    /// </summary>
    public readonly struct CampFrame
    {
        public readonly IntVec3 center;
        public readonly Rot4 facing;

        public CampFrame(IntVec3 center, Rot4 facing) { this.center = center; this.facing = facing; }

        public IntVec3 At(int a, int l) => center + facing.FacingCell * a + facing.RighthandCell * l;

        public int A(IntVec3 c) { IntVec3 d = c - center, f = facing.FacingCell; return d.x * f.x + d.z * f.z; }

        public int L(IntVec3 c) { IntVec3 d = c - center, r = facing.RighthandCell; return d.x * r.x + d.z * r.z; }

        public static Rot4 Toward(IntVec3 from, IntVec3 to) =>
            (to - from).LengthHorizontalSquared < 1 ? Rot4.North : Rot4.FromAngleFlat((to - from).AngleFlat);
    }

    /// <summary>
    /// Plan des camps de siège améliorés (réglage SiegeUpgrade, et camps des équipes d'appui), dans un rayon de
    /// <see cref="Radius"/> cases du drapeau (les bâtisseurs ne travaillent pas au-delà de 16, duty Build vanilla) :
    ///  - parc d'artillerie à l'arrière (a de −9 à −4), pièces explosives espacées d'au moins 4,5 cases ;
    ///  - arc de tourelles à l'avant (a de +5 à +8) ;
    ///  - une allée d'une case dans l'axe (l = 0), jamais bouchée ; cases de servant réservées ;
    ///  - couverture (1 ou 2 rangées de sacs à +11 / +13, parapets devant chaque pièce) posée seulement quand toutes
    ///    les pièces sont construites (<see cref="Patch_SiegeCoverLater"/>), dans un budget de travail.
    /// Rien n'est sauvegardé : l'état du camp (pièces, plans, chantiers, couverture) est relu sur la carte.
    /// </summary>
    public static class SiegeCamp
    {
        public const float Radius = 15f;
        public const int AlleyFrom = -10, AlleyTo = 14;
        private const float ExplosiveSpacing = 4.5f;
        private const float MannedShare = 0.55f;          // pièces servies ≤ 55 % des raiders du groupe
        private const float WorkPerBuilderDay = 15000f;   // travail de construction réalisable par bâtisseur en un jour
        private const int CoverTimeout = 30000;           // chantier bloqué : couverture quand même après une demi-journée

        public static bool Enhanced(Lord lord) =>
            lord?.Map != null && (HRSettings.SiegeUpgrade || MapComponent_HostileTurretSupply.For(lord.Map)?.IsSupportLord(lord) == true);

        public static bool Explosive(ThingDef d) => d?.comps != null && d.comps.Any(c => c is CompProperties_Explosive);

        public static bool IsPiece(ThingDef d) => d?.building?.turretGunDef != null;

        private static bool IsHostilePlanToIgnore(Thing t, Faction f) =>
            f != null && (t is Blueprint || t is Frame) && t.Faction != null && t.Faction != f && t.Faction.HostileTo(f);

        public static bool CanPlace(ThingDef def, IntVec3 at, Rot4 rot, Map map, Faction f, ThingDef stuff) =>
            GenConstruct.CanPlaceBlueprintAt_NewTemp(def, at, rot, map, false, null, null, stuff, ignoreEdgeArea: true,
                ignoreInteractionSpots: true, ignoreClearableFreeBuildings: true, skipBlockingThing: t => IsHostilePlanToIgnore(t, f)).Accepted;

        /// <summary>Comme SiegeBlueprintPlacer.ClearHostilePlansAt, avec la faction en paramètre (pas le champ statique vanilla).</summary>
        private static void ClearHostilePlans(ThingDef def, IntVec3 at, Rot4 rot, ThingDef stuff, Map map, Faction f)
        {
            foreach (IntVec3 c in GenAdj.OccupiedRect(at, rot, def.Size))
            {
                List<Thing> list = c.GetThingList(map);
                for (int i = list.Count - 1; i >= 0; i--)
                {
                    Thing t = list[i];
                    if (IsHostilePlanToIgnore(t, f) && !GenConstruct.CanPlaceBlueprintOver(def, t.def, stuff, t.Stuff)) t.Destroy(DestroyMode.Cancel);
                }
            }
        }

        private static Blueprint_Build Place(ThingDef def, IntVec3 at, Rot4 rot, ThingDef stuff, Map map, Faction f)
        {
            ClearHostilePlans(def, at, rot, stuff, map, f);
            return GenConstruct.PlaceBlueprintForBuild(def, at, map, rot, f, stuff);
        }

        private static bool Reachable(Map map, IntVec3 from, IntVec3 c, PathEndMode mode) =>
            map.reachability.CanReach(from, c, mode, TraverseParms.For(TraverseMode.PassDoors, Danger.Deadly));

        // ------------------------------------------------------------ pièces (LordToil_Siege.Init)
        private sealed class PiecePlanner
        {
            private readonly Map map;
            private readonly Faction faction;
            public readonly CampFrame fr;
            private readonly HashSet<IntVec3> bodies = new HashSet<IntVec3>(), margins = new HashSet<IntVec3>(), gunners = new HashSet<IntVec3>();
            private readonly List<Vector3> explosives = new List<Vector3>();
            public readonly List<Blueprint_Build> placed = new List<Blueprint_Build>();
            public int manned;

            public PiecePlanner(Map map, Faction faction, CampFrame fr) { this.map = map; this.faction = faction; this.fr = fr; }

            private bool Alley(IntVec3 c) { int a = fr.A(c); return fr.L(c) == 0 && a >= AlleyFrom && a <= AlleyTo; }

            /// <summary>Plan déjà posé (artillerie vanilla) : ni recouvert, ni son poste bouché.</summary>
            public void Block(Blueprint_Build b)
            {
                if (b?.def.entityDefToBuild is not ThingDef d) return;
                CellRect body = GenAdj.OccupiedRect(b.Position, b.Rotation, d.size);
                foreach (IntVec3 c in body) bodies.Add(c);
                foreach (IntVec3 c in body.ExpandedBy(1)) margins.Add(c);
                IntVec3 ic = Patch_SiegeBlueprints.GunnerCell(b, map);
                if (ic.IsValid) gunners.Add(ic);
                if (Explosive(d)) explosives.Add(GenThing.TrueCenter(b.Position, b.Rotation, d.size, 0f));
            }

            private bool Valid(ThingDef def, IntVec3 at, Rot4 rot, ThingDef stuff, out IntVec3 ic)
            {
                ic = IntVec3.Invalid;
                CellRect body = GenAdj.OccupiedRect(at, rot, def.size);
                foreach (IntVec3 c in body)
                    if (!c.InBounds(map) || !c.InHorDistOf(fr.center, Radius) || margins.Contains(c) || gunners.Contains(c) || Alley(c) || c.Roofed(map))
                        return false;
                if (def.hasInteractionCell)
                {
                    ic = ThingUtility.InteractionCellWhenAt(def, at, rot, map);
                    if (!ic.InBounds(map) || !ic.InHorDistOf(fr.center, Radius) || bodies.Contains(ic) || gunners.Contains(ic) || Alley(ic)) return false;
                    if (!ic.Standable(map) && !(ic.Walkable(map) && ic.GetEdifice(map) == null)) return false;
                }
                if (Explosive(def))
                {
                    Vector3 me = GenThing.TrueCenter(at, rot, def.size, 0f);
                    foreach (Vector3 o in explosives) if ((o - me).MagnitudeHorizontal() < ExplosiveSpacing) return false;
                }
                if (!CanPlace(def, at, rot, map, faction, stuff)) return false;
                // atteignable depuis le drapeau : la case du servant, sinon une case au contact de la pièce
                return ic.IsValid ? Reachable(map, fr.center, ic, PathEndMode.OnCell) : Reachable(map, fr.center, at, PathEndMode.Touch);
            }

            /// <summary>Pose la pièce à la meilleure place libre de sa zone (score croissant), false si aucune.</summary>
            public bool TryPlace(ThingDef def, int aMin, int aMax, Func<int, int, float> score)
            {
                Rot4 rot = def.rotatable ? fr.facing : Rot4.North;
                ThingDef stuff = Patch_SiegeBlueprints.StuffFor(def);
                var cands = new List<(float s, int a, int l)>();
                for (int a = aMin; a <= aMax; a++)
                    for (int l = -13; l <= 13; l++) cands.Add((score(a, l), a, l));
                foreach ((float _, int a, int l) in cands.OrderBy(c => c.s).ThenBy(c => c.a).ThenBy(c => c.l))
                {
                    IntVec3 at = fr.At(a, l);
                    if (!at.InBounds(map) || !Valid(def, at, rot, stuff, out IntVec3 ic)) continue;
                    Blueprint_Build b = Place(def, at, rot, stuff, map, faction);
                    placed.Add(b);
                    Block(b);
                    if (ic.IsValid) gunners.Add(ic);
                    if (def.HasComp<CompMannable>()) manned++;
                    return true;
                }
                return false;
            }
        }

        private static float ParkScore(int a, int l) => Mathf.Abs(l) + 1.2f * Mathf.Abs(a + 5);
        private static float ArcScore(int a, int l) => 0.6f * Mathf.Abs(l) + 2f * Mathf.Abs(a - (8f - Mathf.Abs(l) / 4f));

        /// <summary>
        /// Pièces du camp (artillerie au parc, tourelles en arc), dimensionnées sur le palier et l'effectif du groupe.
        /// Les plans d'artillerie vanilla sont remplacés quand le plan contient une pièce au tag « Artillery »,
        /// gardés sinon. Retourne les plans à rendre à LordToil_Siege.Init (qui livre leurs matériaux).
        /// </summary>
        public static List<Blueprint_Build> PlacePieces(Map map, Faction faction, IntVec3 center, float x, int raiders, bool support,
                                                        List<Blueprint_Build> vanillaArty, IntVec3 target)
        {
            var fr = new CampFrame(center, CampFrame.Toward(center, target));
            float dist = (target - center).LengthHorizontal;
            var plan = new PiecePlanner(map, faction, fr);
            foreach (Blueprint_Build b in vanillaArty) plan.Block(b);

            // dimensionnement
            int r = Mathf.Max(1, raiders);
            float grow = x >= 6f ? Mathf.Clamp(r / 30f, 1f, 2.5f) : 1f;   // au-delà du palier 6 : le camp suit l'effectif
            int nArt = Mathf.RoundToInt(Mathf.Min(HRSettings.MaxSiegeArtillery, 1 + Mathf.RoundToInt(x * 1.2f)) * grow);
            int nTur = HRSettings.MaxSiegeTurrets <= 0 ? 0
                : Mathf.RoundToInt(Mathf.Clamp(Mathf.RoundToInt((x - 1.5f) * 1.5f), 1, HRSettings.MaxSiegeTurrets) * grow);
            if (support) { nArt = Mathf.Min(nArt, 1); nTur = Mathf.Min(nTur, 1); }
            int manCap = Mathf.Max(1, Mathf.FloorToInt(r * MannedShare));

            // artillerie : à portée de la cible (portée minimale + 5), sinon tout le catalogue
            List<ThingDef> arty = Patch_SiegeBlueprints.ArtilleryDefsFor(faction);
            List<ThingDef> inRange = arty.Where(d => Patch_SiegeBlueprints.MinRange(d) + 5f <= dist).ToList();
            if (inRange.Count > 0) arty = inRange;
            List<ThingDef> kit = Patch_SiegeBlueprints.ChooseKit(Patch_SiegeBlueprints.TurretDefsFor(faction, 5), x);
            bool kitManned = kit.Any(d => d.HasComp<CompMannable>());
            int artCap = Mathf.Max(1, manCap - (nTur > 0 && kitManned ? 1 : 0));   // une place servie gardée pour une tourelle

            bool Manned(ThingDef d) => d.HasComp<CompMannable>();
            bool tagged = false;
            for (int i = 0; i < nArt && arty.Count > 0; i++)
            {
                List<ThingDef> pool = tagged ? arty : arty.Where(Patch_SiegeBlueprints.HasArtilleryTag).ToList();
                if (pool.Count == 0) pool = arty;
                pool = pool.Where(d => !Manned(d) || plan.manned < artCap).ToList();
                if (pool.Count == 0) break;
                ThingDef def = Patch_SiegeBlueprints.PickWeighted(pool, x);
                // pièce lourde sans place : les autres modèles, du plus petit au plus grand, avant d'arrêter
                ThingDef done = def != null && plan.TryPlace(def, -9, -4, ParkScore) ? def
                    : pool.Where(d => d != def).OrderBy(d => d.size.Area).FirstOrDefault(d => plan.TryPlace(d, -9, -4, ParkScore));
                if (done == null) break;
                tagged |= Patch_SiegeBlueprints.HasArtilleryTag(done);
            }
            int artPlaced = plan.placed.Count;
            for (int i = 0; i < nTur && kit.Count > 0; i++)
            {
                ThingDef def = kit[i % kit.Count];
                if (Manned(def) && plan.manned >= manCap) def = kit.FirstOrDefault(d => !Manned(d));
                if (def == null) break;
                // la lourde ne trouve pas de place : la légère (kit[0]) avant d'arrêter
                if (!plan.TryPlace(def, 5, 8, ArcScore) && (def == kit[0] || (Manned(kit[0]) && plan.manned >= manCap) || !plan.TryPlace(kit[0], 5, 8, ArcScore)))
                    break;
            }

            var result = new List<Blueprint_Build>(plan.placed);
            if (tagged)
            {
                foreach (Blueprint_Build b in vanillaArty) if (!b.Destroyed) b.Destroy(DestroyMode.Vanish);
            }
            else result.AddRange(vanillaArty.Where(b => !b.Destroyed));   // aucune pièce « Artillery » dans le plan : celles de vanilla restent

            HRLog.Msg($"siège {faction.Name} (palier {x:F1}, {raiders} raiders{(support ? ", appui" : "")}) vers {target} à {dist:F0} cases, face {fr.facing} : "
                      + $"{artPlaced} artillerie(s)/{nArt}, {plan.placed.Count - artPlaced} tourelle(s)/{nTur}, {plan.manned} servie(s) (max {manCap})"
                      + (tagged ? "" : $", {vanillaArty.Count} mortier(s) vanilla gardé(s)")
                      + $" — {string.Join(", ", result.GroupBy(b => b.def.entityDefToBuild.label).Select(g => g.Count() + "× " + g.Key))}");
            return result;
        }

        // ------------------------------------------------------------ état du camp (relu sur la carte)
        /// <summary>Plans, chantiers et bâtiments de la faction à moins de 20 cases du drapeau.</summary>
        public static List<Thing> CampThings(Map map, Faction f, IntVec3 center)
        {
            var list = new List<Thing>();
            foreach (ThingRequestGroup g in new[] { ThingRequestGroup.Blueprint, ThingRequestGroup.BuildingFrame, ThingRequestGroup.BuildingArtificial })
                foreach (Thing t in map.listerThings.ThingsInGroup(g))
                    if (t.Faction == f && t.Spawned && t.Position.InHorDistOf(center, 20f)) list.Add(t);
            return list;
        }

        /// <summary>Emprise de ce que construit / est le Thing.</summary>
        public static CellRect Body(Thing t) => GenAdj.OccupiedRect(t.Position, t.Rotation, (Built(t) ?? t.def).size);

        /// <summary>Ce que construit / est le Thing (plan, chantier ou bâtiment).</summary>
        public static ThingDef Built(Thing t) => t is Blueprint || t is Frame ? t.def.entityDefToBuild as ThingDef : t.def;

        /// <summary>Repère d'un camp existant : déduit de la position des pièces (artillerie derrière, tourelles devant).</summary>
        public static CampFrame FrameOf(Map map, IntVec3 center, IEnumerable<Thing> pieces)
        {
            var art = new List<IntVec3>();
            var tur = new List<IntVec3>();
            foreach (Thing t in pieces) (Patch_SiegeBlueprints.IsArtillery(Built(t)) ? art : tur).Add(t.Position);
            Vector2 Mean(List<IntVec3> l) => new Vector2((float)l.Average(c => c.x), (float)l.Average(c => c.z));
            var c0 = new Vector2(center.x, center.z);
            Vector2 v = art.Count > 0 && tur.Count > 0 ? Mean(tur) - Mean(art)
                : art.Count > 0 ? c0 - Mean(art)
                : tur.Count > 0 ? Mean(tur) - c0 : Vector2.zero;
            if (v.sqrMagnitude < 1f)
                return new CampFrame(center, CampFrame.Toward(center, Patch_SiegeBlueprints.ThreatTarget(map, center)));
            return new CampFrame(center, Rot4.FromAngleFlat(new Vector3(v.x, 0f, v.y).AngleFlat()));
        }

        private static bool IsCoverDef(ThingDef d) =>
            d != null && (d == ThingDefOf.Sandbags || d == ThingDefOf.Barricade || Patch_SiegeBlueprints.IsCatalogCover(d));

        // ------------------------------------------------------------ couverture, après les pièces (K)
        /// <summary>
        /// Toutes les pièces du camp construites (ou chantier bloqué depuis une demi-journée) et aucune couverture
        /// du camp sur la carte : parapets devant les pièces, puis rangées de sacs, dans le budget de travail des
        /// bâtisseurs ; matériaux livrés par capsule ; plans ajoutés à ceux du lord (annulés par Cleanup vanilla).
        /// </summary>
        public static void TryPlaceCover(LordToil_Siege toil)
        {
            Lord lord = toil.lord;
            Map map = lord.Map;
            Faction f = lord.faction;
            if (map == null || f == null || toil.data is not LordToilData_Siege data) return;
            IntVec3 center = data.siegeCenter;
            List<Thing> things = CampThings(map, f, center);
            bool pending = things.Any(t => (t is Blueprint || t is Frame) && IsPiece(Built(t)));
            if (pending && lord.ticksInToil < CoverTimeout) return;
            if (things.Any(t => IsCoverDef(Built(t)))) return;
            // pièces construites, et celles d'un chantier bloqué (plans / chantiers) passé le délai
            List<Thing> pieces = things.Where(t => IsPiece(Built(t))).ToList();
            if (!pieces.Any(t => t is Building)) return;

            CampFrame fr = FrameOf(map, center, pieces);
            bool support = MapComponent_HostileTurretSupply.For(map)?.IsSupportLord(lord) == true;
            float x = SiegeTier.For(lord.LordJob);
            if (x < 0f) x = Tiers.TierX(f.def, data.blueprintPoints * 4f);
            int builders = Mathf.Max(1, Mathf.RoundToInt(lord.ownedPawns.Count * data.desiredBuilderFraction));
            float budget = builders * WorkPerBuilderDay, spent = 0f;

            // une seule sorte de sacs et de parapet par camp, la même à chaque passage (graine du lord)
            Rand.PushState(Gen.HashCombineInt(lord.loadID, center.GetHashCode()));
            List<ThingDef> covers = Patch_SiegeBlueprints.CoverDefsFor(f), walls = Patch_SiegeBlueprints.ParapetDefsFor(f);
            ThingDef cover = covers.Count > 0 ? covers.RandomElement() : ThingDefOf.Sandbags;
            ThingDef wall = walls.Count > 0 ? walls.RandomElement() : null;
            Rand.PopState();
            ThingDef coverStuff = cover == ThingDefOf.Sandbags ? ThingDefOf.Cloth : Patch_SiegeBlueprints.StuffFor(cover);
            ThingDef wallStuff = wall != null ? Patch_SiegeBlueprints.StuffFor(wall) : null;

            // cases interdites : pièces, postes de servant, allée
            var no = new HashSet<IntVec3>();
            foreach (Thing t in things)
            {
                ThingDef d = Built(t);
                if (d == null) continue;
                if (IsPiece(d)) foreach (IntVec3 c in Body(t)) no.Add(c);
                if (d.hasInteractionCell) no.Add(ThingUtility.InteractionCellWhenAt(d, t.Position, t.Rotation, map));
            }
            for (int a = AlleyFrom; a <= AlleyTo; a++) no.Add(fr.At(a, 0));
            foreach (IntVec3 c in SupplyZone(map, f, center, pieces)) no.Add(c);

            var placed = new List<Blueprint_Build>();
            bool full = false;
            bool Try(ThingDef def, ThingDef stuff, int a, int l)
            {
                if (full) return false;
                IntVec3 at = fr.At(a, l);
                Rot4 rot = def.rotatable ? fr.facing : Rot4.North;   // 1×2 : grand côté le long de la ligne
                CellRect body = GenAdj.OccupiedRect(at, rot, def.size);
                foreach (IntVec3 c in body)
                    if (!c.InBounds(map) || !c.InHorDistOf(center, Radius) || no.Contains(c)) return false;
                if (!CanPlace(def, at, rot, map, f, stuff) || !Reachable(map, center, at, PathEndMode.Touch)) return false;
                float work = def.GetStatValueAbstract(StatDefOf.WorkToBuild, stuff);
                if (spent + work > budget) { full = true; return false; }
                spent += work;
                foreach (IntVec3 c in body) no.Add(c);
                placed.Add(Place(def, at, rot, stuff, map, f));
                return true;
            }

            // 1. parapets : une ligne devant chaque pièce (mur de sacs / embrasure devant l'artillerie)
            foreach (Thing p in pieces.OrderBy(p => fr.A(p.Position)))
            {
                CellRect body = Body(p);
                int front = body.Cells.Max(fr.A) + 1;
                int lMin = body.Cells.Min(fr.L) - 1, lMax = body.Cells.Max(fr.L) + 1;
                bool arty = wall != null && Patch_SiegeBlueprints.IsArtillery(Built(p));
                for (int l = lMin; l <= lMax; l++) Try(arty ? wall : cover, arty ? wallStuff : coverStuff, front, l);
            }
            // 2. rangées de sacs à +11 (et +13 au palier 4+), passages dans l'axe et toutes les 6 cases
            if (!support)
            {
                // passages gardés libres même par un sac 2×2 posé à côté
                for (int a = 11; a <= 14; a++)
                    for (int l = -12; l <= 12; l += 6) no.Add(fr.At(a, l));
                int rows = x >= 4f ? 2 : 1;
                for (int row = 0; row < rows; row++)
                    for (int l = -13; l <= 13; l++)
                        if (l % 6 != 0) Try(cover, coverStuff, 11 + 2 * row, l);
            }
            if (placed.Count == 0) return;

            data.blueprints.AddRange(placed);
            DropMaterials(map, SupplySpot(map, f, center, pieces), placed);
            HRLog.Msg($"siège {f.Name} : pièces prêtes, couverture posée — {placed.Count} plans ({string.Join(", ", placed.GroupBy(b => b.def.entityDefToBuild.label).Select(g => g.Count() + "× " + g.Key))}), "
                      + $"travail {spent:F0}/{budget:F0} ({builders} bâtisseur(s)){(pending ? ", chantier des pièces bloqué" : "")}.");
        }

        /// <summary>Matériaux exacts des plans, par capsules de deux piles (comme LordToil_Siege.Init).</summary>
        private static void DropMaterials(Map map, IntVec3 at, List<Blueprint_Build> plans)
        {
            var total = new Dictionary<ThingDef, int>();
            foreach (Blueprint_Build b in plans)
                foreach (ThingDefCountClass c in b.TotalMaterialCost())
                    total[c.thingDef] = (total.TryGetValue(c.thingDef, out int n) ? n : 0) + c.count;
            var stacks = new List<Thing>();
            foreach (KeyValuePair<ThingDef, int> kv in total)
            {
                int left = kv.Value;
                while (left > 0)
                {
                    Thing t = ThingMaker.MakeThing(kv.Key);
                    t.stackCount = Mathf.Min(left, kv.Key.stackLimit);
                    left -= t.stackCount;
                    stacks.Add(t);
                }
            }
            var groups = new List<List<Thing>>();
            for (int i = 0; i < stacks.Count; i += 2) groups.Add(stacks.Skip(i).Take(2).ToList());
            if (groups.Count > 0) DropPodUtility.DropThingGroupsNear(at, map, groups);
        }

        // ------------------------------------------------------------ ravitaillement
        /// <summary>Point de livraison des vivres, munitions et matériaux (le drapeau pour l'instant).</summary>
        public static IntVec3 SupplySpot(Map map, Faction f, IntVec3 center, List<Thing> pieces) => center;

        /// <summary>Cases gardées libres autour du point de livraison.</summary>
        public static IEnumerable<IntVec3> SupplyZone(Map map, Faction f, IntVec3 center, List<Thing> pieces) => Enumerable.Empty<IntVec3>();
    }

    /// <summary>Couverture du camp posée après les pièces : vérifié toutes les 500 ticks (décalé du recalcul des duties vanilla).</summary>
    [HarmonyPatch(typeof(LordToil_Siege), nameof(LordToil_Siege.LordToilTick))]
    public static class Patch_SiegeCoverLater
    {
        public static void Postfix(LordToil_Siege __instance)
        {
            Lord lord = __instance.lord;
            if (lord == null || lord.ticksInToil < 500 || lord.ticksInToil % 500 != 250 || !SiegeCamp.Enhanced(lord)) return;
            try { SiegeCamp.TryPlaceCover(__instance); }
            catch (Exception e) { Log.WarningOnce("[Hardened Raids] siege cover: " + e, 0x48524356); }
        }
    }
}
