using HarmonyLib;
using RegenerationUpgrade.Replacers;
using RimWorld;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using Verse;

namespace RegenerationUpgrade.Patches
{
    public static class HealthTickInterval_Patch
    {
        public static void ApplyPatch(Harmony harmony)
        {
            var type = typeof(Pawn_HealthTracker);

            // Попробуй найти метод "HealthTickInterval", иначе fallback на "HealthTick"
            var method = AccessTools.Method(type, "HealthTickInterval")
                      ?? AccessTools.Method(type, "HealthTick");

            var transpiler = typeof(HealthTickInterval_Patch).GetMethod(nameof(Transpiler), BindingFlags.Static | BindingFlags.NonPublic);
            harmony.Patch(method, transpiler: new HarmonyMethod(transpiler));
        }

        static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions)
        {
            var codes = new List<CodeInstruction>(instructions);

            var getHediffsMethod = AccessTools.Method(typeof(HediffSet), "GetHediffs")
                .MakeGenericMethod(typeof(Hediff_Injury));
            FieldInfo tmpHediffField = AccessTools.Field(typeof(Pawn_HealthTracker), "tmpHediffInjuries");

            var sortMethod = AccessTools.Method(typeof(HealLogic_Replacer), nameof(HealLogic_Replacer.SortHediffList));

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

            int getHediffsCallCount = 0;

            for (int i = 0; i < codes.Count; i++)
            {
                // Найти вызов GetHediffs<>() — и вставить сортировку сразу после
                if (codes[i].opcode == OpCodes.Callvirt && codes[i].operand is MethodInfo mi && mi == getHediffsMethod)
                {
                    getHediffsCallCount++;

                    if (getHediffsCallCount == 3)
                    {
                        // Вставка вызова сортировки после GetHediffs
                        codes.InsertRange(i + 1, new[]
                        {
                            new CodeInstruction(OpCodes.Ldarg_0),                         // this
                            new CodeInstruction(OpCodes.Ldfld, tmpHediffField),          // this.tmpHediffInjuries
                            new CodeInstruction(OpCodes.Call, sortMethod)                // SortByDanger(tmpHediffInjuries)
                        });
                    }
                }

                // Заменяем RandomElement<T>()
                if (codes[i].Calls(randomElementMethod))
                {
                    codes[i] = new CodeInstruction(OpCodes.Call, customMethod);
                }
            }

            return codes;
        }
    }
}