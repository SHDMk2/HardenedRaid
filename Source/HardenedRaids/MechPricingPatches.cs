using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using HarmonyLib;
using RimWorld;
using RimWorld.BaseGen;
using Verse;

namespace HardenedRaids
{
    // ================================================================ A6 : prix Hardened des mécas blindés hors groupe
    // Chemins qui ne passent pas par PawnGroupKindWorker_Normal.GeneratePawns. Règles :
    // - jamais les mécas du joueur ni les alliés (faction hostile requise) ;
    // - les petites méthodes inlinables (RandomPawnKindDef 88 o d'IL, itérateur GetCombatPawnKindsForPoints) ne sont pas patchées :
    //   on remplace leurs appels dans les appelants (transpilers), robustes à l'inlining du JIT CoreCLR.

    /// <summary>Utilitaires de remplacement d'appels dans des méthodes vanilla.</summary>
    internal static class CallSwap
    {
        public static bool Calls(MethodBase m, MethodInfo target)
        {
            try { return PatchProcessor.ReadMethodBody(m).Any(kv => kv.Value is MethodInfo mi && mi == target); }
            catch { return false; }
        }

        /// <summary>Méthodes déclarées (types imbriqués compris : lambdas) de ces types qui appellent la cible.</summary>
        public static IEnumerable<MethodBase> Callers(MethodInfo target, params Type[] types)
        {
            var seen = new HashSet<MethodBase>();
            foreach (Type t in types)
            {
                if (t == null) continue;
                foreach (Type tt in new[] { t }.Concat(t.GetNestedTypes(AccessTools.all)))
                    foreach (MethodInfo m in AccessTools.GetDeclaredMethods(tt))
                        if (m != target && !m.IsAbstract && !m.ContainsGenericParameters && m.GetMethodBody() != null && Calls(m, target) && seen.Add(m))
                            yield return m;
            }
        }

        public static IEnumerable<CodeInstruction> Swap(IEnumerable<CodeInstruction> ins, MethodInfo from, MethodInfo to, string where)
        {
            int n = 0;
            foreach (CodeInstruction i in ins)
            {
                if ((i.opcode == OpCodes.Call || i.opcode == OpCodes.Callvirt) && i.operand is MethodInfo mi && mi == from)
                {
                    i.opcode = OpCodes.Call;
                    i.operand = to;
                    n++;
                }
                yield return i;
            }
            if (n == 0) Log.Warning($"[Hardened Raids] call to {from.Name} not found in {where}.");
        }
    }

    /// <summary>Capsules mécas / balises de largage : contenu choisi au réveil (CompPawnSpawnOnWakeup.GeneratePawns, 342 o d'IL).</summary>
    [HarmonyPatch(typeof(CompPawnSpawnOnWakeup), "GeneratePawns")]
    public static class Patch_WakeupMechPricing
    {
        public static void Prefix(CompPawnSpawnOnWakeup __instance, out bool __state)
        {
            __state = false;
            if (__instance?.parent == null || !MechPricing.HostileToPlayer(__instance.parent.Faction)) return;
            if (__instance.props is not CompProperties_PawnSpawnOnWakeup props || props.spawnablePawnKinds == null) return;
            if (!props.spawnablePawnKinds.Any(MechPricing.IsHeavy)) return;
            float points = MechPricing.ThreatPointsFor(__instance.parent.MapHeld);   // avant la portée
            if (points < 0f) return;
            __state = MechPricing.Push(props.spawnablePawnKinds, points);
        }

        public static void Finalizer(bool __state)
        {
            if (__state) MechPricing.Pop();
        }
    }

    /// <summary>
    /// Assembleurs mécas (CompSpawnerPawn) : choix uniforme vanilla, sans budget. Hardened : poids 1 / prix relatif
    /// (blindé ×2 = deux fois moins fréquent), 0 si bloqué. Appels remplacés dans Initialize et TrySpawnPawn.
    /// </summary>
    [HarmonyPatch]
    public static class Patch_SpawnerMechPricing
    {
        private static readonly MethodInfo Vanilla = AccessTools.Method(typeof(CompSpawnerPawn), "RandomPawnKindDef");
        private static readonly MethodInfo Mine = AccessTools.Method(typeof(Patch_SpawnerMechPricing), nameof(RandomKind));

        public static IEnumerable<MethodBase> TargetMethods() => CallSwap.Callers(Vanilla, typeof(CompSpawnerPawn));

        public static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions, MethodBase __originalMethod) =>
            CallSwap.Swap(instructions, Vanilla, Mine, "CompSpawnerPawn." + __originalMethod?.Name);

        /// <summary>Même logique que RandomPawnKindDef vanilla, poids Hardened pour les mécas blindés d'une faction hostile.</summary>
        public static PawnKindDef RandomKind(CompSpawnerPawn comp)
        {
            if (comp?.props is not CompProperties_SpawnerPawn props || props.spawnablePawnKinds == null) return null;
            float cur = 0f;
            if (comp.spawnedPawns != null)
                foreach (Pawn p in comp.spawnedPawns) if (p?.kindDef != null) cur += p.kindDef.combatPower;
            IEnumerable<PawnKindDef> source = props.spawnablePawnKinds;
            if (props.maxSpawnedPawnsPoints > -1f) source = source.Where(x => cur + x.combatPower <= props.maxSpawnedPawnsPoints);
            PawnKindDef result;
            try
            {
                // faction encore nulle à l'Initialize (ThingMaker, avant SetFaction) : seuls les assembleurs de grappe portent des mécas
                Faction f = comp.parent?.Faction;
                if ((f == null || MechPricing.HostileToPlayer(f)) && props.spawnablePawnKinds.Any(MechPricing.IsHeavy))
                {
                    float points = MechPricing.ThreatPointsFor(comp.parent?.MapHeld);
                    if (points >= 0f)
                    {
                        ArmorGate gate = ArmorGate.For(points);
                        if (source.TryRandomElementByWeight(k => MechPricing.WeightFactor(k, gate), out result)) return result;
                        // tout bloqué : vanilla (un assembleur muet garderait la grappe « non vaincue »)
                    }
                }
            }
            catch (Exception e)
            {
                Log.WarningOnce("[Hardened Raids] mech assembler pricing: " + e.Message, 0x48524D41);
            }
            return source.TryRandomElement(out result) ? result : null;
        }
    }

    /// <summary>
    /// Mécas endormis (PawnUtility.GetCombatPawnKindsForPoints, itérateur paresseux : un prefix ne couvrirait pas l'énumération).
    /// Appels remplacés dans SymbolResolver_SleepingMechanoids, SymbolResolver_Interior_SleepingMechanoids,
    /// RoomGenUtility.SpawnDormantThreatCluster (via SpawnDormantMechCluster) et ComplexThreatWorker_SleepingMechanoids.
    /// </summary>
    [HarmonyPatch]
    public static class Patch_SleepingMechPricing
    {
        private static readonly MethodInfo Vanilla = AccessTools.Method(typeof(PawnUtility), nameof(PawnUtility.GetCombatPawnKindsForPoints));
        private static readonly MethodInfo Mine = AccessTools.Method(typeof(Patch_SleepingMechPricing), nameof(CombatPawnKindsForPoints));

        public static IEnumerable<MethodBase> TargetMethods() => CallSwap.Callers(Vanilla,
            typeof(SymbolResolver_SleepingMechanoids), typeof(SymbolResolver_Interior_SleepingMechanoids),
            typeof(RoomGenUtility), typeof(ComplexThreatWorker_SleepingMechanoids));

        public static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions, MethodBase __originalMethod) =>
            CallSwap.Swap(instructions, Vanilla, Mine, __originalMethod?.DeclaringType?.Name + "." + __originalMethod?.Name);

        /// <summary>Même boucle que vanilla, au prix Hardened (sans modifier les defs). Sans méca blindé : vanilla.</summary>
        public static IEnumerable<PawnKindDef> CombatPawnKindsForPoints(Func<PawnKindDef, bool> selector, float points, Func<PawnKindDef, float> selectionWeight)
        {
            List<PawnKindDef> all;
            try
            {
                all = DefDatabase<PawnKindDef>.AllDefsListForReading.Where(selector).ToList();
            }
            catch (Exception e)
            {
                Log.WarningOnce("[Hardened Raids] sleeping mech pricing: " + e.Message, 0x48524D53);
                return PawnUtility.GetCombatPawnKindsForPoints(selector, points, selectionWeight);
            }
            if (!all.Any(MechPricing.IsHeavy)) return PawnUtility.GetCombatPawnKindsForPoints(selector, points, selectionWeight);

            ArmorGate gate = ArmorGate.For(points);
            var cost = new Dictionary<PawnKindDef, float>(all.Count);
            foreach (PawnKindDef k in all) cost[k] = MechPricing.Cost(k, gate);
            selectionWeight ??= _ => 1f;
            var result = new List<PawnKindDef>();
            var candidates = new List<PawnKindDef>(all.Count);
            while (points > 0f)
            {
                candidates.Clear();
                foreach (PawnKindDef k in all)
                    if (MechPricing.OriginalPower(k) > 0f && cost[k] <= points && k.appearsRandomlyInCombatGroups) candidates.Add(k);
                if (!candidates.TryRandomElementByWeight(selectionWeight, out PawnKindDef pick)) break;
                points -= cost[pick];
                result.Add(pick);
            }
            if (result.Count > 0) HRLog.Msg($"mécas endormis au prix Hardened : {result.Count} ({string.Join(", ", result.GroupBy(k => k).Select(g => g.Count() + " " + g.Key.defName))})");
            return result;
        }
    }

    /// <summary>
    /// Groupe aléatoire de mécas (temples anciens, SymbolResolver_RandomMechanoidGroup) : nombre fixe, choix pondéré 1 / combatPower.
    /// Hardened : poids ÷ prix relatif des blindés, 0 si bloqué (points : monde / colonie).
    /// </summary>
    [HarmonyPatch(typeof(SymbolResolver_RandomMechanoidGroup), nameof(SymbolResolver_RandomMechanoidGroup.Resolve))]
    public static class Patch_RandomMechGroupPricing
    {
        private static readonly MethodInfo Vanilla = AccessTools.Method(typeof(GenCollection), nameof(GenCollection.RandomElementByWeight))
            .MakeGenericMethod(typeof(PawnKindDef));
        private static readonly MethodInfo Mine = AccessTools.Method(typeof(Patch_RandomMechGroupPricing), nameof(Pick));

        public static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions) =>
            CallSwap.Swap(instructions, Vanilla, Mine, "SymbolResolver_RandomMechanoidGroup.Resolve");

        public static PawnKindDef Pick(IEnumerable<PawnKindDef> source, Func<PawnKindDef, float> weight)
        {
            try
            {
                List<PawnKindDef> list = source.ToList();
                if (list.Any(MechPricing.IsHeavy))
                {
                    float points = MechPricing.ThreatPointsFor(null);
                    if (points >= 0f)
                    {
                        ArmorGate gate = ArmorGate.For(points);
                        if (list.TryRandomElementByWeight(k => weight(k) * MechPricing.WeightFactor(k, gate), out PawnKindDef r)) return r;
                    }
                }
                return list.RandomElementByWeight(weight);
            }
            catch (Exception e)
            {
                Log.WarningOnce("[Hardened Raids] mech group pricing: " + e.Message, 0x48524D47);
                return source.RandomElementByWeight(weight);
            }
        }
    }
}
