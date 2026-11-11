using System;
using System.Collections.Generic;
using System.Linq;
using RimWorld;
using UnityEngine;
using Verse;
using Verse.AI;
using Verse.AI.Group;

namespace HardenedRaids
{
    /// <summary>
    /// Convoi ennemi : tous les véhicules d'un raid dans un même lord.
    /// Chaque véhicule roule (moteur démarré) vers sa place sur une ligne d'arrêt tracée hors de portée des
    /// tourelles du joueur. Dès qu'IL arrive (ou est bloqué, ou prend un coup), il débarque :
    ///  - camion : tout le monde descend, il reste garé ; raid "smart" : une tourelle est déchargée sur la ligne de tir ;
    ///  - blindé : les passagers descendent, pilote et tireurs restent, puis il part seul à l'assaut (IA de combat VF).
    /// Les fantassins débarqués gardent le lord d'infanterie du raid.
    /// Aucun type de VF ici : tout passe par VehicleCompat (chargé seulement si VF est actif).
    /// </summary>
    public class LordJob_HRConvoy : LordJob
    {
        public Faction faction;
        public Lord infantryLord;
        public List<Pawn> dismount = new List<Pawn>();
        public List<Pawn> unloaded = new List<Pawn>();
        public bool deployTurrets;
        public IntVec3 target = IntVec3.Invalid;
        public IntVec3 stagingCenter = IntVec3.Invalid;
        public float tierX;
        public int turretsPlaced;
        public List<ThingDef> kit = new List<ThingDef>();
        // retraite
        public bool retreating;
        public int retreatStart;
        public Lord retreatLord;
        public List<Pawn> leaving = new List<Pawn>();

        public const string MemoAllUnloaded = "HRConvoyArrived";
        public const string MemoRetreat = "HRConvoyRetreat";

        public LordJob_HRConvoy() { }

        public LordJob_HRConvoy(Faction faction, Lord infantryLord, List<Pawn> dismount, bool deployTurrets, float tierX)
        {
            this.faction = faction;
            this.infantryLord = infantryLord;
            this.dismount = dismount ?? new List<Pawn>();
            this.deployTurrets = deployTurrets;
            this.tierX = tierX;
        }

        public override bool AddFleeToil => false;

        public override StateGraph CreateGraph()
        {
            var g = new StateGraph();
            var approach = new LordToil_HRConvoyApproach();
            var assault = new LordToil_HRConvoyAssault();
            var retreat = new LordToil_HRConvoyRetreat();
            g.AddToil(approach);
            g.AddToil(assault);
            g.AddToil(retreat);
            var r1 = new Transition(approach, retreat);
            r1.AddTrigger(new Trigger_Memo(MemoRetreat));
            g.AddTransition(r1);
            var r2 = new Transition(assault, retreat);
            r2.AddTrigger(new Trigger_Memo(MemoRetreat));
            g.AddTransition(r2);
            var t = new Transition(approach, assault);
            t.AddTrigger(new Trigger_Memo(MemoAllUnloaded));
            t.AddTrigger(new Trigger_TicksPassed(6000));   // filet : 2,4 h de jeu au plus avant de tout débarquer
            g.AddTransition(t);
            return g;
        }

        /// <summary>Lord qui reçoit les fantassins débarqués (celui du raid, ou un assaut neuf s'il a disparu ; en retraite : le rembarquement).</summary>
        public Lord InfantryLord(Map map)
        {
            if (retreating) return RetreatLord(map);
            if (infantryLord != null && map.lordManager.lords.Contains(infantryLord)) return infantryLord;
            infantryLord = LordMaker.MakeNewLord(faction, new LordJob_AssaultColony(faction, canKidnap: true, canTimeoutOrFlee: true), map);
            return infantryLord;
        }

        public Lord RetreatLord(Map map, List<Pawn> starting = null)
        {
            if (retreatLord == null || !map.lordManager.lords.Contains(retreatLord))
                retreatLord = LordMaker.MakeNewLord(faction, new LordJob_HRBoard(faction, lord), map, starting);
            else if (starting != null)
                foreach (Pawn p in starting) retreatLord.AddPawn(p);
            return retreatLord;
        }

        // ------------------------------------------------------------ fin du raid
        public static bool ExitLike(Lord l)
        {
            string n = l?.CurLordToil?.GetType().Name ?? "";
            return n.Contains("Exit") || n.Contains("Flee") || n.Contains("Kidnap") || n.Contains("Steal");
        }

        /// <summary>Victoire ou défaite : le lord d'infanterie du raid fuit / quitte la carte, ou n'existe plus.</summary>
        public bool RaidOver(Map map)
        {
            if (infantryLord == null) return false;
            if (!map.lordManager.lords.Contains(infantryLord) || infantryLord.ownedPawns.Count == 0) return true;
            return ExitLike(infantryLord);
        }

        public void StartRetreat(Map map)
        {
            retreating = true;
            retreatStart = Find.TickManager.TicksGame;
            foreach (Pawn v in lord.ownedPawns)
            {
                holding.Remove(v);
                VehicleCompat.Undeploy(v);
            }
            int n = Collect(map);
            HRLog.Msg($"convoi {faction.Name} : retraite, {n} fantassin(s) rappelé(s) vers les véhicules.");
        }

        /// <summary>Les fantassins du raid qui partent (et les servants des tourelles) sont rappelés vers les véhicules.</summary>
        private int Collect(Map map)
        {
            List<Pawn> vehicles = lord.ownedPawns.Where(v => v.Spawned && !leaving.Contains(v) && VehicleCompat.CanMove(v)).ToList();
            if (vehicles.Count == 0) return 0;
            var take = new List<Pawn>();
            var packs = new Dictionary<Pawn, Building>();
            foreach (Pawn p in map.mapPawns.SpawnedPawnsInFaction(faction).ToList())
            {
                if (VehicleGate.IsVehicle(p) || !p.RaceProps.Humanlike || p.Downed || p.InMentalState || p.IsPrisoner) continue;
                if (p.carryTracker?.CarriedThing is Pawn) continue;   // les ravisseurs partent à pied avec leur prise
                Lord l = p.GetLord();
                if (l != null && (l == retreatLord || l.LordJob is LordJob_HRConvoy || l.LordJob is LordJob_HRBoard)) continue;
                bool crew = l?.LordJob is LordJob_HRTurretCrew;
                if (!(l == null || l == infantryLord || crew || ExitLike(l))) continue;
                if (!vehicles.Any(v => v.Position.InHorDistOf(p.Position, 100f))) continue;
                if (crew)
                {
                    Building b = l.ownedBuildings.Where(x => x.Spawned).OrderBy(x => x.Position.DistanceToSquared(p.Position)).FirstOrDefault();
                    if (b != null) packs[p] = b;
                }
                take.Add(p);
            }
            if (take.Count == 0) return 0;
            foreach (Pawn p in take) p.GetLord()?.Notify_PawnLost(p, PawnLostCondition.ForcedToJoinOtherLord);
            Lord rl = RetreatLord(map, take);
            if (rl.LordJob is LordJob_HRBoard bj)
            {
                foreach (var kv in packs) bj.pack[kv.Key] = kv.Value;
                foreach (Pawn p in take) { bj.Assign(p); p.jobs?.EndCurrentJob(JobCondition.InterruptForced); }
            }
            return take.Count;
        }

        /// <summary>
        /// Retraite : les camions attendent leurs passagers puis partent ; les blindés tiennent la position (tir auto)
        /// jusqu'au départ du dernier camion, puis partent à leur tour. Un véhicule immobilisé est abandonné.
        /// </summary>
        public void TickRetreat(Map map, int now)
        {
            int el = now - retreatStart;
            if (el % 300 < 60 && el < 4800) Collect(map);
            var bj = retreatLord?.LordJob as LordJob_HRBoard;
            bool trucksGone = lord.ownedPawns.All(v => !v.Spawned || leaving.Contains(v) || VehicleCompat.HasTurrets(v) || !VehicleCompat.CanMove(v));
            foreach (Pawn v in lord.ownedPawns.ToList())
            {
                if (!v.Spawned) continue;
                VehicleCompat.Ready(v);
                if (leaving.Contains(v)) { VehicleCompat.ExitMap(v); continue; }
                if (!VehicleCompat.CanMove(v) && !VehicleCompat.IsDeployed(v) && !VehicleCompat.IsDeploying(v)) { Abandon(v, map); continue; }
                int pending = bj?.Pending(v) ?? 0;
                bool full = VehicleCompat.FreeSeats(v) == 0;
                bool truck = !VehicleCompat.HasTurrets(v);
                bool go = truck ? (el >= 600 && (pending == 0 || full)) || el > 6000
                                : (el >= 900 && trucksGone && pending == 0) || el > 7500;
                if (go)
                {
                    if (VehicleCompat.ExitMap(v))
                    {
                        leaving.Add(v);
                        HRLog.Msg($"convoi : {v.LabelShort} quitte la carte avec {VehicleCompat.PawnsAboard(v).Count()} homme(s) à bord.");
                    }
                }
                else TickFireSupport(v, now);
            }
        }

        private readonly HashSet<Pawn> abandoned = new HashSet<Pawn>();

        private void Abandon(Pawn v, Map map)
        {
            if (!abandoned.Add(v)) return;
            foreach (Pawn p in VehicleCompat.PawnsAboard(v).ToList())
            {
                VehicleCompat.Disembark(v, p);
                if (p.Spawned && p.GetLord() != retreatLord)
                {
                    p.GetLord()?.Notify_PawnLost(p, PawnLostCondition.ForcedToJoinOtherLord);
                    RetreatLord(map, new List<Pawn> { p });
                }
            }
            HRLog.Msg($"convoi : {v.LabelShort} immobilisé, abandonné.");
        }

        // ------------------------------------------------------------ débarquement d'un véhicule
        public void Unload(Pawn v, Map map)
        {
            if (unloaded.Contains(v)) return;
            unloaded.Add(v);
            if (!v.Spawned) return;
            bool truck = !VehicleCompat.HasTurrets(v);
            Lord inf = InfantryLord(map);
            var down = new List<Pawn>();
            foreach (Pawn p in VehicleCompat.PawnsAboard(v).ToList())
            {
                if (!truck && !dismount.Contains(p)) continue;   // blindé : pilote et tireurs restent
                VehicleCompat.Disembark(v, p);
                if (!p.Spawned) continue;
                if (p.GetLord() == null) inf.AddPawn(p);
                down.Add(p);
            }
            if (truck && deployTurrets && down.Count > 0 && turretsPlaced < TruckTurrets.MaxFor(tierX))
            {
                ThingDef def = NextTurretDef();
                if (def != null) turretsPlaced += TruckTurrets.Deploy(map, faction, v, down, target, stagingCenter, def);
            }
            // le blindé repart tout de suite (ou le camion se gare) : on force une nouvelle décision
            v.mindState.duty = DutyFor(v);
            v.jobs?.EndCurrentJob(JobCondition.InterruptForced);
            HRLog.Msg($"convoi : {v.LabelShort} a débarqué {down.Count} fantassin(s){(truck ? "" : ", part à l'assaut")}.");
        }

        /// <summary>Dotation uniforme du raid : une pièce légère et une lourde, en alternance (la lourde d'abord à haut palier).</summary>
        private ThingDef NextTurretDef()
        {
            kit ??= new List<ThingDef>();
            kit.RemoveAll(d => d == null);
            if (kit.Count == 0) kit = TruckTurrets.ChooseKit(faction, tierX);
            if (kit.Count == 0) return null;
            bool heavy = kit.Count > 1 && turretsPlaced % 2 == (tierX >= 4f ? 0 : 1);
            return heavy ? kit[1] : kit[0];
        }

        // ------------------------------------------------------------ appui-feu : s'arrêter (se déployer), pivoter, tirer
        private readonly Dictionary<Pawn, int> lastFire = new Dictionary<Pawn, int>();
        private readonly HashSet<Pawn> holding = new HashSet<Pawn>();

        public void TickFireSupport(Pawn v, int now)
        {
            if (!v.Spawned || (!unloaded.Contains(v) && !retreating) || !VehicleCompat.IsFireSupport(v)) return;
            if (VehicleCompat.IsDeploying(v)) return;
            bool deploy = VehicleCompat.NeedsDeploy(v);
            bool inPosition = deploy ? VehicleCompat.IsDeployed(v) : holding.Contains(v);
            if (inPosition)
            {
                if (VehicleCompat.ManualFire(v)) { lastFire[v] = now; return; }
                if (VehicleCompat.TargetInRange(v, out IntVec3 c) && VehicleCompat.FaceTarget(v, c) && VehicleCompat.ManualFire(v)) { lastFire[v] = now; return; }
                if (!lastFire.TryGetValue(v, out int t)) lastFire[v] = t = now;
                if (now - t > 1800)  // 30 s sans cible : on replie et on avance
                {
                    if (deploy) VehicleCompat.Undeploy(v);
                    holding.Remove(v);
                    v.mindState.duty = DutyFor(v);
                    v.jobs?.EndCurrentJob(JobCondition.InterruptForced);
                }
                return;
            }
            if (VehicleCompat.TargetInRange(v, out IntVec3 cell))
            {
                v.mindState.duty = VehicleCompat.TravelDuty(v.Position);
                if (deploy) VehicleCompat.StartDeploy(v);
                else
                {
                    holding.Add(v);
                    VehicleCompat.Stop(v);
                    VehicleCompat.FaceTarget(v, cell);
                }
                lastFire[v] = now;
                HRLog.Msg($"convoi : {v.LabelShort} {(deploy ? "se déploie" : "s'arrête")} pour tirer sur {cell}.");
            }
        }

        public PawnDuty DutyFor(Pawn v)
        {
            if (retreating || holding.Contains(v) || VehicleCompat.IsDeployed(v) || VehicleCompat.IsDeploying(v))
                return VehicleCompat.TravelDuty(v.Position);   // retraite : on tient la position / pièce en batterie
            return VehicleCompat.HasTurrets(v) && VehicleCompat.CanMove(v)
                ? VehicleCompat.AssaultDuty()
                : VehicleCompat.TravelDuty(v.Position);   // camion garé
        }

        public override void ExposeData()
        {
            Scribe_References.Look(ref faction, "faction");
            Scribe_References.Look(ref infantryLord, "infantryLord");
            Scribe_Collections.Look(ref dismount, "dismount", LookMode.Reference);
            Scribe_Collections.Look(ref unloaded, "unloaded", LookMode.Reference);
            Scribe_Values.Look(ref deployTurrets, "deployTurrets");
            Scribe_Values.Look(ref target, "target", IntVec3.Invalid);
            Scribe_Values.Look(ref stagingCenter, "stagingCenter", IntVec3.Invalid);
            Scribe_Values.Look(ref tierX, "tierX");
            Scribe_Values.Look(ref turretsPlaced, "turretsPlaced");
            Scribe_Collections.Look(ref kit, "kit", LookMode.Def);
            Scribe_Values.Look(ref retreating, "retreating");
            Scribe_Values.Look(ref retreatStart, "retreatStart");
            Scribe_References.Look(ref retreatLord, "retreatLord");
            Scribe_Collections.Look(ref leaving, "leaving", LookMode.Reference);
            dismount ??= new List<Pawn>();
            unloaded ??= new List<Pawn>();
            kit ??= new List<ThingDef>();
            leaving ??= new List<Pawn>();
            if (Scribe.mode == LoadSaveMode.PostLoadInit)
            {
                dismount.RemoveAll(p => p == null);
                unloaded.RemoveAll(p => p == null);
                leaving.RemoveAll(p => p == null);
                kit.RemoveAll(d => d == null);
            }
        }
    }

    public class LordToil_HRConvoyApproach : LordToil
    {
        private readonly Dictionary<Pawn, IntVec3> slots = new Dictionary<Pawn, IntVec3>();
        private readonly Dictionary<Pawn, (IntVec3 pos, int tick)> lastMove = new Dictionary<Pawn, (IntVec3, int)>();

        private LordJob_HRConvoy Job => (LordJob_HRConvoy)lord.LordJob;

        public override bool ForceHighStoryDanger => true;

        public override void Init()
        {
            base.Init();
            ComputeStaging();
        }

        /// <summary>
        /// Ligne d'arrêt : en partant de la base vers le point d'arrivée, le premier point hors de portée de toutes
        /// les tourelles du joueur (+5 cases), au moins 22 cases de la base.
        /// </summary>
        private static float SafeDistance(Map map, IntVec3 target, Vector2 dir, float maxDist)
        {
            var turrets = map.listerBuildings.allBuildingsColonist.OfType<Building_Turret>()
                .Select(t => (pos: t.Position, r: (t.AttackVerb?.verbProps?.range ?? 0f) + 5f)).Where(t => t.r > 6f && t.r < 90f).ToList();
            for (float d = 22f; d < maxDist; d += 2f)
            {
                var p = new IntVec3(Mathf.RoundToInt(target.x + dir.x * d), 0, Mathf.RoundToInt(target.z + dir.y * d));
                if (turrets.All(t => !p.InHorDistOf(t.pos, t.r))) return d;
            }
            return maxDist;
        }

        private void ComputeStaging()
        {
            Map map = Map;
            List<Pawn> vehicles = lord.ownedPawns.Where(p => p.Spawned).ToList();
            if (vehicles.Count == 0) return;
            var from = new IntVec3((int)vehicles.Average(v => v.Position.x), 0, (int)vehicles.Average(v => v.Position.z));
            if (!Job.target.IsValid) Job.target = Patch_SiegeBlueprints.ThreatTarget(map, from);
            if (!Job.stagingCenter.IsValid)
            {
                Vector2 dir = new Vector2(from.x - Job.target.x, from.z - Job.target.z);
                float dist = dir.magnitude;
                dir = dist < 1f ? Vector2.up : dir / dist;
                float stop = SafeDistance(map, Job.target, dir, Mathf.Max(22f, dist * 0.8f));
                Job.stagingCenter = new IntVec3(Mathf.RoundToInt(Job.target.x + dir.x * stop), 0, Mathf.RoundToInt(Job.target.z + dir.y * stop)).ClampInsideMap(map);
                HRLog.Msg($"convoi : ligne d'arrêt à {stop:F0} cases de {Job.target} ({Job.stagingCenter}).");
            }
            slots.Clear();
            Vector2 axis = new Vector2(Job.target.x - Job.stagingCenter.x, Job.target.z - Job.stagingCenter.z);
            axis = axis.sqrMagnitude < 1f ? Vector2.up : axis.normalized;
            Vector2 perp = new Vector2(-axis.y, axis.x);
            var taken = new HashSet<IntVec3>();
            int i = 0;
            // camions sur la ligne, en largeur (8 de front) ; les blindés n'y font qu'une halte de débarquement
            foreach (Pawn v in vehicles.OrderBy(v => VehicleCompat.HasTurrets(v) ? 1 : 0).ThenBy(v => v.thingIDNumber))
            {
                int rank = i / 8, file = i % 8;
                float lateral = (file % 2 == 0 ? 1 : -1) * ((file + 1) / 2) * 7f;
                Vector2 p = new Vector2(Job.stagingCenter.x, Job.stagingCenter.z) + perp * lateral - axis * (rank * 9f);
                var want = new IntVec3(Mathf.RoundToInt(p.x), 0, Mathf.RoundToInt(p.y)).ClampInsideMap(map);
                slots[v] = VehicleCompat.FindStandCell(v, map, want, Job.target, taken) ?? want;
                i++;
            }
        }

        public override void UpdateAllDuties()
        {
            if (slots.Count == 0) ComputeStaging();
            foreach (Pawn v in lord.ownedPawns)
            {
                VehicleCompat.Ready(v);
                if (Job.unloaded.Contains(v)) { v.mindState.duty = Job.DutyFor(v); continue; }
                if (!slots.TryGetValue(v, out IntVec3 c)) c = v.Position;
                v.mindState.duty = VehicleCompat.TravelDuty(c);
            }
        }

        public override void LordToilTick()
        {
            if (Find.TickManager.TicksGame % 60 != 0) return;
            int now = Find.TickManager.TicksGame;
            Map map = Map;
            foreach (Pawn v in lord.ownedPawns.ToList())
            {
                if (!v.Spawned || Job.unloaded.Contains(v)) continue;
                VehicleCompat.Ready(v);
                IntVec3 goal = slots.TryGetValue(v, out IntVec3 c) ? c : v.Position;
                bool arrived = v.Position.InHorDistOf(goal, 5f);
                bool hit = v.mindState.lastHarmTick > 0 && now - v.mindState.lastHarmTick < 120;
                bool stuck = false;
                if (!lastMove.TryGetValue(v, out var lm) || lm.pos.DistanceToSquared(v.Position) > 2) lastMove[v] = (v.Position, now);
                else stuck = lord.ticksInToil > 1200 && now - lm.tick > 600;   // 10 s sans avancer (après le démarrage)
                if (arrived || hit || stuck || !VehicleCompat.CanMove(v)) Job.Unload(v, map);
            }
            foreach (Pawn v in lord.ownedPawns.ToList()) Job.TickFireSupport(v, now);
            if (Job.RaidOver(map)) { lord.ReceiveMemo(LordJob_HRConvoy.MemoRetreat); return; }
            if (lord.ownedPawns.All(v => Job.unloaded.Contains(v) || !v.Spawned)) lord.ReceiveMemo(LordJob_HRConvoy.MemoAllUnloaded);
        }
    }

    public class LordToil_HRConvoyAssault : LordToil
    {
        private LordJob_HRConvoy Job => (LordJob_HRConvoy)lord.LordJob;

        public override bool ForceHighStoryDanger => true;

        public override void Init()
        {
            base.Init();
            try
            {
                foreach (Pawn v in lord.ownedPawns.ToList()) Job.Unload(v, Map);
            }
            catch (Exception e) { Log.Warning("[Hardened Raids] déploiement du convoi : " + e); }
        }

        public override void UpdateAllDuties()
        {
            foreach (Pawn v in lord.ownedPawns)
            {
                VehicleCompat.Ready(v);
                v.mindState.duty = Job.DutyFor(v);
            }
        }

        public override void LordToilTick()
        {
            int now = Find.TickManager.TicksGame;
            if (now % 60 != 0) return;
            foreach (Pawn v in lord.ownedPawns.ToList())
            {
                if (now % 240 == 0) VehicleCompat.Ready(v);
                Job.TickFireSupport(v, now);
            }
            if (Job.RaidOver(Map)) lord.ReceiveMemo(LordJob_HRConvoy.MemoRetreat);
        }
    }

    /// <summary>Fin du raid : rembarquement, départ des camions, puis des blindés qui couvraient.</summary>
    public class LordToil_HRConvoyRetreat : LordToil
    {
        private LordJob_HRConvoy Job => (LordJob_HRConvoy)lord.LordJob;

        public override void Init()
        {
            base.Init();
            try { Job.StartRetreat(Map); }
            catch (Exception e) { Log.Warning("[Hardened Raids] retraite du convoi : " + e); }
        }

        public override void UpdateAllDuties()
        {
            foreach (Pawn v in lord.ownedPawns)
            {
                VehicleCompat.Ready(v);
                v.mindState.duty = Job.DutyFor(v);
            }
        }

        public override void LordToilTick()
        {
            int now = Find.TickManager.TicksGame;
            if (now % 60 != 0) return;
            try { Job.TickRetreat(Map, now); }
            catch (Exception e) { Log.WarningOnce("[Hardened Raids] retraite du convoi : " + e, 0x48525254); }
        }
    }

    // ---------------------------------------------------------------- tourelles déchargées des camions
    public static class TruckTurrets
    {
        public static int MaxFor(float x) => Mathf.Min(HRSettings.MaxTruckTurrets, Mathf.Max(1, Mathf.RoundToInt(x - 1f)));

        /// <summary>
        /// Tir direct seulement (pas d'obusier / mortier / lance-roquettes multiple) : des pièces qui couvrent les
        /// camions. Flak et autocanons inclus (portée longue sous CE).
        /// </summary>
        public static bool DirectFire(ThingDef turret)
        {
            ThingDef gun = turret.building?.turretGunDef;
            VerbProperties v = gun?.Verbs?.FirstOrDefault();
            if (v == null || !v.requireLineOfSight || v.range > 140f || v.minRange > 10f) return false;
            if (v.defaultProjectile?.projectile?.flyOverhead ?? false) return false;
            string vc = v.verbClass?.Name ?? "";
            if (vc.Contains("Mortar") || vc.Contains("Artillery")) return false;
            string n = turret.defName;
            return !(n.Contains("Mortar") || n.Contains("Artillery") || n.Contains("Katyusha") || n.Contains("Spotter") || n.Contains("Howitzer"));
        }

        /// <summary>Dotation du raid : une pièce légère (mitrailleuse) + une lourde (flak, autocanon, antichar).</summary>
        public static List<ThingDef> ChooseKit(Faction faction, float x)
        {
            List<ThingDef> defs = Patch_SiegeBlueprints.TurretDefsFor(faction, 4).Where(DirectFire).ToList();
            List<ThingDef> manned = defs.Where(d => d.HasComp(typeof(CompMannable))).ToList();
            if (manned.Count > 0) defs = manned;
            List<ThingDef> kit = Patch_SiegeBlueprints.ChooseKit(defs, x);
            HRLog.Msg(kit.Count == 0
                ? "tourelles de camion : aucune tourelle à tir direct disponible pour cette faction."
                : $"tourelles de camion : dotation {string.Join(" + ", kit.Select(d => d.label))} (parmi {defs.Count}).");
            return kit;
        }

        /// <summary>Décharge une tourelle pour ce camion, sur la ligne de tir devant le convoi. Retourne 1 si posée.</summary>
        public static int Deploy(Map map, Faction faction, Pawn truck, List<Pawn> infantry, IntVec3 target, IntVec3 stagingCenter, ThingDef def)
        {
            Vector2 axis = new Vector2(target.x - truck.Position.x, target.z - truck.Position.z);
            axis = axis.sqrMagnitude < 1f ? Vector2.up : axis.normalized;
            // devant le camion ET devant la ligne d'arrêt : rien entre la tourelle et la base
            float ahead = Mathf.Max(truck.def.size.x, truck.def.size.z) / 2f + Mathf.Max(def.size.x, def.size.z) / 2f + 3f;
            var front = new Vector2(truck.Position.x, truck.Position.z) + axis * ahead;
            if (stagingCenter.IsValid)
            {
                float lineDepth = Vector2.Dot(new Vector2(stagingCenter.x, stagingCenter.z) - new Vector2(truck.Position.x, truck.Position.z), axis);
                if (lineDepth + 4f > ahead) front += axis * (lineDepth + 4f - ahead);
            }
            var want = new IntVec3(Mathf.RoundToInt(front.x), 0, Mathf.RoundToInt(front.y));
            if (!TryFindSpot(def, map, want, target, out IntVec3 cell)) { HRLog.Msg($"tourelle de camion : pas de place dégagée devant {truck.LabelShort}."); return 0; }

            ThingDef stuff = def.MadeFromStuff ? (GenStuff.AllowedStuffsFor(def).Contains(ThingDefOf.Steel) ? ThingDefOf.Steel : GenStuff.DefaultStuffFor(def)) : null;
            var b = (Building)ThingMaker.MakeThing(def, stuff);
            b.SetFaction(faction);
            GenSpawn.Spawn(b, cell, map, def.rotatable ? Rot4.FromAngleFlat(new Vector3(axis.x, 0f, axis.y).AngleFlat()) : Rot4.North);
            Sandbags(map, faction, cell, def, axis);
            if (b is Building_Turret bt) MapComponent_HostileTurretSupply.DropAmmoNear(bt, 3);

            var crew = new List<Pawn>();
            if (b.TryGetComp<CompMannable>() != null)
            {
                Pawn gunner = infantry.Where(p => p.Spawned && !p.Downed && !p.WorkTagIsDisabled(WorkTags.Violent)
                                                  && !(p.equipment?.Primary != null && Loadouts.IsDestructive(p.equipment.Primary.def)))
                    .OrderBy(p => p.Position.DistanceToSquared(cell)).FirstOrDefault();
                if (gunner != null) crew.Add(gunner);
            }
            foreach (Pawn p in crew) p.GetLord()?.Notify_PawnLost(p, PawnLostCondition.ForcedToJoinOtherLord);
            Lord l = LordMaker.MakeNewLord(faction, new LordJob_HRTurretCrew(faction), map, crew);
            l.AddBuilding(b);
            Messages.Message($"{faction.Name} décharge un(e) {def.label} devant ses camions.", new TargetInfo(b), MessageTypeDefOf.ThreatBig);
            HRLog.Msg($"tourelle de camion : {def.label} en {cell}, servant : {crew.FirstOrDefault()?.LabelShort ?? "aucun"}.");
            return 1;
        }

        private static bool TryFindSpot(ThingDef def, Map map, IntVec3 near, IntVec3 target, out IntVec3 cell)
        {
            int num = GenRadial.NumCellsInRadius(9f);
            for (int i = 0; i < num; i++)
            {
                IntVec3 c = near + GenRadial.RadialPattern[i];
                if (!c.InBounds(map)) continue;
                CellRect r = GenAdj.OccupiedRect(c, Rot4.North, def.size);
                if (!r.ExpandedBy(1).InBounds(map)) continue;
                // la pièce et une case autour : libres (pas collée à un véhicule ni à une autre tourelle)
                if (!r.ExpandedBy(1).Cells.All(x => x.Standable(map) && x.GetFirstPawn(map) == null && x.GetEdifice(map) == null)) continue;
                if (!r.Cells.All(x => x.GetFirstItem(map) == null
                                      && x.GetTerrain(map).affordances?.Contains(def.terrainAffordanceNeeded ?? TerrainAffordanceDefOf.Light) != false)) continue;
                // champ de tir dégagé sur 12 cases vers la base
                Vector3 d3 = (target - c).ToVector3();
                IntVec3 probe = c + (d3.normalized * 12f).ToIntVec3();
                if (probe.InBounds(map) && !GenSight.LineOfSight(c, probe, map, skipFirstCell: true)) continue;
                cell = c;
                return true;
            }
            cell = IntVec3.Invalid;
            return false;
        }

        private static void Sandbags(Map map, Faction faction, IntVec3 turret, ThingDef def, Vector2 dir)
        {
            Vector2 perp = new Vector2(-dir.y, dir.x);
            float s = Mathf.Max(def.size.x, def.size.z);
            Vector2 front = new Vector2(turret.x, turret.z) + dir * (s / 2f + 1.2f);
            int half = Mathf.CeilToInt(s / 2f) + 1;
            for (int t = -half; t <= half; t++)
            {
                Vector2 p = front + perp * t;
                var c = new IntVec3(Mathf.RoundToInt(p.x), 0, Mathf.RoundToInt(p.y));
                if (!c.InBounds(map) || !c.Standable(map) || c.GetFirstPawn(map) != null || c.GetEdifice(map) != null) continue;
                Thing bag = ThingMaker.MakeThing(ThingDefOf.Sandbags, ThingDefOf.Cloth);
                bag.SetFaction(faction);
                GenSpawn.Spawn(bag, c, map);
            }
        }
    }

    /// <summary>Servants des tourelles de camion : les servir, puis assaut quand elles sont détruites.</summary>
    public class LordJob_HRTurretCrew : LordJob
    {
        private Faction faction;
        public const string MemoLost = "HRTurretsLost";

        public LordJob_HRTurretCrew() { }
        public LordJob_HRTurretCrew(Faction f) { faction = f; }

        public override StateGraph CreateGraph()
        {
            var g = new StateGraph();
            var man = new LordToil_HRManTurrets();
            g.AddToil(man);
            LordToil assault = new LordToil_AssaultColony();
            g.AddToil(assault);
            LordToil exit = new LordToil_ExitMap(LocomotionUrgency.Jog, canDig: false, interruptCurrentJob: true);
            g.AddToil(exit);
            // fin du raid sans convoi pour les rembarquer (sinon c'est la retraite du convoi qui les récupère)
            var e = new Transition(man, exit);
            e.AddSource(assault);
            e.AddTrigger(new Trigger_TickCondition(RaidEnded, 250));
            g.AddTransition(e);
            var t = new Transition(man, assault);
            t.AddTrigger(new Trigger_Memo(MemoLost));
            t.AddTrigger(new Trigger_TicksPassed(40000));
            g.AddTransition(t);
            return g;
        }

        /// <summary>Plus aucun autre groupe de la faction n'attaque sur la carte.</summary>
        private bool RaidEnded()
        {
            Map map = lord?.Map;
            if (map == null) return false;
            return !map.lordManager.lords.Any(l => l != lord && l.faction == lord.faction && l.LordJob is not LordJob_HRTurretCrew
                                                   && l.ownedPawns.Count > 0 && !LordJob_HRConvoy.ExitLike(l));
        }

        public override void ExposeData() => Scribe_References.Look(ref faction, "faction");
    }

    public class LordToil_HRManTurrets : LordToil
    {
        public override void UpdateAllDuties()
        {
            Building first = lord.ownedBuildings.FirstOrDefault(b => b.Spawned);
            foreach (Pawn p in lord.ownedPawns)
            {
                Building near = lord.ownedBuildings.Where(b => b.Spawned).OrderBy(b => b.Position.DistanceToSquared(p.Position)).FirstOrDefault() ?? first;
                p.mindState.duty = new PawnDuty(DutyDefOf.ManClosestTurret, near != null ? near.Position : p.Position);
            }
        }

        public override void LordToilTick()
        {
            if (Find.TickManager.TicksGame % 250 != 0) return;
            if (!lord.ownedBuildings.Any(b => b.Spawned && !b.Destroyed)) lord.ReceiveMemo(LordJob_HRTurretCrew.MemoLost);
        }
    }

    // ---------------------------------------------------------------- retraite : rembarquement
    /// <summary>
    /// Fantassins en retraite : chacun reçoit une place dans un véhicule du convoi (camions d'abord), y court et
    /// embarque. Un servant remballe d'abord sa tourelle. Sans place : sortie à pied (duty HR_BoardConvoy).
    /// </summary>
    public class LordJob_HRBoard : LordJob
    {
        public Faction faction;
        public Lord convoy;
        public Dictionary<Pawn, Pawn> assigned = new Dictionary<Pawn, Pawn>();
        public Dictionary<Pawn, Building> pack = new Dictionary<Pawn, Building>();
        public Dictionary<Pawn, int> packStart = new Dictionary<Pawn, int>();
        private List<Pawn> k1, v1, k2;
        private List<Building> v2;

        public LordJob_HRBoard() { }
        public LordJob_HRBoard(Faction f, Lord convoy) { faction = f; this.convoy = convoy; }

        public override bool AddFleeToil => false;

        public override StateGraph CreateGraph()
        {
            var g = new StateGraph();
            g.AddToil(new LordToil_HRBoard());
            return g;
        }

        private LordJob_HRConvoy Convoy => convoy?.LordJob as LordJob_HRConvoy;

        private bool Usable(Pawn v, LordJob_HRConvoy c) =>
            v != null && v.Spawned && !v.Destroyed && convoy.ownedPawns.Contains(v) && !c.leaving.Contains(v) && VehicleCompat.CanMove(v);

        /// <summary>Passagers attendus par ce véhicule (en route, pas encore à bord).</summary>
        public int Pending(Pawn v, Pawn except = null) =>
            assigned.Count(kv => kv.Value == v && kv.Key != except && kv.Key.Spawned && !kv.Key.Dead && !kv.Key.Downed && kv.Key.GetLord() == lord);

        public Pawn Assign(Pawn p)
        {
            LordJob_HRConvoy c = Convoy;
            if (c == null || !p.Spawned || !VehicleGate.Active) return null;
            if (assigned.TryGetValue(p, out Pawn cur) && Usable(cur, c) && VehicleCompat.FreeSeats(cur) - Pending(cur, p) > 0) return cur;
            assigned.Remove(p);
            Pawn best = null;
            float bestScore = float.MaxValue;
            foreach (Pawn v in convoy.ownedPawns)
            {
                if (!Usable(v, c) || VehicleCompat.FreeSeats(v) - Pending(v) <= 0) continue;
                float d = v.Position.DistanceTo(p.Position);
                if (d > 100f) continue;
                if (VehicleCompat.HasTurrets(v)) d += 25f;   // camions d'abord
                if (d < bestScore && p.CanReach(v, PathEndMode.Touch, Danger.Deadly)) { best = v; bestScore = d; }
            }
            if (best != null) assigned[p] = best;
            return best;
        }

        /// <summary>Servant : aller à sa tourelle, la remballer (4 s), puis embarquer.</summary>
        public Job PackJob(Pawn pawn)
        {
            if (!pack.TryGetValue(pawn, out Building b)) return null;
            if (b == null || !b.Spawned) { pack.Remove(pawn); packStart.Remove(pawn); return null; }
            Map map = pawn.Map;
            int now = Find.TickManager.TicksGame;
            if (b.OccupiedRect().ExpandedBy(1).Contains(pawn.Position))
            {
                if (!packStart.TryGetValue(pawn, out int s)) packStart[pawn] = s = now;
                if (now - s < 240)
                {
                    Job w = JobMaker.MakeJob(JobDefOf.Wait);
                    w.expiryInterval = 240 - (now - s) + 1;
                    return w;
                }
                HRLog.Msg($"retraite : {pawn.LabelShort} remballe {b.LabelShort}.");
                b.Destroy(DestroyMode.Vanish);
                pack.Remove(pawn);
                packStart.Remove(pawn);
                return null;
            }
            IntVec3 cell = GenAdj.CellsAdjacent8Way(b).Where(c => c.InBounds(map) && c.Standable(map) && pawn.CanReach(c, PathEndMode.OnCell, Danger.Deadly))
                .OrderBy(c => c.DistanceToSquared(pawn.Position)).FirstOrFallback(IntVec3.Invalid);
            if (!cell.IsValid) { pack.Remove(pawn); return null; }
            Job g = JobMaker.MakeJob(JobDefOf.Goto, cell);
            g.locomotionUrgency = LocomotionUrgency.Jog;
            g.expiryInterval = 400;
            return g;
        }

        public override void ExposeData()
        {
            Scribe_References.Look(ref faction, "faction");
            Scribe_References.Look(ref convoy, "convoy");
            Scribe_Collections.Look(ref assigned, "assigned", LookMode.Reference, LookMode.Reference, ref k1, ref v1);
            Scribe_Collections.Look(ref pack, "pack", LookMode.Reference, LookMode.Reference, ref k2, ref v2);
            assigned ??= new Dictionary<Pawn, Pawn>();
            pack ??= new Dictionary<Pawn, Building>();
            packStart ??= new Dictionary<Pawn, int>();
            if (Scribe.mode == LoadSaveMode.PostLoadInit)
            {
                foreach (Pawn k in assigned.Keys.Where(k => k == null || assigned[k] == null).ToList()) assigned.Remove(k);
                foreach (Pawn k in pack.Keys.Where(k => k == null || pack[k] == null).ToList()) pack.Remove(k);
            }
        }
    }

    public class LordToil_HRBoard : LordToil
    {
        public override bool AllowSatisfyLongNeeds => false;

        public override void UpdateAllDuties()
        {
            foreach (Pawn p in lord.ownedPawns) p.mindState.duty = new PawnDuty(HRDefOf.HR_BoardConvoy);
        }

        public override void LordToilTick()
        {
            if (Find.TickManager.TicksGame % 250 != 0) return;
            // à bord d'un véhicule sorti de la carte : plus rien à faire ici
            foreach (Pawn p in lord.ownedPawns.ToList())
                if (!p.Dead && p.MapHeld != lord.Map) lord.Notify_PawnLost(p, PawnLostCondition.ExitedMap);
        }
    }

    public class JobGiver_HRBoardConvoy : ThinkNode_JobGiver
    {
        protected override Job TryGiveJob(Pawn pawn)
        {
            if (!VehicleGate.Active || pawn.GetLord()?.LordJob is not LordJob_HRBoard job) return null;
            if (!pawn.health.capacities.CapableOf(PawnCapacityDefOf.Moving)) return null;
            Job packJob = job.PackJob(pawn);
            if (packJob != null) return packJob;
            Pawn v = job.Assign(pawn);
            if (v == null) return null;
            Job j = JobMaker.MakeJob(HRDefOf.HR_BoardVehicle, v);
            j.locomotionUrgency = LocomotionUrgency.Sprint;
            j.expiryInterval = 600;
            j.checkOverrideOnExpire = true;
            return j;
        }
    }

    /// <summary>Aller au véhicule et monter à bord (n'importe quelle place libre).</summary>
    public class JobDriver_HRBoard : JobDriver
    {
        public override bool TryMakePreToilReservations(bool errorOnFailed) => true;

        protected override IEnumerable<Toil> MakeNewToils()
        {
            this.FailOnDespawnedOrNull(TargetIndex.A);
            yield return Toils_Goto.GotoThing(TargetIndex.A, PathEndMode.Touch);
            Toil board = ToilMaker.MakeToil("HRBoard");
            board.initAction = () =>
            {
                if (job.targetA.Thing is not Pawn v || !VehicleCompat.TryBoard(v, pawn)) EndJobWith(JobCondition.Incompletable);
            };
            board.defaultCompleteMode = ToilCompleteMode.Instant;
            yield return board;
        }
    }
}
