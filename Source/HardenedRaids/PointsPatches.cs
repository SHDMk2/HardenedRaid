using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using HarmonyLib;
using RimWorld;
using Verse;

namespace HardenedRaids
{
    /// <summary>Phase 1 : plafond de points et courbes de richesse.</summary>
    [HarmonyPatch(typeof(StorytellerUtility), nameof(StorytellerUtility.DefaultThreatPointsNow))]
    public static class PointsPatches
    {
        // courbes richesse -> points, prolongées avec la pente du dernier segment.
        // FixedWealthModeMapWealthFromTimeCurve : jours -> richesse du mode « richesse fixe » (sature à 2,5M après 1800 jours).
        // Absentes (P9) : PointsFactorForColonyMechsCurve / PointsFactorForColonySubhumanCurve (part du combatPower des
        // mécas et sous-humains de la colonie (Pawn.IsSubhuman : mutants, goules…) comptée en menace, 0,2 → 0,4 à 1M). Prolongées par la pente, elles dépasseraient 1 (un
        // méca compterait plus qu'un colon) : laissées vanilla, plates à 0,4 après 1M.
        private static readonly string[] CurveFields =
        {
            "PointsPerWealthCurve", "PointsPerColonistByWealthCurve",
            "FixedWealthModeMapWealthFromTimeCurve",
        };
        private static readonly Dictionary<string, List<CurvePoint>> originals = new Dictionary<string, List<CurvePoint>>();

        public static float MaxPoints() => HRSettings.UncapPoints ? float.MaxValue : 10000f;

        // Remplace la constante 10000 du Mathf.Clamp final par MaxPoints()
        public static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions) =>
            ReplaceCap(instructions, "DefaultThreatPointsNow");

        /// <summary>Remplace la première constante 10000f de la méthode par un appel à MaxPoints().</summary>
        public static IEnumerable<CodeInstruction> ReplaceCap(IEnumerable<CodeInstruction> instructions, string where)
        {
            bool done = false;
            foreach (CodeInstruction ins in instructions)
            {
                if (!done && ins.opcode == OpCodes.Ldc_R4 && ins.operand is float f && f == 10000f)
                {
                    done = true;
                    yield return new CodeInstruction(OpCodes.Call, AccessTools.Method(typeof(PointsPatches), nameof(MaxPoints))).MoveLabelsFrom(ins);
                    continue;
                }
                yield return ins;
            }
            if (!done) Log.Error($"[Hardened Raids] 10000 cap not found in {where}: points uncapping is inactive there.");
        }

        /// <summary>Ajoute (ou retire) un point lointain qui prolonge la pente du dernier segment.</summary>
        public static void ApplyCurves()
        {
            foreach (string name in CurveFields)
            {
                FieldInfo fi = AccessTools.Field(typeof(StorytellerUtility), name);
                if (fi?.GetValue(null) is not SimpleCurve curve)
                {
                    Log.Warning("[Hardened Raids] curve not found: " + name);
                    continue;
                }
                if (!originals.TryGetValue(name, out List<CurvePoint> orig))
                {
                    orig = curve.Points.ToList();
                    originals[name] = orig;
                }
                var pts = new List<CurvePoint>(orig);
                if (HRSettings.ExtendWealthCurves && pts.Count >= 2)
                {
                    CurvePoint a = pts[pts.Count - 2], b = pts[pts.Count - 1];
                    float slope = (b.y - a.y) / (b.x - a.x);
                    const float farX = 1e9f;
                    pts.Add(new CurvePoint(farX, b.y + slope * (farX - b.x)));
                }
                curve.SetPoints(pts);
            }
            // courbes points -> points hors raid (lot uncap), selon UncapPoints
            try { UncapCurves.Apply(); }
            catch (System.Exception e) { Log.Warning("[Hardened Raids] uncapped curves: " + e); }
        }
    }
}
