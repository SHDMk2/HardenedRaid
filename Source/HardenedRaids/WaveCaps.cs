using System.Collections.Generic;
using System.Linq;
using HarmonyLib;
using RimWorld;
using UnityEngine;
using Verse;

namespace HardenedRaids
{
    /// <summary>
    /// Plafond par vague pour la génération vanilla d'un raid : repli après un échec de la génération Hardened
    /// (B12 / A10) et factions non humaines (A12 : mécanoïdes…). Le tirage vanilla des pawnkinds
    /// (ChoosePawnGenOptionsByPoints, rien n'est encore généré) est tronqué au plafond ; le reste part en vagues
    /// différées, chacune avec les points des pawnkinds qu'elle remplace.
    /// </summary>
    public static class VanillaWaveCap
    {
        private static PawnGroupMakerParms armed;
        private static RaidFrame armedFrame;
        private static int armedCap;

        /// <param name="frame">cadre du raid si c'est son groupe principal (vagues possibles), sinon null (simple troncature).</param>
        public static bool Arm(PawnGroupMakerParms parms, RaidFrame frame, int cap)
        {
            if (parms == null || cap <= 0) return false;
            armed = parms;
            armedFrame = frame;
            armedCap = cap;
            return true;
        }

        public static void Disarm()
        {
            armed = null;
            armedFrame = null;
        }

        /// <summary>
        /// Plafond d'un groupe humain hors raid généré par vanilla (EnableTiers décoché ; avec les paliers, la génération
        /// Hardened et son repli plafonnent déjà) : groupe hostile (site, quête, embuscade…) au plafond par vague, défenseurs
        /// d'une colonie en génération, hostile ou neutre / alliée attaquée (DU1), à ce plafond × SettlementCapFactor (DU2).
        /// Une seule vague, surplus perdu. 0 = pas de plafond.
        /// </summary>
        internal static int OutOfRaidCap(PawnGroupMakerParms parms)
        {
            if (HRSettings.EnableTiers) return 0;
            Faction f = parms?.faction;
            if (f == null || f.IsPlayer || f.def == null || !f.def.humanlikeFaction) return 0;
            if (parms.groupKind != PawnGroupKindDefOf.Combat && parms.groupKind != PawnGroupKindDefOf.Settlement) return 0;
            if (!f.HostileTo(Faction.OfPlayer) && !SettlementDefense.IsDefenderGroup(parms)) return 0;
            return Tiers.WaveCapFor(f.def) * RaidGenerator.SingleWaveCapFactor(parms);
        }

        /// <summary>Plafond des non-humains : le réglage low-tech (décision D5).</summary>
        public static int NonHumanCap => HRSettings.CapLowTech;

        internal static void Apply(PawnGroupMakerParms groupParms, ref IEnumerable<PawnGenOptionWithXenotype> result)
        {
            if (armed == null || groupParms != armed || result == null) return;
            RaidFrame frame = armedFrame;
            int cap = armedCap;
            Disarm();   // une seule fois : les tirages imbriqués éventuels ne sont pas concernés
            List<PawnGenOptionWithXenotype> list = result as List<PawnGenOptionWithXenotype> ?? result.ToList();
            int n = list.Count;
            if (n <= cap * 1.1f) return;     // même tolérance que le plan Hardened : pas de mini-vague
            int waves = Mathf.CeilToInt(n / (float)cap);
            float totalCost = list.Sum(o => o.Cost);
            bool canWave = frame != null && frame.Wave == null && !frame.DebugTest && frame.Parms != null && totalCost > 0f;
            // vagues égales ; sans vagues (hors raid, vague 2+), on garde tout le plafond
            int keep = canWave ? Mathf.CeilToInt(n / (float)waves) : cap;
            // le chef reste dans la première vague
            List<PawnGenOptionWithXenotype> ordered = list.OrderByDescending(o => o.Option.kind.factionLeader).ToList();
            List<PawnGenOptionWithXenotype> kept = ordered.Take(keep).ToList();
            List<PawnGenOptionWithXenotype> rest = ordered.Skip(keep).ToList();
            result = kept;

            HRLog.Msg($"génération vanilla plafonnée ({groupParms.faction?.Name}, {groupParms.points:F0} pts) : {kept.Count}/{n} pawns"
                      + (canWave ? $", {rest.Count} en {waves - 1} vague(s) différée(s)." : ", le reste est abandonné."));
            if (!canWave) return;
            var wp = new WavePlan { total = n, waves = waves, tierX = -1f, vanilla = true };
            for (int i = 0; i < rest.Count; i += keep)
            {
                List<PawnGenOptionWithXenotype> chunk = rest.Skip(i).Take(keep).ToList();
                wp.next.Add(new WavePlanItem { count = chunk.Count, share = chunk.Sum(o => o.Cost) / totalCost });
            }
            frame.Planned = wp;
        }
    }

    [HarmonyPatch(typeof(PawnGroupMakerUtility), nameof(PawnGroupMakerUtility.ChoosePawnGenOptionsByPoints))]
    public static class Patch_ChooseOptionsWaveCap
    {
        public static void Postfix(PawnGroupMakerParms groupParms, ref IEnumerable<PawnGenOptionWithXenotype> __result)
        {
            VanillaWaveCap.Apply(groupParms, ref __result);
        }
    }
}
