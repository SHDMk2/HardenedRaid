using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using HarmonyLib;
using RimWorld;
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
    }

    [HarmonyPatch(typeof(AggressiveAnimalIncidentUtility), nameof(AggressiveAnimalIncidentUtility.GetAnimalsCount))]
    public static class Patch_AnimalsCount
    {
        public static bool Prefix(PawnKindDef animalKind, float points, ref int __result)
        {
            __result = Mathf.Clamp(Mathf.RoundToInt(points / animalKind.combatPower), 2, AnimalCaps.MaxAnimals);
            return false;
        }
    }

    /// <summary>Poids d'une espèce : même formule que vanilla, mais 0 seulement au-delà du nouveau plafond.</summary>
    [HarmonyPatch(typeof(AggressiveAnimalIncidentUtility), nameof(AggressiveAnimalIncidentUtility.AnimalWeight))]
    public static class Patch_AnimalWeight
    {
        public static bool Prefix(PawnKindDef animal, float points, ref float __result)
        {
            points = Mathf.Max(points, 70f);
            if (animal.combatPower * 2f > points) { __result = 0f; return false; }
            int max = AnimalCaps.MaxAnimals;
            int n = Mathf.Min(Mathf.RoundToInt(points / animal.combatPower), max);
            __result = Mathf.Clamp01(Mathf.InverseLerp(max, 10f, n));
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
        private static readonly Dictionary<int, float> maxPowerByMap = new Dictionary<int, float>();
        private static float globalMaxPower = -1f;

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
            if (maxPowerByMap.TryGetValue(map.uniqueID, out float v)) return v;
            v = map.Biomes.SelectMany(b => b.AllWildAnimals).Where(AnimalCaps.CanArriveManhunter)
                .Select(k => k.combatPower).DefaultIfEmpty(0f).Max();
            if (v <= 0f)
            {
                if (globalMaxPower < 0f)
                    globalMaxPower = DefDatabase<PawnKindDef>.AllDefs.Where(AnimalCaps.CanArriveManhunter)
                        .Select(k => k.combatPower).DefaultIfEmpty(100f).Max();
                v = globalMaxPower;
            }
            maxPowerByMap[map.uniqueID] = v;
            return v;
        }
    }
}
