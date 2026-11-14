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
            Lord camp = LordMaker.MakeNewLord(parms.faction, new LordJob_Siege(parms.faction, spot, blueprintPoints), map, team);
            MapComponent_HostileTurretSupply.RegisterSupportLord(camp);   // ses tourelles seront ravitaillées
            Messages.Message(Strings.T("MsgSupportTeam", parms.faction.Name, team.Count),
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
            catch (Exception e) { Log.Warning("[Hardened Raids] support team: " + e); }
        }
    }

    /// <summary>
    /// Munitions des tourelles ennemies posées par le mod, et seulement elles (registre sauvegardé) :
    ///  - camps de siège améliorés (<see cref="Patch_SiegeBlueprints"/>, réglage SiegeUpgrade) et camps des équipes
    ///    d'appui : tourelles construites par un lord de siège (vanilla les lui ajoute, Frame.CompleteConstruction) ;
    ///  - tourelles déchargées des camions (<see cref="TruckTurrets"/>, lord LordJob_HRTurretCrew).
    /// Aucune autre tourelle n'est touchée (joueur, alliés, grappes mécanoïdes, sites, colonies, autres mods).
    ///  - Une caisse de munitions est posée à côté de chaque tourelle servie : sous CE, un servant ne prend son poste
    ///    que s'il voit des munitions au sol (et le joueur peut les récupérer après).
    ///  - Tourelle servie (servant à son poste) ou automatique : chargeur CE / obus / carburant remis à niveau.
    /// Plus de ravitaillement quand il ne reste plus près de la pièce aucun raider de la faction dans un lord actif.
    /// </summary>
    public class MapComponent_HostileTurretSupply : MapComponent
    {
        private static bool reflected;
        private static PropertyInfo compAmmoProp, curMag, magSize, curAmmo, props;
        private static MethodInfo resetAmmo;
        private static FieldInfo ammoSetField, ammoTypesField, ammoField;

        // état sauvegardé (absent d'une save v1.0 : vide)
        private Dictionary<int, int> drops = new Dictionary<int, int>();           // caisses déjà posées, par thingIDNumber
        private List<Building_Turret> turrets = new List<Building_Turret>();        // registre
        private List<Lord> supportLords = new List<Lord>();                         // camps des équipes d'appui
        private List<int> dropKeys, dropValues;

        public MapComponent_HostileTurretSupply(Map map) : base(map) { }

        public static MapComponent_HostileTurretSupply For(Map map) => map?.GetComponent<MapComponent_HostileTurretSupply>();

        public static void Register(Building_Turret t)
        {
            MapComponent_HostileTurretSupply c = For(t?.Map);
            if (c != null && !t.Destroyed && !c.turrets.Contains(t)) c.turrets.Add(t);
        }

        public static void RegisterSupportLord(Lord l)
        {
            MapComponent_HostileTurretSupply c = For(l?.Map);
            if (c != null && !c.supportLords.Contains(l)) c.supportLords.Add(l);
        }

        public bool IsRegistered(Building_Turret t) => turrets.Contains(t);

        private static bool Gone(Thing t) => t == null || t.Destroyed;   // Destroyed inclut Discarded

        private static bool HostileNpc(Faction f) => f != null && !f.IsPlayer && f.HostileTo(Faction.OfPlayer);

        /// <summary>Tourelles construites par nos camps de siège / d'appui et tourelles de camion : ajoutées au registre.</summary>
        private void ScanLords()
        {
            List<Lord> lords = map.lordManager.lords;
            supportLords.RemoveAll(l => l == null || !lords.Contains(l));
            for (int i = 0; i < lords.Count; i++)
            {
                Lord l = lords[i];
                if (l.ownedBuildings.Count == 0 || !HostileNpc(l.faction)) continue;
                bool ours = l.LordJob is LordJob_HRTurretCrew
                            || (l.LordJob is LordJob_Siege && (HRSettings.SiegeUpgrade || supportLords.Contains(l)));
                if (!ours) continue;
                foreach (Building b in l.ownedBuildings)
                    if (b is Building_Turret t && !t.Destroyed && !turrets.Contains(t)) turrets.Add(t);
            }
        }

        private void Purge()
        {
            turrets.RemoveAll(Gone);
            if (drops.Count > 0)
            {
                var keep = new HashSet<int>(turrets.Select(t => t.thingIDNumber));
                foreach (int k in drops.Keys.Where(k => !keep.Contains(k)).ToList()) drops.Remove(k);
            }
            supportLords.RemoveAll(l => l == null || !map.lordManager.lords.Contains(l));
        }

        public override void MapComponentTick()
        {
            if (Find.TickManager.TicksGame % 300 != 0) return;
            try
            {
                ScanLords();
                if (turrets.Count == 0) return;
                for (int i = turrets.Count - 1; i >= 0; i--)
                {
                    Building_Turret t = turrets[i];
                    if (Gone(t)) { if (t != null) drops.Remove(t.thingIDNumber); turrets.RemoveAt(i); continue; }
                    if (!t.Spawned || t.Map != map || !HostileNpc(t.Faction)) continue;   // capturée, abandonnée, désinstallée
                    CompMannable man = t.GetComp<CompMannable>();
                    if (man != null)
                    {
                        if (!FriendNear(t, 15f)) continue;
                        if (!drops.TryGetValue(t.thingIDNumber, out int n)) n = 0;
                        if (n < 4 && !AmmoNear(t)) { DropAmmoNear(t, 3); drops[t.thingIDNumber] = n + 1; }
                        if (!man.MannedNow) continue;
                    }
                    else if (!FriendNear(t, 20f)) continue;
                    Supply(t);
                }
            }
            catch (Exception e)
            {
                Log.WarningOnce("[Hardened Raids] turret resupply: " + e, 0x48524154);
            }
        }

        public override void ExposeData()
        {
            base.ExposeData();
            if (Scribe.mode == LoadSaveMode.Saving)
            {
                try { Purge(); }
                catch (Exception e) { Log.Warning("[Hardened Raids] turret registry cleanup: " + e.Message); }
            }
            Scribe_Collections.Look(ref turrets, "turrets", LookMode.Reference);
            Scribe_Collections.Look(ref supportLords, "supportLords", LookMode.Reference);
            Scribe_Collections.Look(ref drops, "drops", LookMode.Value, LookMode.Value, ref dropKeys, ref dropValues);
            if (Scribe.mode == LoadSaveMode.PostLoadInit)
            {
                turrets ??= new List<Building_Turret>();
                supportLords ??= new List<Lord>();
                drops ??= new Dictionary<int, int>();
                turrets.RemoveAll(Gone);            // référence non résolue = null
                supportLords.RemoveAll(l => l == null);
            }
        }

        /// <summary>Préparer la désactivation : registre, caisses posées et camps oubliés sur toutes les cartes.</summary>
        public static void PrepareUninstall()
        {
            foreach (Map m in Find.Maps)
            {
                MapComponent_HostileTurretSupply c = For(m);
                if (c == null) continue;
                c.turrets.Clear();
                c.supportLords.Clear();
                c.drops.Clear();
            }
        }

        /// <summary>Un raider de la faction de la tourelle, debout, dans un lord actif (pas en fuite / sortie), à portée.</summary>
        private static bool FriendNear(Building_Turret t, float r) =>
            t.Map.mapPawns.SpawnedPawnsInFaction(t.Faction).Any(p => p.RaceProps.Humanlike && !p.Downed && p.Position.InHorDistOf(t.Position, r)
                                                                    && p.GetLord() is Lord l && !LordJob_HRConvoy.ExitLike(l));

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
