using System;
using System.Collections.Generic;
using System.Reflection;
using System.Reflection.Emit;
using HarmonyLib;
using RimWorld;
using UnityEngine;
using Verse;
using Verse.AI;
using Verse.AI.Group;

namespace HardenedRaids
{
    /// <summary>Plus le palier est haut, plus les raids choisissent des stratégies intelligentes (smart, brèche, sape).</summary>
    [HarmonyPatch(typeof(RaidStrategyWorker), nameof(RaidStrategyWorker.SelectionWeightForFaction))]
    public static class Patch_StrategyWeight
    {
        public static void Postfix(RaidStrategyWorker __instance, Faction faction, float basePoints, ref float __result)
        {
            if (!HRSettings.SmarterStrategies || __result <= 0f || faction == null || !faction.def.humanlikeFaction || !faction.HostileTo(Faction.OfPlayer))
                return;
            float x = Tiers.TierX(faction.def, basePoints);
            if (x < 2f) return;
            float s = Mathf.Clamp01((x - 1f) / 4f); // 0 au palier 1, 1 au palier 5+
            switch (__instance.def.defName)
            {
                case "ImmediateAttack": __result *= 1f - 0.6f * s; break;
                case "ImmediateAttackSmart": __result *= 1f + 2f * s; break;
                case "ImmediateAttackBreaching": __result *= 1f + 2f * s; break;
                case "ImmediateAttackBreachingSmart": __result *= 1f + 3f * s; break;
                case "ImmediateAttackSappers": __result *= 1f + 1.5f * s; break;
                case "Siege": __result *= 1f + 1.5f * s; break;
            }
        }
    }

    /// <summary>
    /// Les raiders T2+ contournent les zones couvertes par les tourelles du joueur (grille d'évitement vanilla),
    /// même quand leur lord ne l'active pas (assaut « non smart »). Vanilla (PawnUtility.TryGetAvoidGrid) :
    /// grille si kindDef.canUseAvoidGrid, sinon si onlyIfLordAllows == false (CastPositionFinder), sinon seulement
    /// si CurLordToil.useAvoidGrid (assaut smart, fuite, enlèvement, vol, sortie de carte l'ont déjà). On n'ajoute
    /// la grille que si vanilla l'a refusée ET que :
    ///  - l'appel respecte le lord (onlyIfLordAllows) ;
    ///  - le pawn est dans un toil d'assaut de colonie (pas de grille ajoutée en étape, défense, siège, garde, sans lord…) ;
    ///  - il n'est pas en état mental, ne porte rien (otage, butin) et son job n'est ni fuite, ni enlèvement, ni vol,
    ///    ni sortie de carte ;
    ///  - l'appel ne vient pas de Region.Allows : là, une case de porte saturée (255) rend la porte infranchissable ;
    ///    notre grille ne fait que renchérir le chemin, elle ne bloque jamais un pawn.
    /// La grille elle-même est celle de vanilla (Map.avoidGrid : tourelles du joueur seulement, régénérée
    /// paresseusement quand une tourelle / un mur apparaît ou disparaît) : aucun coût ajouté.
    /// </summary>
    [HarmonyPatch(typeof(PawnUtility), nameof(PawnUtility.TryGetAvoidGrid))]
    public static class Patch_AvoidGrid
    {
        /// <summary>&gt; 0 pendant l'appel de TryGetAvoidGrid fait par Region.Allows (voir Patch_RegionAllowsAvoidGrid).</summary>
        [ThreadStatic] private static int regionDepth;

        public static void Postfix(Pawn p, bool onlyIfLordAllows, ref AvoidGrid grid, ref bool __result)
        {
            if (__result || !onlyIfLordAllows || regionDepth > 0 || !HRSettings.VeteransAvoidTurrets || p == null || !p.Spawned) return;
            Faction f = p.Faction;
            if (f == null || !f.def.canUseAvoidGrid || f.IsPlayer || !f.HostileTo(Faction.OfPlayer)) return;
            LordToil toil = p.GetLord()?.CurLordToil;
            if (!(toil is LordToil_AssaultColony || toil is LordToil_AssaultColonySappers || toil is LordToil_AssaultColonyBreaching)) return;
            if (p.InMentalState || p.carryTracker?.CarriedThing != null) return;
            Job job = p.CurJob;
            if (job != null && (job.exitMapOnArrival || job.def == JobDefOf.Flee || job.def == JobDefOf.FleeAndCower
                || job.def == JobDefOf.Kidnap || job.def == JobDefOf.Steal || job.def == JobDefOf.CarryDownedPawnToExit)) return;
            if (PawnTuning.TierOf(p) < 2) return;
            grid = p.Map.avoidGrid;
            __result = grid != null;
        }

        /// <summary>Appelé à la place de TryGetAvoidGrid dans Region.Allows (test des portes) : vanilla seul, sans notre ajout.</summary>
        public static bool TryGetForRegion(Pawn p, out AvoidGrid grid, bool onlyIfLordAllows)
        {
            regionDepth++;
            try { return p.TryGetAvoidGrid(out grid, onlyIfLordAllows); }
            finally { regionDepth--; }
        }
    }

    /// <summary>
    /// Region.Allows : une porte dont une case vaut 255 dans la grille d'évitement est infranchissable. On redirige
    /// son appel à TryGetAvoidGrid pour que notre grille ajoutée n'y compte pas (seul vanilla peut bloquer une porte).
    /// Le détour n'est pris que sur le chemin « porte + pawn », pas à chaque appel de Region.Allows.
    /// </summary>
    [HarmonyPatch(typeof(Region), nameof(Region.Allows))]
    public static class Patch_RegionAllowsAvoidGrid
    {
        public static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions)
        {
            MethodInfo from = AccessTools.Method(typeof(PawnUtility), nameof(PawnUtility.TryGetAvoidGrid));
            MethodInfo to = AccessTools.Method(typeof(Patch_AvoidGrid), nameof(Patch_AvoidGrid.TryGetForRegion));
            int n = 0;
            foreach (CodeInstruction ci in instructions)
            {
                if (ci.Calls(from)) { ci.opcode = OpCodes.Call; ci.operand = to; n++; }
                yield return ci;
            }
            if (n == 0) Log.Warning("[Hardened Raids] Region.Allows : appel à TryGetAvoidGrid introuvable, les portes peuvent bloquer les vétérans.");
        }
    }

    /// <summary>
    /// Tourelle hors service = plus une menace : vanilla ne regarde que le courant et le servant ; on ajoute le
    /// chargeur vide (CE) et le carburant / les munitions épuisés (CompRefuelable). L'IA cesse donc de la viser.
    /// </summary>
    /// <remarks>
    /// Chemin chaud (chaque recherche de cible × chaque tourelle). Coût : par type de tourelle, l'accès à l'arme
    /// et les accesseurs CE sont résolus une fois ; par tourelle, les comps (CompRefuelable, CompAmmoUser de la
    /// tourelle et de son arme) sont trouvés une fois et lus par délégués liés ; le résultat est gardé pour le
    /// tick en cours (une tourelle est évaluée des dizaines de fois par tick). Cache borné, vidé au changement de partie.
    /// </remarks>
    [HarmonyPatch(typeof(Building_Turret), nameof(Building_Turret.ThreatDisabled))]
    public static class Patch_TurretThreat
    {
        private const int MaxEntries = 4096;

        private static bool init;
        private static Type ammoUser;
        private static MethodInfo useAmmoGetter, emptyMagGetter;

        /// <summary>État résolu d'une tourelle (comps et délégués), plus le résultat du dernier tick évalué.</summary>
        private sealed class Entry
        {
            public Building_Turret turret;
            public CompRefuelable fuel;
            public Func<bool> selfUse, selfEmpty;   // CompAmmoUser de la tourelle elle-même
            public Func<Thing> gunGetter;           // propriété « Gun » (tourelles non vanilla, ex. CE)
            public Thing gun;                       // arme pour laquelle gunUse / gunEmpty sont liés
            public Func<bool> gunUse, gunEmpty;
            public int tick = int.MinValue;
            public bool result;
        }

        private static readonly Dictionary<int, Entry> cache = new Dictionary<int, Entry>();
        private static readonly Dictionary<Type, MethodInfo> gunGetters = new Dictionary<Type, MethodInfo>();
        private static Game cacheGame;

        public static void Postfix(Building_Turret __instance, ref bool __result)
        {
            if (__result || !HRSettings.IgnoreDeadTurrets) return;
            try { __result = OutOfService(__instance); }
            catch { }
        }

        public static bool OutOfService(Building_Turret t)
        {
            Entry e = EntryFor(t);
            int now = Find.TickManager?.TicksGame ?? -1;
            if (now >= 0 && e.tick == now) return e.result;
            bool r = Compute(e);
            e.tick = now;
            e.result = r;
            return r;
        }

        // Même logique que l'ancienne version : carburant vide, sinon (CE) chargeur vide de la tourelle ou de son arme.
        private static bool Compute(Entry e)
        {
            if (e.fuel != null && !e.fuel.HasFuel) return true;
            if (useAmmoGetter == null || emptyMagGetter == null) return false;
            Thing gun = e.turret is Building_TurretGun vg ? vg.gun : e.gunGetter?.Invoke();
            if (gun != e.gun) BindGun(e, gun);
            return (e.selfUse != null && e.selfUse() && e.selfEmpty())
                || (e.gunUse != null && e.gunUse() && e.gunEmpty());
        }

        private static Entry EntryFor(Building_Turret t)
        {
            Game g = Current.Game;
            if (g != cacheGame || cache.Count >= MaxEntries) { cache.Clear(); cacheGame = g; }
            if (cache.TryGetValue(t.thingIDNumber, out Entry e) && e.turret == t) return e;
            e = new Entry { turret = t, fuel = t.GetComp<CompRefuelable>() };
            Init();
            if (useAmmoGetter != null && emptyMagGetter != null)
            {
                Bind(t, out e.selfUse, out e.selfEmpty);
                if (!(t is Building_TurretGun))
                {
                    Type type = t.GetType();
                    if (!gunGetters.TryGetValue(type, out MethodInfo gm))
                    {
                        PropertyInfo gp = AccessTools.Property(type, "Gun");
                        gm = gp != null && typeof(Thing).IsAssignableFrom(gp.PropertyType) ? gp.GetGetMethod(true) : null;
                        gunGetters[type] = gm;
                    }
                    if (gm != null)
                        try { e.gunGetter = (Func<Thing>)Delegate.CreateDelegate(typeof(Func<Thing>), t, gm); }
                        catch { gunGetters[type] = null; }
                }
            }
            cache[t.thingIDNumber] = e;
            return e;
        }

        private static void BindGun(Entry e, Thing gun)
        {
            e.gun = gun;
            Bind(gun as ThingWithComps, out e.gunUse, out e.gunEmpty);
        }

        /// <summary>Premier CompAmmoUser de la chose (comme avant), lu par délégués liés à l'instance.</summary>
        private static void Bind(ThingWithComps th, out Func<bool> use, out Func<bool> empty)
        {
            use = empty = null;
            if (th == null) return;
            try
            {
                foreach (ThingComp comp in th.AllComps)
                    if (ammoUser.IsInstanceOfType(comp))
                    {
                        use = (Func<bool>)Delegate.CreateDelegate(typeof(Func<bool>), comp, useAmmoGetter);
                        empty = (Func<bool>)Delegate.CreateDelegate(typeof(Func<bool>), comp, emptyMagGetter);
                        return;
                    }
            }
            catch { use = empty = null; } // liaison impossible : traité comme « pas de chargeur » (jamais relancé à chaque appel)
        }

        private static void Init()
        {
            if (init) return;
            init = true;
            ammoUser = AccessTools.TypeByName("CombatExtended.CompAmmoUser");
            if (ammoUser == null) return;
            useAmmoGetter = AccessTools.PropertyGetter(ammoUser, "UseAmmo");
            emptyMagGetter = AccessTools.PropertyGetter(ammoUser, "EmptyMagazine");
            if (useAmmoGetter?.ReturnType != typeof(bool) || emptyMagGetter?.ReturnType != typeof(bool))
                useAmmoGetter = emptyMagGetter = null;
        }
    }

    /// <summary>Les raiders ne « saccagent » pas (tir à volonté) une tourelle hors service : ils passent aux vraies cibles.</summary>
    [HarmonyPatch(typeof(TrashUtility), nameof(TrashUtility.ShouldTrashBuilding), new[] { typeof(Pawn), typeof(Building), typeof(bool) })]
    public static class Patch_TrashDeadTurret
    {
        public static void Postfix(Pawn pawn, Building b, ref bool __result)
        {
            if (!__result || !HRSettings.IgnoreDeadTurrets || pawn?.Faction == null || pawn.Faction.IsPlayer) return;
            if (b is Building_Turret t && t.ThreatDisabled(pawn)) __result = false;
        }
    }
}
