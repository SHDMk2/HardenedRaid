using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using HarmonyLib;
using RimWorld;
using RimWorld.BaseGen;
using RimWorld.Planet;
using UnityEngine;
using Verse;
using Verse.AI.Group;

namespace HardenedRaids
{
    /// <summary>
    /// Lot uncap, décision D2 : défenseurs des colonies de faction hostiles.
    ///  - Points des défenseurs = part (25 %) de la menace actuelle du joueur, jamais moins que vanilla (1150-1600).
    ///  - Tourelles des mods (catalogue de siège) à la place des mini-tourelles vanilla de la défense périphérique, plus
    ///    quelques-unes selon le palier ; servants pris parmi les défenseurs (lord vanilla LordJob_ManTurrets).
    ///  - Véhicules Vehicle Framework garés autour de la colonie (1 pour 15 défenseurs), sans lord : les tourelles des
    ///    véhicules PNJ tirent seules (VF active le tir automatique des véhicules non joueurs) dès qu'un tireur est à bord.
    /// Rien n'est sauvegardé par le mod : seuls des objets vanilla / VF ordinaires sont créés pendant la génération de la carte.
    /// S'applique à toute carte générée dont le parent est une colonie (Settlement) d'une faction humaine non joueur :
    /// jamais aux colonies du joueur, abandonnées / détruites (autres types de WorldObject), ni à la génération du monde.
    /// D1 : une colonie neutre ou alliée attaquée aussi. Vanilla (caravane, capsules, navette, gravship) et VF génèrent
    /// sa carte AVANT SettlementUtility.AffectRelationsOnAttacked, et aucun chemin vanilla ne génère la carte d'une colonie
    /// étrangère sans l'attaquer : elle reçoit la défense d'une hostile (TreatAsHostile, testé par RaidGenerator.Applies).
    /// Aucun effet sur la diplomatie : rien ici ne touche aux relations, vanilla les change ensuite comme d'habitude.
    /// </summary>
    public static class SettlementDefense
    {
        /// <summary>Génération en cours d'une colonie hostile (null sinon). Un par MapGenerator.GenerateMap actif.</summary>
        public class Context
        {
            public Game game;
            public Map map;
            public Faction faction;
            public float points;       // points des défenseurs
            public float tierX;
            public int expectedDefenders;
            public int mannedLeft;     // tourelles servies encore autorisées
            public int seed;           // R3 : graine des défenseurs (aperçu = génération), tirée une fois, jamais sauvegardée
            public readonly HashSet<ThingDef> turretDefs = new HashSet<ThingDef>(); // modèles posés par nous
            public readonly List<Servant> servants = new List<Servant>();           // P12 : servants vanilla non générés
        }

        /// <summary>Servant de mortier vanilla (SymbolResolver_MannedMortar) remplacé par un défenseur après la génération.</summary>
        public struct Servant
        {
            public Lord lord;                       // LordJob_ManTurrets créé par vanilla, vide
            public IntVec3 cell;                    // case d'interaction du mortier
            public PawnGenerationRequest request;   // repli vanilla si aucun défenseur n'est disponible
        }

        // Un contexte par génération de carte active (pile) : chaque MapGenerator.GenerateMap part sans contexte, celui de la
        // génération englobante est remis à la fin (finalizer, même sur exception). Hors génération : jamais de contexte.
        // La pile est bornée par l'imbrication des générations ; tout (contextes, carte, Game) est lâché au changement de
        // partie (R4 : Reset, appelé par MemoryUtility.ClearAllMapsAndWorld et Game.FinalizeInit).
        private static Context current;
        private static readonly List<Context> outer = new List<Context>();
        private static int depth;
        private static Game stackGame;

        /// <summary>Contexte de la génération de carte en cours (null hors génération ou si ce n'est pas une colonie hostile).</summary>
        public static Context Current => current != null && current.game == Verse.Current.Game ? current : null;

        /// <summary>Début de MapGenerator.GenerateMap : le contexte englobant est mis de côté.</summary>
        public static void GenerationBegin()
        {
            if (stackGame != Verse.Current.Game) { outer.Clear(); depth = 0; current = null; stackGame = Verse.Current.Game; }
            outer.Add(current);
            current = null;
            depth++;
        }

        /// <summary>Changement / chargement de partie, retour au menu : aucune référence à l'ancienne Game ni à ses cartes.</summary>
        public static void Reset()
        {
            current = null;
            outer.Clear();
            depth = 0;
            stackGame = null;
        }

        /// <summary>Fin de MapGenerator.GenerateMap (toujours appelé) : contexte de cette génération jeté, l'englobant remis.</summary>
        public static void GenerationEnd()
        {
            if (depth <= 0 || outer.Count == 0) { current = null; depth = 0; outer.Clear(); return; }
            depth--;
            current = outer[outer.Count - 1];
            outer.RemoveAt(outer.Count - 1);
        }

        public static readonly FloatRange VanillaPoints = new FloatRange(1150f, 1600f);

        /// <summary>Faction humaine non joueur d'une colonie parent de la carte (hostile, ou neutre / alliée attaquée : D1) ; null sinon.</summary>
        public static Faction DefendedSettlementFaction(Map map)
        {
            if (map?.info?.parent is not Settlement s) return null;
            Faction f = s.Faction;
            if (f == null || f.IsPlayer || f.def == null || !f.def.humanlikeFaction) return null;
            return f;
        }

        /// <summary>
        /// D1 : groupe d'une faction neutre / alliée pendant la génération de la carte de sa colonie, que la génération
        /// Hardened doit traiter comme hostile (appelé par RaidGenerator.Applies).
        /// </summary>
        public static bool TreatAsHostile(PawnGroupMakerParms parms)
        {
            if (parms?.faction == null || parms.faction.HostileTo(Faction.OfPlayer)) return false;   // hostile : chemin normal
            if (!HRSettings.EnableTiers || GenContext.NonCombat > 0) return false;
            return IsDefenderGroup(parms);
        }

        /// <summary>
        /// Groupe de combat / de colonie de la faction dont la colonie est en génération (hostile ou non), paliers actifs
        /// ou non : sert aussi au plafond vanilla quand EnableTiers est décoché (DU1, VanillaWaveCap.OutOfRaidCap).
        /// </summary>
        public static bool IsDefenderGroup(PawnGroupMakerParms parms)
        {
            Context c = Current;
            if (c == null || parms?.faction == null || c.faction != parms.faction) return false;
            return parms.groupKind == PawnGroupKindDefOf.Combat || parms.groupKind == PawnGroupKindDefOf.Settlement;
        }

        /// <summary>
        /// Menace actuelle du joueur : la plus forte de ses cartes de colonie (sa base principale) ; sans colonie (nomade,
        /// gravship en vol), le monde entier comme vanilla pour les sites. Une carte plutôt que Find.World : le monde
        /// additionne la richesse de toutes les colonies et caravanes, ce qui n'est pas « la colonie du joueur ».
        /// </summary>
        public static float PlayerThreatNow()
        {
            float best = -1f;
            List<Map> maps = Find.Maps;
            for (int i = 0; i < maps.Count; i++)
                if (maps[i].IsPlayerHome) best = Mathf.Max(best, StorytellerUtility.DefaultThreatPointsNow(maps[i]));
            return best >= 0f ? best : StorytellerUtility.DefaultThreatPointsNow(Find.World);
        }

        /// <summary>Crée le contexte pour la carte en génération si c'est une colonie hostile ; renvoie les points des défenseurs.</summary>
        public static Context Begin(Map map, Faction rpFaction)
        {
            if (depth <= 0 || map == null) return null;   // hors génération de carte : vanilla, aucun contexte posé
            Context cur = Current;
            if (cur != null) return cur.map == map ? cur : null;
            Faction f = DefendedSettlementFaction(map);
            if (f == null || (rpFaction != null && rpFaction != f)) return null;
            float vanilla = VanillaPoints.RandomInRange;
            float points = vanilla;
            // D8 : indépendant de UncapPoints. Réglage décoché : la menace reste plafonnée à 10 000 comme vanilla
            // (StorytellerUtility.DefaultThreatPointsNow, MaxPoints), soit au plus 2 500 pts de défenseurs à 25 % ;
            // tourelles et véhicules ne dépendent que de leurs propres réglages.
            if (HRSettings.SettlementThreatShare > 0f)
                points = Mathf.Max(vanilla, Mathf.Min(PlayerThreatNow(), PointsPatches.MaxPoints()) * HRSettings.SettlementThreatShare);
            var ctx = new Context { game = Verse.Current.Game, map = map, faction = f, points = points, tierX = Tiers.TierX(f.def, points), seed = Rand.Int };
            ctx.expectedDefenders = EstimateDefenders(f, points);
            // servants : au plus 1 défenseur sur 10 (les autres restent mobiles)
            ctx.mannedLeft = ctx.expectedDefenders / 10;
            current = ctx;
            HRLog.Msg($"colonie {f.Name}{(f.HostileTo(Faction.OfPlayer) ? "" : " (neutre / alliée attaquée)")} : défenseurs {points:F0} pts (vanilla {vanilla:F0}, part {HRSettings.SettlementThreatShare:P0}), palier {ctx.tierX:F2}, ~{ctx.expectedDefenders} défenseurs prévus");
            return ctx;
        }

        /// <summary>
        /// Effectif prévu (servants, véhicules) : plan Hardened, sinon (paliers coupés, palier &lt; 1) l'estimation vanilla
        /// points / 120, bornée comme la génération : plafond par vague × <see cref="RaidPlan.SettlementCapFactor"/> (DU2),
        /// le même dans le plan Hardened, son repli vanilla et la génération vanilla sans paliers (VanillaWaveCap).
        /// </summary>
        private static int EstimateDefenders(Faction f, float points)
        {
            try
            {
                if (HRSettings.EnableTiers)
                {
                    RaidPlan plan = RaidPlan.Make(f.def, points, null, null, false, new System.Random(Rand.Int), allowWaves: false,
                                                  capFactor: RaidPlan.SettlementCapFactor);
                    if (plan != null) return plan.total;
                }
            }
            catch (Exception e) { HRLog.Msg("estimation des défenseurs : " + e.Message); }
            int cap = Mathf.Max(1, Tiers.WaveCapFor(f.def) * RaidPlan.SettlementCapFactor);
            return Mathf.Clamp(Mathf.RoundToInt(points / 120f), 1, cap);
        }

        /// <summary>
        /// R3 : graine des groupes de défenseurs de la colonie en génération (sur sa case), pour l'aperçu comme pour la
        /// génération ; null hors génération d'une colonie défendue.
        /// </summary>
        public static int? SeedFor(PawnGroupMakerParms parms)
        {
            if (!IsDefenderGroup(parms)) return null;
            Context c = Current;
            return c.map?.Tile == parms.tile ? c.seed : (int?)null;
        }

        /// <summary>Points à imposer quand le générateur n'a pas reçu de points explicites (null = ne rien changer).</summary>
        public static float? PointsFor(Map map, Faction rpFaction)
        {
            Context ctx = Begin(map, rpFaction);
            return ctx?.points;
        }
    }

    // ---------------------------------------------------------------- points des défenseurs : vanilla (GenStep_Settlement -> "settlement")
    [HarmonyPatch(typeof(RimWorld.BaseGen.SymbolResolver_Settlement), nameof(RimWorld.BaseGen.SymbolResolver_Settlement.Resolve))]
    public static class Patch_SettlementPoints
    {
        public static void Prefix(ref ResolveParams rp) => Apply(ref rp);

        public static void Apply(ref ResolveParams rp)
        {
            try
            {
                if (rp.settlementDontGeneratePawns == true || rp.pawnGroupMakerParams != null || rp.settlementPawnGroupPoints.HasValue) return;
                float? p = SettlementDefense.PointsFor(BaseGen.globalSettings.map, rp.faction);
                if (p.HasValue) rp.settlementPawnGroupPoints = p.Value;
            }
            catch (Exception e) { Log.Warning("[Hardened Raids] settlement defenders: " + e.Message); }
        }
    }

    // ---------------------------------------------------------------- R3 : choix du PawnGroupMaker sous la graine de la colonie
    /// <summary>
    /// GeneratePawnKindsExample (lits) et GeneratePawns tirent chacun leur PawnGroupMaker ; vanilla ne les accorde que
    /// avec parms.seed. Sans graine, celle de la colonie en génération (SettlementDefense.SeedFor) : même choix des deux côtés.
    /// </summary>
    [HarmonyPatch(typeof(PawnGroupMakerUtility), nameof(PawnGroupMakerUtility.TryGetRandomPawnGroupMaker))]
    public static class Patch_SettlementGroupMakerSeed
    {
        public static void Prefix(PawnGroupMakerParms parms, out bool __state)
        {
            __state = false;
            if (parms == null || parms.seed.HasValue) return;
            int? seed = SettlementDefense.SeedFor(parms);
            if (!seed.HasValue) return;
            Rand.PushState(seed.Value);
            __state = true;
        }

        public static void Finalizer(bool __state)
        {
            if (__state) Rand.PopState();
        }
    }

    // ---------------------------------------------------------------- P12 : servants des mortiers
    /// <summary>
    /// SymbolResolver_MannedMortar (défense périphérique) génère son servant à part : faction.RandomPawnKind(), hors
    /// PawnGroupKindWorker_Normal, donc hors palier et dotation Hardened. Pendant la génération d'une colonie défendue,
    /// ce pawn n'est pas généré : son lord vanilla (LordJob_ManTurrets) reçoit après la génération le défenseur Hardened le
    /// plus proche, comme les tourelles servies des mods. Sans défenseur disponible, le pawn vanilla est généré (repli).
    /// Aucun coût : quelques tests sur le symbole "pawn" de BaseGen, 0 à 3 mortiers par colonie.
    /// </summary>
    [HarmonyPatch(typeof(SymbolResolver_SinglePawn), nameof(SymbolResolver_SinglePawn.Resolve))]
    public static class Patch_SettlementMortarServant
    {
        public static bool Prefix(ResolveParams rp)
        {
            try
            {
                if (rp.singlePawnToSpawn != null || !rp.singlePawnGenerationRequest.HasValue || rp.singlePawnLord?.LordJob is not LordJob_ManTurrets) return true;
                if (!HRSettings.EnableTiers) return true;   // défenseurs vanilla : servant vanilla
                SettlementDefense.Context ctx = SettlementDefense.Current;
                if (ctx == null || ctx.map != BaseGen.globalSettings.map) return true;
                PawnGenerationRequest req = rp.singlePawnGenerationRequest.Value;
                if (req.Faction != ctx.faction || rp.rect.Area != 1) return true;
                ctx.servants.Add(new SettlementDefense.Servant { lord = rp.singlePawnLord, cell = rp.rect.CenterCell, request = req });
                return false;
            }
            catch (Exception e)
            {
                Log.Warning("[Hardened Raids] mortar crew: " + e.Message);
                return true;
            }
        }

        /// <summary>Après la génération : chaque mortier reçoit le défenseur le plus proche (pas le chef), avant les tourelles.</summary>
        public static void AfterGeneration(SettlementDefense.Context ctx, List<Pawn> defenders)
        {
            if (ctx.servants.Count == 0) return;
            Map map = ctx.map;
            int taken = 0, fallback = 0;
            foreach (SettlementDefense.Servant sv in ctx.servants)
            {
                try
                {
                    Lord lord = sv.lord != null && map.lordManager.lords.Contains(sv.lord) ? sv.lord
                        : LordMaker.MakeNewLord(ctx.faction, new LordJob_ManTurrets(), map);
                    // au moins un défenseur reste mobile
                    Pawn man = defenders.Count <= 1 ? null : defenders.Where(p => !p.kindDef.factionLeader)
                        .OrderBy(p => (p.Position - sv.cell).LengthHorizontalSquared).FirstOrDefault();
                    if (man != null)
                    {
                        defenders.Remove(man);
                        man.GetLord()?.RemovePawn(man);
                        lord.AddPawn(man);
                        taken++;
                        continue;
                    }
                    // repli : le servant vanilla, comme sans le mod
                    Pawn p = PawnGenerator.GeneratePawn(sv.request);
                    IntVec3 c = sv.cell.Standable(map) && sv.cell.GetFirstPawn(map) == null ? sv.cell : CellFinder.RandomClosewalkCellNear(sv.cell, map, 3);
                    GenSpawn.Spawn(p, c, map);
                    lord.AddPawn(p);
                    fallback++;
                }
                catch (Exception e) { Log.Warning("[Hardened Raids] mortar crew: " + e.Message); }
            }
            HRLog.Msg($"colonie {ctx.faction.Name} : {ctx.servants.Count} mortier(s), {taken} servi(s) par des défenseurs, {fallback} servant(s) vanilla");
        }
    }

    // ---------------------------------------------------------------- KCSG (Vanilla Expanded Framework) : même champ settlementPawnGroupPoints
    [HarmonyPatch]
    public static class Patch_SettlementPointsKCSG
    {
        public static bool Prepare() => TargetMethod() != null;

        public static MethodBase TargetMethod()
        {
            Type t = AccessTools.TypeByName("KCSG.SymbolResolver_Settlement");
            return t == null ? null : AccessTools.Method(t, "Resolve", new[] { typeof(ResolveParams) });
        }

        public static void Prefix(ref ResolveParams rp) => Patch_SettlementPoints.Apply(ref rp);
    }

    // ---------------------------------------------------------------- MapGenUtility.GeneratePawns (GenStep_SettlementPawnsLoot et mods)
    [HarmonyPatch(typeof(MapGenUtility), nameof(MapGenUtility.GeneratePawns))]
    public static class Patch_SettlementPointsMapGen
    {
        public static void Prefix(Map map, Faction faction, PawnGroupKindDef pawnGroupKindDef, PawnGroupMakerParms pawnGroupMakerParams, ref float? points)
        {
            try
            {
                if (pawnGroupMakerParams != null || points.HasValue) return;
                if (pawnGroupKindDef != null && pawnGroupKindDef != PawnGroupKindDefOf.Settlement) return;
                float? p = SettlementDefense.PointsFor(map, faction);
                if (p.HasValue) points = p.Value;
            }
            catch (Exception e) { Log.Warning("[Hardened Raids] settlement defenders: " + e.Message); }
        }
    }

    // ---------------------------------------------------------------- tourelles de la défense périphérique
    /// <summary>
    /// SymbolResolver_EdgeDefense (et sa version KCSG) empile des « edgeThing » mini-tourelle : on les reprend sur la pile
    /// de BaseGen (API publique Pop / Push) pour changer de modèle, et on en ajoute selon le palier.
    /// Dosage : même nombre que vanilla (≈ 1 pour 30 cases de périmètre, 4-5 pour une colonie standard), + 1 par palier
    /// au-dessus de T2, au plus le double de vanilla. Elles ne coûtent pas de points (comme les tourelles vanilla) ;
    /// les tourelles servies sont limitées à 1 pour 10 défenseurs prévus, sinon on prend une tourelle automatique.
    /// </summary>
    public static class SettlementTurrets
    {
        private static readonly SymbolResolver_EdgeThing tester = new SymbolResolver_EdgeThing();

        public static void Prefix(out int __state) => __state = BaseGen.symbolStack?.Count ?? 0;

        public static void Postfix(int __state)
        {
            SettlementDefense.Context ctx = SettlementDefense.Current;
            if (ctx == null || !HRSettings.SettlementTurrets || ctx.map != BaseGen.globalSettings.map) return;
            if (ctx.faction.def.techLevel < TechLevel.Industrial) return;   // vanilla : pas de tourelles en dessous
            try { Rework(ctx, __state); }
            catch (Exception e) { Log.Warning("[Hardened Raids] settlement turrets: " + e); }
        }

        private static bool Replaceable(ThingDef d) => d != null && (d == ThingDefOf.Turret_MiniTurret || d == ThingDefOf.Turret_AutoMiniTurret);

        private static bool Manned(ThingDef d) => d.HasComp(typeof(CompMannable));

        private static void Rework(SettlementDefense.Context ctx, int before)
        {
            SymbolStack stack = BaseGen.symbolStack;
            int n = stack.Count - before;
            if (n <= 0) return;
            var pushed = new List<SymbolStack.Element>(n);
            for (int i = 0; i < n; i++) pushed.Add(stack.Pop());
            pushed.Reverse();   // ordre d'empilement d'origine

            var log = new List<string>();
            try
            {
                List<int> idx = Enumerable.Range(0, pushed.Count).Where(i => pushed[i].symbol == "edgeThing" && Replaceable(pushed[i].resolveParams.singleThingDef)).ToList();
                if (idx.Count == 0) return;
                List<ThingDef> all = Patch_SiegeBlueprints.TurretDefsFor(ctx.faction, 3);
                List<ThingDef> kit = Patch_SiegeBlueprints.ChooseKit(all, ctx.tierX);
                if (kit.Count == 0) return;
                List<ThingDef> autos = all.Where(d => !Manned(d)).ToList();
                ThingDef autoPick = Patch_SiegeBlueprints.PickWeighted(autos, ctx.tierX);

                ThingDef Choose(int k, ResolveParams rp, ThingDef fallback)
                {
                    var cands = new List<ThingDef> { kit[k % kit.Count] };
                    if (kit.Count > 1) cands.Add(kit[(k + 1) % kit.Count]);
                    if (autoPick != null) cands.Add(autoPick);
                    foreach (ThingDef d in cands)
                    {
                        if (Manned(d) && ctx.mannedLeft <= 0) continue;
                        ResolveParams test = rp;
                        test.singleThingDef = d;
                        if (!tester.CanResolve(test)) continue;
                        if (Manned(d)) ctx.mannedLeft--;
                        ctx.turretDefs.Add(d);
                        return d;
                    }
                    return fallback;
                }

                int k0 = 0;
                foreach (int i in idx)
                {
                    SymbolStack.Element e = pushed[i];
                    ThingDef d = Choose(k0++, e.resolveParams, e.resolveParams.singleThingDef);
                    e.resolveParams.singleThingDef = d;
                    pushed[i] = e;
                    log.Add(d.defName);
                }
                // en plus selon le palier : +1 par palier au-dessus de T2, au plus autant que vanilla
                int extra = Mathf.Clamp(Mathf.FloorToInt(ctx.tierX - 2f), 0, idx.Count);
                ResolveParams baseRp = pushed[idx[0]].resolveParams;
                var extras = new List<ResolveParams>();
                for (int j = 0; j < extra; j++)
                {
                    ThingDef d = Choose(k0++, baseRp, null);
                    if (d == null) break;
                    ResolveParams rp = baseRp;
                    rp.singleThingDef = d;
                    extras.Add(rp);
                    log.Add("+" + d.defName);
                }
                foreach (SymbolStack.Element e in pushed) stack.Push(e.symbol, e.resolveParams);
                pushed.Clear();
                foreach (ResolveParams rp in extras) stack.Push("edgeThing", rp);
                HRLog.Msg($"colonie {ctx.faction.Name} : tourelles périphériques {string.Join(", ", log)} (servies restantes {ctx.mannedLeft})");
            }
            finally
            {
                // en cas d'erreur (ou rien à changer) : tout ce qui a été retiré est remis tel quel
                foreach (SymbolStack.Element e in pushed) stack.Push(e.symbol, e.resolveParams);
            }
        }

        /// <summary>Après la génération : plein des tourelles posées, servants pris parmi les défenseurs, munitions au sol.</summary>
        public static void AfterGeneration(SettlementDefense.Context ctx, List<Pawn> defenders)
        {
            if (ctx.turretDefs.Count == 0) return;
            Map map = ctx.map;
            var turrets = map.listerBuildings.allBuildingsNonColonist.OfType<Building_Turret>()
                .Where(t => t.Faction == ctx.faction && ctx.turretDefs.Contains(t.def)).ToList();
            int manned = 0;
            foreach (Building_Turret t in turrets)
            {
                CompRefuelable fuel = t.GetComp<CompRefuelable>();
                if (fuel != null && fuel.Fuel < fuel.Props.fuelCapacity) fuel.Refuel(fuel.Props.fuelCapacity - fuel.Fuel);
                if (t.GetComp<CompMannable>() == null) continue;
                Pawn man = defenders.Where(p => !p.kindDef.factionLeader).OrderBy(p => (p.Position - t.Position).LengthHorizontalSquared).FirstOrDefault();
                if (man == null) continue;
                defenders.Remove(man);
                man.GetLord()?.RemovePawn(man);
                LordMaker.MakeNewLord(ctx.faction, new LordJob_ManTurrets(), map, new[] { man });
                try { MapComponent_HostileTurretSupply.DropAmmoNear(t, 3); } catch (Exception e) { HRLog.Msg("munitions de tourelle : " + e.Message); }
                manned++;
            }
            HRLog.Msg($"colonie {ctx.faction.Name} : {turrets.Count} tourelle(s) des mods, {manned} servie(s)");
        }
    }

    [HarmonyPatch(typeof(SymbolResolver_EdgeDefense), nameof(SymbolResolver_EdgeDefense.Resolve))]
    public static class Patch_SettlementEdgeTurrets
    {
        public static void Prefix(out int __state) => SettlementTurrets.Prefix(out __state);
        public static void Postfix(int __state) => SettlementTurrets.Postfix(__state);
    }

    /// <summary>KCSG : SymbolResolver_EdgeDefenseCustomizable (tourelles choisies par le plan ; seules les mini-tourelles vanilla sont remplacées).</summary>
    [HarmonyPatch]
    public static class Patch_SettlementEdgeTurretsKCSG
    {
        public static bool Prepare() => TargetMethod() != null;

        public static MethodBase TargetMethod()
        {
            Type t = AccessTools.TypeByName("KCSG.SymbolResolver_EdgeDefenseCustomizable");
            return t == null ? null : AccessTools.Method(t, "Resolve", new[] { typeof(ResolveParams) });
        }

        public static void Prefix(out int __state) => SettlementTurrets.Prefix(out __state);
        public static void Postfix(int __state) => SettlementTurrets.Postfix(__state);
    }

    // ---------------------------------------------------------------- R4 : rien ne survit à la partie précédente
    [HarmonyPatch(typeof(Verse.Profile.MemoryUtility), nameof(Verse.Profile.MemoryUtility.ClearAllMapsAndWorld))]
    public static class Patch_SettlementDefenseClear
    {
        public static void Prefix() => SettlementDefense.Reset();
    }

    [HarmonyPatch(typeof(Game), nameof(Game.FinalizeInit))]
    public static class Patch_SettlementDefenseClearInit
    {
        public static void Postfix() => SettlementDefense.Reset();
    }

    // ---------------------------------------------------------------- fin de génération de la carte
    [HarmonyPatch(typeof(MapGenerator), nameof(MapGenerator.GenerateMap))]
    public static class Patch_SettlementAfterGen
    {
        public static void Prefix() => SettlementDefense.GenerationBegin();

        public static void Postfix(Map __result)
        {
            SettlementDefense.Context ctx = SettlementDefense.Current;
            if (ctx == null || __result == null || ctx.map != __result) return;
            try
            {
                // défenseurs : pawns humains de la faction, valides, dans un lord de défense de base
                List<Pawn> defenders = __result.mapPawns.SpawnedPawnsInFaction(ctx.faction)
                    .Where(p => p.RaceProps.Humanlike && !p.Downed && !p.Dead && IsDefendLord(p.GetLord()?.LordJob)).ToList();
                Patch_SettlementMortarServant.AfterGeneration(ctx, defenders);
                SettlementTurrets.AfterGeneration(ctx, defenders);
                if (HRSettings.SettlementVehicles && VehicleGate.Active && ctx.faction.def.techLevel >= TechLevel.Industrial
                    && ctx.tierX >= HRSettings.VehicleMinTier)
                    SettlementVehicles.Park(ctx, defenders);
            }
            catch (Exception e)
            {
                Log.Warning("[Hardened Raids] settlement defense: " + e);
            }
        }

        // LordJob_DefendBase vanilla, ou LordJob_DefendBaseNoEat de KCSG
        private static bool IsDefendLord(LordJob j) => j != null && (j is LordJob_DefendBase || j.GetType().Name.StartsWith("LordJob_DefendBase"));

        // toujours, même sur exception : contexte de cette génération jeté, celui d'une génération englobante remis
        public static void Finalizer() => SettlementDefense.GenerationEnd();
    }
}
