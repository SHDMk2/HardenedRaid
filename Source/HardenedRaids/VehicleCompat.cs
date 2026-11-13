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
            HRLog.Msg($"véhicules de combat disponibles : {string.Join(", ", combatVehicles.Select(v => $"{v.defName} ({(Class(v) == VClass.Ifv ? TransportCost(v) : Cost(v)):F0} pts, {Class(v)}{(Role(v) == FireRole.Direct ? "" : " " + Role(v))})"))}");
            return combatVehicles.Cast<VehicleDef>().ToList();
        }

        public const float MinVehicleCost = 400f;

        public enum FireRole { Direct, Artillery, RocketSwarm }

        private static readonly Dictionary<ThingDef, FireRole> roles = new Dictionary<ThingDef, FireRole>();

        /// <summary>
        /// Coût en points : VVE ne renseigne pas combatPower (100 partout), on part de la valeur marchande, au même taux
        /// que l'équipement des raiders, × un multiplicateur blindé (CE : les armes légères ne passent pas). Les roquettes
        /// en essaim et l'artillerie tirent par-dessus les défenses (saturation anti-missile, frappe des soutes) : × en plus.
        /// </summary>
        public static float Cost(ThingDef v)
        {
            float baseCost = Mathf.Max(v.BaseMarketValue / Mathf.Max(1f, HRSettings.ValueDivisor) * HRSettings.VehicleValueFactor, MinVehicleCost);
            return baseCost * RoleFactor(Role(v)) * HRSettings.CostFactor;
        }

        public static float RoleFactor(FireRole r) => r switch
        {
            FireRole.RocketSwarm => HRSettings.RocketVehicleFactor,
            FireRole.Artillery => HRSettings.ArtilleryVehicleFactor,
            _ => 1f,
        };

        /// <summary>Rôle de tir le plus dangereux parmi les tourelles du véhicule.</summary>
        public static FireRole Role(ThingDef v)
        {
            if (roles.TryGetValue(v, out FireRole cached)) return cached;
            FireRole best = FireRole.Direct;
            CompProperties_VehicleTurrets props = v.GetCompProperties<CompProperties_VehicleTurrets>();
            if (props?.turrets != null)
                foreach (VehicleTurret t in props.turrets)
                {
                    FireRole r = TurretRole(t?.def);
                    if (r > best) best = r;
                }
            roles[v] = best;
            return best;
        }

        private static FireRole TurretRole(VehicleTurretDef td)
        {
            if (td == null) return FireRole.Direct;
            var projectiles = new List<ThingDef>();
            if (td.projectile != null) projectiles.Add(td.projectile);
            if (td.ammunition != null)
                foreach (ThingDef a in td.ammunition.AllowedThingDefs)
                    if (a.projectileWhenLoaded != null) projectiles.Add(a.projectileWhenLoaded);
            string name = (td.defName + " " + td.label).ToLowerInvariant();
            bool rocket = name.Contains("rocket") || name.Contains("swarm") || name.Contains("missile")
                          || projectiles.Any(p => p.defName.IndexOf("Rocket", StringComparison.OrdinalIgnoreCase) >= 0
                                                  || p.defName.IndexOf("Missile", StringComparison.OrdinalIgnoreCase) >= 0);
            if (rocket) return FireRole.RocketSwarm;
            bool indirect = name.Contains("artillery") || name.Contains("mortar")
                            || projectiles.Any(p => p.projectile?.flyOverhead ?? false);
            return indirect ? FireRole.Artillery : FireRole.Direct;
        }

        // ------------------------------------------------------------ classes et composition
        public enum VClass { Light, Tank, FireSupport, Ifv }

        private static readonly Dictionary<ThingDef, VClass> classes = new Dictionary<ThingDef, VClass>();

        /// <summary>
        /// Appui-feu : roquettes / artillerie. IFV : ≥ 3 places passagers et tir direct (compté comme transport).
        /// Tank : canon à obus explosifs. Léger : le reste (minigun, lance-flammes).
        /// </summary>
        public static VClass Class(ThingDef v)
        {
            if (classes.TryGetValue(v, out VClass c)) return c;
            if (Role(v) != FireRole.Direct) c = VClass.FireSupport;
            else if (v is VehicleDef vd && Seats(vd, true) >= 3) c = VClass.Ifv;
            else if (HasCannon(v)) c = VClass.Tank;
            else c = VClass.Light;
            classes[v] = c;
            return c;
        }

        private static bool HasCannon(ThingDef v)
        {
            CompProperties_VehicleTurrets props = v.GetCompProperties<CompProperties_VehicleTurrets>();
            if (props?.turrets == null) return false;
            foreach (VehicleTurret t in props.turrets)
            {
                VehicleTurretDef td = t?.def;
                if (td == null) continue;
                if ((td.defName + " " + td.label).ToLowerInvariant().Contains("cannon")) return true;
                var projectiles = new List<ThingDef>();
                if (td.projectile != null) projectiles.Add(td.projectile);
                if (td.ammunition != null)
                    foreach (ThingDef a in td.ammunition.AllowedThingDefs)
                        if (a.projectileWhenLoaded != null) projectiles.Add(a.projectileWhenLoaded);
                if (projectiles.Any(p => (p.projectile?.explosionRadius ?? 0f) > 0.5f || (p.label ?? "").Contains("shell"))) return true;
            }
            return false;
        }

        private static int StochasticRound(float x, System.Random rng)
        {
            int n = Mathf.FloorToInt(x);
            return n + (rng.NextDouble() < x - n ? 1 : 0);
        }

        private static VehicleDef WeightedPick(List<VehicleDef> cands, Func<VehicleDef, float> weight, System.Random rng)
        {
            if (cands.Count == 0) return null;
            float total = cands.Sum(weight);
            double r = rng.NextDouble() * total;
            foreach (VehicleDef v in cands)
            {
                r -= weight(v);
                if (r <= 0) return v;
            }
            return cands[cands.Count - 1];
        }

        /// <summary>
        /// Choisit les blindés d'un raid (hors transports) : 1 tank pour 3 à 6 véhicules (selon son prix), 1 appui-feu
        /// pour 3 tanks, le reste en véhicules légers. Achat dans l'ordre appui-feu → tanks → légers, chacun en gardant
        /// de quoi payer le prix plancher des véhicules restants. Retourne les defs et le coût total.
        /// </summary>
        public static List<ThingDef> Plan(Faction faction, float points, float tierX, System.Random rng, out float totalCost)
        {
            totalCost = 0f;
            var res = new List<ThingDef>();
            if (tierX < HRSettings.VehicleMinTier) return res;
            List<VehicleDef> cands = Catalog().Where(v => (v.techLevel <= faction.def.techLevel || v.techLevel == TechLevel.Undefined)
                                                          && Class(v) != VClass.Ifv).ToList();
            if (cands.Count == 0) return res;
            List<VehicleDef> fire = cands.Where(v => Class(v) == VClass.FireSupport).ToList();
            List<VehicleDef> tanks = cands.Where(v => Class(v) == VClass.Tank).ToList();
            List<VehicleDef> lights = cands.Where(v => Class(v) == VClass.Light).ToList();
            if (lights.Count == 0) lights = tanks;

            float budget = points * HRSettings.VehiclePointShare;
            int wanted = Mathf.Clamp(Mathf.RoundToInt(tierX - HRSettings.VehicleMinTier + 1f + points / 25000f), 1, HRSettings.MaxVehicles);
            // plus le palier est haut, plus on préfère les gros blindés
            float exp = Mathf.Lerp(0f, 1.5f, Mathf.InverseLerp(HRSettings.VehicleMinTier, 6f, tierX));
            float minCost = cands.Min(v => Cost(v));
            int left = wanted;

            bool Buy(List<VehicleDef> pool)
            {
                float cap = budget - (left - 1) * minCost;
                VehicleDef pick = WeightedPick(pool.Where(v => Cost(v) <= cap).ToList(), v => Mathf.Pow(Cost(v), exp), rng);
                if (pick == null) return false;
                res.Add(pick);
                budget -= Cost(pick);
                left--;
                return true;
            }

            // nombre de tanks : l'intervalle (3 à 6 véhicules par tank) dépend du prix du tank qu'on achèterait
            int nTanks = 0, nFire = 0;
            if (tanks.Count > 0)
            {
                float lo = tanks.Min(v => Cost(v)), hi = tanks.Max(v => Cost(v));
                VehicleDef typical = WeightedPick(tanks, v => Mathf.Pow(Cost(v), exp), rng);
                float interval = Mathf.Lerp(3f, 6f, hi > lo ? Mathf.InverseLerp(lo, hi, Cost(typical)) : 0f);
                nTanks = Mathf.Min(StochasticRound(wanted / interval, rng), wanted);
                nFire = Mathf.Min(StochasticRound(nTanks / 3f, rng), wanted - nTanks);
            }
            for (int i = 0; i < nFire && left > 0; i++) if (!Buy(fire)) break;
            for (int i = 0; i < nTanks && left > 0; i++) if (!Buy(tanks)) break;
            while (left > 0) if (!Buy(lights) && !Buy(tanks)) break;
            totalCost = res.Sum(v => Cost(v));
            return res;
        }

        // ------------------------------------------------------------ transports (payants, moins chers que les blindés)
        /// <summary>Prix d'un transport : camion = valeur ÷ taux × facteur transport ; IFV = prix d'un blindé.</summary>
        public static float TransportCost(ThingDef v) =>
            Class(v) == VClass.Ifv ? Cost(v)
                : Mathf.Max(v.BaseMarketValue / Mathf.Max(1f, HRSettings.ValueDivisor) * HRSettings.TransportValueFactor, 20f) * HRSettings.CostFactor;

        private static int TransportSeats(VehicleDef v) => Class(v) == VClass.Ifv ? Seats(v, true) : Seats(v, false);

        /// <summary>Places pour l'infanterie dans les blindés déjà choisis (équipage compris).</summary>
        public static int SeatsIn(IEnumerable<ThingDef> defs) => defs.OfType<VehicleDef>().Sum(v => Seats(v, false));

        /// <summary>
        /// Transports pour l'infanterie restante, un seul modèle par raid. Le prix par place visé monte avec le palier :
        /// vagues pauvres → voitures, milieu → camions, fin de partie → IFV. Ce qui ne rentre pas dans le budget marche.
        /// </summary>
        public static List<ThingDef> PlanTransports(Faction faction, float points, float tierX, int seatsNeeded, System.Random rng, out float totalCost)
        {
            totalCost = 0f;
            var res = new List<ThingDef>();
            if (tierX < HRSettings.VehicleMinTier || seatsNeeded <= 0 || HRSettings.MaxTransports <= 0) return res;
            List<VehicleDef> cands = TransportCatalog()
                .Concat(Catalog().Where(v => Class(v) == VClass.Ifv))
                .Where(v => (v.techLevel <= faction.def.techLevel || v.techLevel == TechLevel.Undefined) && TransportSeats(v) > 0)
                .Distinct().ToList();
            if (cands.Count == 0) return res;

            float budget = points * HRSettings.TransportPointShare;
            int Count(VehicleDef v) => Mathf.Min(Mathf.CeilToInt(seatsNeeded / (float)TransportSeats(v)), HRSettings.MaxTransports);
            float PerSeat(VehicleDef v) => TransportCost(v) / TransportSeats(v);
            // exposant sur le prix par place : bas palier → favorise les moins chers, haut palier → les plus chers
            float exp = Mathf.Lerp(-2f, 2f, Mathf.InverseLerp(HRSettings.VehicleMinTier, 6f, tierX));
            List<VehicleDef> full = cands.Where(v => Count(v) * TransportCost(v) <= budget).ToList();
            VehicleDef pick = full.Count > 0
                ? WeightedPick(full, v => Mathf.Pow(PerSeat(v), exp), rng)
                : cands.OrderBy(PerSeat).First();   // rien ne couvre tout le monde : le moins cher par place, autant que possible
            int n = Mathf.Min(Count(pick), Mathf.FloorToInt(budget / TransportCost(pick)));
            for (int i = 0; i < n; i++)
            {
                res.Add(pick);
                totalCost += TransportCost(pick);
            }
            return res;
        }

        public static bool IsTruck(ThingDef v) => v is VehicleDef vd && (vd.vehicleCategory & VehicleCategory.Combat) == 0;

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

        /// <summary>Tourelle à tir courbe (artillerie, roquettes en cloche).</summary>
        public static bool HasIndirectTurret(Pawn v) =>
            v is VehiclePawn vp && vp.CompVehicleTurrets != null && vp.CompVehicleTurrets.Turrets.Any(t => t.ProjectileDef?.projectile?.flyOverhead ?? false);

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

        private static bool Moves(VehicleRoleHandler h) => (h.role.HandlingTypes & HandlingType.Movement) != 0;

        /// <summary>Places de pilote libres (véhicule qui ne peut pas rouler faute de conducteur).</summary>
        public static int DriverSeats(Pawn v) =>
            v is VehiclePawn vp && vp.handlers != null ? vp.handlers.Where(Moves).Sum(h => Mathf.Max(0, h.role.SlotsToOperate - h.thingOwner.Count)) : 0;

        public static bool NeedsCrew(Pawn v) => DriverSeats(v) > 0;

        /// <summary>Monte à bord : poste de pilote d'abord si demandé, puis tireur manquant, puis passager.</summary>
        public static bool TryBoard(Pawn v, Pawn p, bool driver)
        {
            if (v is not VehiclePawn vp || !vp.Spawned || vp.handlers == null) return false;
            if (driver)
                foreach (VehicleRoleHandler h in vp.handlers.Where(h => Moves(h) && h.AreSlotsAvailable && h.CanOperateRole(p)).ToList())
                    if (vp.TryAddPawn(p, h)) return true;
            foreach (VehicleRoleHandler h in vp.handlers.Where(h => h.role.HandlingTypes != HandlingType.None && h.thingOwner.Count < h.role.SlotsToOperate && h.CanOperateRole(p)).ToList())
                if (vp.TryAddPawn(p, h)) return true;
            foreach (VehicleRoleHandler h in vp.handlers.Where(h => h.role.HandlingTypes == HandlingType.None && h.AreSlotsAvailable).ToList())
                if (vp.TryAddPawn(p, h)) return true;
            return vp.TryAddPawn(p);
        }

        /// <summary>Un passager valide prend le volant d'un véhicule sans pilote (sans descendre).</summary>
        public static bool PromoteDriver(Pawn v)
        {
            if (v is not VehiclePawn vp || vp.handlers == null || !NeedsCrew(v)) return false;
            bool done = false;
            foreach (VehicleRoleHandler seat in vp.handlers.Where(Moves).ToList())
            {
                while (seat.thingOwner.Count < seat.role.SlotsToOperate)
                {
                    Pawn cand = vp.handlers.Where(h => !Moves(h)).SelectMany(h => h.thingOwner.InnerListForReading)
                        .FirstOrDefault(x => !x.Downed && x.Faction == vp.Faction && seat.CanOperateRole(x));
                    if (cand == null || !cand.holdingOwner.TryTransferToContainer(cand, seat.thingOwner, true)) break;
                    done = true;
                }
            }
            return done;
        }

        /// <summary>Ce que porte le fantassin (prisonnier, butin) passe dans la soute du véhicule.</summary>
        public static void StowCarried(Pawn v, Pawn p)
        {
            Thing c = p.carryTracker?.CarriedThing;
            if (c == null || v is not VehiclePawn vp || !vp.Spawned) return;
            vp.AddOrTransfer(c);
        }

        /// <summary>
        /// Véhicule ennemi qui sort de la carte : VF emporte ses passagers tels quels. Un colon (ou tout pawn d'une
        /// autre faction) à bord est enlevé comme par un ravisseur à pied.
        /// </summary>
        public static void KidnapAboard(Pawn vehicle)
        {
            if (vehicle is not VehiclePawn vp || vp.Faction == null || vp.Faction.IsPlayer) return;
            var victims = new List<Pawn>();
            foreach (Thing t in vp.inventory.innerContainer) if (t is Pawn x) victims.Add(x);
            if (vp.handlers != null) foreach (VehicleRoleHandler h in vp.handlers) victims.AddRange(h.thingOwner.InnerListForReading);
            foreach (Pawn x in victims.Distinct().ToList())
            {
                if (x.Faction == null || x.Faction == vp.Faction || !x.RaceProps.Humanlike || x.Dead) continue;
                x.holdingOwner?.Remove(x);
                vp.Faction.kidnapped.Kidnap(x, vp);
                HRLog.Msg($"{vp.LabelShort} emporte {x.LabelShort}.");
            }
        }

        public static bool IsExiting(Pawn v) => v.CurJob != null && v.CurJob.exitMapOnArrival;

        /// <summary>Le véhicule quitte la carte (avec tout ce qu'il a à bord). Faux si impossible pour l'instant.</summary>
        public static bool ExitMap(Pawn v)
        {
            if (v is not VehiclePawn vp || !vp.Spawned) return false;
            if (IsExiting(v)) return true;
            if (IsDeployed(v)) { Undeploy(v); return false; }
            if (IsDeploying(v)) return false;
            PromoteDriver(v);
            Ready(v);
            if (!vp.CanMoveFinal) return false;
            Verse.AI.Job j = exitGiver.TryIssueJobPackage(vp, default(Verse.AI.JobIssueParams)).Job;
            if (j == null) return false;
            vp.jobs.StartJob(j, Verse.AI.JobCondition.InterruptForced);
            return true;
        }

        /// <summary>Nombre de cases entre l'emprise du véhicule et le bord le plus proche.</summary>
        public static int EdgeDistance(Pawn v)
        {
            CellRect r = v.OccupiedRect(); IntVec3 s = v.Map.Size;
            return Math.Min(Math.Min(r.minX, r.minZ), Math.Min(s.x - 1 - r.maxX, s.z - 1 - r.maxZ));
        }

        /// <summary>
        /// Sortie forcée : VF ne fait sortir un véhicule que si son emprise touche le bord à la fin du Goto, or la case
        /// visée (rembourrée pour la hitbox) est souvent inatteignable ("ran out of cells") -> il reste planté à 3-4 cases.
        /// </summary>
        public static bool ForceExit(Pawn v)
        {
            if (v is not VehiclePawn vp || !vp.Spawned) return false;
            PromoteDriver(v);
            vp.ExitMap(false, CellRect.WholeMap(vp.Map).GetClosestEdge(vp.Position));
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
                HRLog.Msg($"transports de troupes disponibles : {string.Join(", ", transports.Select(v => $"{v.defName} ({Seats((VehicleDef)v, false)} places, {TransportCost(v):F0} pts)"))}");
            }
            return transports.Cast<VehicleDef>().ToList();
        }

        /// <summary>
        /// Convoi : blindés et IFV achetés (pilote + tireurs restent, passagers débarquent) puis les camions achetés
        /// au plan pour le reste de l'infanterie (tous débarquent ; ce qui ne rentre pas marche). Les embarqués restent membres du lord
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

            // 1. blindés et IFV (équipage reste à bord, passagers débarquent)
            foreach (ThingDef d in defs)
            {
                if (d is not VehicleDef vd || IsTruck(vd)) continue;
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
            // 2. camions achetés au plan (tout le monde débarque) ; ceux qui restent vides ne viennent pas
            List<VehicleDef> trucks = defs.OfType<VehicleDef>().Where(IsTruck).ToList();
            VehicleDef truckDef = trucks.FirstOrDefault();
            int nTrucks = 0;
            foreach (VehicleDef td in trucks)
            {
                if (pool.Count == 0 || nTrucks >= HRSettings.MaxTransports) break;
                truckDef = td;
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

            if (vehicles.Count == 0) { HRLog.Msg($"0/{defs.Count} véhicule(s) pour {parms.faction.Name}."); return; }
            foreach (VehiclePawn v in vehicles) { Supply(v); Ready(v); pawns.Add(v); }
            ConvoyHandoff.Pending = new ConvoyHandoff
            {
                vehicles = vehicles.Cast<Pawn>().ToList(),
                dismount = dismount,
                deployTurrets = HRSettings.TruckTurrets && parms.raidStrategy != null && parms.raidStrategy.defName.Contains("Smart"),
                tierX = Tiers.TierX(parms.faction.def, parms.points),
            };
            HRLog.Msg($"convoi {parms.faction.Name} : {vehicles.Count - nTrucks} blindé(s), {nTrucks}/{trucks.Count} camion(s) {(truckDef != null ? "(" + truckDef.label + ")" : "")}, "
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
            var exit = HarmonyLib.AccessTools.Method("Vehicles.VehiclePawn:ExitMap");
            if (exit != null) harmony.Patch(exit, prefix: new HarmonyLib.HarmonyMethod(typeof(Patch_SpawnNearVehicle), nameof(ExitPrefix)));
            // sortie « naturelle » (fin du Goto) : VF passe par PathingHelper.ExitMap, pas par VehiclePawn.ExitMap
            var exit2 = HarmonyLib.AccessTools.Method("Vehicles.PathingHelper:ExitMap");
            if (exit2 != null) harmony.Patch(exit2, prefix: new HarmonyLib.HarmonyMethod(typeof(Patch_SpawnNearVehicle), nameof(ExitPrefix2)));
            Patch_EnemyHandbrake.Apply(harmony);
        }

        public static void ExitPrefix2(VehiclePawn vehicle) => ExitPrefix(vehicle);

        public static void ExitPrefix(Pawn __instance)
        {
            try { VehicleCompat.KidnapAboard(__instance); }
            catch (Exception e) { Log.WarningOnce("[Hardened Raids] sortie de véhicule : " + e.Message, 0x4B49444E); }
        }
    }

    /// <summary>
    /// Vanilla Vehicles Expanded : coup de frein à main quand un véhicule n'a pas la place de décélérer (message,
    /// crissement, pneus abîmés). Désactivé pour les véhicules non joueurs : l'IA s'arrête sans cesse.
    /// </summary>
    public static class Patch_EnemyHandbrake
    {
        private static System.Reflection.FieldInfo handbrake;

        public static void Apply(HarmonyLib.Harmony harmony)
        {
            var m = HarmonyLib.AccessTools.Method("VanillaVehiclesExpanded.CompVehicleMovementController:Slowdown");
            if (m == null) return;
            handbrake = HarmonyLib.AccessTools.Field(m.DeclaringType, "handbrakeApplied");
            if (handbrake == null) return;
            harmony.Patch(m, prefix: new HarmonyLib.HarmonyMethod(typeof(Patch_EnemyHandbrake), nameof(Prefix)),
                postfix: new HarmonyLib.HarmonyMethod(typeof(Patch_EnemyHandbrake), nameof(Postfix)));
        }

        public static void Prefix(object __instance, out bool __state)
        {
            __state = false;
            if (__instance is ThingComp c && c.parent?.Faction != null && !c.parent.Faction.IsPlayer && !(bool)handbrake.GetValue(__instance))
            {
                handbrake.SetValue(__instance, true);   // la branche "frein à main" est sautée
                __state = true;
            }
        }

        public static void Postfix(object __instance, bool __state)
        {
            if (__state) handbrake.SetValue(__instance, false);
        }
    }
}
