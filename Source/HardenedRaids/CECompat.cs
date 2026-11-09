using System;
using System.Reflection;
using HarmonyLib;
using RimWorld;
using Verse;

namespace HardenedRaids
{
    /// <summary>
    /// Combat Extended (optionnel, par réflexion) : CE génère les munitions de l'arme principale dans son postfix
    /// de TryGenerateWeaponFor, à partir du LoadoutPropertiesExtension du pawnkind. Si le pawnkind était prévu
    /// pour le corps à corps (0 chargeur) et qu'on lui a donné un fusil, on ajoute des chargeurs de réserve.
    /// </summary>
    public static class CECompat
    {
        private static bool init;
        private static Type extType;
        private static FieldInfo magField;
        private static MethodInfo generate;

        public static bool Active => Init() && extType != null;

        private static bool Init()
        {
            if (init) return true;
            init = true;
            extType = AccessTools.TypeByName("CombatExtended.LoadoutPropertiesExtension");
            if (extType == null) return true;
            magField = AccessTools.Field(extType, "primaryMagazineCount");
            generate = AccessTools.Method(extType, "GenerateLoadoutFor", new[] { typeof(Pawn), typeof(float) });
            if (magField == null || generate == null) extType = null;
            return true;
        }

        public static void EnsureAmmo(Pawn pawn)
        {
            if (!Active) return;
            ThingWithComps gun = pawn.equipment?.Primary;
            if (gun == null || !gun.def.IsRangedWeapon) return;
            try
            {
                object existing = null;
                if (pawn.kindDef.modExtensions != null)
                    foreach (DefModExtension e in pawn.kindDef.modExtensions)
                        if (extType.IsInstanceOfType(e)) { existing = e; break; }
                if (existing != null && ((FloatRange)magField.GetValue(existing)).max >= 2f) return; // CE s'en est déjà occupé

                object ext = Activator.CreateInstance(extType);
                magField.SetValue(ext, new FloatRange(3f, 5f));
                generate.Invoke(ext, new object[] { pawn, 0f });
            }
            catch (Exception e)
            {
                HRLog.Msg("munitions CE pour " + pawn + " : " + (e.InnerException?.Message ?? e.Message));
            }
        }
    }
}
