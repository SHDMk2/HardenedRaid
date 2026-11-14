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
    ///  - marqueur HR_RaiderTier retiré de tous les pawns (cartes, monde, caravanes, prisonniers, recrutés, morts).
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
        private static void Replace(Lord old, Map map, Func<LordJob> makeJob, Func<Pawn, bool> toExit = null)
        {
            Faction f = old.faction;
            List<Pawn> pawns = old.ownedPawns.ToList();
            map.lordManager.RemoveLord(old);   // Cleanup : lord et duty des pawns remis à zéro, jobs du lord arrêtés
            nLords++;
            pawns.RemoveAll(p => p == null || p.Destroyed || p.Dead || p.GetLord() != null);
            if (f == null || pawns.Count == 0) return;
            List<Pawn> exit = toExit != null ? pawns.Where(toExit).ToList() : new List<Pawn>();
            List<Pawn> rest = pawns.Where(p => !exit.Contains(p)).ToList();
            if (rest.Count > 0) LordMaker.MakeNewLord(f, makeJob(), map).AddPawns(rest);
            if (exit.Count > 0) LordMaker.MakeNewLord(f, ExitJob(), map).AddPawns(exit);
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
                if (!leave)
                {
                    // ceux qui n'ont pas encore débarqué descendent maintenant, dans le lord d'infanterie (vanilla) ;
                    // pas de nouvelle tourelle de camion. Pilote et tireurs restent à bord (équipage VF).
                    job.deployTurrets = false;
                    foreach (Pawn v in l.ownedPawns.ToList())
                        if (v.Spawned && !job.unloaded.Contains(v)) job.Unload(v, map);
                }
                else foreach (Pawn v in l.ownedPawns) if (v.Spawned) UndeployVF(v);
                Faction f = l.faction;
                // raid en cours : les blindés continuent l'assaut (lord VF), les camions vides repartent
                if (leave) Replace(l, map, ExitJob);
                else Replace(l, map, () => ArmoredJobVF(f), v => !HasTurretsVF(v));
            }
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        private static void UndeployVF(Pawn v) => VehicleCompat.Undeploy(v);

        [MethodImpl(MethodImplOptions.NoInlining)]
        private static bool HasTurretsVF(Pawn v) => VehicleCompat.HasTurrets(v);

        [MethodImpl(MethodImplOptions.NoInlining)]
        private static LordJob ArmoredJobVF(Faction f) => VehicleCompat.ArmoredLordJob(f);

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
        private static IEnumerable<Pawn> AllPawns()
        {
            var seen = new HashSet<Pawn>();
            foreach (Pawn p in PawnsFinder.All_AliveOrDead.ToList()) if (p != null && seen.Add(p)) yield return p;
            foreach (Map map in Find.Maps.ToList())
                foreach (Thing t in map.listerThings.ThingsInGroup(ThingRequestGroup.Corpse).ToList())
                    if (t is Corpse c && c.InnerPawn != null && seen.Add(c.InnerPawn)) yield return c.InnerPawn;
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
