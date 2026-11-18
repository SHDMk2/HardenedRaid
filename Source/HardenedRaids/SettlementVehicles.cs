using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using RimWorld;
using UnityEngine;
using Vehicles;
using Verse;
using Verse.AI.Group;

namespace HardenedRaids
{
    /// <summary>
    /// Lot uncap, D2 : véhicules garés autour des colonies hostiles (Vehicle Framework). Touche aux types VF : appelé
    /// seulement si VehicleGate.Active (la signature publique n'expose aucun type VF).
    ///
    /// Choix : défense sur place, sans lord. VF met tout véhicule non joueur en « drafted » et active le tir automatique
    /// de ses tourelles à l'apparition (VehiclePawn.SpawnSetup) ; une tourelle tire dès que son poste de tireur est occupé
    /// (VehicleTurret.RecacheMannedStatus). Sans lord, l'arbre de pensée VF tombe sur JobGiver_AwaitOrders : le véhicule
    /// reste garé et tire sur tout ce qui entre à portée. Aucun lord vanilla (DefendBase) : ses JobGivers humains ne sont
    /// pas faits pour un VehiclePawn. Rien de nouveau n'est sauvegardé (véhicule + équipage = objets VF ordinaires).
    /// Équipage : seulement les postes de tireur, pris parmi les défenseurs les plus proches (pas le chef). Un transport
    /// sans tourelle reste garé vide (obstacle, butin) et ne consomme aucun défenseur.
    /// </summary>
    public static class SettlementVehicles
    {
        public const int DefendersPerVehicle = 15;
        public const float CombatChance = 0.75f;

        private static readonly MethodInfo supply = HarmonyLib.AccessTools.Method(typeof(VehicleCompat), "Supply");

        public static void Park(SettlementDefense.Context ctx, List<Pawn> defenders)
        {
            Map map = ctx.map;
            int n = Mathf.Min(defenders.Count / DefendersPerVehicle, Mathf.Max(1, HRSettings.MaxVehicles));
            if (n <= 0) return;
            var rng = new System.Random(Rand.Int);
            // modèles : la sélection des raids (palier, tech de la faction), sans dépenser les points des défenseurs
            List<ThingDef> combat = VehicleCompat.Plan(ctx.faction, ctx.points, ctx.tierX, 4, rng, out _);
            List<ThingDef> transport = VehicleCompat.PlanTransports(ctx.faction, ctx.points, ctx.tierX, 30, rng, out _).Distinct().ToList();
            if (combat.Count == 0 && transport.Count == 0) { HRLog.Msg($"colonie {ctx.faction.Name} : aucun véhicule disponible."); return; }

            CellRect area = SettlementArea(map, ctx.faction);
            if (area.Area == 0) return;
            var taken = new HashSet<IntVec3>();
            var log = new List<string>();
            int ci = 0, ti = 0;
            for (int i = 0; i < n; i++)
            {
                bool wantCombat = Rand.Chance(CombatChance);
                ThingDef def = wantCombat && combat.Count > 0 ? combat[ci++ % combat.Count]
                    : transport.Count > 0 ? transport[ti++ % transport.Count] : combat[ci++ % combat.Count];
                if (def is not VehicleDef vd) continue;
                VehiclePawn v = null;
                try
                {
                    v = SpawnAt(vd, ctx.faction, map, area, taken);
                    if (v == null) continue;
                    int gunners = Crew(v, defenders);
                    bool hasTurrets = v.CompVehicleTurrets != null && v.CompVehicleTurrets.Turrets.Count > 0;
                    if (hasTurrets && gunners == 0)
                    {
                        HRLog.Msg($"véhicule {vd.defName} : aucun tireur disponible -> retiré.");
                        v.Destroy();
                        continue;
                    }
                    try { supply?.Invoke(null, new object[] { v }); } catch (Exception e) { HRLog.Msg("plein du véhicule : " + e.Message); }
                    log.Add($"{vd.defName}{(hasTurrets ? $" ({gunners} tireur(s))" : " (vide)")}");
                }
                catch (Exception e)
                {
                    Log.Warning($"[Hardened Raids] settlement vehicle {vd.defName}: {e}");
                    if (v != null && !v.Destroyed)
                    {
                        foreach (Pawn p in v.AllPawnsAboard.ToList()) v.DisembarkPawn(p);
                        v.Destroy();
                    }
                }
            }
            HRLog.Msg($"colonie {ctx.faction.Name} : {log.Count}/{n} véhicule(s) garé(s) : {string.Join(", ", log)}");
        }

        /// <summary>Rectangle des bâtiments de la faction (vanilla et KCSG : pas besoin de connaître le générateur).</summary>
        private static CellRect SettlementArea(Map map, Faction f)
        {
            int minX = int.MaxValue, minZ = int.MaxValue, maxX = int.MinValue, maxZ = int.MinValue;
            foreach (Building b in map.listerBuildings.allBuildingsNonColonist)
            {
                if (b.Faction != f) continue;
                IntVec3 c = b.Position;
                if (c.x < minX) minX = c.x;
                if (c.z < minZ) minZ = c.z;
                if (c.x > maxX) maxX = c.x;
                if (c.z > maxZ) maxZ = c.z;
            }
            if (minX > maxX) return CellRect.Empty;
            return CellRect.FromLimits(minX, minZ, maxX, maxZ).ClipInsideMap(map);
        }

        /// <summary>Case au bord de la colonie (anneau de 1 à 8 cases autour des bâtiments), tournée vers l'extérieur.</summary>
        private static VehiclePawn SpawnAt(VehicleDef vd, Faction f, Map map, CellRect area, HashSet<IntVec3> taken)
        {
            Vehicles.World.Ext_Caravan.EnsureMapInitialized(vd, map);
            IntVec3 center = area.CenterCell;
            for (int ring = 1; ring <= 8; ring++)
            {
                foreach (IntVec3 c in area.ExpandedBy(ring).EdgeCells.InRandomOrder())
                {
                    if (!c.InBounds(map)) continue;
                    Rot4 rot = Outward(c, center);
                    CellRect r = vd.VehicleRect(c, rot);
                    if (!r.ExpandedBy(1).InBounds(map)) continue;
                    bool ok = true;
                    foreach (IntVec3 x in r)
                        if (taken.Contains(x) || !x.Standable(vd, map) || x.Roofed(map) || x.GetFirstPawn(map) != null) { ok = false; break; }
                    if (!ok) continue;
                    if (!map.reachability.CanReach(c, center, Verse.AI.PathEndMode.Touch, TraverseParms.For(TraverseMode.PassDoors))) continue;
                    foreach (IntVec3 x in r.ExpandedBy(1)) taken.Add(x);
                    VehiclePawn v = VehicleSpawner.GenerateVehicle(new VehicleGenerationRequest(vd, f, randomizeColors: true, randomizeMask: true));
                    GenSpawn.Spawn(v, c, map, rot);
                    return v;
                }
            }
            HRLog.Msg($"véhicule {vd.defName} : aucune place libre autour de la colonie.");
            return null;
        }

        private static Rot4 Outward(IntVec3 c, IntVec3 center)
        {
            IntVec3 d = c - center;
            return d.x == 0 && d.z == 0 ? Rot4.North : Rot4.FromAngleFlat(d.AngleFlat);
        }

        /// <summary>Postes de tireur : défenseurs les plus proches, retirés de leur lord avant d'embarquer.</summary>
        private static int Crew(VehiclePawn v, List<Pawn> defenders)
        {
            int n = 0;
            foreach (VehicleRoleHandler h in v.handlers.Where(h => (h.role.HandlingTypes & HandlingType.Turret) != 0).ToList())
            {
                while (h.AreSlotsAvailable)
                {
                    Pawn p = defenders.Where(x => !x.kindDef.factionLeader && x.Spawned && h.CanOperateRole(x))
                        .OrderBy(x => (x.Position - v.Position).LengthHorizontalSquared).FirstOrDefault();
                    if (p == null) return n;
                    Verse.AI.Group.Lord lord = p.GetLord();
                    lord?.RemovePawn(p);
                    if (!v.TryAddPawn(p, h))
                    {
                        lord?.AddPawn(p);
                        defenders.Remove(p);   // pas réessayé
                        continue;
                    }
                    defenders.Remove(p);
                    n++;
                }
            }
            return n;
        }
    }

    /// <summary>
    /// Équipage éjecté d'un véhicule garé (véhicule sans lord) : VF le laisse sans lord. Il rejoint le lord de défense
    /// de base de sa faction sur la carte, comme les autres défenseurs. Patch sur Vehicles.dll, actif seulement avec VF.
    /// </summary>
    [HarmonyLib.HarmonyPatch]
    public static class Patch_SettlementCrewEjected
    {
        public static bool Prepare() => VehicleGate.Active && TargetMethod() != null;

        public static MethodBase TargetMethod() => HarmonyLib.AccessTools.Method("Vehicles.Ext_Vehicles:SpawnPawnNearVehicle");

        public static void Postfix(Pawn pawn)
        {
            try
            {
                if (pawn == null || !pawn.Spawned || pawn.GetLord() != null || pawn.Faction == null || pawn.Faction.IsPlayer || !pawn.RaceProps.Humanlike) return;
                List<Lord> lords = pawn.Map.lordManager.lords;
                for (int i = 0; i < lords.Count; i++)
                {
                    LordJob j = lords[i].LordJob;
                    if (lords[i].faction == pawn.Faction && j != null && (j is LordJob_DefendBase || j.GetType().Name.StartsWith("LordJob_DefendBase")))
                    {
                        lords[i].AddPawn(pawn);
                        return;
                    }
                }
            }
            catch (Exception e)
            {
                Log.WarningOnce("[Hardened Raids] ejected crew: " + e.Message, 0x48525345);
            }
        }
    }
}
