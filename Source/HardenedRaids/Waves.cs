using System.Collections.Generic;
using System.Linq;
using RimWorld;
using UnityEngine;
using Verse;

namespace HardenedRaids
{
    /// <summary>
    /// Vague différée d'un raid. Compatibilité des saves : tous les champs ajoutés en v1.1 ont un défaut sûr ; une
    /// entrée v1.0 (champs absents) se comporte comme avant, sans véhicules, sans dotation imposée.
    /// </summary>
    public class WaveEntry : IExposable
    {
        public int fireTick;
        public Map map;
        public Faction faction;
        public RaidStrategyDef strategy;
        public PawnsArrivalModeDef arrival;
        public float points;           // points avant ajustement (stratégie / arrivée), comme les points d'un incident
        public float tierX;
        public int count;
        public int index;
        public int totalWaves;

        // ---- v1.1
        /// <summary>defNames des véhicules prévus pour cette vague (null = aucun ; save v1.0 : aucun, jamais d'achat).</summary>
        public List<string> vehicles;
        /// <summary>Points déjà ajustés de la vague (0 = save v1.0 : vanilla réajuste <see cref="points"/>).</summary>
        public float adjustedPoints;
        /// <summary>Paramètres du raid d'origine, nettoyés (quête, signal de fin, vol, enlèvement, âge…). null en v1.0.</summary>
        public IncidentParms raidParms;
        /// <summary>Quête du raid d'origine (-1 = aucune).</summary>
        public int questId = -1;
        /// <summary>Même dotation que la vague 1 : graine des dotations et plafond d'armure du raid entier.</summary>
        public bool sameLoadout;
        public int loadoutSeed;
        public bool needBreacher;
        public int gateMode;           // 0 = v1.0 (recalcul sur les points de la vague), 1 = sans plafond, 2 = plafond ci-dessous
        public float gatePoints = -1f; // points du raid entier (plafond d'armure, mécanoïdes blindés compris)
        public float gateCap;
        public int gateTech;
        /// <summary>Incident à relancer (null = raid ennemi ; infestation : voir InfestationWaves).</summary>
        public IncidentDef incident;
        public int postponed;

        /// <summary>Annulée par un signal pendant le traitement des vagues (non sauvegardé).</summary>
        public bool cancelled;

        public bool IsInfestation => incident != null && incident == IncidentDefOf.Infestation;

        /// <summary>Véhicules de la vague encore présents dans le jeu (defs retirées ignorées).</summary>
        public List<ThingDef> ResolveVehicles()
        {
            if (vehicles == null || vehicles.Count == 0) return null;
            var res = new List<ThingDef>();
            foreach (string n in vehicles)
            {
                ThingDef d = n.NullOrEmpty() ? null : DefDatabase<ThingDef>.GetNamedSilentFail(n);
                if (d != null) res.Add(d);
            }
            return res.Count > 0 ? res : null;
        }

        /// <summary>Plafond d'armure du raid d'origine ; false (save v1.0) = à recalculer.</summary>
        public bool TryGetGate(out ArmorGate gate)
        {
            gate = null;
            if (gateMode == 0) return false;
            if (gateMode == 2 && HRSettings.ArmorGate)
                gate = new ArmorGate { points = gatePoints, cap = gateCap, colonyTech = (TechLevel)gateTech };
            return true;
        }

        public void SetGate(ArmorGate gate, float raidPoints)
        {
            gatePoints = raidPoints;
            if (gate == null) { gateMode = 1; return; }
            gateMode = 2;
            gateCap = gate.cap;
            gateTech = (int)gate.colonyTech;
            gatePoints = gate.points;
        }

        public void ExposeData()
        {
            if (Scribe.mode == LoadSaveMode.Saving) WaveScheduler.CleanForSave(raidParms);
            Scribe_Values.Look(ref fireTick, "fireTick");
            Scribe_References.Look(ref map, "map");
            Scribe_References.Look(ref faction, "faction");
            Scribe_Defs.Look(ref strategy, "strategy");
            Scribe_Defs.Look(ref arrival, "arrival");
            Scribe_Values.Look(ref points, "points");
            Scribe_Values.Look(ref tierX, "tierX");
            Scribe_Values.Look(ref count, "count");
            Scribe_Values.Look(ref index, "index");
            Scribe_Values.Look(ref totalWaves, "totalWaves");
            Scribe_Collections.Look(ref vehicles, "vehicles", LookMode.Value);
            Scribe_Values.Look(ref adjustedPoints, "adjustedPoints", 0f);
            Scribe_Deep.Look(ref raidParms, "raidParms");
            Scribe_Values.Look(ref questId, "questId", -1);
            Scribe_Values.Look(ref sameLoadout, "sameLoadout", false);
            Scribe_Values.Look(ref loadoutSeed, "loadoutSeed", 0);
            Scribe_Values.Look(ref needBreacher, "needBreacher", false);
            Scribe_Values.Look(ref gateMode, "gateMode", 0);
            Scribe_Values.Look(ref gatePoints, "gatePoints", -1f);
            Scribe_Values.Look(ref gateCap, "gateCap", 0f);
            Scribe_Values.Look(ref gateTech, "gateTech", 0);
            Scribe_Defs.Look(ref incident, "incident");
            Scribe_Values.Look(ref postponed, "postponed", 0);
            if (Scribe.mode == LoadSaveMode.PostLoadInit)
                vehicles?.RemoveAll(v => v.NullOrEmpty());
        }
    }

    /// <summary>Une vague suivante prévue : effectif et part des points du raid.</summary>
    public class WavePlanItem
    {
        public int count;
        public float share;   // fraction des points du raid emportée par cette vague
        public List<ThingDef> vehicles;   // part des véhicules du raid (null = aucun)
    }

    /// <summary>Vagues suivantes prévues pendant la génération, programmées seulement si le raid réussit.</summary>
    public class WavePlan
    {
        public int total;      // effectif total du raid (toutes vagues)
        public int waves;
        public float tierX;
        public bool vanilla;   // génération vanilla plafonnée (repli, non-humains)
        public List<WavePlanItem> next = new List<WavePlanItem>();   // vagues 2..n
        // dotation du raid (Hardened)
        public int loadoutSeed;
        public bool needBreacher;
        public ArmorGate gate;
        public bool hasGate;
        // infestation
        public IncidentDef incident;
    }

    public static class WaveScheduler
    {
        /// <summary>
        /// Plan des vagues 2+ d'un raid Hardened : effectifs égaux (ou <paramref name="counts"/> quand les véhicules
        /// sont répartis), points au prorata, part des véhicules du raid.
        /// </summary>
        public static WavePlan FromRaidPlan(RaidPlan plan, int[] counts = null, List<List<ThingDef>> vehicles = null)
        {
            var wp = new WavePlan { total = plan.total, waves = plan.waves, tierX = plan.tierX };
            int remaining = plan.total - (counts != null ? counts[0] : plan.thisWave);
            for (int i = 2; i <= plan.waves && remaining > 0; i++)
            {
                int n = counts != null && counts.Length >= i ? Mathf.Min(counts[i - 1], remaining) : Mathf.Min(plan.thisWave, remaining);
                if (n <= 0) continue;
                remaining -= n;
                wp.next.Add(new WavePlanItem
                {
                    count = n, share = n / (float)plan.total,
                    vehicles = vehicles != null && vehicles.Count >= i && vehicles[i - 1].Count > 0 ? vehicles[i - 1] : null,
                });
            }
            return wp;
        }

        /// <summary>
        /// Copie nettoyée des paramètres du raid pour ses vagues : on garde ce qui définit le raid (quête, signal de
        /// fin, vol / enlèvement / fuite, restriction d'âge, lettres, cibles, graine…), on retire ce qui appartient à
        /// l'exécution d'origine (carte, lord, groupes de pawns, cadeaux, contrôleur) et les références fragiles.
        /// </summary>
        public static IncidentParms MakeTemplate(IncidentParms p, IntVec3 presetSpawnCenter)
        {
            IncidentParms t = p.ShallowCopy();
            t.target = null;
            t.faction = null;          // gardée dans WaveEntry.faction
            t.points = -1f;
            t.quest = null;            // gardée par son id (WaveEntry.questId)
            t.lord = null;
            t.controllerPawn = null;
            t.pawnGroups = null;
            t.pawnCount = 0;
            t.gifts = null;
            t.mechClusterSketch = null;
            t.storeGeneratedNeutralPawns = null;
            t.forced = true;
            // point d'arrivée imposé par l'appelant seulement (sinon chaque vague cherche le sien)
            t.spawnCenter = presetSpawnCenter;
            t.attackTargets = p.attackTargets?.Where(x => x != null && !x.Destroyed).ToList();
            t.letterHyperlinkThingDefs = p.letterHyperlinkThingDefs?.ToList();
            t.letterHyperlinkHediffDefs = p.letterHyperlinkHediffDefs?.ToList();
            return t;
        }

        internal static void CleanForSave(IncidentParms t)
        {
            t?.attackTargets?.RemoveAll(x => x == null || x.Destroyed);
        }

        /// <summary>Siège : seule la vague 1 monte le camp ; les suivantes viennent en assaut.</summary>
        private static RaidStrategyDef WaveStrategy(RaidStrategyDef s) =>
            s?.Worker is RaidStrategyWorker_Siege && !(s.Worker is RaidStrategyWorker_SiegeMechanoid) ? RaidStrategyDefOf.ImmediateAttack : s;

        /// <summary>Programme les vagues prévues par ce raid ; appelé après un TryGenerateRaidInfo réussi.</summary>
        public static void Commit(RaidFrame frame)
        {
            WavePlan wp = frame?.Planned;
            if (frame != null) frame.Planned = null;
            if (wp == null || wp.next.Count == 0 || frame.DebugTest || frame.Wave != null) return;
            IncidentParms parms = frame.Parms;
            if (parms?.target is not Map map || parms.faction == null) return;
            float basePoints = frame.PreAdjustPoints > 0f ? frame.PreAdjustPoints : parms.points;
            Schedule(wp, parms, map, parms.faction, WaveStrategy(parms.raidStrategy), parms.raidArrivalMode, basePoints, parms.points, frame.PresetSpawnCenter);
            Messages.Message(Strings.T("MsgWaves", parms.faction.Name, wp.waves, wp.total,
                                 $"{HRSettings.WaveDelayHoursMin:F0}-{HRSettings.WaveDelayHoursMax:F0} h"),
                             MessageTypeDefOf.ThreatBig);
        }

        internal static void Schedule(WavePlan wp, IncidentParms parms, Map map, Faction faction, RaidStrategyDef strategy, PawnsArrivalModeDef arrival,
                                      float basePoints, float adjusted, IntVec3 presetSpawnCenter)
        {
            var comp = Current.Game?.GetComponent<GameComponent_Waves>();
            if (comp == null) return;
            IncidentParms template = MakeTemplate(parms, presetSpawnCenter);
            template.raidStrategy = strategy;
            int questId = parms.quest?.id ?? -1;
            int tick = Find.TickManager.TicksGame;
            int index = 2;
            foreach (WavePlanItem it in wp.next)
            {
                tick += Mathf.RoundToInt(Rand.Range(HRSettings.WaveDelayHoursMin, HRSettings.WaveDelayHoursMax) * GenDate.TicksPerHour);
                var w = new WaveEntry
                {
                    fireTick = tick, map = map, faction = faction, strategy = strategy, arrival = arrival,
                    points = basePoints * it.share, adjustedPoints = adjusted * it.share,
                    tierX = wp.tierX, count = it.count, index = index++, totalWaves = wp.waves,
                    vehicles = it.vehicles?.Select(v => v.defName).ToList(),
                    raidParms = template, questId = questId, incident = wp.incident,
                    sameLoadout = !wp.vanilla && wp.incident == null, loadoutSeed = wp.loadoutSeed, needBreacher = wp.needBreacher,
                    gatePoints = adjusted,
                };
                if (wp.hasGate) w.SetGate(wp.gate, adjusted);
                comp.pending.Add(w);
            }
        }
    }

    public class GameComponent_Waves : GameComponent, ISignalReceiver
    {
        public List<WaveEntry> pending = new List<WaveEntry>();
        private static readonly List<WaveEntry> due = new List<WaveEntry>();

        public GameComponent_Waves(Game game) { RaidContext.Reset(); }

        public override void ExposeData()
        {
            Scribe_Collections.Look(ref pending, "pending", LookMode.Deep);
            if (Scribe.mode == LoadSaveMode.PostLoadInit)
            {
                pending ??= new List<WaveEntry>();
                pending.RemoveAll(w => w == null);
            }
        }

        /// <summary>Écoute les signaux : le signal de fin d'un raid de quête annule ses vagues (non sauvegardé).</summary>
        public override void FinalizeInit()
        {
            SignalManager sm = Find.SignalManager;
            if (sm != null && !sm.receivers.Contains(this)) sm.RegisterReceiver(this);
        }

        public void Notify_SignalReceived(Signal signal)
        {
            if (pending.Count == 0 || signal.tag.NullOrEmpty()) return;
            for (int i = pending.Count - 1; i >= 0; i--)
            {
                WaveEntry w = pending[i];
                string end = w.raidParms?.inSignalEnd;
                if (end.NullOrEmpty() || end != signal.tag) continue;
                w.cancelled = true;
                pending.RemoveAt(i);
                HRLog.Msg($"vague {w.index}/{w.totalWaves} annulée (fin du raid de quête : {signal.tag}).");
            }
        }

        /// <summary>Préparer la désactivation du mod : plus aucune vague en attente.</summary>
        public static void PrepareUninstall()
        {
            var comp = Current.Game?.GetComponent<GameComponent_Waves>();
            comp?.pending.Clear();
            RaidContext.Reset();
        }

        public override void GameComponentTick()
        {
            if (pending.Count == 0 || Find.TickManager.TicksGame % 250 != 0) return;
            int now = Find.TickManager.TicksGame;
            due.Clear();
            for (int i = pending.Count - 1; i >= 0; i--)
            {
                if (pending[i].fireTick > now) continue;
                due.Add(pending[i]);
                pending.RemoveAt(i);
            }
            // un signal envoyé pendant une vague peut en annuler une autre déjà sortie de la liste
            foreach (WaveEntry w in due.ToList())
                if (!w.cancelled) Fire(w, this);
            due.Clear();
        }

        /// <summary>Quête du raid terminée (ou disparue) : ses vagues n'arrivent plus.</summary>
        private static bool QuestOver(WaveEntry w, out Quest quest)
        {
            quest = null;
            if (w.questId < 0) return false;
            List<Quest> quests = Find.QuestManager.QuestsListForReading;
            for (int i = 0; i < quests.Count; i++) if (quests[i].id == w.questId) { quest = quests[i]; break; }
            return quest == null || quest.Historical;
        }

        private static void Fire(WaveEntry w, GameComponent_Waves comp)
        {
            if (w.map == null || !Find.Maps.Contains(w.map))
            {
                HRLog.Msg($"vague {w.index}/{w.totalWaves} annulée (carte plus valide).");
                return;
            }
            if (QuestOver(w, out Quest quest))
            {
                HRLog.Msg($"vague {w.index}/{w.totalWaves} annulée (quête terminée).");
                return;
            }
            if (w.faction == null || w.faction.defeated || !w.faction.HostileTo(Faction.OfPlayer))
            {
                HRLog.Msg($"vague {w.index}/{w.totalWaves} annulée (faction plus valide).");
                return;
            }
            try
            {
                if (!TryFire(w, w.arrival, quest) && !TryFire(w, null, quest))
                    HRLog.Msg($"vague {w.index}/{w.totalWaves} : le raid n'a pas pu être lancé.");
            }
            finally
            {
                RaidContext.NextWave = null;
            }
        }

        private static bool TryFire(WaveEntry w, PawnsArrivalModeDef arrival, Quest quest)
        {
            IncidentParms parms = w.raidParms != null ? w.raidParms.ShallowCopy() : new IncidentParms();
            parms.target = w.map;
            parms.faction = w.faction;
            parms.points = w.points;
            parms.raidStrategy = w.strategy;
            parms.raidArrivalMode = arrival;
            parms.forced = true;
            parms.quest = quest;
            if (arrival == null) parms.spawnCenter = IntVec3.Invalid;   // autre mode d'arrivée : son propre point
            parms.attackTargets = parms.attackTargets?.Where(x => x != null && !x.Destroyed).ToList();
            RaidContext.NextWave = w;   // attachée au cadre ouvert par ce raid (pas à un raid imbriqué)
            return IncidentDefOf.RaidEnemy.Worker.TryExecute(parms);
        }
    }
}
