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
    /// 1. Approche : moteur démarré, chaque véhicule roule vers sa place sur une ligne d'arrêt face à la base.
    /// 2. Déploiement (arrivée, tir reçu ou délai) : les passagers débarquent et rejoignent le lord d'infanterie du raid ;
    ///    pilotes et tireurs restent dans les blindés. Raid "smart" : tourelles déchargées devant les camions.
    /// 3. Assaut : les blindés passent sous l'IA de combat de Vehicle Framework, les camions restent garés.
    /// Aucun type de VF ici : tout passe par VehicleCompat (chargé seulement si VF est actif).
    /// </summary>
    public class LordJob_HRConvoy : LordJob
    {
        public Faction faction;
        public Lord infantryLord;
        public List<Pawn> dismount = new List<Pawn>();
        public bool deployTurrets;
        public IntVec3 target = IntVec3.Invalid;
        public IntVec3 stagingCenter = IntVec3.Invalid;
        public float tierX;

        public const string MemoArrived = "HRConvoyArrived";

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
            t.AddTrigger(new Trigger_Memo(MemoArrived));
            t.AddTrigger(new Trigger_PawnHarmed());
            t.AddTrigger(new Trigger_TicksPassed(7500));
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

        public override void ExposeData()
        {
            Scribe_References.Look(ref faction, "faction");
            Scribe_References.Look(ref infantryLord, "infantryLord");
            Scribe_Collections.Look(ref dismount, "dismount", LookMode.Reference);
            Scribe_Values.Look(ref deployTurrets, "deployTurrets");
            Scribe_Values.Look(ref target, "target", IntVec3.Invalid);
            Scribe_Values.Look(ref stagingCenter, "stagingCenter", IntVec3.Invalid);
            Scribe_Values.Look(ref tierX, "tierX");
            if (Scribe.mode == LoadSaveMode.PostLoadInit) dismount?.RemoveAll(p => p == null);
            dismount ??= new List<Pawn>();
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

        private void ComputeStaging()
        {
            Map map = Map;
            List<Pawn> vehicles = lord.ownedPawns.Where(p => p.Spawned).ToList();
            if (vehicles.Count == 0) return;
            var from = new IntVec3((int)vehicles.Average(v => v.Position.x), 0, (int)vehicles.Average(v => v.Position.z));
            if (!Job.target.IsValid) Job.target = Patch_SiegeBlueprints.ThreatTarget(map, from);
            if (!Job.stagingCenter.IsValid)
            {
                // ligne d'arrêt : à ~32 cases de la base, sur l'axe d'arrivée (hors de portée de la plupart des tourelles)
                Vector2 dir = new Vector2(from.x - Job.target.x, from.z - Job.target.z);
                float dist = dir.magnitude;
                dir = dist < 1f ? Vector2.up : dir / dist;
                float stop = Mathf.Min(32f, dist * 0.6f);
                Job.stagingCenter = new IntVec3(Mathf.RoundToInt(Job.target.x + dir.x * stop), 0, Mathf.RoundToInt(Job.target.z + dir.y * stop)).ClampInsideMap(map);
            }
            slots.Clear();
            Vector2 axis = new Vector2(Job.target.x - Job.stagingCenter.x, Job.target.z - Job.stagingCenter.z);
            axis = axis.sqrMagnitude < 1f ? Vector2.up : axis.normalized;
            Vector2 perp = new Vector2(-axis.y, axis.x);
            var taken = new HashSet<IntVec3>();
            int i = 0;
            // blindés devant, camions derrière
            foreach (Pawn v in vehicles.OrderByDescending(v => VehicleCompat.HasTurrets(v) ? 1 : 0).ThenBy(v => v.thingIDNumber))
            {
                int rank = i / 6, file = i % 6;
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
                if (!slots.TryGetValue(v, out IntVec3 c)) c = v.Position;
                VehicleCompat.Ready(v);
                v.mindState.duty = VehicleCompat.TravelDuty(c);
            }
        }

        public override void LordToilTick()
        {
            if (Find.TickManager.TicksGame % 120 != 0) return;
            int now = Find.TickManager.TicksGame;
            bool allArrived = true;
            foreach (Pawn v in lord.ownedPawns)
            {
                if (!v.Spawned || !VehicleCompat.CanMove(v)) continue;
                VehicleCompat.Ready(v);
                IntVec3 goal = slots.TryGetValue(v, out IntVec3 c) ? c : v.Position;
                if (v.Position.InHorDistOf(goal, 5f)) continue;
                // bloqué depuis 10 s : on considère qu'il est arrivé (route coupée, embouteillage)
                if (!lastMove.TryGetValue(v, out var lm) || lm.pos != v.Position) { lastMove[v] = (v.Position, now); allArrived = false; continue; }
                if (now - lm.tick < 600) allArrived = false;
            }
            if (allArrived) lord.ReceiveMemo(LordJob_HRConvoy.MemoArrived);
        }
    }

    public class LordToil_HRConvoyAssault : LordToil
    {
        private LordJob_HRConvoy Job => (LordJob_HRConvoy)lord.LordJob;

        public override bool ForceHighStoryDanger => true;

        public override void Init()
        {
            base.Init();
            try { Deploy(); }
            catch (Exception e) { Log.Warning("[Hardened Raids] déploiement du convoi : " + e); }
        }

        private void Deploy()
        {
            Map map = Map;
            Lord inf = Job.InfantryLord(map);
            var dismounted = new List<Pawn>();
            var trucks = new List<Pawn>();
            foreach (Pawn v in lord.ownedPawns.ToList())
            {
                if (!v.Spawned) continue;
                bool truck = !VehicleCompat.HasTurrets(v);
                if (truck) trucks.Add(v);
                foreach (Pawn p in VehicleCompat.PawnsAboard(v).ToList())
                {
                    if (!truck && !Job.dismount.Contains(p)) continue;   // blindé : pilote et tireurs restent
                    VehicleCompat.Disembark(v, p);
                    if (!p.Spawned) continue;
                    if (p.GetLord() == null) inf.AddPawn(p);
                    dismounted.Add(p);
                }
            }
            HRLog.Msg($"convoi {Job.faction?.Name} : {dismounted.Count} fantassins débarqués de {lord.ownedPawns.Count} véhicules.");
            if (Job.deployTurrets && trucks.Count > 0 && dismounted.Count > 0)
                TruckTurrets.Deploy(map, Job.faction, trucks, dismounted, Job.target, Job.tierX);
        }

        public override void UpdateAllDuties()
        {
            foreach (Pawn v in lord.ownedPawns)
            {
                VehicleCompat.Ready(v);
                v.mindState.duty = VehicleCompat.HasTurrets(v) && VehicleCompat.CanMove(v)
                    ? VehicleCompat.AssaultDuty()
                    : VehicleCompat.TravelDuty(v.Position);   // camions garés
            }
        }

        public override void LordToilTick()
        {
            if (Find.TickManager.TicksGame % 250 != 0) return;
            foreach (Pawn v in lord.ownedPawns) VehicleCompat.Ready(v);
        }
    }

    // ---------------------------------------------------------------- tourelles déchargées des camions
    public static class TruckTurrets
    {
        public static void Deploy(Map map, Faction faction, List<Pawn> trucks, List<Pawn> infantry, IntVec3 target, float x)
        {
            List<ThingDef> defs = Patch_SiegeBlueprints.TurretDefsFor(faction, 3).Where(d => d.HasComp(typeof(CompMannable))).ToList();
            if (defs.Count == 0) defs = Patch_SiegeBlueprints.TurretDefsFor(faction, 3);
            if (defs.Count == 0) { HRLog.Msg("tourelles de camion : aucune tourelle disponible pour cette faction."); return; }
            int n = Mathf.Min(trucks.Count, HRSettings.MaxTruckTurrets, Mathf.Max(1, Mathf.RoundToInt(x - 1f)));
            var crew = new List<Pawn>();
            var turrets = new List<Building>();
            List<Pawn> pool = infantry.Where(p => p.Spawned && !p.Downed && !p.WorkTagIsDisabled(WorkTags.Violent)
                                                  && !(p.equipment?.Primary != null && Loadouts.IsDestructive(p.equipment.Primary.def))).ToList();
            foreach (Pawn truck in trucks.Take(n))
            {
                ThingDef def = defs.RandomElementByWeight(d => Mathf.Pow(Mathf.Max(50f, d.BaseMarketValue), 0.7f));
                Vector2 dir = new Vector2(target.x - truck.Position.x, target.z - truck.Position.z);
                dir = dir.sqrMagnitude < 1f ? Vector2.up : dir.normalized;
                float reach = Mathf.Max(truck.def.size.x, truck.def.size.z) / 2f + Mathf.Max(def.size.x, def.size.z) / 2f + 2f;
                var want = new IntVec3(Mathf.RoundToInt(truck.Position.x + dir.x * reach), 0, Mathf.RoundToInt(truck.Position.z + dir.y * reach));
                if (!TryFindSpot(def, map, want, out IntVec3 cell)) continue;
                ThingDef stuff = def.MadeFromStuff ? (GenStuff.AllowedStuffsFor(def).Contains(ThingDefOf.Steel) ? ThingDefOf.Steel : GenStuff.DefaultStuffFor(def)) : null;
                var b = (Building)ThingMaker.MakeThing(def, stuff);
                b.SetFaction(faction);
                GenSpawn.Spawn(b, cell, map, def.rotatable ? Rot4.FromAngleFlat(new Vector3(dir.x, 0f, dir.y).AngleFlat()) : Rot4.North);
                turrets.Add(b);
                Sandbags(map, faction, cell, def, dir);
                if (b.TryGetComp<CompMannable>() != null)
                {
                    Pawn gunner = pool.OrderBy(p => p.Position.DistanceToSquared(cell)).FirstOrDefault();
                    if (gunner != null) { pool.Remove(gunner); crew.Add(gunner); }
                }
            }
            if (turrets.Count == 0) return;
            foreach (Pawn p in crew) p.GetLord()?.Notify_PawnLost(p, PawnLostCondition.ForcedToJoinOtherLord);
            Lord l = LordMaker.MakeNewLord(faction, new LordJob_HRTurretCrew(faction), map, crew);
            foreach (Building b in turrets) l.AddBuilding(b);
            Messages.Message($"{faction.Name} décharge {turrets.Count} tourelle(s) devant ses camions.", new TargetInfo(turrets[0]), MessageTypeDefOf.ThreatBig);
            HRLog.Msg($"tourelles de camion : {string.Join(", ", turrets.Select(t => t.def.label))}, {crew.Count} servants.");
        }

        private static bool TryFindSpot(ThingDef def, Map map, IntVec3 near, out IntVec3 cell)
        {
            for (int i = 0; i < GenRadial.NumCellsInRadius(8f); i++)
            {
                IntVec3 c = near + GenRadial.RadialPattern[i];
                if (!c.InBounds(map)) continue;
                CellRect r = GenAdj.OccupiedRect(c, Rot4.North, def.size);
                if (!r.InBounds(map)) continue;
                if (r.Cells.All(x => x.Standable(map) && x.GetFirstPawn(map) == null && x.GetEdifice(map) == null && x.GetFirstItem(map) == null
                                     && x.GetTerrain(map).affordances?.Contains(def.terrainAffordanceNeeded ?? TerrainAffordanceDefOf.Light) != false))
                {
                    cell = c;
                    return true;
                }
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
