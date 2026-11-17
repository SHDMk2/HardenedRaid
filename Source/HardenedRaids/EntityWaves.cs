using System.Collections.Generic;
using System.Linq;
using RimWorld;
using UnityEngine;
using Verse;
using Verse.AI.Group;

namespace HardenedRaids
{
    /// <summary>
    /// Entités d'Anomaly (DU13). Leurs groupes (sightstealers, gorehulks, devoreurs, chimères, chairbêtes, noctols…) sont
    /// générés hors raid (incident, quête, site, réaction de la chair) avec des points tirés de la menace, déplafonnée par le
    /// mod : 100 000 de menace → ~1 000 sightstealers en un seul groupe. Plafond par vague = plafond des non-humains
    /// (CapLowTech) ; le surplus arrive plus tard en vagues par le bord de la carte (réglage EntityWaves) ou est perdu
    /// (réglage décoché). Exceptions : défenseurs de l'Éveil du Vide (métalhorreurs) sans vagues (surplus perdu) ;
    /// shamblers non concernés (leur générateur vanilla s'arrête à 200).
    /// Les incidents d'entités deviennent aussi plus rares quand la menace dépasse ce qu'une vague peut dépenser
    /// (<see cref="IncidentCapacity"/>, même plancher que les events animaux).
    /// </summary>
    public static class EntityWaves
    {
        /// <summary>Génération d'une vague différée en cours : pas de nouvelles vagues (simple troncature).</summary>
        private static bool firing;
        private static readonly Dictionary<PawnGroupKindDef, float> maxCostByKind = new Dictionary<PawnGroupKindDef, float>();
        /// <summary>Générateur vanilla des shamblers (PawnGroupKindWorker_Shamblers) : 200 tirages au plus.</summary>
        private const int VanillaShamblerMax = 200;

        public static void ClearCache() => maxCostByKind.Clear();

        public static bool IsEntityGroup(PawnGroupMakerParms p)
        {
            Faction e = Find.FactionManager?.OfEntities;
            return e != null && p?.faction == e && p.groupKind != null;
        }

        /// <summary>Carte qui recevra les vagues différées ; null = surplus perdu (réglage décoché, défenseurs, carte introuvable).</summary>
        public static Map WaveMap(PawnGroupMakerParms p)
        {
            if (!HRSettings.EntityOverflowWaves || firing || p == null || !p.tile.Valid) return null;
            if (p.groupKind == PawnGroupKindDefOf.Metalhorrors) return null;   // défenseurs de structure : pas d'assaut différé
            Map pocket = null;
            foreach (Map m in Find.Maps)
            {
                if (m.Tile != p.tile) continue;
                if (!m.IsPocketMap) return m;
                pocket ??= m;
            }
            return pocket;
        }

        /// <summary>Programme le surplus d'un groupe d'entités plafonné (appelé par VanillaWaveCap.Apply).</summary>
        internal static void Schedule(PawnGroupMakerParms gp, Map map, List<PawnGenOptionWithXenotype> rest, int keep, float totalCost, int total, int kept)
        {
            var comp = Current.Game?.GetComponent<GameComponent_Waves>();
            if (comp == null || rest.Count == 0 || keep <= 0) return;
            int waves = 1 + Mathf.CeilToInt(rest.Count / (float)keep);
            int tick = Find.TickManager.TicksGame;
            int index = 2;
            for (int i = 0; i < rest.Count; i += keep)
            {
                List<PawnGenOptionWithXenotype> chunk = rest.Skip(i).Take(keep).ToList();
                float pts = gp.points * chunk.Sum(o => o.Cost) / totalCost;
                tick += Mathf.RoundToInt(Rand.Range(HRSettings.WaveDelayHoursMin, HRSettings.WaveDelayHoursMax) * GenDate.TicksPerHour);
                comp.pending.Add(new WaveEntry
                {
                    fireTick = tick, map = map, faction = gp.faction, points = pts, adjustedPoints = pts, tierX = -1f,
                    count = chunk.Count, index = index++, totalWaves = waves, groupKind = gp.groupKind, entity = true,
                });
            }
            Messages.Message(Strings.T("MsgEntityWaves", total, kept, waves - 1,
                                 $"{HRSettings.WaveDelayHoursMin:F0}-{HRSettings.WaveDelayHoursMax:F0} h"),
                             MessageTypeDefOf.ThreatBig);
        }

        /// <summary>Vague différée : nouveau groupe du même type, aux points de sa part, par le bord de la carte, en assaut.</summary>
        internal static void Fire(WaveEntry w)
        {
            Map map = w.map;
            Faction f = Find.FactionManager?.OfEntities;
            if (!ModsConfig.AnomalyActive || f == null || w.groupKind == null)
            {
                HRLog.Msg($"vague d'entités {w.index}/{w.totalWaves} annulée (Anomaly ou type de groupe absent).");
                return;
            }
            var ip = new IncidentParms { target = map, faction = f, points = w.points, raidArrivalMode = PawnsArrivalModeDefOf.EdgeWalkIn };
            if (!ip.raidArrivalMode.Worker.TryResolveRaidSpawnCenter(ip))
            {
                HRLog.Msg($"vague d'entités {w.index}/{w.totalWaves} annulée (pas de point d'entrée).");
                return;
            }
            var gp = new PawnGroupMakerParms
            {
                groupKind = w.groupKind, tile = map.Tile, faction = f,
                points = Mathf.Max(w.points, f.def.MinPointsToGeneratePawnGroup(w.groupKind) * 1.05f),
            };
            List<Pawn> pawns;
            firing = true;
            try { pawns = PawnGroupMakerUtility.GeneratePawns(gp, warnOnZeroResults: false).ToList(); }
            finally { firing = false; }
            if (pawns.Count == 0)
            {
                HRLog.Msg($"vague d'entités {w.index}/{w.totalWaves} : aucun pawn généré.");
                return;
            }
            ip.raidArrivalMode.Worker.Arrive(pawns, ip);
            LordMaker.MakeNewLord(f, MakeLordJob(w.groupKind, f), map, pawns);
            Find.LetterStack.ReceiveLetter(Strings.T("LetterEntityWave"), Strings.T("LetterEntityWaveText", w.index, w.totalWaves, pawns.Count),
                                           LetterDefOf.ThreatBig, new LookTargets(pawns));
            HRLog.Msg($"vague d'entités {w.index}/{w.totalWaves} ({w.groupKind.defName}) : {pawns.Count} pawns, {gp.points:F0} pts.");
        }

        /// <summary>Même comportement que l'incident vanilla du type de groupe ; à défaut, assaut de la colonie.</summary>
        private static LordJob MakeLordJob(PawnGroupKindDef k, Faction f)
        {
            if (k == PawnGroupKindDefOf.Sightstealers) return new LordJob_SightstealerAssault();
            if (k == PawnGroupKindDefOf.Gorehulks) return new LordJob_GorehulkAssault();
            if (k == PawnGroupKindDefOf.Devourers) return new LordJob_DevourerAssault();
            if (k == PawnGroupKindDefOf.Chimeras) return new LordJob_ChimeraAssault();
            if (k == PawnGroupKindDefOf.Shamblers) return new LordJob_ShamblerAssault();
            if (k == PawnGroupKindDefOf.Fleshbeasts || k == PawnGroupKindDefOf.FleshbeastsWithDreadmeld) return new LordJob_FleshbeastAssault();
            return new LordJob_AssaultColony(f, canKidnap: false, canTimeoutOrFlee: false, sappers: false, useAvoidGridSmart: false, canSteal: false);
        }

        /// <summary>
        /// Points de menace qu'un incident d'entités peut dépenser en une vague (-1 : pas un incident d'entités) :
        /// plafond × entité la plus chère du type de groupe ÷ part de la menace que l'incident utilise.
        /// </summary>
        public static bool IsEntityIncident(IncidentWorker w) =>
            ModsConfig.AnomalyActive
            && (w is IncidentWorker_SightstealerArrival || w is IncidentWorker_SightstealerSwarm || w is IncidentWorker_GorehulkAssault
                || w is IncidentWorker_DevourerAssault || w is IncidentWorker_DevourerWaterAssault || w is IncidentWorker_ChimeraAssault
                || w is IncidentWorker_ShamblerAssault || w is IncidentWorker_ShamblerSwarm);

        public static float IncidentCapacity(IncidentWorker w, float threat)
        {
            PawnGroupKindDef k;
            float factor = 1f;
            switch (w)
            {
                case IncidentWorker_SightstealerArrival _:
                    k = PawnGroupKindDefOf.Sightstealers;
                    factor = IncidentWorker_SightstealerArrival.SightstealerPointsFactorCurve.Evaluate(threat);   // dernière vague : 100 %
                    break;
                case IncidentWorker_SightstealerSwarm _: k = PawnGroupKindDefOf.Sightstealers; factor = 0.5f; break;
                case IncidentWorker_GorehulkAssault _: k = PawnGroupKindDefOf.Gorehulks; break;
                case IncidentWorker_DevourerAssault _: k = PawnGroupKindDefOf.Devourers; break;
                case IncidentWorker_DevourerWaterAssault _: k = PawnGroupKindDefOf.Devourers; factor = 0.7f; break;
                case IncidentWorker_ChimeraAssault _: k = PawnGroupKindDefOf.Chimeras; break;
                case IncidentWorker_ShamblerAssault _: k = PawnGroupKindDefOf.Shamblers; break;
                case IncidentWorker_ShamblerSwarm _: k = PawnGroupKindDefOf.Shamblers; factor = 1.3f; break;   // variance max de la nuée
                default: return -1f;
            }
            if (k == null || factor <= 0f) return -1f;
            float cost = MaxCost(k);
            if (cost <= 0f) return -1f;
            int n = VanillaWaveCap.NonHumanCap;
            if (k == PawnGroupKindDefOf.Shamblers) n = Mathf.Min(n, VanillaShamblerMax);
            return n * cost / factor;
        }

        private static float MaxCost(PawnGroupKindDef k)
        {
            if (maxCostByKind.TryGetValue(k, out float c)) return c;
            c = -1f;
            List<PawnGroupMaker> makers = Find.FactionManager?.OfEntities?.def?.pawnGroupMakers;
            if (makers != null)
                foreach (PawnGroupMaker m in makers)
                {
                    if (m.kindDef != k || m.options == null) continue;
                    foreach (PawnGenOption o in m.options) c = Mathf.Max(c, o.Cost);
                }
            maxCostByKind[k] = c;
            return c;
        }
    }
}
