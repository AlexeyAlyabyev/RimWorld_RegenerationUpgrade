using HarmonyLib;
using RimWorld;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using Verse;

namespace RegenerationUpgrade.Patches
{
    [HarmonyPatch(typeof(Pawn_HealthTracker), "HealthTickInterval")]
    public static class HealthTickInterval_Patch
    {
        static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions)
        {
            var codes = new List<CodeInstruction>(instructions);

            // Получаем generic-метод RandomElement<T>(this IList<T>)
            var randomElementMethod = typeof(GenCollection)
                .GetMethods(BindingFlags.Public | BindingFlags.Static)
                .First(m => m.Name == "RandomElement"
                         && m.IsGenericMethod
                         && m.GetParameters().Length == 1
                         && m.GetParameters()[0].ParameterType.IsGenericType
                         && m.GetParameters()[0].ParameterType.GetGenericTypeDefinition() == typeof(IEnumerable<>))
                .MakeGenericMethod(typeof(Hediff_Injury));
            // Наш кастомный метод
            var customMethod = AccessTools.Method(typeof(HealthTickInterval_Patch), nameof(GetMostDangerousInjury));

            for (int i = 0; i < codes.Count; i++)
            {
                if (codes[i].Calls(randomElementMethod))
                {
                    codes[i] = new CodeInstruction(OpCodes.Call, customMethod);
                }
            }

            return codes;
        }

        public static Hediff_Injury GetMostDangerousInjury(IList<Hediff_Injury> injuries)
        {
            //Log.Message("TEST");
            if (injuries == null || injuries.Count == 0)
                return null;

            Hediff_Injury mostDangerous = injuries[0];
            float injuryPriority        = 0;

            for (int i = 1; i < injuries.Count; i++)
            {
                if (GetInjuryPriority(injuries[i]) > injuryPriority)
                {
                    mostDangerous  = injuries[i];
                    injuryPriority = GetInjuryPriority(injuries[i]);
                }
            }
            return mostDangerous;
        }

        public static float GetInjuryPriority(Hediff_Injury injury)
        {
            float priority = 0f;

            if (injury.Part.def.tags.Contains(BodyPartTagDefOf.ConsciousnessSource))
                priority += 100f; // Голова
            else if (injury.Part.def.tags.Contains(BodyPartTagDefOf.BreathingSource))
                priority += 80f; // Дыхательные органы
            else if (injury.Part.def.tags.Contains(BodyPartTagDefOf.BloodPumpingSource))
                priority += 90f; // Сердце
            else if (injury.Part.groups.Contains(BodyPartGroupDefOf.Torso))
                priority += 70f; // Торс

            // Учитываем кровотечение
            if (injury.Bleeding)
                priority += 50f + injury.Severity * 10f;

            // Учитываем тяжесть повреждения
            priority += injury.Severity;
            Log.Message($"Injury {injury.Label} on {injury.Part?.Label ?? "null"} gets priority {priority}");

            return priority;
        }
    }
}