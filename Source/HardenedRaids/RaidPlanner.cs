using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using RimWorld;
using UnityEngine;
using Verse;

namespace HardenedRaids
{
    public class GroupPlan
    {
        public string name;
        public int tierIndex;
        public TierDef tier;
        public int count;
        public Loadout loadout;
        public float price;    // prix moyen prévu d'un raider du groupe (points)
    }

    /// <summary>Modèle C : une troupe au palier bas + un noyau au palier haut, proportion continue.</summary>
    public class RaidPlan
    {
        public FactionDef faction;
        public float points;
        public float tierX;
        public int total;      // raiders sur l'ensemble des vagues
        public int waves;
        public int thisWave;   // raiders de cette vague
        public GroupPlan core; // peut être null
        public GroupPlan troop;
        public int ncos, officers; // grades de cette vague
        public ArmorGate gate;
        public float frac;     // part du noyau (palier haut)
        public float avgPrice; // prix moyen prévu d'un raider (noyau et troupe au prorata)

        /// <summary>Colonie de faction (D2) : une seule vague, jusqu'à ce multiple du plafond par vague ; le surplus est perdu.</summary>
        public const int SettlementCapFactor = 2;

        /// <param name="allowWaves">false hors raid (navette écrasée, embuscade, site…) : une seule vague, plafonnée.</param>
        /// <param name="capFactor">hors raid seulement : multiple du plafond par vague (<see cref="SettlementCapFactor"/> pour une colonie).</param>
        /// <param name="rng">sert uniquement aux dotations : même graine = même dotation (vagues d'un même raid).</param>
        /// <param name="gateGiven">true : <paramref name="gate"/> (null = sans plafond) est le plafond d'armure du raid entier.</param>
        public static RaidPlan Make(FactionDef f, float points, float? xOverride, int? countOverride, bool needBreacher, System.Random rng, bool allowWaves = true,
                                    ArmorGate gate = null, bool gateGiven = false, int capFactor = 1)
        {
            float x = xOverride ?? Tiers.TierX(f, points);
            if (x < 1f) return null;
            TierDef[] ladder = Tiers.LadderFor(f);
            int lo = Mathf.Clamp(Mathf.FloorToInt(x), 1, ladder.Length - 1);
            int hi = Mathf.Min(lo + 1, ladder.Length - 1);
            float frac = lo == hi ? 0f : Mathf.Clamp01(x - lo);

            // dotations d'abord : le prix d'un raider dépend de ce qu'il porte
            if (!gateGiven) gate = ArmorGate.For(points);
            Loadout loLoadout = Loadouts.BuildLoadout(f, ladder[lo], needBreacher, rng, gate);
            Loadout hiLoadout = lo == hi ? loLoadout : Loadouts.BuildLoadout(f, ladder[hi], needBreacher, rng, gate);
            float loPrice = Pricing.ExpectedPrice(f, ladder[lo], lo, loLoadout);
            float hiPrice = lo == hi ? loPrice : Pricing.ExpectedPrice(f, ladder[hi], hi, hiLoadout);
            float avg = Mathf.Max(1f, (1f - frac) * loPrice + frac * hiPrice);

            var p = new RaidPlan { faction = f, points = points, tierX = x, gate = gate, frac = frac, avgPrice = avg };
            if (countOverride.HasValue)
            {
                p.total = p.thisWave = Mathf.Max(1, countOverride.Value);
                p.waves = 1;
            }
            else
            {
                p.total = Mathf.Max(1, Mathf.FloorToInt(points / avg));
                int cap = Tiers.WaveCapFor(f);
                if (!allowWaves)
                {
                    p.total = Mathf.Min(p.total, cap * Mathf.Max(1, capFactor));
                    p.waves = 1;
                }
                // un reste de moins de 10 % est absorbé par la vague, pas de mini-vague inutile
                else p.waves = p.total > cap * 1.1f ? Mathf.CeilToInt(p.total / (float)cap) : 1;
                p.thisWave = Mathf.CeilToInt(p.total / (float)p.waves);
            }

            int k = Mathf.RoundToInt(p.thisWave * frac);
            if (k > 0)
                p.core = new GroupPlan { name = "noyau", tierIndex = hi, tier = ladder[hi], count = k, loadout = hiLoadout, price = hiPrice };
            p.troop = new GroupPlan { name = "troupe", tierIndex = lo, tier = ladder[lo], count = p.thisWave - k, loadout = loLoadout, price = loPrice };
            p.ncos = Pricing.NcoCount(p.thisWave);
            p.officers = Pricing.OfficerCount(p.thisWave);
            return p;
        }

        /// <summary>Change l'effectif de cette vague (véhicules répartis) : noyau, troupe et grades recalculés.</summary>
        public void SetWaveCount(int n)
        {
            n = Mathf.Max(1, n);
            thisWave = n;
            int k = core != null || frac > 0f ? Mathf.RoundToInt(n * frac) : 0;
            if (core != null) core.count = k; else k = 0;
            troop.count = n - k;
            ncos = Pricing.NcoCount(n);
            officers = Pricing.OfficerCount(n);
        }

        public string Describe()
        {
            var sb = new StringBuilder();
            sb.Append($"{faction.label} @ {points:F0} pts : {total} raiders (palier {tierX:F2})");
            if (waves > 1) sb.Append($" en {waves} vagues de ~{thisWave}");
            sb.Append($", par vague : {ncos} sous-officier(s), {officers} officier(s)");
            if (gate != null) sb.Append($", armure max {gate.cap * ArmorGate.MarineSharp:F1} (marine {ArmorGate.MarineSharp:F1}), tech colonie {gate.colonyTech}");
            sb.AppendLine();
            foreach (GroupPlan g in new[] { core, troop })
            {
                if (g == null || g.count <= 0) continue;
                sb.AppendLine($"   [{g.name}] {g.count}× {g.tier.label} à ~{g.price:F0} pts — skill {g.tier.combatSkill.min}-{g.tier.combatSkill.max}, implants {g.tier.implantCount.min}-{g.tier.implantCount.max}");
                sb.AppendLine($"      {g.loadout.Describe()}");
            }
            return sb.ToString().TrimEnd();
        }
    }

    /// <summary>Ce qu'on est en train de générer pour un raider précis.</summary>
    public class Slot
    {
        public PawnKindDef kind;
        public XenotypeDef xenotype;
        public Faction faction;
        public int tierIndex;
        public TierDef tier;
        public Loadout loadout;
        public Role role;
        public Rank rank;
        public List<ThingDef> implants;
        public ThingDef brain;
        public System.Random rng;
        public int depth;                  // profondeur de PawnGenerator.GeneratePawn à laquelle CE raider est généré
        public Pawn freshPawn;             // dernier pawn neuf passé par GenerateInitialHediffs (sinon : pawn du monde rhabillé)
        public List<ThingDef> installed;   // pièces réellement posées par nous (dotation, membres remplacés)
        public List<ThingDef> required;    // pièces imposées par le pawnkind (techHediffsRequired) réellement posées, facturées
        public bool requiredBreacher;      // exigé par la stratégie (brèche / sape) : arme destructrice
        public bool sapperRaid;            // raid de sape : le sapeur doit savoir creuser (validateur vanilla)
    }

    public static class GenContext
    {
        public static Slot Current;
        /// <summary>Profondeur d'imbrication de PawnGenerator.GeneratePawn (les proches générés pendant un raider sont plus profonds).</summary>
        public static int Depth;
        /// <summary>> 0 : génération de groupes non combattants (cadavres…) en cours, pas de génération Hardened.</summary>
        public static int NonCombat;

        public static Slot For(Pawn p)
        {
            Slot s = Current;
            if (s == null || p == null || Depth != s.depth || p.kindDef != s.kind || p.Faction != s.faction) return null;
            return s;
        }
    }

    public static class RaidGenerator
    {
        public static bool Applies(PawnGroupMakerParms parms)
        {
            // raids, mais aussi tout groupe hostile hors raid : quêtes (navette écrasée…), embuscades,
            // rançons de caravane, défenseurs de sites et de colonies, pourparlers ratés
            if (!HRSettings.EnableTiers) return false;
            if (GenContext.NonCombat > 0) return false;   // cadavres d'une colonie abandonnée, d'un appel de détresse…
            Faction f = parms.faction;
            if (f == null || f.IsPlayer || !f.def.humanlikeFaction) return false;
            // D1 : colonie neutre / alliée attaquée, carte générée avant le passage à l'hostilité → traitée comme hostile
            if (!f.HostileTo(Faction.OfPlayer) && !SettlementDefense.TreatAsHostile(parms)) return false;
            if (parms.groupKind != PawnGroupKindDefOf.Combat && parms.groupKind != PawnGroupKindDefOf.Settlement) return false;
            return true;
        }

        /// <summary>
        /// Multiple du plafond par vague d'un groupe hors raid : défenseurs d'une colonie de faction (groupe Settlement sur la
        /// case d'une colonie, pas un avant-poste ni un site) → <see cref="RaidPlan.SettlementCapFactor"/> (D2), sinon 1.
        /// </summary>
        internal static int SingleWaveCapFactor(PawnGroupMakerParms parms)
        {
            if (parms?.groupKind != PawnGroupKindDefOf.Settlement || !parms.tile.Valid) return 1;
            return Find.WorldObjects?.SettlementAt(parms.tile) != null ? RaidPlan.SettlementCapFactor : 1;
        }

        private static bool StrategyNeedsBreachers(RaidStrategyDef s, out Func<PawnKindDef, bool> isRequired)
        {
            isRequired = null;
            if (s?.Worker is RaidStrategyWorker_ImmediateAttackBreaching) { isRequired = k => k.isGoodBreacher; return true; }
            if (s?.Worker is RaidStrategyWorker_ImmediateAttackSappers) { isRequired = k => k.canBeSapper; return true; }
            return false;
        }

        public static bool TryGenerate(PawnGroupMakerParms parms, PawnGroupMaker groupMaker, List<Pawn> outPawns)
        {
            // groupe principal du raid en cours (réclamé par Patch_GeneratePawns) ; tout autre groupe = hors raid, une vague
            RaidFrame frame = RaidContext.Current;
            bool inRaid = frame != null && frame.IsMainGroup(parms);
            WaveEntry wave = inRaid ? frame.Wave : null;
            bool needBreacher = StrategyNeedsBreachers(parms.raidStrategy, out Func<PawnKindDef, bool> isRequired);
            // graine du groupe si fournie (sites, colonies) : la génération réelle concorde avec l'aperçu
            var rng = new System.Random(parms.seed ?? Rand.Int);
            float tierX = wave?.tierX ?? Tiers.TierX(parms.faction.def, parms.points);
            if (tierX < 1f) return false;
            if (inRaid) frame.TierX = tierX;

            // ---- même dotation sur toutes les vagues (A5) : graine des dotations et plafond d'armure du raid entier
            bool sameLoadout = wave != null && wave.sameLoadout;
            int loadoutSeed = sameLoadout ? wave.loadoutSeed : rng.Next();
            bool loadoutBreacher = sameLoadout ? wave.needBreacher : needBreacher;
            ArmorGate gate = null;
            bool gateGiven = wave != null ? wave.TryGetGate(out gate) : true;
            if (wave == null) gate = ArmorGate.For(parms.points);
            System.Random LoadoutRng() => new System.Random(loadoutSeed);

            // ---- véhicules : planifiés UNE fois pour tout le raid (budget sur ses points totaux), puis répartis sur
            //      les vagues ; chaque vague emporte sa part et son infanterie en déduit le coût (D4). Une vague 2+
            //      n'achète jamais rien : elle reçoit la part prévue par la vague 1 (rien pour une vague d'une save v1.0).
            float raidPoints = parms.points;
            float infantryPoints = raidPoints;
            List<ThingDef> vehicles = null;
            List<List<ThingDef>> waveVehicles = null;
            float[] waveVehicleCost = null;
            List<ThingDef> armored = null;
            bool vehiclesOk = inRaid && HRSettings.EnableVehicles && VehicleGate.Active && !frame.DebugTest && VehicleGate.ArrivalOk(frame.Parms?.raidArrivalMode)
                              && parms.faction.def.techLevel >= TechLevel.Industrial;
            if (vehiclesOk && wave == null)
            {
                try
                {
                    // premier passage sans transports (que des légers) : sert à estimer les places et le reste pour l'infanterie
                    armored = VehicleCompat.Plan(parms.faction, raidPoints, tierX, 0, rng, out float vCost);
                    infantryPoints = Mathf.Max(raidPoints * 0.5f, raidPoints - vCost);
                }
                catch (Exception e)
                {
                    Log.Warning("[Hardened Raids] vehicle selection: " + e.Message);
                    armored = null;
                }
            }
            else if (vehiclesOk)
            {
                vehicles = wave.ResolveVehicles();
            }

            RaidPlan plan = RaidPlan.Make(parms.faction.def, infantryPoints, tierX, wave?.count, loadoutBreacher, LoadoutRng(), inRaid, gate, gateGiven,
                                          inRaid ? 1 : SingleWaveCapFactor(parms));
            if (plan == null) return false;

            // ---- vague 2+ arrivée sans ses véhicules (autre mode d'arrivée, véhicules / VF coupés, defs retirées) : leur
            //      coût, déduit de son infanterie à la planification, lui revient (au plus le plafond par vague, +10 %)
            if (wave != null && wave.vehicleCost > 0f && wave.vehicles != null && wave.vehicles.Count > 0)
            {
                int planned = wave.vehicles.Count;
                int coming = vehiclesOk ? Mathf.Min(planned, vehicles?.Count ?? 0) : 0;
                if (coming < planned)
                {
                    float refund = wave.vehicleCost * (planned - coming) / planned;
                    int max = Mathf.Max(plan.thisWave, Mathf.FloorToInt(Tiers.WaveCapFor(parms.faction.def) * 1.1f));
                    int n = Mathf.Min(max, plan.thisWave + Mathf.FloorToInt(refund / Mathf.Max(1f, plan.avgPrice)));
                    if (n > plan.thisWave)
                    {
                        HRLog.Msg($"vague {wave.index}/{wave.totalWaves} : {planned - coming} véhicule(s) prévu(s) absent(s), {refund:F0} pts rendus à l'infanterie ({plan.thisWave} → {n}).");
                        plan.SetWaveCount(n);
                    }
                }
            }

            // ---- transports (payants) pour l'infanterie qui ne monte pas dans les blindés ; on refait le plan avec le reste
            if (armored != null)
            {
                vehicles = armored;
                try
                {
                    int w0 = plan.waves;
                    List<ThingDef> transports = VehicleCompat.PlanTransports(parms.faction, raidPoints, tierX,
                        plan.total - VehicleCompat.SeatsIn(armored), rng, out float tCost, HRSettings.MaxTransports * w0);
                    // second passage : tanks / appui-feu selon le nombre de transports (1 tank pour 4)
                    float vCost0 = armored.Sum(VehicleCompat.Cost);
                    List<ThingDef> armored2 = VehicleCompat.Plan(parms.faction, raidPoints, tierX, transports.Count, rng, out float vCost, HRSettings.MaxVehicles * w0);
                    if (armored2.Count > 0) armored = armored2; else vCost = vCost0;
                    if (transports.Count > 0 || vCost != vCost0)
                    {
                        infantryPoints = Mathf.Max(raidPoints * 0.5f, raidPoints - vCost - tCost);
                        plan = RaidPlan.Make(parms.faction.def, infantryPoints, tierX, null, loadoutBreacher, LoadoutRng(), true, gate, gateGiven) ?? plan;
                    }
                    waveVehicles = VehicleCompat.Distribute(armored, transports, plan.waves, out int dropped, out waveVehicleCost);
                    if (dropped > 0)
                    {
                        // le plan refait a moins de vagues que le premier : ce qui dépasse les plafonds par vague n'est pas
                        // acheté et son coût revient à l'infanterie. Distribute écarte les blindés les moins chers puis les
                        // derniers transports ; plus de points = autant de vagues ou plus, donc tout le reste rentre.
                        int w1 = plan.waves;
                        armored = armored.OrderByDescending(VehicleCompat.Cost).Take(HRSettings.MaxVehicles * w1).ToList();
                        transports = transports.Take(HRSettings.MaxTransports * w1).ToList();
                        float kept = armored.Sum(VehicleCompat.Cost) + transports.Sum(VehicleCompat.TransportCost);
                        infantryPoints = Mathf.Max(raidPoints * 0.5f, raidPoints - kept);
                        plan = RaidPlan.Make(parms.faction.def, infantryPoints, tierX, null, loadoutBreacher, LoadoutRng(), true, gate, gateGiven) ?? plan;
                        waveVehicles = VehicleCompat.Distribute(armored, transports, plan.waves, out int dropped2, out waveVehicleCost);
                        HRLog.Msg($"véhicules : {dropped} véhicule(s) au-delà des plafonds par vague non achetés, coût rendu à l'infanterie ({infantryPoints:F0} pts)"
                                  + (dropped2 > 0 ? $", {dropped2} abandonné(s)." : "."));
                    }
                    vehicles = waveVehicles[0];
                    if (plan.waves > 1) plan.SetWaveCount(WaveCounts(plan, waveVehicleCost, raidPoints)[0]);
                }
                catch (Exception e)
                {
                    Log.Warning("[Hardened Raids] transport selection: " + e.Message);
                    waveVehicles = null;
                    vehicles = armored.Take(HRSettings.MaxVehicles).ToList();
                }
            }

            // ---- choix des pawnkinds et répartition noyau / troupe (commun avec l'aperçu GeneratePawnKindsExample)
            List<Slot> slots = BuildSlots(parms, groupMaker, plan, plan.thisWave, rng);
            if (slots.Count == 0) return false;
            MarkRequiredBreachers(slots, parms, isRequired);
            AssignRoles(slots.Where(s => s.loadout == plan.core?.loadout).ToList());
            AssignRoles(slots.Where(s => s.loadout == plan.troop.loadout).ToList());

            // ---- grades (chef d'abord, puis noyau) : 1 officier / 100, 1 sous-officier / 10 ; implants tirés ici pour le prix
            int nOff = Pricing.OfficerCount(slots.Count), nNco = Pricing.NcoCount(slots.Count);
            float spent = 0f;
            for (int i = 0; i < slots.Count; i++)
            {
                Slot s = slots[i];
                s.rank = i < nOff ? Rank.Officer : i < nOff + nNco ? Rank.Nco : Rank.Soldier;
                s.implants = Loadouts.PickImplants(parms.faction.def, s.tier, rng);
                s.brain = Tiers.IsLowTech(parms.faction.def) ? null : Loadouts.PickCyberbrain(s.tier.cyberbrainRank);
            }

            // ---- génération
            bool allowFood = parms.raidStrategy == null || parms.raidStrategy.pawnsCanBringFood || (parms.faction != null && !parms.faction.HostileTo(Faction.OfPlayer));
            Predicate<Pawn> validator = parms.raidStrategy != null ? (Pawn p) => parms.raidStrategy.Worker.CanUsePawn(parms.points, p, outPawns) : null;
            bool downedDone = false;
            // les pawns exigés par la stratégie d'abord : le validateur de sape veut un sapeur en premier
            foreach (Slot s in slots.OrderByDescending(x => x.requiredBreacher).ToList())
            {
                Pawn pawn = GenerateOne(s, parms, allowFood, validator);
                if (pawn == null) continue;
                spent += Pricing.SlotPrice(s, pawn);   // ce qui a réellement été posé
                if (parms.forceOneDowned && !downedDone)
                {
                    pawn.health.forceDowned = true;
                    if (pawn.guest != null) pawn.guest.Recruitable = true;
                    pawn.mindState.canFleeIndividual = false;
                    downedDone = true;
                }
                outPawns.Add(pawn);
            }

            if (inRaid && outPawns.Count > 0)
            {
                if (vehicles != null && vehicles.Count > 0) frame.PendingVehicles = vehicles;
                // vagues suivantes : programmées par le postfix de TryGenerateRaidInfo si le raid aboutit (B12)
                if (plan.waves > 1 && wave == null && !frame.DebugTest)
                {
                    WavePlan wp = WaveScheduler.FromRaidPlan(plan, waveVehicleCost != null ? WaveCounts(plan, waveVehicleCost, raidPoints) : null, waveVehicles, waveVehicleCost);
                    wp.loadoutSeed = loadoutSeed;
                    wp.needBreacher = loadoutBreacher;
                    wp.gate = gate;
                    wp.hasGate = gateGiven;
                    frame.Planned = wp;
                }
            }
            HRLog.Msg((inRaid ? "" : $"hors raid ({parms.groupKind?.defName}) — ") + (wave != null ? $"vague {wave.index}/{wave.totalWaves} — " : "") + plan.Describe() +
                      $"\n   stratégie : {parms.raidStrategy?.defName ?? "?"}, générés : {outPawns.Count}, coût réel ≈ {spent:F0} / {infantryPoints:F0} pts" +
                      (vehicles != null && vehicles.Count > 0 ? $", véhicules : {string.Join(", ", vehicles.Select(v => v.label))}" : ""));
            return outPawns.Count > 0;
        }

        /// <summary>
        /// Effectif de chaque vague quand les véhicules sont répartis : l'infanterie totale du plan, partagée au prorata
        /// de (part des points de la vague − coût de ses véhicules) ; au plus le plafond par vague (+10 %).
        /// </summary>
        private static int[] WaveCounts(RaidPlan plan, float[] vehicleCost, float raidPoints)
        {
            int w = Mathf.Max(1, plan.waves);
            var counts = new int[w];
            if (w == 1 || vehicleCost == null || vehicleCost.Length != w) { counts[0] = plan.thisWave; return counts; }
            float per = raidPoints / w;
            var weight = new float[w];
            float sum = 0f;
            for (int i = 0; i < w; i++) { weight[i] = Mathf.Max(per * 0.25f, per - vehicleCost[i]); sum += weight[i]; }
            int max = Mathf.Max(plan.thisWave, Mathf.FloorToInt(Tiers.WaveCapFor(plan.faction) * 1.1f));
            int given = 0;
            var rem = new List<(int i, float r)>();
            for (int i = 0; i < w; i++)
            {
                float exact = plan.total * weight[i] / sum;
                counts[i] = Mathf.Clamp(Mathf.FloorToInt(exact), 1, max);
                given += counts[i];
                rem.Add((i, exact - Mathf.Floor(exact)));
            }
            foreach (var (i, _) in rem.OrderByDescending(x => x.r))
            {
                if (given >= plan.total) break;
                if (counts[i] < max) { counts[i]++; given++; }
            }
            // reste des vagues bloquées au plafond : aux autres, tour à tour (w × max ≥ total, tout est placé)
            for (bool moved = true; given < plan.total && moved;)
            {
                moved = false;
                for (int i = 0; i < w && given < plan.total; i++)
                    if (counts[i] < max) { counts[i]++; given++; moved = true; }
            }
            return counts;
        }

        /// <summary>
        /// Choix des pawnkinds (règles vanilla : chef unique, pawns requis par la stratégie, xénotypes) et répartition noyau / troupe.
        /// Avec parms.seed, le tirage Verse est fait sous cette graine, comme ChoosePawnGenOptionsByPoints.
        /// </summary>
        private static List<Slot> BuildSlots(PawnGroupMakerParms parms, PawnGroupMaker groupMaker, RaidPlan plan, int count, System.Random rng)
        {
            var slots = new List<Slot>();
            var chosen = new List<PawnGenOptionWithXenotype>();
            List<PawnGenOptionWithXenotype> order;
            if (parms.seed.HasValue) Rand.PushState(parms.seed.Value);
            try
            {
                bool leaderChosen = false;
                for (int i = 0; i < count; i++)
                {
                    List<PawnGenOptionWithXenotype> opts = PawnGroupMakerUtility.GetOptions(parms, parms.faction.def, groupMaker.options, parms.points, 1e9f, 1e9f, chosen, leaderChosen)
                        .Where(o => o.Option.kind.isFighter && o.Option.kind.RaceProps.Humanlike).ToList();
                    if (!opts.TryRandomElementByWeight(o => o.SelectionWeight, out PawnGenOptionWithXenotype pick)) break;
                    chosen.Add(pick);
                    if (pick.Option.kind.factionLeader) leaderChosen = true;
                }
                // le chef va dans le noyau (ou la troupe s'il n'y a pas de noyau)
                order = chosen.InRandomOrder().ToList().OrderByDescending(o => o.Option.kind.factionLeader).ToList();
            }
            finally
            {
                if (parms.seed.HasValue) Rand.PopState();
            }
            int coreCount = plan.core?.count ?? 0;
            TierDef[] ladder = Tiers.LadderFor(parms.faction.def);
            for (int i = 0; i < order.Count; i++)
            {
                GroupPlan g = i < coreCount ? plan.core : plan.troop;
                PawnKindDef kind = order[i].Option.kind;
                int ti = kind.factionLeader ? Mathf.Min(g.tierIndex + 1, ladder.Length - 1) : g.tierIndex;
                slots.Add(new Slot
                {
                    kind = kind, xenotype = order[i].Xenotype, faction = parms.faction,
                    tierIndex = ti, tier = ladder[ti], loadout = g.loadout, role = Role.Main, rank = Rank.Soldier, rng = rng,
                });
            }
            return slots;
        }

        /// <summary>
        /// Aperçu (PawnGroupKindWorker_Normal.GeneratePawnKindsExample : effectif d'un avant-poste, pawnkinds annoncés d'un raid
        /// de quête…) : même planification que la génération réelle, sans générer de pawn. null = laisser vanilla.
        /// Aperçu d'un raid (stratégie connue) : toutes les vagues ; véhicules non connus à ce stade.
        /// </summary>
        public static List<PawnKindDef> ExampleKinds(PawnGroupMakerParms parms, PawnGroupMaker groupMaker)
        {
            float tierX = Tiers.TierX(parms.faction.def, parms.points);
            if (tierX < 1f) return null;
            var rng = new System.Random(parms.seed ?? Rand.Int);
            bool needBreacher = StrategyNeedsBreachers(parms.raidStrategy, out _);
            bool raid = parms.raidStrategy != null && parms.groupKind == PawnGroupKindDefOf.Combat;
            // même consommation du générateur que TryGenerate (raid d'origine) : graine des dotations tirée d'abord,
            // dotations sur leur propre générateur, plafond d'armure sur les points du groupe
            int loadoutSeed = rng.Next();
            RaidPlan plan = RaidPlan.Make(parms.faction.def, parms.points, tierX, null, needBreacher, new System.Random(loadoutSeed), allowWaves: raid,
                                          gate: ArmorGate.For(parms.points), gateGiven: true, capFactor: raid ? 1 : SingleWaveCapFactor(parms));
            if (plan == null) return null;
            List<Slot> slots = BuildSlots(parms, groupMaker, plan, raid ? plan.total : plan.thisWave, rng);
            if (slots.Count == 0) return null;
            return slots.Select(x => x.kind).ToList();
        }

        private static readonly System.Reflection.MethodInfo minRequired =
            HarmonyLib.AccessTools.Method(typeof(RaidStrategyWorker_WithRequiredPawnKinds), "MinRequiredPawnsForPoints");

        /// <summary>Nombre de pawns exigés par la stratégie, comme vanilla (sape : 1 ; brèche : 1 à 4 selon les points).</summary>
        private static int RequiredCount(PawnGroupMakerParms parms)
        {
            if (parms.raidStrategy?.Worker is not RaidStrategyWorker_WithRequiredPawnKinds w) return 0;
            try { return minRequired != null ? (int)minRequired.Invoke(w, new object[] { parms.points, parms.faction }) : 1; }
            catch { return 1; }
        }

        /// <summary>
        /// Brèche / sape : seul le nombre de pawns exigé par vanilla (MinRequiredPawnsForPoints) reçoit d'office l'arme destructrice ;
        /// la part de brèche normale du palier s'y ajoute ensuite (AssignRoles). Le chef n'est pris qu'en dernier recours.
        /// </summary>
        private static void MarkRequiredBreachers(List<Slot> slots, PawnGroupMakerParms parms, Func<PawnKindDef, bool> isRequired)
        {
            if (isRequired == null) return;
            bool sappers = parms.raidStrategy?.Worker is RaidStrategyWorker_ImmediateAttackSappers;
            foreach (Slot s in slots) s.sapperRaid = sappers;
            int n = RequiredCount(parms);
            foreach (Slot s in slots.Where(s => isRequired(s.kind)).OrderBy(s => s.kind.factionLeader).Take(Mathf.Max(0, n)))
            {
                s.role = Role.Breacher;
                s.requiredBreacher = true;
            }
        }

        private static void AssignRoles(List<Slot> group)
        {
            if (group.Count == 0) return;
            TierDef t = group[0].tier;
            void Fill(Role r, float share)
            {
                if (share <= 0f) return;
                int n = Mathf.RoundToInt(group.Count * share);
                if (n == 0 && group.Count >= 6) n = 1;
                n -= group.Count(s => s.role == r && !s.requiredBreacher);   // les exigés par la stratégie sont en plus
                foreach (Slot s in group.Where(s => s.role == Role.Main && !s.kind.factionLeader).Take(Mathf.Max(0, n))) s.role = r;
            }
            Fill(Role.Breacher, t.breacherShare);
            Fill(Role.Heavy, t.heavyShare);
            Fill(Role.Sniper, t.sniperShare);
            Fill(Role.Melee, t.meleeShare);
        }

        private static Pawn GenerateOne(Slot s, PawnGroupMakerParms parms, bool allowFood, Predicate<Pawn> validator)
        {
            FloatRange? age = null;
            if (s.tier.healthy && parms.raidAgeRestriction == null && s.kind.race?.race != null && s.kind.race.race.lifeExpectancy is > 50f and < 120f)
            {
                float lo = Mathf.Max(s.tier.ageRange.min, s.kind.minGenerationAge);
                float hi = Mathf.Min(s.tier.ageRange.max, s.kind.maxGenerationAge);
                if (hi > lo) age = new FloatRange(lo, hi);
            }
            var request = new PawnGenerationRequest(s.kind, parms.faction, PawnGenerationContext.NonPlayer, tile: parms.tile,
                allowDowned: parms.faction.deactivated, canGeneratePawnRelations: true, mustBeCapableOfViolence: true,
                allowPregnant: !s.tier.healthy, allowFood: allowFood, allowAddictions: !s.tier.healthy, inhabitant: parms.inhabitants,
                validatorPostGear: validator, fixedIdeo: parms.ideo, forcedXenotype: s.xenotype, biologicalAgeRange: age);
            if (parms.raidAgeRestriction != null && parms.raidAgeRestriction.Worker.ShouldApplyToKind(s.kind))
            {
                request.BiologicalAgeRange = parms.raidAgeRestriction.ageRange;
                request.AllowedDevelopmentalStages = parms.raidAgeRestriction.developmentStage;
            }
            if (s.kind.pawnGroupDevelopmentStage.HasValue) request.AllowedDevelopmentalStages = s.kind.pawnGroupDevelopmentStage.Value;
            if (!Find.Storyteller.difficulty.ChildRaidersAllowed) request.AllowedDevelopmentalStages = DevelopmentalStage.Adult;

            Pawn pawn;
            s.freshPawn = null; s.installed = null; s.required = null;
            s.depth = GenContext.Depth + 1;   // GeneratePawn incrémente la profondeur : les proches générés dedans sont à +2
            GenContext.Current = s;
            try
            {
                pawn = PawnGenerator.GeneratePawn(request);
            }
            finally
            {
                GenContext.Current = null;
            }
            if (pawn == null) return null;
            try
            {
                // pawn du monde réutilisé par vanilla (RedressPawn) : implants du slot, membres manquants remplacés, santé gardée
                if (s.tier.healthy && pawn != s.freshPawn) PawnTuning.FitRedressed(pawn, s);
                PawnTuning.ApplySkills(pawn, s);
                PawnTuning.Mark(pawn, s.tierIndex);
                PawnTuning.ApplyHarshDeath(pawn, s);
                if (s.tier.healthy) PawnTuning.ReplaceSidearms(pawn, s);
            }
            catch (Exception e)
            {
                Log.Warning("[Hardened Raids] post-processing of " + pawn + " : " + e.Message);
            }
            return pawn;
        }
    }
}
