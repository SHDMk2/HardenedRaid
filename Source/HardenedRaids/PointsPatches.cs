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
        private static readonly string[] CurveFields = { "PointsPerWealthCurve", "PointsPerColonistByWealthCurve" };
        private static readonly Dictionary<string, List<CurvePoint>> originals = new Dictionary<string, List<CurvePoint>>();

        public static float MaxPoints() => HRSettings.UncapPoints ? float.MaxValue : 10000f;

        // Remplace la constante 10000 du Mathf.Clamp final par MaxPoints()
        public static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions)
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
            if (!done) Log.Error("[Hardened Raids] plafond de 10000 introuvable dans DefaultThreatPointsNow : le déplafonnement est inactif.");
        }

        /// <summary>Ajoute (ou retire) un point lointain qui prolonge la pente du dernier segment.</summary>
        public static void ApplyCurves()
        {
            foreach (string name in CurveFields)
            {
                FieldInfo fi = AccessTools.Field(typeof(StorytellerUtility), name);
                if (fi?.GetValue(null) is not SimpleCurve curve)
                {
                    Log.Warning("[Hardened Raids] courbe introuvable : " + name);
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
        }
    }
}
