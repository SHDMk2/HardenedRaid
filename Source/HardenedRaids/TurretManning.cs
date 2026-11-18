using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using HarmonyLib;
using RimWorld;
using Verse;
using Verse.AI;
using Verse.AI.Group;

namespace HardenedRaids
{
    /// <summary>
    /// Servants des tourelles ennemies (toute tourelle servie : vanilla, Fortifications, VFE Security…, détectée à
    /// l'exécution, aucune référence dure). Rien n'est sauvegardé par le mod : seules des duties / jobs vanilla
    /// (ManClosestTurret, ManTurret) sont posés, que vanilla réécrit lui-même si le mod est retiré.
    ///  - Camps de siège / d'appui : vanilla ne fait servir les pièces que par les bâtisseurs (duty Build), et
    ///    seulement quand il n'y a plus rien à construire ni d'ennemi à 35 cases ; la duty Defend ne sert jamais.
    ///    Chaque pièce construite reçoit donc un servant attitré (duty vanilla ManClosestTurret : servir passe avant
    ///    le combat), pris parmi les défenseurs, sinon parmi les bâtisseurs en en gardant toujours un.
    ///  - JobGiver_ManTurrets vanilla exige un obus au sol pour toute tourelle (même sans obus : autocanon moddé
    ///    sans CE), et la version CE une caisse de munitions au sol : une tourelle déjà chargée est servie quand même.
    ///  - Les tourelles « à occupant » (bunkers Fortifications : le pawn entre dans le bâtiment, sans CompMannable)
    ///    sont retirées des catalogues : l'IA ne sait pas y entrer, elles ne tireraient jamais.
    /// </summary>
    public static class TurretManning
    {
        /// <summary>Portée vanilla des servants de siège (JobGiver_ManTurretsNearPoint de la duty Build).</summary>
        public const float SiegeRadius = 25f;

        // ------------------------------------------------------------ catalogue
        /// <summary>Tourelle qui ne tire qu'avec un pawn à l'intérieur (Building_TurretCapacity de Fortifications…).</summary>
        public static bool RequiresOccupant(ThingDef d)
        {
            Type c = d?.thingClass;
            if (c == null || d.HasComp(typeof(CompMannable))) return false;
            return c.GetInterfaces().Any(i => i.Name == "IPawnCapacity")
                   || (typeof(IThingHolder).IsAssignableFrom(c) && c.Name.Contains("Capacity"));
        }

        /// <summary>Pièce à servir : tourelle avec CompMannable et case de servant.</summary>
        public static bool NeedsGunner(Thing b) =>
            b is Building_Turret && b.def.hasInteractionCell && b.TryGetComp<CompMannable>() != null;

        // ------------------------------------------------------------ état de charge (CE par réflexion)
        private static bool ceInit;
        private static Type ceTurret;
        private static PropertyInfo ceCompAmmo, ceUseAmmo, ceCurMag;

        private static void InitCE()
        {
            if (ceInit) return;
            ceInit = true;
            ceTurret = AccessTools.TypeByName("CombatExtended.Building_TurretGunCE");
            ceCompAmmo = ceTurret != null ? AccessTools.Property(ceTurret, "CompAmmo") : null;
            Type user = ceCompAmmo?.PropertyType;
            ceUseAmmo = user != null ? AccessTools.Property(user, "UseAmmo") : null;
            ceCurMag = user != null ? AccessTools.Property(user, "CurMagCount") : null;
        }

        /// <summary>La pièce peut tirer sans qu'on lui apporte d'abord des munitions / du carburant.</summary>
        public static bool Loaded(Building_Turret t)
        {
            CompRefuelable fuel = t.GetComp<CompRefuelable>();
            if (fuel != null && !fuel.HasFuel) return false;
            InitCE();
            if (ceTurret != null && ceTurret.IsInstanceOfType(t) && ceCompAmmo != null)
            {
                object comp = ceCompAmmo.GetValue(t);
                if (comp == null) return true;
                if (ceUseAmmo != null && !(bool)ceUseAmmo.GetValue(comp)) return true;
                return ceCurMag == null || (int)ceCurMag.GetValue(comp) > 0;
            }
            if (t is Building_TurretGun g)
            {
                CompChangeableProjectile shell = g.gun?.TryGetComp<CompChangeableProjectile>();
                if (shell != null) return shell.Loaded;
            }
            return true;
        }

        // ------------------------------------------------------------ servants attitrés des camps de siège
        private static readonly MethodInfo setAsBuilder = AccessTools.Method(typeof(LordToil_Siege), "SetAsBuilder");
        private static readonly MethodInfo canBeBuilder = AccessTools.Method(typeof(LordToil_Siege), "CanBeBuilder");

        private static bool CanGun(Pawn p) =>
            p != null && p.Spawned && !p.Downed && !p.Dead && !p.InMentalState && p.RaceProps.Humanlike
            && !VehicleGate.IsVehicle(p) && !p.kindDef.factionLeader && !p.WorkTagIsDisabled(WorkTags.Violent)
            && p.mindState != null;

        private static bool IsBuilder(Pawn p) => p.mindState?.duty?.def == DutyDefOf.Build;

        /// <summary>
        /// Après LordToil_Siege.UpdateAllDuties : un servant par pièce servie construite du camp. Les servants du
        /// passage précédent (prev : pawn → pièce) gardent leur pièce ; un bâtisseur pris est remplacé par un défenseur.
        /// </summary>
        public static void AssignSiegeGunners(LordToil_Siege toil, Dictionary<Pawn, IntVec3> prev)
        {
            Lord lord = toil?.lord;
            Map map = lord?.Map;
            if (map == null || lord.faction == null || lord.ticksInToil < 450) return;   // vanilla : tous défenseurs avant 450
            if (!HRSettings.SiegeUpgrade && MapComponent_HostileTurretSupply.For(map)?.IsSupportLord(lord) != true) return;

            IntVec3 flag = toil.FlagLoc;
            List<Building> pieces = map.listerBuildings.allBuildingsNonColonist
                .Where(b => b.Spawned && b.Faction == lord.faction && NeedsGunner(b) && b.Position.InHorDistOf(flag, SiegeRadius)).ToList();
            if (pieces.Count == 0) return;

            var gunners = new Dictionary<Building, Pawn>();
            var used = new HashSet<Pawn>();
            int builders = lord.ownedPawns.Count(IsBuilder);
            int stolen = 0;

            bool Take(Pawn p, Building b)
            {
                if (IsBuilder(p))
                {
                    if (builders <= 1) return false;   // toujours un bâtisseur tant que vanilla en veut
                    builders--;
                    stolen++;
                }
                gunners[b] = p;
                used.Add(p);
                return true;
            }

            // 1. les servants déjà en place gardent la pièce la plus proche de leur poste
            if (prev != null)
                foreach (KeyValuePair<Pawn, IntVec3> kv in prev)
                {
                    if (!lord.ownedPawns.Contains(kv.Key) || !CanGun(kv.Key)) continue;
                    Building b = pieces.Where(x => !gunners.ContainsKey(x)).OrderBy(x => (x.Position - kv.Value).LengthHorizontalSquared).FirstOrDefault();
                    if (b == null) break;
                    Take(kv.Key, b);
                }

            // 2. pièces sans servant : le défenseur le plus proche, sinon un bâtisseur
            int fresh = 0;
            foreach (Building b in pieces)
            {
                if (gunners.ContainsKey(b)) continue;
                IEnumerable<Pawn> free = lord.ownedPawns.Where(x => !used.Contains(x) && CanGun(x));
                Pawn p = free.Where(x => !IsBuilder(x)).OrderBy(x => (x.Position - b.Position).LengthHorizontalSquared).FirstOrDefault()
                         ?? (builders > 1 ? free.Where(IsBuilder).OrderBy(x => (x.Position - b.Position).LengthHorizontalSquared).FirstOrDefault() : null);
                if (p != null && Take(p, b)) fresh++;
            }

            foreach (KeyValuePair<Building, Pawn> kv in gunners)
            {
                kv.Value.mindState.duty = new PawnDuty(DutyDefOf.ManClosestTurret, kv.Key.Position);
                toil.rememberedDuties[kv.Value] = DutyDefOf.ManClosestTurret;
            }

            // bâtisseurs pris : remplacés par des défenseurs capables de construire (comme vanilla les choisit)
            if (stolen > 0 && setAsBuilder != null && canBeBuilder != null)
                foreach (Pawn p in lord.ownedPawns.Where(x => !used.Contains(x) && x.mindState?.duty?.def == DutyDefOf.Defend).ToList())
                {
                    if (stolen <= 0) break;
                    if (!(bool)canBeBuilder.Invoke(toil, new object[] { p })) continue;
                    setAsBuilder.Invoke(toil, new object[] { p });
                    toil.rememberedDuties[p] = DutyDefOf.Build;
                    stolen--;
                }

            if (fresh > 0)
                HRLog.Msg($"siège {lord.faction.Name} : {fresh} nouveau(x) servant(s), {gunners.Count}/{pieces.Count} pièce(s) servie(s).");
        }
    }

    [HarmonyPatch(typeof(LordToil_Siege), nameof(LordToil_Siege.UpdateAllDuties))]
    public static class Patch_SiegeGunners
    {
        public static void Prefix(LordToil_Siege __instance, out Dictionary<Pawn, IntVec3> __state)
        {
            __state = null;
            List<Pawn> pawns = __instance.lord?.ownedPawns;
            if (pawns == null) return;
            foreach (Pawn p in pawns)
            {
                PawnDuty d = p?.mindState?.duty;
                if (d != null && d.def == DutyDefOf.ManClosestTurret)
                    (__state ??= new Dictionary<Pawn, IntVec3>())[p] = d.focus.Cell;
            }
        }

        public static void Postfix(LordToil_Siege __instance, Dictionary<Pawn, IntVec3> __state)
        {
            try { TurretManning.AssignSiegeGunners(__instance, __state); }
            catch (Exception e) { Log.WarningOnce("[Hardened Raids] siege gunners: " + e, 0x48524755); }
        }
    }

    /// <summary>
    /// JobGiver_ManTurrets (vanilla, et ses versions CE) sans résultat pour un PNJ : servir la tourelle servie de sa
    /// faction la plus proche qui peut déjà tirer (chargeur CE non vide, obus chargé, carburant, ou tourelle sans
    /// munitions au sol requises). Joueur jamais concerné ; le résultat vanilla / CE est gardé quand il existe.
    /// </summary>
    [HarmonyPatch]
    public static class Patch_ManTurretsFallback
    {
        private static readonly MethodInfo getRoot = AccessTools.Method(typeof(JobGiver_ManTurrets), "GetRoot");

        public static IEnumerable<MethodBase> TargetMethods()
        {
            MethodInfo vanilla = AccessTools.Method(typeof(JobGiver_ManTurrets), "TryGiveJob");
            if (vanilla != null) yield return vanilla;
            foreach (string n in new[] { "CombatExtended.JobGiver_ManTurretsNearPointCE", "CombatExtended.JobGiver_ManTurretsNearSelfCE" })
            {
                Type t = AccessTools.TypeByName(n);
                MethodInfo m = t != null ? AccessTools.DeclaredMethod(t, "TryGiveJob") : null;
                if (m != null) yield return m;
            }
        }

        public static void Postfix(JobGiver_ManTurrets __instance, Pawn pawn, ref Job __result)
        {
            if (__result != null || pawn?.Map == null || pawn.Faction == null || pawn.Faction.IsPlayer || getRoot == null) return;
            try
            {
                var root = (IntVec3)getRoot.Invoke(__instance, new object[] { pawn });
                if (!root.IsValid) return;
                Thing t = GenClosest.ClosestThingReachable(root, pawn.Map, ThingRequest.ForGroup(ThingRequestGroup.BuildingArtificial),
                    PathEndMode.InteractionCell, TraverseParms.For(pawn), __instance.maxDistFromPoint,
                    x => x.Faction == pawn.Faction && TurretManning.NeedsGunner(x) && pawn.CanReserve(x)
                         && TurretManning.Loaded((Building_Turret)x));
                if (t == null) return;
                Job job = JobMaker.MakeJob(JobDefOf.ManTurret, t);
                job.expiryInterval = 2000;
                job.checkOverrideOnExpire = true;
                __result = job;
            }
            catch (Exception e)
            {
                Log.WarningOnce("[Hardened Raids] turret manning fallback: " + e, 0x4852474D);
            }
        }
    }
}
