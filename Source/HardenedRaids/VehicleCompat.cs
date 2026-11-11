using System;
using System.Collections.Generic;
using System.Linq;
using RimWorld;
using UnityEngine;
using Vehicles;
using Verse;
using Verse.AI.Group;

namespace HardenedRaids
{
    /// <summary>
    /// Partie 2 — véhicules (Vehicle Framework + Vanilla Vehicles Expanded).
    /// VF contient l'IA PNJ (LordJob_ArmoredAssault, duty VF_RangedAggressive, tir des tourelles) mais ne l'active
    /// pas dans ses builds publics. On fait le câblage : achat des véhicules sur le budget du raid, apparition au bord
    /// de la carte, équipage pris dans l'infanterie, munitions + carburant, lord blindé séparé.
    ///
    /// IMPORTANT : seules les méthodes de cette classe touchent aux types de VF. Les appelants vérifient
    /// VehicleGate.Active avant, pour que la classe ne soit jamais chargée sans le mod.
    /// </summary>
    public static class VehicleCompat
    {
        // ThingDef (et pas VehicleDef) : aucun champ typé VF, la classe se charge même sans le mod
        private static List<ThingDef> combatVehicles;

        private static List<VehicleDef> Catalog()
        {
            if (combatVehicles != null) return combatVehicles.Cast<VehicleDef>().ToList();
            combatVehicles = DefDatabase<VehicleDef>.AllDefsListForReading
                .Where(v => v.type == VehicleType.Land && (v.vehicleCategory & VehicleCategory.Combat) != 0
                            && (v.enabled & VehicleEnabled.For.Raiders) != 0)
                .Cast<ThingDef>().ToList();
            HRLog.Msg($"véhicules de combat disponibles : {string.Join(", ", combatVehicles.Select(v => $"{v.defName} ({Cost(v):F0} pts)"))}");
            return combatVehicles.Cast<VehicleDef>().ToList();
        }

        /// <summary>Coût en points : VVE ne renseigne pas combatPower (100 partout), on part de la valeur marchande.</summary>
        public static float Cost(ThingDef v) => Mathf.Clamp(v.BaseMarketValue / 12f, 250f, 4000f) * HRSettings.CostFactor;

        /// <summary>Choisit les véhicules d'un raid. Retourne les defs et le coût total à retirer du budget de l'infanterie.</summary>
        public static List<ThingDef> Plan(Faction faction, float points, float tierX, System.Random rng, out float totalCost)
        {
            totalCost = 0f;
            var res = new List<ThingDef>();
            if (tierX < HRSettings.VehicleMinTier) return res;
            List<VehicleDef> cands = Catalog().Where(v => v.techLevel <= faction.def.techLevel || v.techLevel == TechLevel.Undefined).ToList();
            if (cands.Count == 0) return res;

            float budget = points * HRSettings.VehiclePointShare;
            int wanted = Mathf.Clamp(Mathf.RoundToInt(tierX - HRSettings.VehicleMinTier + 1f + points / 25000f), 1, HRSettings.MaxVehicles);
            // plus le palier est haut, plus on préfère les gros blindés
            float exp = Mathf.Lerp(0f, 1.5f, Mathf.InverseLerp(HRSettings.VehicleMinTier, 6f, tierX));
            for (int i = 0; i < wanted; i++)
            {
                float perVehicle = budget / (wanted - i);
                List<VehicleDef> afford = cands.Where(v => Cost(v) <= perVehicle).ToList();
                if (afford.Count == 0) break;
                float total = afford.Sum(v => Mathf.Pow(Cost(v), exp));
                double r = rng.NextDouble() * total;
                VehicleDef pick = afford[afford.Count - 1];
                foreach (VehicleDef v in afford)
                {
                    r -= Mathf.Pow(Cost(v), exp);
                    if (r <= 0) { pick = v; break; }
                }
                res.Add(pick);
                budget -= Cost(pick);
                totalCost += Cost(pick);
            }
            return res;
        }

        public static bool IsVehicle(Pawn p) => p is VehiclePawn;

        // ------------------------------------------------------------ petites façades pour Convoy.cs (sans types VF)
        public static bool HasTurrets(Pawn v) => v is VehiclePawn vp && vp.CompVehicleTurrets != null && vp.CompVehicleTurrets.Turrets.Count > 0;
        public static bool CanMove(Pawn v) => v is VehiclePawn vp && vp.CanMoveFinal;
        public static IEnumerable<Pawn> PawnsAboard(Pawn v) => v is VehiclePawn vp ? vp.AllPawnsAboard : Enumerable.Empty<Pawn>();
        public static void Disembark(Pawn v, Pawn p) { if (v is VehiclePawn vp) vp.DisembarkPawn(p); }
        public static Verse.AI.PawnDuty TravelDuty(IntVec3 c) => new Verse.AI.PawnDuty(DutyDefOf_Vehicles.TravelOrWaitVehicle, c);
        public static Verse.AI.PawnDuty AssaultDuty() => new Verse.AI.PawnDuty(DutyDefOf_Vehicles.VF_RangedAggressive);

        // ------------------------------------------------------------ appui-feu (lance-roquettes Burya...)
        private static readonly System.Reflection.FieldInfo deployTicksField = HarmonyLib.AccessTools.Field(typeof(CompVehicleTurrets), "deployTicks");

        private static bool Manual(VehicleTurret t) => !t.CanAutoTarget || t.deployment == DeploymentType.Deployed;

        /// <summary>Véhicule dont une tourelle ne tire pas seule : tir sur ordre et/ou déploiement obligatoire.</summary>
        public static bool IsFireSupport(Pawn v) =>
            v is VehiclePawn vp && vp.CompVehicleTurrets != null && vp.CompVehicleTurrets.Turrets.Any(Manual);

        public static bool IsDeployed(Pawn v) => v is VehiclePawn vp && vp.CompVehicleTurrets != null && vp.CompVehicleTurrets.Deployed;
        public static bool IsDeploying(Pawn v) => v is VehiclePawn vp && vp.CompVehicleTurrets != null && vp.CompVehicleTurrets.Deploying;

        /// <summary>Burya : la pièce ne tire que véhicule déployé. Scytheman : pas de déploiement, juste à l'arrêt.</summary>
        public static bool NeedsDeploy(Pawn v) =>
            v is VehiclePawn vp && vp.CompVehicleTurrets != null && vp.CompVehicleTurrets.Turrets.Any(t => Manual(t) && t.deployment == DeploymentType.Deployed);

        /// <summary>
        /// La cible la plus proche à portée d'une tourelle manuelle, sans tenir compte de l'angle de tir (le
        /// Scytheman ne tire que vers l'arrière : on le fera pivoter).
        /// </summary>
        public static bool TargetInRange(Pawn v, out IntVec3 cell)
        {
            cell = IntVec3.Invalid;
            if (v is not VehiclePawn vp || !vp.Spawned || vp.CompVehicleTurrets == null) return false;
            List<VehicleTurret> manual = vp.CompVehicleTurrets.Turrets.Where(t => Manual(t) && t.HasAmmo).ToList();
            if (manual.Count == 0) return false;
            float best = float.MaxValue;
            foreach (Verse.AI.IAttackTarget a in vp.Map.attackTargetsCache.GetPotentialTargetsFor(vp))
            {
                Thing th = a.Thing;
                if (th == null || !th.Spawned || a.ThreatDisabled(vp)) continue;
                var lt = new LocalTargetInfo(th);
                foreach (VehicleTurret t in manual)
                {
                    if (!t.InRange(lt)) continue;
                    bool overhead = t.ProjectileDef?.projectile?.flyOverhead ?? false;
                    if (!overhead && !GenSight.LineOfSight(vp.Position, th.Position, vp.Map, true)) continue;
                    float d = (th.Position - vp.Position).LengthHorizontalSquared;
                    if (d < best) { best = d; cell = th.Position; }
                    break;
                }
            }
            return cell.IsValid;
        }

        private static readonly System.Reflection.MethodInfo angleBetween = HarmonyLib.AccessTools.Method(typeof(VehicleTurret), "AngleBetween");

        /// <summary>Fait pivoter le véhicule sur place pour que la cible entre dans l'arc de tir d'une tourelle manuelle.</summary>
        public static bool FaceTarget(Pawn v, IntVec3 cell)
        {
            if (v is not VehiclePawn vp || !vp.Spawned || vp.CompVehicleTurrets == null) return false;
            Map map = vp.Map;
            object[] at = { cell.ToVector3Shifted() };
            // AngleBetween(Vector3) : appel par réflexion (le Vector3 de Vehicles.dll n'est pas celui des références de compilation)
            bool Aligned() => angleBetween != null && vp.CompVehicleTurrets.Turrets.Any(t => Manual(t) && (bool)angleBetween.Invoke(t, at));
            if (Aligned()) return true;
            Rot4 old = vp.Rotation;
            float oldAngle = vp.Angle;
            foreach (Rot4 r in new[] { Rot4.North, Rot4.East, Rot4.South, Rot4.West })
            {
                if (r == old) continue;
                CellRect rect = vp.VehicleDef.VehicleRect(vp.Position, r);
                if (!rect.InBounds(map)) continue;
                bool free = true;
                foreach (IntVec3 c in rect)
                    if (!c.Standable(vp.VehicleDef, map) || c.GetThingList(map).Any(t => t is VehiclePawn o && o != vp)) { free = false; break; }
                if (!free) continue;
                vp.Rotation = r;
                vp.Angle = 0f;
                if (Aligned()) { vp.ReclaimPosition(); return true; }
            }
            vp.Rotation = old;
            vp.Angle = oldAngle;
            return false;
        }

        public static void Stop(Pawn v)
        {
            if (v is not VehiclePawn vp || !vp.Spawned) return;
            if (vp.vehiclePather.Moving) vp.vehiclePather.StopDead();
            vp.jobs?.EndCurrentJob(Verse.AI.JobCondition.InterruptForced);
        }

        // ------------------------------------------------------------ retraite
        private static readonly JobGiver_ExitMapBest exitGiver = new JobGiver_ExitMapBest();

        public static int FreeSeats(Pawn v) =>
            v is VehiclePawn vp && vp.handlers != null ? vp.handlers.Sum(h => Mathf.Max(0, h.role.Slots - h.thingOwner.Count)) : 0;

        public static bool TryBoard(Pawn v, Pawn p) => v is VehiclePawn vp && vp.Spawned && vp.TryAddPawn(p);

        public static bool IsExiting(Pawn v) => v.CurJob != null && v.CurJob.exitMapOnArrival;

        /// <summary>Le véhicule quitte la carte (avec tout ce qu'il a à bord). Faux si impossible pour l'instant.</summary>
        public static bool ExitMap(Pawn v)
        {
            if (v is not VehiclePawn vp || !vp.Spawned) return false;
            if (IsExiting(v)) return true;
            if (IsDeployed(v)) { Undeploy(v); return false; }
            if (IsDeploying(v)) return false;
            Ready(v);
            if (!vp.CanMoveFinal) return false;
            Verse.AI.Job j = exitGiver.TryIssueJobPackage(vp, default(Verse.AI.JobIssueParams)).Job;
            if (j == null) return false;
            vp.jobs.StartJob(j, Verse.AI.JobCondition.InterruptForced);
            return true;
        }

        public static void StartDeploy(Pawn v)
        {
            if (v is not VehiclePawn vp || vp.CompVehicleTurrets == null || vp.CompVehicleTurrets.Deployed || vp.CompVehicleTurrets.Deploying) return;
            if (vp.vehiclePather.Moving) vp.vehiclePather.StopDead();
            vp.jobs.StartJob(new Verse.AI.Job(JobDefOf_Vehicles.DeployVehicle, vp), Verse.AI.JobCondition.InterruptForced);
            deployTicksField?.SetValue(vp.CompVehicleTurrets, vp.CompVehicleTurrets.DeployTicks);
        }

        public static void Undeploy(Pawn v)
        {
            if (v is VehiclePawn vp && vp.CompVehicleTurrets != null && vp.CompVehicleTurrets.Deployed && !vp.CompVehicleTurrets.Deploying)
                vp.CompVehicleTurrets.ToggleDeployment();
        }

        /// <summary>Désigne une cible à chaque tourelle manuelle prête. Retourne vrai si au moins une tire.</summary>
        public static bool ManualFire(Pawn v)
        {
            if (v is not VehiclePawn vp || vp.CompVehicleTurrets == null) return false;
            bool firing = false;
            foreach (VehicleTurret t in vp.CompVehicleTurrets.Turrets)
            {
                if (!Manual(t) || !t.DeploymentSatisfied || !t.HasAmmo) continue;
                if (t.targetInfo.IsValid && !(t.targetInfo.Thing?.Destroyed ?? false) && !(t.targetInfo.Pawn?.Downed ?? false)) { firing = true; continue; }
                if (t.TryGetTarget(out LocalTargetInfo target)) { t.SetTarget(target); firing = true; }
            }
            return firing;
        }

        /// <summary>Moteur démarré (sinon VF refuse de bouger) et tir automatique de toutes les tourelles.</summary>
        public static void Ready(Pawn v)
        {
            if (v is not VehiclePawn vp || !vp.Spawned) return;
            try
            {
                if (!vp.ignition.Drafted && vp.CanMoveFinal) vp.ignition.Drafted = true;
                if (vp.CompVehicleTurrets != null)
                    foreach (VehicleTurret t in vp.CompVehicleTurrets.Turrets)
                        if (!t.AutoTarget && t.CanAutoTarget) t.AutoTarget = true;
            }
            catch (Exception e) { Log.WarningOnce("[Hardened Raids] démarrage du véhicule : " + e.Message, vp.thingIDNumber ^ 0x5652); }
        }

        public static IntVec3? FindStandCell(Pawn v, Map map, IntVec3 want, IntVec3 target, HashSet<IntVec3> taken)
        {
            if (v is not VehiclePawn vp) return null;
            Rot4 rot = Rot4.FromAngleFlat((target - want).AngleFlat);
            return FindVehicleCell(vp.VehicleDef, map, want, rot, taken, out IntVec3 c, requireNoPawn: false) ? c : (IntVec3?)null;
        }

        /// <summary>
        /// Case libre pour tout le rectangle du véhicule. VF ne construit la grille de passage d'un type de
        /// véhicule qu'à sa première apparition sur la carte : sans la forcer, toutes les cases sont refusées.
        /// </summary>
        private static bool FindVehicleCell(VehicleDef vd, Map map, IntVec3 center, Rot4 rot, HashSet<IntVec3> taken, out IntVec3 cell, bool requireNoPawn = true)
        {
            Vehicles.World.Ext_Caravan.EnsureMapInitialized(vd, map);
            int num = GenRadial.NumCellsInRadius(56f);
            for (int i = 0; i < num; i++)
            {
                IntVec3 c = center + GenRadial.RadialPattern[i];
                if (!c.InBounds(map)) continue;
                CellRect r = vd.VehicleRect(c, rot);
                if (!r.ExpandedBy(1).InBounds(map)) continue;
                bool ok = true;
                foreach (IntVec3 x in r)
                {
                    if (taken.Contains(x) || !x.Standable(vd, map) || (requireNoPawn && (x.Fogged(map) || x.GetFirstPawn(map) != null))) { ok = false; break; }
                }
                if (!ok) continue;
                if (!map.reachability.CanReach(c, map.Center, Verse.AI.PathEndMode.OnCell, TraverseParms.For(TraverseMode.PassDoors))) continue;
                foreach (IntVec3 x in r.ExpandedBy(1)) taken.Add(x);
                cell = c;
                return true;
            }
            cell = IntVec3.Invalid;
            return false;
        }

        // ------------------------------------------------------------ transports
        private static List<ThingDef> transports;

        private static int Seats(VehicleDef vd, bool passengersOnly) =>
            vd.properties?.roles?.Where(r => !passengersOnly || r.HandlingTypes == HandlingType.None).Sum(r => r.slots) ?? 0;

        private static List<VehicleDef> TransportCatalog()
        {
            if (transports == null)
            {
                transports = DefDatabase<VehicleDef>.AllDefsListForReading
                    .Where(v => v.type == VehicleType.Land && (v.vehicleCategory & VehicleCategory.Combat) == 0
                                && (v.enabled & VehicleEnabled.For.Raiders) != 0 && Seats(v, false) >= 3
                                && v.properties.roles.Any(r => (r.HandlingTypes & HandlingType.Movement) != 0))
                    .Cast<ThingDef>().ToList();
                HRLog.Msg($"transports de troupes disponibles : {string.Join(", ", transports.Select(v => $"{v.defName} ({Seats((VehicleDef)v, false)} places)"))}");
            }
            return transports.Cast<VehicleDef>().ToList();
        }

        /// <summary>
        /// Convoi : blindés achetés (pilote + tireurs restent, passagers débarquent) puis assez de camions pour
        /// embarquer tout le reste de l'infanterie (tous débarquent). Les embarqués restent membres du lord
        /// d'infanterie du raid (voir Patch_VehicleLord).
        /// </summary>
        public static void SpawnVehicles(List<ThingDef> defs, IncidentParms parms, List<Pawn> pawns)
        {
            if (parms.target is not Map map) return;
            IntVec3 center = parms.spawnCenter.IsValid ? parms.spawnCenter : pawns.FirstOrDefault(p => p.Spawned)?.Position ?? IntVec3.Invalid;
            if (!center.IsValid) { HRLog.Msg("véhicules : pas de point d'arrivée."); return; }

            // équipage des blindés : des fantassins "de base" (pas le chef, pas les porteurs d'armes de brèche)
            List<Pawn> pool = pawns.Where(p => p.Spawned && !p.Downed && p.RaceProps.Humanlike && !(p is VehiclePawn))
                .OrderBy(p => p.kindDef.factionLeader ? 2 : (p.equipment?.Primary != null && Loadouts.IsDestructive(p.equipment.Primary.def) ? 1 : 0))
                .ToList();
            IntVec3 target = Patch_SiegeBlueprints.ThreatTarget(map, center);
            Rot4 rot = Rot4.FromAngleFlat((target - center).AngleFlat);
            var taken = new HashSet<IntVec3>();
            var vehicles = new List<VehiclePawn>();
            var dismount = new List<Pawn>();

            VehiclePawn Spawn(VehicleDef vd)
            {
                if (!FindVehicleCell(vd, map, center, rot, taken, out IntVec3 cell))
                {
                    HRLog.Msg($"véhicule {vd.defName} : aucune place libre près de {center}.");
                    return null;
                }
                VehiclePawn v = VehicleSpawner.GenerateVehicle(new VehicleGenerationRequest(vd, parms.faction, randomizeColors: true, randomizeMask: true));
                GenSpawn.Spawn(v, cell, map, rot);
                return v;
            }

            bool Board(VehiclePawn v, VehicleRoleHandler h, bool leaves)
            {
                Pawn crew = pool.FirstOrDefault(p => h.role.HandlingTypes == HandlingType.None || h.CanOperateRole(p));
                if (crew == null) return false;
                pool.Remove(crew);
                if (!v.TryAddPawn(crew, h)) return false;
                pawns.Remove(crew);
                if (leaves) dismount.Add(crew);
                return true;
            }

            // 1. blindés
            foreach (ThingDef d in defs)
            {
                if (d is not VehicleDef vd) continue;
                VehiclePawn v = null;
                try
                {
                    v = Spawn(vd);
                    if (v == null) continue;
                    int crewed = 0;
                    foreach (VehicleRoleHandler h in v.handlers.Where(h => h.role.HandlingTypes != HandlingType.None))
                        while (h.AreSlotsAvailable && pool.Count > 0 && Board(v, h, false)) crewed++;
                    if (crewed == 0)
                    {
                        HRLog.Msg($"véhicule {vd.defName} : aucun équipage possible ({v.handlers.Count} postes, {pool.Count} raiders disponibles) -> retiré.");
                        v.Destroy();
                        continue;
                    }
                    foreach (VehicleRoleHandler h in v.handlers.Where(h => h.role.HandlingTypes == HandlingType.None))
                        while (h.AreSlotsAvailable && pool.Count > 0 && Board(v, h, true)) { }
                    vehicles.Add(v);
                }
                catch (Exception e)
                {
                    Log.Warning($"[Hardened Raids] véhicule {d.defName} : {e}");
                    if (v != null && !v.Destroyed && !vehicles.Contains(v)) v.Destroy();
                }
            }
            if (vehicles.Count == 0) { HRLog.Msg($"0/{defs.Count} véhicule(s) pour {parms.faction.Name}."); return; }

            // 2. camions pour tout le reste
            List<VehicleDef> trucks = TransportCatalog().Where(v => v.techLevel <= parms.faction.def.techLevel || v.techLevel == TechLevel.Undefined).ToList();
            // gros raid : le plus gros camion (Wisent...) ; petit raid : le plus petit qui embarque tout le monde
            VehicleDef truckDef = null;
            if (trucks.Count > 0)
            {
                int biggest = trucks.Max(v => Seats(v, false));
                truckDef = pool.Count > biggest
                    ? trucks.Where(v => Seats(v, false) == biggest).RandomElement()
                    : trucks.Where(v => Seats(v, false) >= pool.Count).OrderBy(v => Seats(v, false)).First();
            }
            int nTrucks = 0;
            while (truckDef != null && pool.Count > 0 && nTrucks < HRSettings.MaxTransports)
            {
                VehiclePawn t = null;
                try
                {
                    t = Spawn(truckDef);
                    if (t == null) break;
                    int aboard = 0;
                    foreach (VehicleRoleHandler h in t.handlers.OrderByDescending(h => (h.role.HandlingTypes & HandlingType.Movement) != 0 ? 1 : 0))
                        while (h.AreSlotsAvailable && pool.Count > 0 && Board(t, h, true)) aboard++;
                    if (aboard == 0 || !t.CanMoveFinal)
                    {
                        foreach (Pawn p in t.AllPawnsAboard.ToList()) { t.DisembarkPawn(p); dismount.Remove(p); pawns.Add(p); }
                        t.Destroy();
                        break;
                    }
                    vehicles.Add(t);
                    nTrucks++;
                }
                catch (Exception e)
                {
                    Log.Warning($"[Hardened Raids] camion {truckDef.defName} : {e}");
                    if (t != null && !t.Destroyed && !vehicles.Contains(t)) t.Destroy();
                    break;
                }
            }

            foreach (VehiclePawn v in vehicles) { Supply(v); Ready(v); pawns.Add(v); }
            ConvoyHandoff.Pending = new ConvoyHandoff
            {
                vehicles = vehicles.Cast<Pawn>().ToList(),
                dismount = dismount,
                deployTurrets = HRSettings.TruckTurrets && parms.raidStrategy != null && parms.raidStrategy.defName.Contains("Smart"),
                tierX = Tiers.TierX(parms.faction.def, parms.points),
            };
            HRLog.Msg($"convoi {parms.faction.Name} : {vehicles.Count - nTrucks} blindé(s), {nTrucks} camion(s) {(truckDef != null ? "(" + truckDef.label + ")" : "")}, "
                      + $"{dismount.Count} fantassins embarqués, {pool.Count} à pied.");
        }

        /// <summary>Plein de carburant et munitions pour chaque tourelle.</summary>
        private static void Supply(VehiclePawn vehicle)
        {
            CompFueledTravel fuel = vehicle.CompFueledTravel;
            if (fuel != null && fuel.FuelCapacity - fuel.Fuel > 0.01f) fuel.Refuel(fuel.FuelCapacity - fuel.Fuel);

            if (vehicle.CompVehicleTurrets == null) return;
            foreach (VehicleTurret t in vehicle.CompVehicleTurrets.Turrets)
            {
                if (t.def.ammunition == null)
                {
                    t.Reload(null, true);
                    continue;
                }
                ThingDef ammo = t.def.ammunition.AllowedThingDefs.Where(a => a.category == ThingCategory.Item)
                    .OrderByDescending(a => a.BaseMarketValue).FirstOrDefault(); // obus le plus cher = le plus destructeur
                if (ammo == null) continue;
                int count = Mathf.Max(t.def.magazineCapacity, 1) * 8;
                while (count > 0)
                {
                    Thing stack = ThingMaker.MakeThing(ammo);
                    stack.stackCount = Mathf.Min(count, ammo.stackLimit);
                    count -= stack.stackCount;
                    if (!vehicle.inventory.innerContainer.TryAdd(stack)) break;
                }
                t.Reload(ammo, true);
            }
        }

        /// <summary>Lord PNJ blindé de VF pour les véhicules.</summary>
        public static LordJob ArmoredLordJob(Faction f) =>
            new LordJob_ArmoredAssault(f, LordJob_ArmoredAssault.RaiderPermissions.All);

        public static bool IsVehicleLordJob(LordJob job) => job is LordJob_VehicleNPC;
    }

    /// <summary>Sans aucune référence aux types de VF : sûr à appeler même sans le mod.</summary>
    public static class VehicleGate
    {
        private static bool? active;
        public static bool Active => active ??= ModsConfig.IsActive("SmashPhil.VehicleFramework");

        /// <summary>Test par nom de type : ne charge pas Vehicles.dll.</summary>
        public static bool IsVehicle(Pawn p)
        {
            if (!Active || p == null) return false;
            for (Type t = p.GetType(); t != null && t != typeof(Pawn); t = t.BaseType)
                if (t.Name == "VehiclePawn") return true;
            return false;
        }

        public static bool ArrivalOk(PawnsArrivalModeDef mode) =>
            mode?.workerClass != null && mode.workerClass.Name.Contains("EdgeWalkIn");
    }

    // ---------------------------------------------------------------- apparition après l'arrivée de l'infanterie
    [HarmonyLib.HarmonyPatch(typeof(IncidentWorker_Raid), nameof(IncidentWorker_Raid.TryGenerateRaidInfo))]
    public static class Patch_SpawnVehicles
    {
        [HarmonyLib.HarmonyPriority(HarmonyLib.Priority.Low)]
        public static void Postfix(IncidentParms parms, ref List<Pawn> pawns, bool debugTest, bool __result)
        {
            List<ThingDef> pending = RaidContext.PendingVehicles;
            RaidContext.PendingVehicles = null;
            if (!__result || debugTest || pending == null || pending.Count == 0 || pawns == null || !VehicleGate.Active) return;
            try
            {
                VehicleCompat.SpawnVehicles(pending, parms, pawns);
            }
            catch (Exception e)
            {
                Log.Warning("[Hardened Raids] véhicules : " + e);
            }
        }
    }

    // ---------------------------------------------------------------- lord séparé pour les véhicules
    /// <summary>Convoi en attente : rempli à l'apparition des véhicules, consommé à la création du lord du raid.</summary>
    public class ConvoyHandoff
    {
        public static ConvoyHandoff Pending;
        public List<Pawn> vehicles;
        public List<Pawn> dismount;
        public bool deployTurrets;
        public float tierX;
    }

    [HarmonyLib.HarmonyPatch(typeof(LordMaker), nameof(LordMaker.MakeNewLord))]
    public static class Patch_VehicleLord
    {
        public static void Prefix(Faction faction, LordJob lordJob, ref IEnumerable<Pawn> startingPawns, out List<Pawn> __state)
        {
            __state = null;
            if (!VehicleGate.Active || startingPawns == null || faction == null || faction.IsPlayer || !faction.HostileTo(Faction.OfPlayer)) return;
            if (lordJob is LordJob_HRConvoy || lordJob is LordJob_HRTurretCrew || lordJob is LordJob_HRBoard || VehicleCompat.IsVehicleLordJob(lordJob)) return;
            List<Pawn> all = startingPawns.ToList();
            List<Pawn> vehicles = all.Where(VehicleGate.IsVehicle).ToList();
            if (vehicles.Count == 0) { startingPawns = all; return; }
            List<Pawn> infantry = all.Where(p => !vehicles.Contains(p)).ToList();
            // les fantassins embarqués font partie du lord du raid dès le départ (ils le gardent en débarquant)
            ConvoyHandoff h = ConvoyHandoff.Pending;
            if (h != null && h.vehicles.Any(vehicles.Contains))
                foreach (Pawn p in h.dismount) if (!infantry.Contains(p) && p.GetLord() == null) infantry.Add(p);
            startingPawns = infantry;
            __state = vehicles;
        }

        public static void Postfix(Faction faction, Map map, List<Pawn> __state, Lord __result)
        {
            if (__state == null || __state.Count == 0) return;
            ConvoyHandoff h = ConvoyHandoff.Pending;
            ConvoyHandoff.Pending = null;
            try
            {
                bool mine = h != null && h.vehicles.Any(__state.Contains);
                LordMaker.MakeNewLord(faction, new LordJob_HRConvoy(faction, __result, mine ? h.dismount : null, mine && h.deployTurrets, mine ? h.tierX : 3f), map, __state);
            }
            catch (Exception e)
            {
                Log.Warning("[Hardened Raids] lord du convoi : " + e);
            }
        }
    }

    /// <summary>
    /// Débarquement : VF met le fantassin dans le lord du véhicule (et le retire du sien). Pour nos convois on le
    /// fait apparaître nous-mêmes en lui laissant son lord d'infanterie ; un équipage éjecté sans lord le rejoint.
    /// Patch manuel (HardenedRaidsMod) : la méthode est dans Vehicles.dll.
    /// </summary>
    public static class Patch_SpawnNearVehicle
    {
        public static bool Prefix(Pawn vehicle, Pawn pawn)
        {
            try
            {
                if (pawn == null || vehicle == null || pawn.Spawned || !vehicle.Spawned) return true;
                if (vehicle.GetLord()?.LordJob is not LordJob_HRConvoy convoy) return true;
                Map map = vehicle.Map;
                CellRect r = vehicle.OccupiedRect().ExpandedBy(1);
                IntVec3 cell = r.EdgeCells.Where(c => c.InBounds(map) && c.Standable(map) && c.GetFirstPawn(map) == null).RandomElementWithFallback(IntVec3.Invalid);
                if (!cell.IsValid) cell = CellFinder.RandomClosewalkCellNear(vehicle.Position, map, 6);
                GenSpawn.Spawn(pawn, cell, map);
                if (pawn.GetLord() == null) convoy.InfantryLord(map).AddPawn(pawn);
                return false;
            }
            catch (Exception e)
            {
                Log.WarningOnce("[Hardened Raids] débarquement : " + e.Message, 0x44454241);
                return true;
            }
        }

        public static void Apply(HarmonyLib.Harmony harmony)
        {
            if (!VehicleGate.Active) return;
            var m = HarmonyLib.AccessTools.Method("Vehicles.Ext_Vehicles:SpawnPawnNearVehicle");
            if (m == null) { Log.Warning("[Hardened Raids] Ext_Vehicles.SpawnPawnNearVehicle introuvable : le débarquement utilisera le lord de VF."); return; }
            harmony.Patch(m, prefix: new HarmonyLib.HarmonyMethod(typeof(Patch_SpawnNearVehicle), nameof(Prefix)));
        }
    }
}
