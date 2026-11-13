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
            // implants et cyberbrain tirés à la planification (ils entrent dans le prix du raider)
            var parts = new List<ThingDef>(s.implants ?? new List<ThingDef>());
            if (s.brain != null) parts.Add(s.brain);
            foreach (ThingDef d in parts) InstallPart(pawn, d);
            SkipAdaptation(pawn, parts);
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

        private static void InstallPart(Pawn pawn, ThingDef d)
        {
            if (installPart == null) return;
            try
            {
                installPart.Invoke(null, new object[] { pawn, d });
            }
            catch (Exception e)
            {
                HRLog.Msg($"implant {d.defName} impossible sur {pawn} : {e.InnerException?.Message ?? e.Message}");
            }
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
        public static bool WearLoadout(Pawn pawn, Slot s)
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
            if (worn > 0) AddWarmthIfNeeded(pawn, s);
            return worn > 0;
        }

        private static void AddWarmthIfNeeded(Pawn pawn, Slot s)
        {
            if (RaidContext.Parms?.target is not Map map) return;
            float outdoor = map.mapTemperature.OutdoorTemp;
            float comfy = pawn.GetStatValue(StatDefOf.ComfyTemperatureMin);
            float missing = comfy - outdoor;
            if (missing <= 5f) return;
            ThingStuffPair? warm = Loadouts.BestWarmLayer(Loadouts.TablesFor(s.faction.def), pawn, missing);
            if (!warm.HasValue) return;
            var ap = (Apparel)ThingMaker.MakeThing(warm.Value.thing, warm.Value.stuff);
            FinishGear(ap, pawn, s);
            pawn.apparel.Wear(ap, dropReplacedApparel: false);
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
