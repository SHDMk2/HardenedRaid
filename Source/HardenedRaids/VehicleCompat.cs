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

        /// <summary>
        /// Case libre pour tout le rectangle du véhicule, près du point d'arrivée. VF ne construit la grille de
        /// passage d'un type de véhicule qu'à sa première apparition sur la carte : sans la forcer, toutes les
        /// cases sont refusées et le véhicule était détruit en silence (cause de l'absence de blindés).
        /// </summary>
        private static bool FindVehicleCell(VehicleDef vd, Map map, IntVec3 center, Rot4 rot, HashSet<IntVec3> taken, out IntVec3 cell)
        {
            // VF génère la grille de passage d'un type de véhicule à la demande : sans elle tout est "infranchissable"
            Vehicles.World.Ext_Caravan.EnsureMapInitialized(vd, map);
            int num = GenRadial.NumCellsInRadius(40f);
            for (int i = 0; i < num; i++)
            {
                IntVec3 c = center + GenRadial.RadialPattern[i];
                if (!c.InBounds(map)) continue;
                CellRect r = vd.VehicleRect(c, rot);
                if (!r.ExpandedBy(1).InBounds(map)) continue;
                bool ok = true;
                foreach (IntVec3 x in r)
                {
                    if (taken.Contains(x) || !x.Standable(vd, map) || x.Fogged(map) || x.GetFirstPawn(map) != null) { ok = false; break; }
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

        /// <summary>Fait apparaître les véhicules près du point d'arrivée et y installe un équipage pris parmi les raiders.</summary>
        public static void SpawnVehicles(List<ThingDef> defs, IncidentParms parms, List<Pawn> pawns)
        {
            if (parms.target is not Map map) return;
            IntVec3 center = parms.spawnCenter.IsValid ? parms.spawnCenter : pawns.FirstOrDefault(p => p.Spawned)?.Position ?? IntVec3.Invalid;
            if (!center.IsValid) { HRLog.Msg("véhicules : pas de point d'arrivée."); return; }

            // équipage : des fantassins "de base" (pas le chef, pas les porteurs d'armes de brèche de préférence)
            List<Pawn> pool = pawns.Where(p => p.Spawned && !p.Downed && p.RaceProps.Humanlike && !p.kindDef.factionLeader)
                .OrderBy(p => p.equipment?.Primary != null && Loadouts.IsDestructive(p.equipment.Primary.def) ? 1 : 0)
                .ToList();
            IntVec3 target = Patch_SiegeBlueprints.ThreatTarget(map, center);
            var taken = new HashSet<IntVec3>();
            int spawned = 0;
            foreach (ThingDef d in defs)
            {
                if (d is not VehicleDef vd) continue;
                VehiclePawn vehicle = null;
                try
                {
                    Rot4 rot = Rot4.FromAngleFlat((target - center).AngleFlat);
                    if (!FindVehicleCell(vd, map, center, rot, taken, out IntVec3 cell))
                    {
                        HRLog.Msg($"véhicule {vd.defName} : aucune place libre près de {center}.");
                        continue;
                    }
                    vehicle = VehicleSpawner.GenerateVehicle(new VehicleGenerationRequest(vd, parms.faction, randomizeColors: true, randomizeMask: true));
                    GenSpawn.Spawn(vehicle, cell, map, rot);

                    // équipage : uniquement les rôles qui conduisent / servent une tourelle
                    int crewed = 0;
                    foreach (VehicleRoleHandler h in vehicle.handlers.Where(h => h.role.HandlingTypes != HandlingType.None))
                    {
                        while (h.AreSlotsAvailable && pool.Count > 0)
                        {
                            Pawn crew = pool.FirstOrDefault(h.CanOperateRole);
                            if (crew == null) break;
                            pool.Remove(crew);
                            if (vehicle.TryAddPawn(crew, h)) { pawns.Remove(crew); crewed++; }
                        }
                    }
                    if (crewed == 0)
                    {
                        HRLog.Msg($"véhicule {vd.defName} : aucun équipage possible ({vehicle.handlers.Count} postes, {pool.Count} raiders disponibles) -> retiré.");
                        vehicle.Destroy();
                        continue;
                    }
                    Supply(vehicle);
                    pawns.Add(vehicle);
                    spawned++;
                    HRLog.Msg($"véhicule {vd.label} en {cell} : {crewed} membres d'équipage.");
                }
                catch (Exception e)
                {
                    Log.Warning($"[Hardened Raids] véhicule {d.defName} : {e}");
                    if (vehicle != null && !vehicle.Destroyed && !pawns.Contains(vehicle)) vehicle.Destroy();
                }
            }
            HRLog.Msg($"{spawned}/{defs.Count} véhicule(s) pour {parms.faction.Name}.");
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
    [HarmonyLib.HarmonyPatch(typeof(LordMaker), nameof(LordMaker.MakeNewLord))]
    public static class Patch_VehicleLord
    {
        public static void Prefix(Faction faction, LordJob lordJob, ref IEnumerable<Pawn> startingPawns, out List<Pawn> __state)
        {
            __state = null;
            if (!VehicleGate.Active || startingPawns == null || faction == null || faction.IsPlayer || !faction.HostileTo(Faction.OfPlayer)) return;
            if (VehicleCompat.IsVehicleLordJob(lordJob)) return;
            List<Pawn> all = startingPawns.ToList();
            List<Pawn> vehicles = all.Where(VehicleCompat.IsVehicle).ToList();
            if (vehicles.Count == 0) { startingPawns = all; return; }
            startingPawns = all.Where(p => !vehicles.Contains(p)).ToList();
            __state = vehicles;
        }

        public static void Postfix(Faction faction, Map map, List<Pawn> __state)
        {
            if (__state == null || __state.Count == 0) return;
            try
            {
                LordMaker.MakeNewLord(faction, VehicleCompat.ArmoredLordJob(faction), map, __state);
            }
            catch (Exception e)
            {
                Log.Warning("[Hardened Raids] lord blindé : " + e.Message);
            }
        }
    }
}
