using HarmonyLib;
using RegenerationUpgrade.Replacers;
using System.Collections.Generic;
using Verse;

namespace RegenerationUpgrade.VEFPatches
{
    public static class GetInjuries_Patch
    {
        public static void ApplyPatch(Harmony harmony)
        {
            var type = AccessTools.TypeByName("VEF.AnimalBehaviours.HediffComp_Regeneration")
                    ?? AccessTools.TypeByName("AnimalBehaviours.HediffComp_Regeneration");

            var method = AccessTools.Method(type, "GetInjuries");

            var postfix = typeof(GetInjuries_Patch).GetMethod(nameof(Postfix));
            // Определяем какой постфикс использовать на основе параметров метода
            var parameters = method.GetParameters();
            if (parameters.Length == 1 && parameters[0].ParameterType == typeof(Pawn))
            {
                postfix = typeof(GetInjuries_Patch).GetMethod(nameof(PostfixOld));
            }

            harmony.Patch(method, postfix: new HarmonyMethod(postfix));


            type = AccessTools.TypeByName("VEF.AnimalBehaviours.CompRegeneration")
                    ?? AccessTools.TypeByName("AnimalBehaviours.CompRegeneration");

            method = AccessTools.Method(type, "GetInjuries");

            postfix = typeof(GetInjuries_Patch).GetMethod(nameof(Postfix));
            harmony.Patch(method, postfix: new HarmonyMethod(postfix));
        }

        public static void Postfix(Pawn pawn, BodyPartDef bodypart, ref List<Hediff_Injury> __result)
        {
            HealLogic_Replacer.SortHediffListVEF(__result);
        }
        public static void PostfixOld(Pawn pawn, ref List<Hediff_Injury> __result)
        {
            HealLogic_Replacer.SortHediffListVEF(__result);
        }
    }
}