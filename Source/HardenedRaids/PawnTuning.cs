using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using HarmonyLib;
using RimWorld;
using RimWorld.Planet;
using UnityEngine;
using Verse;

namespace HardenedRaids
{
    [DefOf]
    public static class HRDefOf
    {
        public static HediffDef HR_RaiderTier;
        public static Verse.AI.DutyDef HR_BoardConvoy;
        public static JobDef HR_BoardVehicle;
        static HRDefOf() => DefOfHelper.EnsureInitializedInCtor(typeof(HRDefOf));
    }

    public static class PawnTuning
    {
        private static readonly MethodInfo installPart = AccessTools.Method(typeof(PawnTechHediffsGenerator), "InstallPart");

        // ------------------------------------------------------------ santé
        public static void CleanHealth(Pawn pawn)
        {
            List<Hediff> list = pawn.health.hediffSet.hediffs.ToList();
            foreach (Hediff h in list)
            {
                bool remove =
                    (h is Hediff_Injury inj && inj.IsPermanent()) ||
                    (h is Hediff_MissingPart mp && mp.Part != null && !pawn.health.hediffSet.PartOrAnyAncestorHasDirectlyAddedParts(mp.Part)) ||
                    h.def.chronic ||
                    h is Hediff_Addiction;
                if (remove) pawn.health.RemoveHediff(h);
            }
        }

        // ------------------------------------------------------------ implants
        public static void InstallImplants(Pawn pawn, Slot s)
        {
            if (installPart == null) return;
            // implants et cyberbrain tirés à la planification ; seuls ceux réellement posés sont facturés (Slot.installed)
            s.installed ??= new List<ThingDef>();
            var parts = new List<ThingDef>(s.implants ?? new List<ThingDef>());
            if (s.brain != null) parts.Add(s.brain);
            foreach (ThingDef d in parts) if (InstallPart(pawn, d)) s.installed.Add(d);
            SkipAdaptation(pawn, s.installed);
        }

        /// <summary>
        /// Pawn du monde rhabillé par vanilla (PawnGenerator.RedressPawn ne passe pas par GenerateInitialHediffs) : on garde sa
        /// santé telle quelle (lésions, séquelles permanentes), on remplace seulement ses membres manquants par une prothèse du
        /// palier quand c'est possible, puis on pose les implants et le cyberbrain du slot. L'âge n'est pas contraint.
        /// </summary>
        public static void FitRedressed(Pawn pawn, Slot s)
        {
            s.installed = new List<ThingDef>();
            s.required = null;
            ReplaceMissingLimbs(pawn, s);
            InstallImplants(pawn, s);
        }

        private static readonly List<Thing> noIngredients = new List<Thing>();
        private static List<KeyValuePair<ThingDef, List<RecipeDef>>> prostheses;

        /// <summary>Pièces de remplacement (techHediffsTags) et leurs opérations « installer une partie artificielle ».</summary>
        private static List<KeyValuePair<ThingDef, List<RecipeDef>>> Prostheses()
        {
            if (prostheses != null) return prostheses;
            var byThing = new Dictionary<ThingDef, List<RecipeDef>>();
            foreach (RecipeDef r in DefDatabase<RecipeDef>.AllDefsListForReading)
            {
                if (r.addsHediff == null || r.workerClass == null || !typeof(Recipe_InstallArtificialBodyPart).IsAssignableFrom(r.workerClass)) continue;
                if (r.ingredients == null) continue;
                foreach (IngredientCount ic in r.ingredients)
                {
                    if (ic.filter?.AllowedThingDefs == null) continue;
                    foreach (ThingDef d in ic.filter.AllowedThingDefs)
                    {
                        if (!d.isTechHediff || d.techHediffsTags.NullOrEmpty() || !r.IsIngredient(d)) continue;
                        if (!byThing.TryGetValue(d, out var l)) byThing[d] = l = new List<RecipeDef>();
                        if (!l.Contains(r)) l.Add(r);
                    }
                }
            }
            prostheses = byThing.ToList();
            return prostheses;
        }

        private static void ReplaceMissingLimbs(Pawn pawn, Slot s)
        {
            HediffSet hs = pawn.health.hediffSet;
            BodyPartRecord brain = hs.GetBrain();
            foreach (Hediff_MissingPart mp in hs.GetMissingPartsCommonAncestors().ToList())
            {
                BodyPartRecord part = mp.Part;
                if (part == null || !hs.PartIsMissing(part) || hs.PartOrAnyAncestorHasDirectlyAddedParts(part)) continue;
                // doigts et orteils : pas un membre, on ne remplace pas un bras entier pour un doigt
                if (part.def.tags != null && (part.def.tags.Contains(BodyPartTagDefOf.ManipulationLimbDigit) || part.def.tags.Contains(BodyPartTagDefOf.MovingLimbDigit))) continue;
                // de la partie manquante vers le haut (main manquante → bras bionique posé à l'épaule), sans toucher au torse ni au cerveau
                BodyPartRecord target = part;
                for (int up = 0; up < 3 && target != null && target.parent != null; up++, target = target.parent)
                {
                    if (brain != null && target != part && IsAncestorOrSelf(target, brain)) break;
                    if (TryReplace(pawn, s, target)) break;
                }
            }
        }

        private static bool IsAncestorOrSelf(BodyPartRecord anc, BodyPartRecord p)
        {
            for (BodyPartRecord x = p; x != null; x = x.parent) if (x == anc) return true;
            return false;
        }

        private static bool TryReplace(Pawn pawn, Slot s, BodyPartRecord target)
        {
            FactionTables t = Loadouts.TablesFor(s.faction.def);
            ThingDef best = null, cheapest = null;
            RecipeDef bestR = null, cheapestR = null;
            foreach (KeyValuePair<ThingDef, List<RecipeDef>> kv in Prostheses())
            {
                ThingDef d = kv.Key;
                if (!d.techHediffsTags.Any(t.implantTags.Contains)) continue;   // jamais hors des tables de la faction (low-tech : rien)
                RecipeDef r = kv.Value.FirstOrDefault(x => pawn.def.AllRecipes.Contains(x) && x.Worker.GetPartsToApplyOn(pawn, x).Contains(target));
                if (r == null) continue;
                float v = d.BaseMarketValue;
                if (v <= s.tier.implantBudget && (best == null || v > best.BaseMarketValue)) { best = d; bestR = r; }
                if (cheapest == null || v < cheapest.BaseMarketValue) { cheapest = d; cheapestR = r; }
            }
            // la meilleure dans le budget d'implant du palier, sinon la moins chère (T1 : pas de budget d'implant)
            ThingDef pick = best ?? cheapest;
            RecipeDef rec = best != null ? bestR : cheapestR;
            if (pick == null) return false;
            try
            {
                rec.Worker.ApplyOnPawn(pawn, target, null, noIngredients, null);
            }
            catch (Exception e)
            {
                HRLog.Msg($"prothèse {pick.defName} impossible sur {pawn} : {e.Message}");
                return false;
            }
            if (!pawn.health.hediffSet.HasDirectlyAddedPartFor(target)) return false;
            s.installed.Add(pick);
            HRLog.Msg($"{pawn.LabelShort} (pawn du monde rhabillé) : {target.Label} manquant(e) → {pick.label}.");
            return true;
        }

        /// <summary>
        /// Pièces imposées par le pawnkind (techHediffsRequired : bioniques de cataphracte, acidificateur…) à la place de
        /// PawnTechHediffsGenerator.GenerateTechHediffsFor : les implants vanilla tirés au hasard ne sont pas posés sur un
        /// raider Hardened (gratuits, et parfois sur des factions qui n'en ont pas dans la dotation) ; les imposés le sont et
        /// sont facturés, sauf l'acidificateur (sans effet sur le prix, comme ApplyHarshDeath).
        /// </summary>
        public static void InstallRequiredTechHediffs(Pawn pawn, Slot s)
        {
            s.required = new List<ThingDef>();   // la boucle de GenerateInitialHediffs peut réinitialiser la santé et repasser ici
            if (pawn.kindDef.techHediffsRequired == null) return;
            foreach (ThingDef d in pawn.kindDef.techHediffsRequired)
                if (InstallPart(pawn, d) && d.defName != "DeathAcidifier") s.required.Add(d);
        }
        /// <summary>
        /// Pièces à phase d'adaptation (cyberbrains GiTS : stade « adapting » qui monte de 0,5/jour) : un raider arrive
        /// avec des implants posés depuis longtemps, on saute directement au dernier stade.
        /// </summary>
        private static void SkipAdaptation(Pawn pawn, List<ThingDef> parts)
        {
            foreach (Hediff h in pawn.health.hediffSet.hediffs)
            {
                if (h.def.spawnThingOnRemoved == null || !parts.Contains(h.def.spawnThingOnRemoved)) continue;
                if (h.def.stages == null || h.def.stages.Count < 2) continue;
                if (h.TryGetComp<HediffComp_SeverityPerDay>() == null) continue;
                float target = h.def.stages[h.def.stages.Count - 1].minSeverity;
                if (h.def.maxSeverity > 0f) target = Math.Min(target, h.def.maxSeverity);
                if (h.Severity < target) h.Severity = target;
            }
        }

        /// <returns>true si la pièce a réellement été posée</returns>
        private static bool InstallPart(Pawn pawn, ThingDef d)
        {
            if (installPart == null) return false;
            int before = CountFrom(pawn, d);
            try
            {
                installPart.Invoke(null, new object[] { pawn, d });
            }
            catch (Exception e)
            {
                HRLog.Msg($"implant {d.defName} impossible sur {pawn} : {e.InnerException?.Message ?? e.Message}");
                return false;
            }
            return CountFrom(pawn, d) > before;
        }

        /// <summary>Hediffs issus de cette pièce (pièce rendue au retrait, ou hediff d'un implant à usage direct).</summary>
        private static int CountFrom(Pawn pawn, ThingDef d)
        {
            HediffDef direct = d.GetCompProperties<CompProperties_UseEffectInstallImplant>()?.hediffDef;
            int n = 0;
            foreach (Hediff h in pawn.health.hediffSet.hediffs)
            {
                if (h.def.spawnThingOnRemoved == d || (direct != null && h.def == direct))
                    n += h is Hediff_Level lv ? lv.level : 1;
            }
            return n;
        }

        // ------------------------------------------------------------ armes de secours
        /// <summary>
        /// Simple Sidearms tire l'arme de secours dans les tags et le budget du pawnkind vanilla (bières, bûches,
        /// massues, lance-roquettes jetables...) : on retire ces armes de l'inventaire et on donne à la place l'arme
        /// de corps à corps de la dotation (qualité selon le grade). Les grenades restent ; un jetable seulement en brèche.
        /// </summary>
        public static void ReplaceSidearms(Pawn pawn, Slot s)
        {
            if (pawn.inventory?.innerContainer == null) return;
            var removed = new List<string>();
            foreach (Thing t in pawn.inventory.innerContainer.ToList())
            {
                ThingDef d = t.def;
                if (!d.IsWeapon || d.IsStuff || Loadouts.IsGrenade(d)) continue;
                if (s.role == Role.Breacher && Loadouts.IsDestructive(d)) continue;
                if (!(d.IsMeleeWeapon || d.IsRangedWeapon)) continue;
                pawn.inventory.innerContainer.Remove(t);
                removed.Add(d.label);
                t.Destroy();
            }
            ThingStuffPair? side = s.loadout.SidearmFor(s.role);
            Thing added = null;
            if (side.HasValue && pawn.equipment?.Primary != null && pawn.equipment.Primary.def != side.Value.thing)
            {
                added = ThingMaker.MakeThing(side.Value.thing, side.Value.stuff);
                FinishGear(added, pawn, s);
                if (!pawn.inventory.innerContainer.TryAdd(added)) { added.Destroy(); added = null; }
            }
            if (removed.Count == 0 && added == null) return;
            // mémoire de Simple Sidearms : reconstruite d'après ce qui est réellement porté
            ThingComp mem = (pawn as ThingWithComps).AllComps.FirstOrDefault(c => c.GetType().Name == "CompSidearmMemory");
            if (mem != null) AccessTools.Method(mem.GetType(), "GenerateRememberedWeaponsFromEquipped")?.Invoke(mem, null);
            if (removed.Count > 0)
                HRLog.Msg($"{pawn.LabelShort} : arme(s) de secours remplacée(s) ({string.Join(", ", removed)} → {added?.LabelCap ?? "rien"}).");
        }

        // ------------------------------------------------------------ biocodage + acidificateur (sans effet sur le prix)
        private static ThingDef deathAcidifier;
        private static bool deathAcidifierLooked;

        public static void ApplyHarshDeath(Pawn pawn, Slot s)
        {
            if (HRSettings.HarshDeathShare <= 0f || s.faction.def.techLevel < TechLevel.Industrial) return;
            if (s.rng.NextDouble() >= HRSettings.HarshDeathShare) return;
            ThingWithComps gun = pawn.equipment?.Primary;
            CompBiocodable code = gun?.TryGetComp<CompBiocodable>();
            if (code != null && !code.Biocoded) code.CodeFor(pawn);
            if (!deathAcidifierLooked)
            {
                deathAcidifier = DefDatabase<ThingDef>.GetNamedSilentFail("DeathAcidifier");
                deathAcidifierLooked = true;
            }
            HediffDef h = DefDatabase<HediffDef>.GetNamedSilentFail("DeathAcidifier");
            if (deathAcidifier != null && (h == null || !pawn.health.hediffSet.HasHediff(h))) InstallPart(pawn, deathAcidifier);
        }

        // ------------------------------------------------------------ skills
        public static void ApplySkills(Pawn pawn, Slot s)
        {
            if (pawn.skills == null || !s.tier.healthy) return;
            bool melee = pawn.equipment?.Primary == null ? s.role == Role.Melee : pawn.equipment.Primary.def.IsMeleeWeapon;
            SkillDef main = melee ? SkillDefOf.Melee : SkillDefOf.Shooting;
            SkillDef other = melee ? SkillDefOf.Shooting : SkillDefOf.Melee;
            Raise(pawn, main, s.rng.Next(s.tier.combatSkill.min, s.tier.combatSkill.max + 1));
            Raise(pawn, other, Mathf.Max(0, s.tier.combatSkill.min - 4));
        }

        private static void Raise(Pawn pawn, SkillDef def, int level)
        {
            SkillRecord r = pawn.skills.GetSkill(def);
            if (r == null || r.TotallyDisabled) return;
            if (r.Level < level) r.Level = Mathf.Min(20, level);
        }

        // ------------------------------------------------------------ marqueur invisible (IA)
        public static void Mark(Pawn pawn, int tierIndex)
        {
            if (HRDefOf.HR_RaiderTier == null || tierIndex <= 0) return;
            Hediff h = HediffMaker.MakeHediff(HRDefOf.HR_RaiderTier, pawn);
            h.Severity = tierIndex;
            pawn.health.AddHediff(h);
        }

        public static int TierOf(Pawn pawn)
        {
            Hediff h = pawn.health?.hediffSet?.GetFirstHediffOfDef(HRDefOf.HR_RaiderTier);
            return h == null ? 0 : Mathf.RoundToInt(h.Severity);
        }

        // ------------------------------------------------------------ équipement
        public static QualityCategory RollQuality(Slot s)
        {
            return QualityModel.Sample(s.rank, s.tierIndex, Tiers.LadderFor(s.faction.def).Length, s.rng);
        }

        public static void FinishGear(Thing t, Pawn pawn, Slot s)
        {
            PawnGenerator.PostProcessGeneratedGear(t, pawn);
            t.TryGetComp<CompQuality>()?.SetQuality(RollQuality(s), ArtGenerationContext.Outsider);
            if (t.def.useHitPoints) t.HitPoints = t.MaxHitPoints;
        }

        /// <returns>false si rien n'a pu être porté (on laisse alors vanilla faire)</returns>
        public static bool WearLoadout(Pawn pawn, Slot s, PawnGenerationRequest request)
        {
            List<ThingStuffPair> set = s.loadout.ApparelFor(s.role);
            if (set.Count == 0) return false;
            pawn.apparel.DestroyAll();
            int worn = 0;
            foreach (ThingStuffPair p in set)
            {
                if (!p.thing.apparel.PawnCanWear(pawn) || !ApparelUtility.HasPartsToWear(pawn, p.thing)) continue;
                if (!pawn.apparel.CanWearWithoutDroppingAnything(p.thing)) continue;
                var ap = (Apparel)ThingMaker.MakeThing(p.thing, p.stuff);
                FinishGear(ap, pawn, s);
                pawn.apparel.Wear(ap, dropReplacedApparel: false);
                worn++;
            }
            if (worn == 0) return false;
            AddWarmthIfNeeded(pawn, s, request);
            AddVacuumResistanceIfNeeded(pawn, s, request);
            return true;
        }

        /// <summary>Case du monde de la génération, comme vanilla (PawnApparelGenerator) : celle de la requête, sinon une base du joueur.</summary>
        private static PlanetTile GenerationTile(PawnGenerationRequest request)
        {
            PlanetTile tile = request.Tile;
            if (!tile.Valid && Find.AnyPlayerHomeMap != null) tile = Find.AnyPlayerHomeMap.Tile;
            return tile;
        }

        /// <summary>
        /// Température à affronter : carte du raid si elle existe (température réelle), sinon carte déjà générée sur la case,
        /// sinon la case du monde (embuscade, site, quête…) : la plus froide entre la température actuelle et la moyenne
        /// de ce douzième et du suivant (vanilla regarde aussi deux douzièmes).
        /// </summary>
        private static float? OutdoorTempFor(PawnGenerationRequest request)
        {
            if (RaidContext.Parms?.target is Map raidMap) return raidMap.mapTemperature.OutdoorTemp;
            PlanetTile tile = GenerationTile(request);
            if (!tile.Valid) return null;
            Map map = Current.Game?.FindMap(tile);
            if (map != null) return map.mapTemperature.OutdoorTemp;
            Twelfth tw = GenLocalDate.Twelfth(tile);
            float t = Mathf.Min(GenTemperature.AverageTemperatureAtTileForTwelfth(tile, tw),
                                GenTemperature.AverageTemperatureAtTileForTwelfth(tile, tw.NextTwelfth()));
            if (Find.World?.tileTemperatures != null) t = Mathf.Min(t, Find.World.tileTemperatures.GetOutdoorTemp(tile));
            return t;
        }

        private static void AddWarmthIfNeeded(Pawn pawn, Slot s, PawnGenerationRequest request)
        {
            float? outdoor = OutdoorTempFor(request);
            if (!outdoor.HasValue) return;
            float comfy = pawn.GetStatValue(StatDefOf.ComfyTemperatureMin);
            float missing = comfy - outdoor.Value;
            if (missing <= 5f) return;
            ThingStuffPair? warm = Loadouts.BestWarmLayer(Loadouts.TablesFor(s.faction.def), pawn, missing);
            if (!warm.HasValue) return;
            var ap = (Apparel)ThingMaker.MakeThing(warm.Value.thing, warm.Value.stuff);
            FinishGear(ap, pawn, s);
            pawn.apparel.Wear(ap, dropReplacedApparel: false);
        }

        /// <summary>
        /// Odyssey : sur une case dans le vide, vanilla ajoute gratuitement une tenue étanche (PawnApparelGenerator.NeedVacuumResistance
        /// + AddFreeVacuumResistanceAsNeeded). Même règle ici : objets « canBeGeneratedToSatisfyVacuumResistance », qui remplacent
        /// la pièce incompatible seulement s'ils apportent nettement plus de résistance (+0,15, comme vanilla), jusqu'à 0,9.
        /// </summary>
        private static void AddVacuumResistanceIfNeeded(Pawn pawn, Slot s, PawnGenerationRequest request)
        {
            if (!ModsConfig.OdysseyActive || StatDefOf.VacuumResistance == null) return;
            PlanetTile tile = GenerationTile(request);
            if (!tile.Valid || tile.Tile?.PrimaryBiome == null || !tile.Tile.PrimaryBiome.inVacuum || !pawn.ConcernedByVacuum) return;
            for (int i = 0; i < 10; i++)
            {
                if (pawn.GetStatValue(StatDefOf.VacuumResistance) >= 0.9f) break;
                ThingStuffPair? pick = Loadouts.VacuumCandidates(pawn).Where(p => VacuumGain(pawn, p) > 0.15f)
                    .Cast<ThingStuffPair?>().RandomElementByWeightWithDefault(p => p.Value.Commonality / (p.Value.Price * p.Value.Price + 1f), 0f);
                if (!pick.HasValue) break;
                foreach (Apparel worn in pawn.apparel.WornApparel.ToList())
                {
                    if (ApparelUtility.CanWearTogether(worn.def, pick.Value.thing, pawn.RaceProps.body)) continue;
                    pawn.apparel.Remove(worn);
                    worn.Destroy();
                }
                var ap = (Apparel)ThingMaker.MakeThing(pick.Value.thing, pick.Value.stuff);
                FinishGear(ap, pawn, s);
                pawn.apparel.Wear(ap, dropReplacedApparel: false);
            }
        }

        private static float VacuumGain(Pawn pawn, ThingStuffPair p)
        {
            if (pawn.apparel.WornApparel.Any(w => w.def == p.thing)) return 0f;
            float lost = 0f;
            foreach (Apparel w in pawn.apparel.WornApparel)
                if (!ApparelUtility.CanWearTogether(w.def, p.thing, pawn.RaceProps.body))
                    lost += w.def.equippedStatOffsets.GetStatOffsetFromList(StatDefOf.VacuumResistance);
            return p.VacuumResistance - lost;
        }

        /// <summary>Retire ce qui empêche le raider d'utiliser son arme (bouclier + arme à deux mains, ceinture bouclier + fusil...).
        /// Utilise le test du jeu (Apparel.AllowVerbCast), donc couvre aussi les boucliers des mods.</summary>
        public static void DropBlockingApparel(Pawn pawn)
        {
            ThingWithComps prim = pawn.equipment?.Primary;
            if (prim == null || pawn.apparel == null) return;
            Verb verb = prim.GetComp<CompEquippable>()?.PrimaryVerb;
            foreach (Apparel ap in pawn.apparel.WornApparel.ToList())
            {
                bool blocks = Loadouts.BlocksWeapon(ap.def, prim.def);
                if (!blocks && verb != null)
                {
                    try { blocks = !ap.AllowVerbCast(verb); } catch { }
                }
                if (!blocks) continue;
                HRLog.Msg($"{pawn.LabelShort} : {ap.def.label} retiré (incompatible avec {prim.def.label}).");
                pawn.apparel.Remove(ap);
                ap.Destroy();
            }
        }

        public static bool EquipWeapon(Pawn pawn, Slot s)
        {
            Role role = s.role;
            bool cannotShoot = pawn.WorkTagIsDisabled(WorkTags.Shooting) || (pawn.story?.traits?.HasTrait(TraitDefOf.Brawler) ?? false);
            if (cannotShoot) role = Role.Melee;
            ThingStuffPair? w = s.loadout.WeaponFor(role);
            if (!w.HasValue) return false;
            ThingDef def = w.Value.thing;
            if (cannotShoot && def.IsRangedWeapon) return false;
            if (pawn.genes?.Xenotype?.forbiddenWeaponClasses != null && def.weaponClasses != null &&
                pawn.genes.Xenotype.forbiddenWeaponClasses.Any(def.weaponClasses.Contains)) return false;

            pawn.equipment.DestroyAllEquipment();
            var thing = (ThingWithComps)ThingMaker.MakeThing(def, w.Value.stuff);
            FinishGear(thing, pawn, s);
            if (pawn.Ideo != null && thing.TryGetComp<CompEquippable>() != null) thing.StyleDef = pawn.Ideo.GetStyleFor(thing.def);
            pawn.equipment.AddEquipment(thing);
            return true;
        }
    }
}
