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

        public const string MemoAllUnloaded = "HRConvoyArrived";

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
            g.AddToil(approach);
            g.AddToil(assault);
            var t = new Transition(approach, assault);
            t.AddTrigger(new Trigger_Memo(MemoAllUnloaded));
            t.AddTrigger(new Trigger_TicksPassed(6000));   // filet : 2,4 h de jeu au plus avant de tout débarquer
            g.AddTransition(t);
            return g;
        }

        /// <summary>Lord qui reçoit les fantassins débarqués (celui du raid, ou un assaut neuf s'il a disparu).</summary>
        public Lord InfantryLord(Map map)
        {
            if (infantryLord != null && map.lordManager.lords.Contains(infantryLord)) return infantryLord;
            infantryLord = LordMaker.MakeNewLord(faction, new LordJob_AssaultColony(faction, canKidnap: true, canTimeoutOrFlee: true), map);
            return infantryLord;
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
                turretsPlaced += TruckTurrets.Deploy(map, faction, v, down, target, stagingCenter);
            // le blindé repart tout de suite (ou le camion se gare) : on force une nouvelle décision
            v.mindState.duty = DutyFor(v);
            v.jobs?.EndCurrentJob(JobCondition.InterruptForced);
            HRLog.Msg($"convoi : {v.LabelShort} a débarqué {down.Count} fantassin(s){(truck ? "" : ", part à l'assaut")}.");
        }

        // ------------------------------------------------------------ appui-feu : s'arrêter, se déployer, tirer
        private readonly Dictionary<Pawn, int> lastFire = new Dictionary<Pawn, int>();

        public void TickFireSupport(Pawn v, int now)
        {
            if (!v.Spawned || !unloaded.Contains(v) || !VehicleCompat.IsFireSupport(v)) return;
            if (VehicleCompat.IsDeploying(v)) return;
            if (VehicleCompat.IsDeployed(v))
            {
                if (VehicleCompat.ManualFire(v)) { lastFire[v] = now; return; }
                if (!lastFire.TryGetValue(v, out int t)) lastFire[v] = t = now;
                if (now - t > 1800)  // 30 s sans cible : on replie et on avance
                {
                    VehicleCompat.Undeploy(v);
                    v.mindState.duty = DutyFor(v);
                    v.jobs?.EndCurrentJob(JobCondition.InterruptForced);
                }
                return;
            }
            if (VehicleCompat.TargetInRange(v))
            {
                v.mindState.duty = VehicleCompat.TravelDuty(v.Position);
                VehicleCompat.StartDeploy(v);
                lastFire[v] = now;
                HRLog.Msg($"convoi : {v.LabelShort} se déploie pour tirer.");
            }
        }

        public PawnDuty DutyFor(Pawn v) =>
            VehicleCompat.HasTurrets(v) && VehicleCompat.CanMove(v) && !VehicleCompat.IsDeployed(v) && !VehicleCompat.IsDeploying(v)
                ? VehicleCompat.AssaultDuty()
                : VehicleCompat.TravelDuty(v.Position);   // camion garé / pièce déployée

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
            dismount ??= new List<Pawn>();
            unloaded ??= new List<Pawn>();
            if (Scribe.mode == LoadSaveMode.PostLoadInit) { dismount.RemoveAll(p => p == null); unloaded.RemoveAll(p => p == null); }
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
        }
    }

    // ---------------------------------------------------------------- tourelles déchargées des camions
    public static class TruckTurrets
    {
        public static int MaxFor(float x) => Mathf.Min(HRSettings.MaxTruckTurrets, Mathf.Max(1, Mathf.RoundToInt(x - 1f)));

        /// <summary>Tir direct seulement (pas d'obusier / mortier) : on veut des pièces qui couvrent les camions.</summary>
        private static bool DirectFire(ThingDef turret)
        {
            ThingDef gun = turret.building?.turretGunDef;
            VerbProperties v = gun?.Verbs?.FirstOrDefault();
            if (v == null || !v.requireLineOfSight || v.range > 75f || v.minRange > 6f) return false;
            return !(v.verbClass?.Name.Contains("Mortar") ?? false) && !(v.verbClass?.Name.Contains("Artillery") ?? false);
        }

        /// <summary>Décharge une tourelle pour ce camion, sur la ligne de tir devant le convoi. Retourne 1 si posée.</summary>
        public static int Deploy(Map map, Faction faction, Pawn truck, List<Pawn> infantry, IntVec3 target, IntVec3 stagingCenter)
        {
            List<ThingDef> defs = Patch_SiegeBlueprints.TurretDefsFor(faction, 3).Where(DirectFire).ToList();
            List<ThingDef> manned = defs.Where(d => d.HasComp(typeof(CompMannable))).ToList();
            if (manned.Count > 0) defs = manned;
            if (defs.Count == 0) { HRLog.Msg("tourelles de camion : aucune tourelle à tir direct disponible pour cette faction."); return 0; }
            ThingDef def = defs.RandomElementByWeight(d => Mathf.Pow(Mathf.Max(50f, d.BaseMarketValue), 0.7f));

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
            var t = new Transition(man, assault);
            t.AddTrigger(new Trigger_Memo(MemoLost));
            t.AddTrigger(new Trigger_TicksPassed(40000));
            g.AddTransition(t);
            return g;
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
}
