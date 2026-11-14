using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using System.Runtime.CompilerServices;
using HarmonyLib;
using RimWorld;
using RimWorld.Planet;
using UnityEngine;
using Verse;

namespace HardenedRaids
{
    // ================================================================ bêtes enragées (scarie)

    /// <summary>Nombre max de bêtes d'une meute = plafond de raiders néolithiques (vanilla : 100).</summary>
    public static class AnimalCaps
    {
        public static int MaxAnimals => Mathf.Max(100, HRSettings.CapLowTech);

        public static bool CanArriveManhunter(PawnKindDef k) =>
            k?.race != null && k.RaceProps.Animal && k.canArriveManhunter && k.RaceProps.CanPassFences;

        /// <summary>GetAnimalsCount vanilla avec le plafond du mod.</summary>
        public static int AnimalsCount(PawnKindDef animalKind, float points) =>
            Mathf.Clamp(Mathf.RoundToInt(points / animalKind.combatPower), 2, MaxAnimals);

        /// <summary>Remplace l'appel à SitePartWorker_Manhunters.GetAnimalsCount (privée, minuscule).</summary>
        public static int SiteAnimalsCount(SitePartWorker_Manhunters worker, SitePartParams parms) =>
            AnimalsCount(parms.animalKind, parms.threatPoints);

        /// <summary>Poids d'une espèce : même formule que vanilla, mais 0 seulement au-delà du nouveau plafond.</summary>
        public static float Weight(PawnKindDef animal, float points)
        {
            points = Mathf.Max(points, 70f);
            if (animal.combatPower * 2f > points) return 0f;
            int max = MaxAnimals;
            int n = Mathf.Min(Mathf.RoundToInt(points / animal.combatPower), max);
            return Mathf.Clamp01(Mathf.InverseLerp(max, 10f, n));
        }

        /// <summary>TryGetAnimalFromList vanilla, avec le poids du mod.</summary>
        public static bool TryGetAnimalFromList(float points, List<PawnKindDef> animals, out PawnKindDef animalKind)
        {
            if (animals != null && animals.Count > 0)
            {
                if (animals.TryRandomElementByWeight(a => Weight(a, points), out animalKind)) return true;
                if (points > animals.Min(a => a.combatPower) * 2f)
                {
                    animalKind = animals.MaxBy(a => a.combatPower);
                    return true;
                }
            }
            animalKind = null;
            return false;
        }
    }

    // B6 : GetAnimalsCount (22 o d'IL) et AnimalWeight (78 o) sont inlinables par le JIT CoreCLR : un patch sur elles peut être
    // contourné. On patche donc des méthodes trop grosses pour être inlinées (> 100 o) :
    // - le choix d'espèce : TryGetAnimalFromList (141 o), seul appelant vanilla d'AnimalWeight (via sa lambda), utilisé par les
    //   deux TryFindAggressiveAnimalKind (event, quête, site, embuscade de caravane, signal d'embuscade) ;
    // - le nombre : appels à GetAnimalsCount remplacés dans GenerateAnimals (193 o), IncidentWorker_AggressiveAnimals,
    //   QuestNode_ManhunterPack et les appelants publics de SitePartWorker_Manhunters.GetAnimalsCount (privée, 13 o).
    // VEE ManhunterSingleArrival : une seule bête (GenerateAnimals avec animalCount = 1) et son propre choix d'espèce par
    // AnimalWeight vanilla : volontairement non touché (une bête seule doit rester la plus forte espèce possible).

    [HarmonyPatch(typeof(AggressiveAnimalIncidentUtility), "TryGetAnimalFromList")]
    public static class Patch_AnimalFromList
    {
        public static bool Prefix(float points, List<PawnKindDef> animals, ref PawnKindDef animalKind, ref bool __result)
        {
            __result = AnimalCaps.TryGetAnimalFromList(points, animals, out animalKind);
            return false;
        }
    }

    [HarmonyPatch]
    public static class Patch_AnimalsCountCallers
    {
        private static readonly MethodInfo VanillaCount = AccessTools.Method(typeof(AggressiveAnimalIncidentUtility), nameof(AggressiveAnimalIncidentUtility.GetAnimalsCount));
        private static readonly MethodInfo SiteCount = AccessTools.Method(typeof(SitePartWorker_Manhunters), "GetAnimalsCount");
        private static readonly MethodInfo MyCount = AccessTools.Method(typeof(AnimalCaps), nameof(AnimalCaps.AnimalsCount));
        private static readonly MethodInfo MySiteCount = AccessTools.Method(typeof(AnimalCaps), nameof(AnimalCaps.SiteAnimalsCount));

        public static IEnumerable<MethodBase> TargetMethods()
        {
            Type[] types = { typeof(AggressiveAnimalIncidentUtility), typeof(IncidentWorker_AggressiveAnimals),
                             typeof(RimWorld.QuestGen.QuestNode_ManhunterPack), typeof(SitePartWorker_Manhunters) };
            return CallSwap.Callers(VanillaCount, types).Concat(CallSwap.Callers(SiteCount, types))
                .Where(m => m != SiteCount).Distinct();
        }

        public static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions)
        {
            foreach (CodeInstruction i in instructions)
            {
                if ((i.opcode == OpCodes.Call || i.opcode == OpCodes.Callvirt) && i.operand is MethodInfo mi)
                {
                    if (mi == VanillaCount) { i.opcode = OpCodes.Call; i.operand = MyCount; }
                    else if (mi == SiteCount) { i.opcode = OpCodes.Call; i.operand = MySiteCount; }
                }
                yield return i;
            }
        }
    }

    /// <summary>Filet pour les appelants d'autres mods (sans effet si le JIT a inliné la méthode).</summary>
    [HarmonyPatch(typeof(AggressiveAnimalIncidentUtility), nameof(AggressiveAnimalIncidentUtility.GetAnimalsCount))]
    public static class Patch_AnimalsCount
    {
        public static bool Prefix(PawnKindDef animalKind, float points, ref int __result)
        {
            __result = AnimalCaps.AnimalsCount(animalKind, points);
            return false;
        }
    }

    /// <summary>
    /// Events animaux plus rares quand la menace dépasse ce qu'ils peuvent dépenser :
    /// chance × (points absorbables ÷ points de la colonie), plancher réglable (jamais supprimés).
    /// </summary>
    [HarmonyPatch(typeof(StorytellerComp), "IncidentChanceFinal")]
    public static class Patch_AnimalEventRarity
    {
        // B7 : indexé par l'objet Map (pas uniqueID, qui repart de 0 à chaque partie) ; entrées libérées avec la carte
        private sealed class PowerEntry { public float value; }
        private static ConditionalWeakTable<Map, PowerEntry> maxPowerByMap = new ConditionalWeakTable<Map, PowerEntry>();
        private static float globalMaxPower = -1f;

        /// <summary>Vidé à chaque chargement / nouvelle partie (Game.FinalizeInit).</summary>
        public static void ClearCache() => maxPowerByMap = new ConditionalWeakTable<Map, PowerEntry>();

        public static void Postfix(IncidentDef def, IIncidentTarget target, ref float __result)
        {
            if (__result <= 0f || def?.Worker == null || target is not Map map) return;
            try
            {
                float capacity = Capacity(def.Worker, map);
                if (capacity < 0f) return;
                float points = StorytellerUtility.DefaultThreatPointsNow(map);
                if (points <= capacity) return;
                __result *= Mathf.Clamp(capacity / points, HRSettings.AnimalRarityFloor, 1f);
            }
            catch (Exception e)
            {
                Log.WarningOnce("[Hardened Raids] animal event rarity: " + e.Message, 0x48524152);
            }
        }

        /// <summary>Points qu'un event animal peut réellement dépenser (-1 : pas un event animal).</summary>
        private static float Capacity(IncidentWorker w, Map map)
        {
            if (w is IncidentWorker_AggressiveAnimals) return AnimalCaps.MaxAnimals * MaxManhunterPower(map);
            if (w.GetType().Name == "ManhunterSingleArrival") return MaxManhunterPower(map);   // VEE : une seule bête
            if (w is IncidentWorker_AnimalInsanityMass)
            {
                float best = map.mapPawns.AllPawnsSpawned.Where(IncidentWorker_AnimalInsanityMass.AnimalUsable)
                    .GroupBy(p => p.kindDef).Where(g => g.Count() >= 3)
                    .Select(g => g.Count() * g.Key.combatPower).DefaultIfEmpty(0f).Max();
                // inverse de la réduction vanilla : au-delà de 250, seuls 50 % des points servent
                return best <= 250f ? best : (best - 250f) * 2f + 250f;
            }
            return -1f;
        }

        /// <summary>Bête la plus forte qui peut arriver enragée sur cette carte (biome d'abord, comme vanilla).</summary>
        private static float MaxManhunterPower(Map map)
        {
            if (maxPowerByMap.TryGetValue(map, out PowerEntry e)) return e.value;
            float v = map.Biomes.SelectMany(b => b.AllWildAnimals).Where(AnimalCaps.CanArriveManhunter)
                .Select(k => k.combatPower).DefaultIfEmpty(0f).Max();
            if (v <= 0f)
            {
                if (globalMaxPower < 0f)
                    globalMaxPower = DefDatabase<PawnKindDef>.AllDefs.Where(AnimalCaps.CanArriveManhunter)
                        .Select(k => k.combatPower).DefaultIfEmpty(100f).Max();
                v = globalMaxPower;
            }
            maxPowerByMap.AddOrUpdate(map, new PowerEntry { value = v });
            return v;
        }
    }

    [HarmonyPatch(typeof(Game), nameof(Game.FinalizeInit))]
    public static class Patch_ClearEventCaches
    {
        public static void Postfix() => Patch_AnimalEventRarity.ClearCache();
    }
}
