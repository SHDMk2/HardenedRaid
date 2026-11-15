using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using System.Text;
using System.Xml;
using HarmonyLib;
using RimWorld;
using RimWorld.QuestGen;
using UnityEngine;
using Verse;

namespace HardenedRaids
{
    /// <summary>
    /// Lot « uncap » : combats hors raid encore plafonnés en amont du plafond de 10 000 (A1, décision D1 « garder le ratio »).
    /// Chaque courbe points → points qui sature à ou avant 10 000 garde sa valeur vanilla jusqu'à 10 000 (DU7), puis est
    /// prolongée par la droite passant par l'origine et l'ancre (max(dernier x, 10 000), dernier y) : continue, et le ratio
    /// au plafond vanilla de la menace est conservé. Exemples : combat lointain (10 000 → 2 500, ¼) : 50 000 → 12 500 ;
    /// points de site (5 000 → 1 000, plat jusqu'à 10 000) : 10 000 → 1 000, 50 000 → 5 000.
    /// Seulement quand UncapPoints est actif ; sinon les points d'origine sont remis (réversible à chaud).
    /// </summary>
    public static class UncapCurves
    {
        /// <summary>Courbes statiques (type, champ). Type absent (version du jeu) : ignoré avec un avertissement.</summary>
        private static readonly (string type, string field)[] StaticCurves =
        {
            ("RimWorld.Planet.SiteTuning", "ThreatPointsToSiteThreatPointsCurve"),          // points de site par défaut (5000 -> 1000)
            ("RimWorld.QuestGen.QuestNode_Root_RelicHunt", "ExteriorThreatPointsOverPoints"),  // 10000 -> 10000
            ("RimWorld.QuestGen.QuestNode_Root_RelicHunt", "InteriorThreatPointsOverPoints"),  // 10000 -> 5000
            ("RimWorld.QuestGen.QuestNode_Root_AncientComplex", "ThreatPointsOverPointsCurve"), // 10000 -> 4125 (butin, piratage, mission)
            ("RimWorld.QuestGen.QuestPart_SleepingMechs", "ThreatPointsToMechPointsCurve"),   // 10000 -> 2000
            ("RimWorld.SitePartWorker_Outpost", "ThreatPointsLootMarketValue"),              // butin d'avant-poste 10000 -> 2000
            ("RimWorld.SitePartWorker_WorkSite", "PointsMarketValue"),                       // butin de chantier 10000 -> 5000
            ("RimWorld.QuestGen.QuestPart_WanderingEntities", "MaxPointsByCurrentThreatPoints"), // 10000 -> 3000
            ("RimWorld.QuestGen.QuestNode_Root_DistressCall", "AmbushPointsCurve"),          // 5000 -> 1000
            ("RimWorld.Planet.SitePartWorker_DistressCall_Fleshbeasts", "FleshbeastsPointsModifierCurve"), // 5000 -> 800
            ("RimWorld.Utility.VoidAwakeningUtility", "DefenderPointsByCombatPoints"),       // 10000 -> 5000
            ("RimWorld.InsectAmbush", "AmbushThreatPointsCurve"),                            // 5000 -> 1500
            ("RimWorld.FleshbeastUtility", "FleshbeastResponsePointsCurve"),                 // 5000 -> 650
            ("RimWorld.CompMechhiveAssembler", "MaxThreatPointsCurve"),                      // 10000 -> 1250
            ("RimWorld.GenStep_InsectLairCave", "CocoonThreatPointsCurve"),                  // 10000 -> 2500
            ("RimWorld.UndercaveMapComponent", "ThreatPointsCurve"),                         // 8000 -> 3000
            // Volontairement absente : CompObelisk_Duplicator.CombatPointsSpawnPointsCurve (5000 -> 200) est le COÛT d'un
            // double, retiré de 0,6 × menace (déjà déplafonnée) : figée, le nombre de doubles croît avec la menace (30 à 10k,
            // 300 à 100k) ; prolongée, il resterait bloqué à 30. Nombre plafonné par Patch_DuplicatorObeliskCap (DU12).
        };

        /// <summary>Scripts de quête contenant des QuestNode_EvaluateSimpleCurve points → points à prolonger.</summary>
        private static readonly string[] QuestScripts = { "Util_AdjustPointsForDistantFight" };

        private static readonly Dictionary<SimpleCurve, List<CurvePoint>> originals = new Dictionary<SimpleCurve, List<CurvePoint>>();
        private static readonly Dictionary<QuestNode_EvaluateSimpleCurve, string> questOriginals = new Dictionary<QuestNode_EvaluateSimpleCurve, string>();
        private static bool collected;
        private const float FarX = 1e9f;
        private const float VanillaCap = 10000f;   // plafond vanilla de la menace : valeur vanilla inchangée jusque-là

        /// <summary>Ajoute (ou retire) le point lointain de chaque courbe selon UncapPoints. Appelé au démarrage et au changement du réglage.</summary>
        public static void Apply()
        {
            Collect();
            bool on = HRSettings.UncapPoints;
            foreach (KeyValuePair<SimpleCurve, List<CurvePoint>> kv in originals)
                kv.Key.SetPoints(on ? ExtendByRatio(kv.Value) : kv.Value);
            foreach (KeyValuePair<QuestNode_EvaluateSimpleCurve, string> kv in questOriginals)
            {
                string s = kv.Value;
                if (on)
                {
                    List<CurvePoint> pts = ParseCurveXml(kv.Value);
                    if (pts != null && pts.Count > 0) s = CurveXml(ExtendByRatio(pts));
                }
                kv.Key.curve = new SlateRef<SimpleCurve>(s);
            }
        }

        /// <summary>
        /// Prolongement « garder le ratio » ancré à x0 = max(dernier x, 10 000) : plat (valeur vanilla) jusqu'à x0, puis
        /// y = dernier y / x0 × x. Ex. (5 000 → 1 000) : point (10 000, 1 000) ajouté, puis 1e9 → 1e8.
        /// </summary>
        public static List<CurvePoint> ExtendByRatio(List<CurvePoint> orig)
        {
            var pts = new List<CurvePoint>(orig);
            if (pts.Count == 0) return pts;
            CurvePoint last = pts[pts.Count - 1];
            if (last.x <= 0f || last.y <= 0f || last.x >= FarX) return pts;
            float x0 = Mathf.Max(last.x, VanillaCap);
            if (x0 > last.x) pts.Add(new CurvePoint(x0, last.y));
            pts.Add(new CurvePoint(FarX, last.y / x0 * FarX));
            return pts;
        }

        private static void Collect()
        {
            if (collected) return;
            collected = true;
            foreach ((string type, string field) in StaticCurves)
            {
                Type t = AccessTools.TypeByName(type);
                FieldInfo fi = t == null ? null : AccessTools.Field(t, field);
                if (fi == null || !fi.IsStatic || fi.GetValue(null) is not SimpleCurve curve)
                {
                    Log.Warning($"[Hardened Raids] curve not found: {type}.{field} (not extended).");
                    continue;
                }
                if (!originals.ContainsKey(curve)) originals[curve] = curve.Points.ToList();
            }
            foreach (string name in QuestScripts)
            {
                QuestScriptDef def = DefDatabase<QuestScriptDef>.GetNamedSilentFail(name);
                if (def?.root == null) { Log.Warning("[Hardened Raids] quest script not found: " + name); continue; }
                var found = new List<QuestNode_EvaluateSimpleCurve>();
                Walk(def.root, found, new HashSet<object>());
                foreach (QuestNode_EvaluateSimpleCurve n in found)
                {
                    string s = ((ISlateRef)n.curve).SlateRef;
                    // seulement une courbe écrite en dur (pas une variable $...), points -> points
                    if (s.NullOrEmpty() || s.Contains("$") || ParseCurveXml(s) == null) continue;
                    if (!questOriginals.ContainsKey(n)) questOriginals[n] = s;
                }
                if (found.Count == 0) Log.Warning("[Hardened Raids] no curve found in quest script " + name);
            }
            HRLog.Msg($"déplafonnement hors raid : {originals.Count} courbes statiques, {questOriginals.Count} courbes de quête.");
        }

        /// <summary>Parcourt l'arbre de nœuds (champs QuestNode et List&lt;QuestNode&gt;), une seule fois au démarrage.</summary>
        private static void Walk(QuestNode node, List<QuestNode_EvaluateSimpleCurve> found, HashSet<object> seen)
        {
            if (node == null || !seen.Add(node)) return;
            if (node is QuestNode_EvaluateSimpleCurve e) found.Add(e);
            for (Type t = node.GetType(); t != null && t != typeof(object); t = t.BaseType)
                foreach (FieldInfo fi in t.GetFields(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly))
                {
                    if (typeof(QuestNode).IsAssignableFrom(fi.FieldType)) Walk(fi.GetValue(node) as QuestNode, found, seen);
                    else if (typeof(IEnumerable<QuestNode>).IsAssignableFrom(fi.FieldType) && fi.GetValue(node) is IEnumerable<QuestNode> list)
                        foreach (QuestNode c in list) Walk(c, found, seen);
                }
        }

        /// <summary>« &lt;points&gt;&lt;li&gt;35, 35&lt;/li&gt;… » (InnerXml du SlateRef) → points ; null si illisible.</summary>
        private static List<CurvePoint> ParseCurveXml(string s)
        {
            try
            {
                var doc = new XmlDocument();
                doc.LoadXml("<root>" + s + "</root>");
                XmlNode points = doc.DocumentElement?.SelectSingleNode("points");
                if (points == null) return null;
                var res = new List<CurvePoint>();
                foreach (XmlNode li in points.ChildNodes)
                {
                    if (li.NodeType != XmlNodeType.Element) continue;
                    string[] parts = li.InnerText.Trim().TrimStart('(').TrimEnd(')').Split(',');
                    if (parts.Length != 2) return null;
                    res.Add(new CurvePoint(float.Parse(parts[0].Trim(), CultureInfo.InvariantCulture), float.Parse(parts[1].Trim(), CultureInfo.InvariantCulture)));
                }
                res.Sort((a, b) => a.x.CompareTo(b.x));
                return res;
            }
            catch
            {
                return null;
            }
        }

        private static string CurveXml(List<CurvePoint> pts)
        {
            var sb = new StringBuilder("<points>");
            foreach (CurvePoint p in pts)
                sb.Append("<li>(").Append(p.x.ToString("R", CultureInfo.InvariantCulture)).Append(',').Append(p.y.ToString("R", CultureInfo.InvariantCulture)).Append(")</li>");
            return sb.Append("</points>").ToString();
        }
    }

    /// <summary>
    /// Infestation de foreuse profonde : vanilla Clamp(points × 0,3-0,6, 200, 1000). Avec UncapPoints, le plafond devient
    /// max(1000, points × 0,1) : même ratio qu'au plafond vanilla (1000 pour 10 000 pts).
    /// </summary>
    [HarmonyPatch(typeof(IncidentWorker_DeepDrillInfestation), "TryExecuteWorker")]
    public static class Patch_DeepDrillInfestationCap
    {
        public static float MaxInsects(IncidentParms parms) =>
            HRSettings.UncapPoints && parms != null ? Mathf.Max(1000f, parms.points * 0.1f) : 1000f;

        public static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions)
        {
            MethodInfo clamp = AccessTools.Method(typeof(Mathf), nameof(Mathf.Clamp), new[] { typeof(float), typeof(float), typeof(float) });
            List<CodeInstruction> list = instructions.ToList();
            bool done = false;
            for (int i = 0; i < list.Count - 1; i++)
            {
                if (list[i].opcode == OpCodes.Ldc_R4 && list[i].operand is float f && f == 1000f
                    && (list[i + 1].opcode == OpCodes.Call) && list[i + 1].operand as MethodInfo == clamp)
                {
                    var load = new CodeInstruction(OpCodes.Ldarg_1).MoveLabelsFrom(list[i]);
                    list[i] = new CodeInstruction(OpCodes.Call, AccessTools.Method(typeof(Patch_DeepDrillInfestationCap), nameof(MaxInsects)));
                    list.Insert(i, load);
                    done = true;
                    break;
                }
            }
            if (!done) Log.Warning("[Hardened Raids] deep drill infestation cap not found: not uncapped.");
            return list;
        }
    }

    /// <summary>
    /// DU12 : obélisque duplicateur (Anomaly). Vanilla : 0,6 × menace de points, chaque double en coûte
    /// CombatPointsSpawnPointsCurve(menace) (200 au-delà de 5000) → menace / 333 doubles (30 à 10k, 3000 à 1M).
    /// À l'activation, les points restants sont bornés à « plafond par vague (industriel) » doubles, et la part apparue
    /// hors de l'explosion est recalculée comme vanilla. Champs vanilla uniquement (pointsRemaining, outsideBlastCount).
    /// </summary>
    [HarmonyPatch(typeof(CompObelisk_Duplicator), nameof(CompObelisk_Duplicator.OnActivityActivated))]
    public static class Patch_DuplicatorObeliskCap
    {
        private static readonly AccessTools.FieldRef<CompObelisk_ExplodingSpawner, float> PointsRemaining =
            AccessTools.FieldRefAccess<CompObelisk_ExplodingSpawner, float>("pointsRemaining");
        private static readonly AccessTools.FieldRef<CompObelisk_ExplodingSpawner, float> TotalCombatPoints =
            AccessTools.FieldRefAccess<CompObelisk_ExplodingSpawner, float>("totalCombatPoints");
        private static readonly AccessTools.FieldRef<CompObelisk_Duplicator, int> OutsideBlastCount =
            AccessTools.FieldRefAccess<CompObelisk_Duplicator, int>("outsideBlastCount");
        private static readonly FieldInfo CostCurve = AccessTools.Field(typeof(CompObelisk_Duplicator), "CombatPointsSpawnPointsCurve");

        public static void Postfix(CompObelisk_Duplicator __instance)
        {
            try
            {
                if (CostCurve?.GetValue(null) is not SimpleCurve curve) return;
                float cost = curve.Evaluate(TotalCombatPoints(__instance));
                int cap = Mathf.Max(1, HRSettings.CapHighTech);
                ref float points = ref PointsRemaining(__instance);
                if (cost <= 0f || points <= cost * cap) return;
                HRLog.Msg($"obélisque duplicateur : {points / cost:F0} doubles prévus, plafonnés à {cap}");
                points = cost * cap;
                // même formule que vanilla (points / 35 × 0,7)
                OutsideBlastCount(__instance) = Mathf.CeilToInt(Mathf.CeilToInt(points / 35f) * 0.7f);
            }
            catch (Exception e)
            {
                Log.Warning("[Hardened Raids] duplicator obelisk cap: " + e.Message);
            }
        }
    }
}
