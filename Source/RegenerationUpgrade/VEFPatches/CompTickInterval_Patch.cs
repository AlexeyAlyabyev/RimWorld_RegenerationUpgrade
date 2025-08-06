using HarmonyLib;
using RegenerationUpgrade.Replacers;
using RimWorld;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using Verse;

namespace RegenerationUpgrade.VEFPatches
{
    //[HarmonyPatch(typeof(CompRegeneration), "CompTickInterval")]
    public static class CompTickInterval_Patch
    {
        public static void ApplyPatch(Harmony harmony)
        {
            var type = AccessTools.TypeByName("VEF.AnimalBehaviours.CompRegeneration")
                    ?? AccessTools.TypeByName("AnimalBehaviours.CompRegeneration");

            var method = AccessTools.Method(type, "CompTickInterval")
                        ?? AccessTools.Method(type, "CompTick");
            Log.Message($"{method.Name}");

            var transpiler = typeof(CompTickInterval_Patch).GetMethod(nameof(Transpiler), BindingFlags.Static | BindingFlags.NonPublic);
            harmony.Patch(method, transpiler: new HarmonyMethod(transpiler));
        }
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
            var customMethod = AccessTools.Method(typeof(HealLogic_Replacer), nameof(HealLogic_Replacer.GetMostDangerousInjury));

            for (int i = 0; i < codes.Count; i++)
            {
                // Заменяем RandomElement<T>()
                if (codes[i].Calls(randomElementMethod))
                {
                    codes[i] = new CodeInstruction(OpCodes.Call, customMethod);
                    Log.Message($"Произогшла замена метода в CompTick");
                }
            }

            return codes;
        }
    }
}