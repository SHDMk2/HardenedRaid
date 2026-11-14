using System.Collections.Generic;
using System.Runtime.CompilerServices;
using RimWorld;
using Verse;

namespace HardenedRaids
{
    /// <summary>
    /// État d'un raid en cours d'exécution (de l'entrée à la sortie d'IncidentWorker_Raid.TryGenerateRaidInfo).
    /// Un raid déclenché pendant un autre (quête, signal, mod) a son propre cadre : voir <see cref="RaidContext"/>.
    /// Rien n'est sauvegardé.
    /// </summary>
    public class RaidFrame
    {
        public IncidentParms Parms;
        public bool DebugTest;
        public float PreAdjustPoints = -1f;
        /// <summary>Vague différée en cours de lancement (null = raid d'origine).</summary>
        public WaveEntry Wave;
        /// <summary>Véhicules achetés pour cette vague, apparus après l'arrivée de l'infanterie.</summary>
        public List<ThingDef> PendingVehicles;
        /// <summary>spawnCenter fourni par l'appelant (quête…), avant la résolution vanilla ; Invalid sinon.</summary>
        public IntVec3 PresetSpawnCenter = IntVec3.Invalid;
        /// <summary>Palier forcé du raid (vague 1 comprise), -1 tant qu'inconnu.</summary>
        public float TierX = -1f;
        /// <summary>Paramètres du groupe principal du raid (le premier groupe de combat de la faction du raid).</summary>
        public PawnGroupMakerParms MainGroup;
        public bool MainGroupClaimed;
        /// <summary>Vagues suivantes à programmer si le raid réussit (validées dans le postfix de TryGenerateRaidInfo).</summary>
        public WavePlan Planned;

        public bool TryClaimMainGroup(PawnGroupMakerParms parms)
        {
            if (MainGroupClaimed || parms == null || Parms == null || parms.faction != Parms.faction) return false;
            MainGroupClaimed = true;
            MainGroup = parms;
            return true;
        }

        public bool IsMainGroup(PawnGroupMakerParms parms) => parms != null && MainGroup == parms;

        /// <summary>Génération ratée : on oublie les vagues et véhicules prévus.</summary>
        public void CancelPlanned()
        {
            Planned = null;
            PendingVehicles = null;
        }
    }

    /// <summary>
    /// Pile des raids en cours. L'API statique (Parms, DebugTest, PreAdjustPoints, PendingWave, PendingVehicles)
    /// lit le raid le plus interne, comme avant ; null / valeurs neutres hors raid.
    /// </summary>
    public static class RaidContext
    {
        private static readonly List<RaidFrame> stack = new List<RaidFrame>();
        /// <summary>Vague à attacher au prochain cadre ouvert (posée par GameComponent_Waves juste avant TryExecute).</summary>
        internal static WaveEntry NextWave;
        private static readonly ConditionalWeakTable<IncidentParms, StrongBoxF> tierByParms = new ConditionalWeakTable<IncidentParms, StrongBoxF>();

        private class StrongBoxF { public float v; }

        public static RaidFrame Current => stack.Count > 0 ? stack[stack.Count - 1] : null;
        public static int Depth => stack.Count;

        public static IncidentParms Parms => Current?.Parms;
        public static bool DebugTest => Current?.DebugTest ?? false;
        public static WaveEntry PendingWave => Current?.Wave;
        public static float TierX => Current?.TierX ?? -1f;

        public static float PreAdjustPoints
        {
            get => Current?.PreAdjustPoints ?? -1f;
            set { if (Current != null) Current.PreAdjustPoints = value; }
        }

        public static List<ThingDef> PendingVehicles
        {
            get => Current?.PendingVehicles;
            set { if (Current != null) Current.PendingVehicles = value; }
        }

        internal static RaidFrame Push(IncidentParms parms, bool debugTest)
        {
            var f = new RaidFrame
            {
                Parms = parms, DebugTest = debugTest, Wave = NextWave,
                PresetSpawnCenter = parms != null ? parms.spawnCenter : IntVec3.Invalid,
            };
            NextWave = null;
            stack.Add(f);
            return f;
        }

        /// <summary>Retire ce cadre (et tout cadre resté au-dessus après une exception).</summary>
        internal static void Pop(RaidFrame f)
        {
            int i = stack.LastIndexOf(f);
            if (i >= 0) stack.RemoveRange(i, stack.Count - i);
            if (f?.Parms != null && f.TierX > 0f)
            {
                tierByParms.Remove(f.Parms);
                tierByParms.Add(f.Parms, new StrongBoxF { v = f.TierX });
            }
        }

        /// <summary>Palier forcé d'un raid déjà généré (lu par les lords créés après TryGenerateRaidInfo), -1 sinon.</summary>
        public static float TierXFor(IncidentParms parms)
        {
            if (parms == null) return -1f;
            RaidFrame c = Current;
            if (c != null && c.Parms == parms && c.TierX > 0f) return c.TierX;
            return tierByParms.TryGetValue(parms, out StrongBoxF b) ? b.v : -1f;
        }

        /// <summary>Sécurité au changement de partie : rien ne doit survivre d'une partie à l'autre.</summary>
        internal static void Reset()
        {
            stack.Clear();
            NextWave = null;
        }
    }
}
