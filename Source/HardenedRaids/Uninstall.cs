using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;
using RimWorld;
using Verse;
using Verse.AI;
using Verse.AI.Group;

namespace HardenedRaids
{
    /// <summary>
    /// « Préparer la désactivation » (réglages du mod, en partie seulement) : retire de la partie tout ce qui ne se
    /// chargerait pas sans le mod, pour que le joueur puisse sauvegarder puis retirer Hardened Raids sans casser la carte.
    ///  - lords Hardened (convoi, servants de tourelle, rembarquement) -> lords vanilla (assaut ou sortie de carte),
    ///    pawns à bord des véhicules gardés dans leur lord (VF les retire du lord quand le véhicule sort) ;
    ///  - jobs HR_BoardVehicle et duties HR_* retirés ;
    ///  - vagues en attente, registre des tourelles ravitaillées, convoi en attente vidés ;
    ///  - marqueur HR_RaiderTier retiré de tous les pawns (cartes, monde, caravanes, prisonniers, recrutés, morts, y compris
    ///    les cadavres et pawns rangés dans un conteneur : tombe, sarcophage, caisse, navette, porteur, véhicule, inventaire).
    /// </summary>
    public static class Uninstall
    {
        /// <summary>
        /// Point d'extension : méthodes statiques PrepareUninstall() des autres fichiers (état sauvegardé à neutraliser),
        /// appelées après les étapes de ce fichier. Le coordinateur les branche à la fusion, par exemple :
        ///     SomeFile.PrepareUninstall,
        /// Chacune est appelée dans son propre try/catch.
        /// </summary>
        public static readonly Action[] OtherSteps =
        {
            MapComponent_HostileTurretSupply.PrepareUninstall,   // lot S : registre des tourelles, caisses posées
            GameComponent_Waves.PrepareUninstall,                // lot waves : vagues en attente, pile de contextes
        };

        /// <summary>Bouton des réglages : seulement en partie.</summary>
        public static void DrawButton(Listing_Standard l)
        {
            if (Current.ProgramState != ProgramState.Playing || Current.Game == null) return;
            l.GapLine();
            if (l.ButtonText(Strings.T("PrepUninstall")))
                Find.WindowStack.Add(Dialog_MessageBox.CreateConfirmation(Strings.T("PrepUninstallConfirm"), Run, destructive: true));
        }

        private static int nLords, nPawns, nHediffs, nWaves, nErrors;

        public static void Run()
        {
            nLords = nPawns = nHediffs = nWaves = nErrors = 0;
            Step("convoy handoff", () => ConvoyHandoff.Pending = null);
            foreach (Map map in Find.Maps.ToList())
            {
                Step("convoys", () => ConvertConvoys(map));
                Step("turret crews", () => ConvertCrews(map));
                Step("boarding", () => ConvertBoarding(map));
            }
            Step("jobs and duties", ClearJobsAndDuties);
            Step("waves", () =>
            {
                List<WaveEntry> pending = Current.Game.GetComponent<GameComponent_Waves>()?.pending;
                if (pending == null) return;
                nWaves = pending.Count;
                pending.Clear();
            });
            Step("tier marker", RemoveTierMarkers);
            foreach (Action a in OtherSteps) if (a != null) Step(a.Method.DeclaringType?.Name ?? "step", a);

            Find.TickManager.Pause();
            string msg = Strings.T("PrepUninstallDone", nLords, nWaves, nHediffs);
            if (nErrors > 0) msg += "\n\n" + Strings.T("PrepUninstallErrors", nErrors);
            Find.WindowStack.Add(new Dialog_MessageBox(msg));
            Log.Message($"[Hardened Raids] prepare uninstall: {nLords} lords converted, {nPawns} pawns cleaned, {nWaves} waves cancelled, "
                        + $"{nHediffs} tier markers removed, {nErrors} error(s).");
        }

        private static void Step(string name, Action a)
        {
            try { a(); }
            catch (Exception e)
            {
                nErrors++;
                Log.Error($"[Hardened Raids] prepare uninstall ({name}): {e}");
            }
        }

        // ------------------------------------------------------------ lords
        private static bool Exiting(Lord l) => l.CurLordToil is LordToil_ExitMap || LordJob_HRConvoy.ExitLike(l);

        /// <summary>Un groupe de la faction (hors lords Hardened) attaque encore la carte.</summary>
        private static bool FactionAttacking(Map map, Faction f) =>
            map.lordManager.lords.Any(l => l.faction == f && l.ownedPawns.Count > 0 && !Exiting(l)
                                           && l.LordJob is not LordJob_HRConvoy && l.LordJob is not LordJob_HRTurretCrew && l.LordJob is not LordJob_HRBoard);

        private static LordJob ExitJob() => new LordJob_ExitMapBest(LocomotionUrgency.Jog, canDig: false, canDefendSelf: true);

        private static LordJob AssaultJob(Faction f) => new LordJob_AssaultColony(f, canKidnap: true, canTimeoutOrFlee: true);

        /// <summary>
        /// Remplace le lord : mêmes pawns (y compris ceux à bord d'un véhicule, non apparus), nouveau LordJob vanilla.
        /// Lord créé vide puis rempli : Patch_VehicleLord ne le transforme pas en convoi.
        /// </summary>
        private static void Replace(Lord old, Map map, Func<LordJob> makeJob, Func<Pawn, bool> toExit = null) =>
            Replace(old, map, makeJob, toExit, null, out _, out _);

        /// <param name="skip">pawns laissés sans lord</param>
        /// <param name="main">lord makeJob créé (null si personne)</param>
        /// <param name="exitLord">lord de sortie des pawns toExit (null si personne)</param>
        private static void Replace(Lord old, Map map, Func<LordJob> makeJob, Func<Pawn, bool> toExit, Func<Pawn, bool> skip, out Lord main, out Lord exitLord)
        {
            main = exitLord = null;
            Faction f = old.faction;
            List<Pawn> pawns = old.ownedPawns.ToList();
            map.lordManager.RemoveLord(old);   // Cleanup : lord et duty des pawns remis à zéro, jobs du lord arrêtés
            nLords++;
            pawns.RemoveAll(p => p == null || p.Destroyed || p.Dead || p.GetLord() != null || (skip != null && skip(p)));
            if (f == null || pawns.Count == 0) return;
            List<Pawn> exit = toExit != null ? pawns.Where(toExit).ToList() : new List<Pawn>();
            List<Pawn> rest = pawns.Where(p => !exit.Contains(p)).ToList();
            if (rest.Count > 0)
            {
                main = LordMaker.MakeNewLord(f, makeJob(), map);
                main.AddPawns(rest);
            }
            if (exit.Count > 0)
            {
                exitLord = LordMaker.MakeNewLord(f, ExitJob(), map);
                exitLord.AddPawns(exit);
            }
        }

        private static void ConvertConvoys(Map map)
        {
            foreach (Lord l in map.lordManager.lords.Where(x => x.LordJob is LordJob_HRConvoy).ToList())
            {
                var job = (LordJob_HRConvoy)l.LordJob;
                bool leave = job.retreating || job.RaidOver(map);
                if (!VehicleGate.Active)
                {
                    // véhicules perdus (VF absent) : rien à piloter
                    Replace(l, map, ExitJob);
                    continue;
                }
                // colons enlevés à bord : déposés (les lords qui suivent n'enregistrent pas l'enlèvement à la sortie)
                foreach (Pawn v in l.ownedPawns.ToList()) if (v.Spawned) DropCaptivesVF(v);
                if (!leave)
                {
                    // ceux qui n'ont pas encore débarqué descendent maintenant, dans le lord d'infanterie (vanilla) ;
                    // pas de nouvelle tourelle de camion. Pilote et tireurs restent à bord (équipage VF), le pilote
                    // d'un camion aussi : le camion repart seul.
                    job.deployTurrets = false;
                    foreach (Pawn v in l.ownedPawns.ToList())
                        if (v.Spawned && !job.unloaded.Contains(v)) job.Unload(v, map, keepDriver: true);
                }
                else foreach (Pawn v in l.ownedPawns) if (v.Spawned) UndeployVF(v);
                Faction f = l.faction;
                // Véhicule qui ne peut ni partir ni se battre (camion garé dont le pilote est descendu, véhicule immobilisé) :
                // VF ne fait pas sortir un véhicule sans équipage (JobGiver_ExitMap ne donne aucun job, il resterait pour
                // toujours dans son lord) -> laissé sur place sans lord, comme un véhicule abandonné du convoi (VF le rend
                // revendicable une fois la faction partie) ; ses occupants descendent et rejoignent l'infanterie ou la sortie.
                var stranded = new HashSet<Pawn>(l.ownedPawns.Where(v => StrandedVF(v, fight: !leave)));
                // DU5 : fantassin du lord du convoi (débarqué que VF a remis dans le lord du véhicule, sans notre prefix de
                // SpawnPawnNearVehicle) : ni blindé ni camion, il rejoint l'infanterie (raid en cours) ou la sortie
                var onFoot = new HashSet<Pawn>(l.ownedPawns.Where(p => !VehicleGate.IsVehicle(p)));
                bool Skip(Pawn p) => stranded.Contains(p) || onFoot.Contains(p);
                Lord main, exit;
                // raid en cours : les blindés continuent l'assaut (lord VF), les camions repartent
                if (leave) Replace(l, map, ExitJob, null, Skip, out main, out exit);
                else Replace(l, map, () => ArmoredJobVF(f), v => !HasTurretsVF(v), Skip, out main, out exit);
                if (leave) exit = main;
                foreach (Lord nl in new[] { main, exit }.Where(x => x != null).Distinct())
                    foreach (Pawn v in nl.ownedPawns) ReadyVF(v);   // moteur démarré : VF refuse de bouger sinon
                if (f == null) continue;
                Lord foot = null;
                void Join(Pawn p)
                {
                    // passager : il garde son lord (infanterie, rembarquement)
                    if (p == null || p.Destroyed || p.Dead || p.GetLord() != null) return;
                    foot ??= leave ? exit ?? LordMaker.MakeNewLord(f, ExitJob(), map) : InfantryLordOf(job, map, f);
                    foot.AddPawn(p);
                }
                foreach (Pawn p in onFoot) Join(p);
                foreach (Pawn v in stranded)
                {
                    List<Pawn> down = EmptyOutVF(v);
                    foreach (Pawn p in down) Join(p);
                    HRLog.Msg($"désactivation : {v.LabelShort} ne peut ni partir ni se battre, laissé sur place ({down.Count} occupant(s) descendu(s)).");
                }
            }
        }

        /// <summary>Lord d'infanterie du raid s'il attaque encore, sinon un assaut vanilla neuf.</summary>
        private static Lord InfantryLordOf(LordJob_HRConvoy job, Map map, Faction f)
        {
            Lord inf = job.infantryLord;
            if (inf != null && map.lordManager.lords.Contains(inf) && inf.ownedPawns.Count > 0 && !Exiting(inf)) return inf;
            return LordMaker.MakeNewLord(f, AssaultJob(f), map);
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        private static void UndeployVF(Pawn v) => VehicleCompat.Undeploy(v);

        [MethodImpl(MethodImplOptions.NoInlining)]
        private static bool HasTurretsVF(Pawn v) => VehicleCompat.HasTurrets(v);

        [MethodImpl(MethodImplOptions.NoInlining)]
        private static LordJob ArmoredJobVF(Faction f) => VehicleCompat.ArmoredLordJob(f);

        [MethodImpl(MethodImplOptions.NoInlining)]
        private static void ReadyVF(Pawn v) => VehicleCompat.Ready(v);

        [MethodImpl(MethodImplOptions.NoInlining)]
        private static void DropCaptivesVF(Pawn v) => VehicleCompat.DropCaptives(v);

        /// <summary>
        /// Véhicule qui ne peut ni partir ni se battre : sans pilote (après avoir mis un passager au volant) ou immobilisé. Pendant
        /// le raid, un blindé immobilisé qui a encore ses tireurs se bat sur place ; une pièce en batterie (ou qui se replie)
        /// n'est pas bloquée. Jamais vrai pour un pawn qui n'est pas un véhicule (traité à part, comme fantassin).
        /// </summary>
        [MethodImpl(MethodImplOptions.NoInlining)]
        private static bool StrandedVF(Pawn v, bool fight)
        {
            if (!VehicleCompat.IsVehicle(v) || !v.Spawned || VehicleCompat.IsDeployed(v) || VehicleCompat.IsDeploying(v)) return false;
            VehicleCompat.PromoteDriver(v);
            if (VehicleCompat.CanMove(v)) return false;
            return !(fight && VehicleCompat.HasTurrets(v) && VehicleCompat.PawnsAboard(v).Any());
        }

        /// <summary>Fait descendre tout l'équipage ; retourne ceux qui sont apparus sur la carte.</summary>
        [MethodImpl(MethodImplOptions.NoInlining)]
        private static List<Pawn> EmptyOutVF(Pawn v)
        {
            var down = new List<Pawn>();
            foreach (Pawn p in VehicleCompat.PawnsAboard(v).ToList())
            {
                VehicleCompat.Disembark(v, p);
                if (p.Spawned) down.Add(p);
            }
            return down;
        }

        private static void ConvertCrews(Map map)
        {
            foreach (Lord l in map.lordManager.lords.Where(x => x.LordJob is LordJob_HRTurretCrew).ToList())
            {
                Faction f = l.faction;
                bool leave = l.CurLordToil is LordToil_ExitMap || f == null || !FactionAttacking(map, f);
                Replace(l, map, leave ? ExitJob : () => AssaultJob(f));
            }
        }

        private static void ConvertBoarding(Map map)
        {
            foreach (Lord l in map.lordManager.lords.Where(x => x.LordJob is LordJob_HRBoard).ToList())
                Replace(l, map, ExitJob);
        }

        // ------------------------------------------------------------ pawns
        /// <summary>
        /// Tous les pawns, vivants ou morts : ceux que connaît le jeu (cartes, monde, caravanes, transports en vol), les
        /// cadavres posés, et tout ce qui est rangé dans un conteneur (tombes, sarcophages, caisses, navettes, porteurs,
        /// véhicules, inventaires, caravanes...), parcouru récursivement sur chaque carte et sur le monde. Un clic : pas de coût en jeu.
        /// </summary>
        private static List<Pawn> AllPawns()
        {
            var seen = new HashSet<Pawn>();
            var all = new List<Pawn>();
            void Add(Pawn p) { if (p != null && seen.Add(p)) all.Add(p); }
            void AddFrom(List<Thing> things)
            {
                foreach (Thing t in things)
                {
                    if (t is Pawn p) Add(p);
                    else if (t is Corpse c) Add(c.InnerPawn);
                }
            }
            foreach (Pawn p in PawnsFinder.All_AliveOrDead.ToList()) Add(p);
            var held = new List<Thing>();
            foreach (Map map in Find.Maps.ToList())
            {
                AddFrom(map.listerThings.ThingsInGroup(ThingRequestGroup.Corpse).ToList());
                ThingOwnerUtility.GetAllThingsRecursively(map, held);
                AddFrom(held);
            }
            if (Find.World != null)
            {
                ThingOwnerUtility.GetAllThingsRecursively(Find.World, held);
                AddFrom(held);
            }
            return all;
        }

        private static void ClearJobsAndDuties()
        {
            JobDef board = HRDefOf.HR_BoardVehicle;
            DutyDef duty = HRDefOf.HR_BoardConvoy;
            foreach (Pawn p in AllPawns().ToList())
            {
                try
                {
                    bool touched = false;
                    if (p.jobs != null && board != null)
                    {
                        if (p.jobs.jobQueue != null && p.jobs.jobQueue.Any(q => q.job?.def == board))
                        {
                            p.jobs.jobQueue.RemoveAll(p, j => j.def == board);
                            touched = true;
                        }
                        if (p.jobs.curJob?.def == board)
                        {
                            if (p.Spawned) p.jobs.EndCurrentJob(JobCondition.InterruptForced);
                            else p.jobs.StopAll();
                            touched = true;
                        }
                    }
                    if (p.mindState?.duty != null && duty != null && p.mindState.duty.def == duty)
                    {
                        p.mindState.duty = null;
                        touched = true;
                    }
                    if (touched) nPawns++;
                }
                catch (Exception e)
                {
                    nErrors++;
                    Log.Warning($"[Hardened Raids] prepare uninstall, jobs of {p}: {e.Message}");
                }
            }
        }

        private static void RemoveTierMarkers()
        {
            HediffDef def = HRDefOf.HR_RaiderTier;
            if (def == null) return;
            foreach (Pawn p in AllPawns().ToList())
            {
                try
                {
                    List<Hediff> hs = p.health?.hediffSet?.hediffs;
                    if (hs == null) continue;
                    for (int i = hs.Count - 1; i >= 0; i--)
                    {
                        if (hs[i]?.def != def) continue;
                        // marqueur invisible sans effet : retrait direct chez les morts (pas de recalcul d'état de santé)
                        if (p.Dead) hs.RemoveAt(i);
                        else p.health.RemoveHediff(hs[i]);
                        nHediffs++;
                    }
                    p.health?.hediffSet?.DirtyCache();
                }
                catch (Exception e)
                {
                    nErrors++;
                    Log.Warning($"[Hardened Raids] prepare uninstall, tier marker of {p}: {e.Message}");
                }
            }
        }
    }
}
