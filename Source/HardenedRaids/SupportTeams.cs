using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using HarmonyLib;
using RimWorld;
using UnityEngine;
using Verse;
using Verse.AI.Group;

namespace HardenedRaids
{
    /// <summary>
    /// Équipe d'appui : pendant un assaut d'un raid industriel+ de haut palier, une partie des raiders monte un camp
    /// avancé (lord de siège vanilla, enrichi par <see cref="Patch_SiegeBlueprints"/>) : tourelles servies,
    /// artillerie et couverture tournées vers la base, matériaux livrés par capsule.
    /// </summary>
    public static class SupportTeams
    {
        public static void TryDeploy(IncidentParms parms, List<Pawn> pawns)
        {
            if (!HRSettings.EnableSupportTeams || parms?.target is not Map map || parms.faction == null || pawns == null) return;
            if (parms.raidStrategy?.Worker == null || parms.raidStrategy.Worker is RaidStrategyWorker_Siege) return;
            if (parms.faction.def.techLevel < TechLevel.Industrial || !parms.faction.def.humanlikeFaction) return;
            if (parms.raidArrivalMode?.workerClass == null || !parms.raidArrivalMode.workerClass.Name.Contains("Edge")) return;
            float x = Tiers.TierX(parms.faction.def, parms.points);
            if (x < HRSettings.SupportMinTier) return;

            List<Pawn> cands = pawns.Where(p => p.Spawned && !p.Downed && p.RaceProps.Humanlike && !VehicleGate.IsVehicle(p)
                                                && !p.kindDef.factionLeader
                                                && !(p.equipment?.Primary != null && Loadouts.IsDestructive(p.equipment.Primary.def))
                                                && !p.WorkTypeIsDisabled(WorkTypeDefOf.Construction) && !p.WorkTypeIsDisabled(WorkTypeDefOf.Firefighter))
                .ToList();
            if (cands.Count < 10) return;
            int n = Mathf.Clamp(Mathf.RoundToInt(cands.Count * HRSettings.SupportShare), 3, 20);

            IntVec3 from = parms.spawnCenter.IsValid ? parms.spawnCenter : cands[0].Position;
            IntVec3 spot = RCellFinder.FindSiegePositionFrom(from, map, errorOnFail: false);
            if (!spot.IsValid)
            {
                HRLog.Msg("équipe d'appui : aucune position de camp trouvée.");
                return;
            }
            List<Pawn> team = cands.InRandomOrder().Take(n).ToList();
            foreach (Pawn p in team) pawns.Remove(p);
            float blueprintPoints = Mathf.Max(60f, parms.points * 0.25f);
            LordMaker.MakeNewLord(parms.faction, new LordJob_Siege(parms.faction, spot, blueprintPoints), map, team);
            Messages.Message($"Une équipe d'appui de {parms.faction.Name} ({team.Count} hommes) installe des positions de tir.",
                new TargetInfo(spot, map), MessageTypeDefOf.ThreatBig);
            HRLog.Msg($"équipe d'appui : {team.Count} raiders, camp en {spot} (palier {x:F1}).");
        }
    }

    [HarmonyPatch(typeof(IncidentWorker_Raid), nameof(IncidentWorker_Raid.TryGenerateRaidInfo))]
    public static class Patch_SupportTeam
    {
        [HarmonyPriority(Priority.VeryLow)] // après l'équipage des véhicules
        public static void Postfix(IncidentParms parms, ref List<Pawn> pawns, bool debugTest, bool __result)
        {
            if (!__result || debugTest) return;
            try { SupportTeams.TryDeploy(parms, pawns); }
            catch (Exception e) { Log.Warning("[Hardened Raids] équipe d'appui : " + e); }
        }
    }

    /// <summary>
    /// Munitions des tourelles ennemies.
    ///  - Une caisse de munitions est posée à côté de chaque tourelle servie : sous CE, un servant ne prend son poste
    ///    que s'il voit des munitions au sol (et le joueur peut les récupérer après).
    ///  - Tourelle servie (servant à son poste) ou automatique : chargeur CE / obus / carburant remis à niveau.
    /// Plus de ravitaillement quand il ne reste plus aucun raider près de la pièce.
    /// </summary>
    public class MapComponent_HostileTurretSupply : MapComponent
    {
        private static bool reflected;
        private static PropertyInfo compAmmoProp, curMag, magSize, curAmmo, props;
        private static MethodInfo resetAmmo;
        private static FieldInfo ammoSetField, ammoTypesField, ammoField;
        private readonly Dictionary<int, int> drops = new Dictionary<int, int>();

        public MapComponent_HostileTurretSupply(Map map) : base(map) { }

        public override void MapComponentTick()
        {
            if (Find.TickManager.TicksGame % 300 != 0) return;
            try
            {
                foreach (Building b in map.listerBuildings.allBuildingsNonColonist.ToList())
                {
                    if (b is not Building_Turret t || t.Faction == null || t.Faction.IsPlayer || !t.Faction.HostileTo(Faction.OfPlayer)) continue;
                    CompMannable man = t.GetComp<CompMannable>();
                    if (man != null)
                    {
                        if (!FriendNear(t, 15f)) continue;
                        if (!drops.TryGetValue(t.thingIDNumber, out int n)) n = 0;
                        if (n < 4 && !AmmoNear(t)) { DropAmmoNear(t, 3); drops[t.thingIDNumber] = n + 1; }
                        if (!man.MannedNow) continue;
                    }
                    Supply(t);
                }
            }
            catch (Exception e)
            {
                Log.WarningOnce("[Hardened Raids] ravitaillement des tourelles : " + e, 0x48524154);
            }
        }

        private static bool FriendNear(Building_Turret t, float r) =>
            t.Map.mapPawns.SpawnedPawnsInFaction(t.Faction).Any(p => p.RaceProps.Humanlike && !p.Downed && p.Position.InHorDistOf(t.Position, r));

        private static void Reflect(Type turretType)
        {
            if (reflected) return;
            reflected = true;
            compAmmoProp = turretType.GetProperty("CompAmmo");
            Type ammoUser = compAmmoProp?.PropertyType;
            if (ammoUser == null) return;
            curMag = ammoUser.GetProperty("CurMagCount");
            magSize = ammoUser.GetProperty("MagSize");
            curAmmo = ammoUser.GetProperty("CurrentAmmo");
            props = ammoUser.GetProperty("Props");
            resetAmmo = ammoUser.GetMethod("ResetAmmoCount");
            ammoSetField = props?.PropertyType.GetField("ammoSet");
            ammoTypesField = ammoSetField?.FieldType.GetField("ammoTypes");
            Type linkType = ammoTypesField?.FieldType.GetGenericArguments().FirstOrDefault();
            ammoField = linkType?.GetField("ammo");
        }

        private static object CEComp(Building_Turret t)
        {
            PropertyInfo pi = t.GetType().GetProperty("CompAmmo");
            if (pi == null) return null;
            if (!reflected) Reflect(t.GetType());
            return pi.GetValue(t);
        }

        /// <summary>Munition du chargeur CE (actuelle, sinon la première du jeu de munitions).</summary>
        private static ThingDef CEAmmo(object comp, out List<ThingDef> all)
        {
            all = new List<ThingDef>();
            if (props != null && ammoSetField != null && ammoTypesField != null && ammoField != null)
            {
                object set = ammoSetField.GetValue(props.GetValue(comp));
                if (set != null && ammoTypesField.GetValue(set) is System.Collections.IList links)
                    foreach (object l in links) if (ammoField.GetValue(l) is ThingDef d) all.Add(d);
            }
            return curAmmo?.GetValue(comp) as ThingDef ?? all.FirstOrDefault();
        }

        private static bool AmmoNear(Building_Turret t)
        {
            object comp = CEComp(t);
            List<ThingDef> ok;
            if (comp != null) CEAmmo(comp, out ok);
            else ok = null;
            foreach (Thing th in GenRadial.RadialDistinctThingsAround(t.Position, t.Map, 8f, true))
            {
                if (th.def.category != ThingCategory.Item) continue;
                if (ok != null ? ok.Contains(th.def) : th.def.IsShell) return true;
            }
            return false;
        }

        /// <summary>Pose une caisse de munitions (CE) ou d'obus à côté de la tourelle.</summary>
        public static void DropAmmoNear(Building_Turret t, int mags)
        {
            ThingDef ammo = null;
            int count = 10;
            object comp = CEComp(t);
            if (comp != null)
            {
                ammo = CEAmmo(comp, out _);
                int mag = magSize != null ? (int)magSize.GetValue(comp) : 10;
                count = Mathf.Max(10, mag * mags);
            }
            else if (t is Building_TurretGun g && g.gun?.TryGetComp<CompChangeableProjectile>() != null)
            {
                ammo = TurretGunUtility.TryFindRandomShellDef(t.def, false, false, true, t.Faction.def.techLevel, false, 250f, t.Faction);
                count = 2 * mags;
            }
            if (ammo == null) return;
            count = Mathf.Min(count, ammo.stackLimit * 2);
            IntVec3 at = t.def.hasInteractionCell ? t.InteractionCell : t.Position;
            while (count > 0)
            {
                Thing stack = ThingMaker.MakeThing(ammo);
                stack.stackCount = Mathf.Min(count, ammo.stackLimit);
                count -= stack.stackCount;
                if (!GenPlace.TryPlaceThing(stack, at, t.Map, ThingPlaceMode.Near)) break;
            }
        }

        private static void Supply(Building_Turret t)
        {
            // Combat Extended : chargeur
            object comp = CEComp(t);
            if (comp != null && curMag != null && magSize != null && resetAmmo != null)
            {
                int mag = (int)magSize.GetValue(comp), cur = (int)curMag.GetValue(comp);
                if (mag > 0 && cur <= mag / 4) resetAmmo.Invoke(comp, new object[] { CEAmmo(comp, out _) });
            }

            // vanilla : obus (mortier) et carburant / canon
            if (t is Building_TurretGun g && g.gun != null)
            {
                CompChangeableProjectile shell = g.gun.TryGetComp<CompChangeableProjectile>();
                if (shell != null && !shell.Loaded)
                {
                    ThingDef s = TurretGunUtility.TryFindRandomShellDef(t.def, false, false, true, t.Faction.def.techLevel, false, 250f, t.Faction);
                    if (s != null) shell.LoadShell(s, 1);
                }
            }
            CompRefuelable fuel = t.GetComp<CompRefuelable>();
            if (fuel != null && fuel.FuelPercentOfMax < 0.3f) fuel.Refuel(fuel.Props.fuelCapacity - fuel.Fuel);
        }
    }
}
