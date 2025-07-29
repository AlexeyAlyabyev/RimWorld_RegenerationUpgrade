using HarmonyLib;
using RimWorld;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using System.Security.Cryptography;
using VEF.Things;
using Verse;

namespace RegenerationUpgrade.Patches
{
    [HarmonyPatch(typeof(Pawn_HealthTracker), "HealthTickInterval")]
    public static class HealthTickInterval_Patch
    {
        // 🔽 КЭШ ДЛЯ GetTagsUsedByCapacityWithWeights
        public static Dictionary<PawnCapacityDef, Dictionary<BodyPartTagDef, float>> capacityTagWeightsCache = new Dictionary<PawnCapacityDef, Dictionary<BodyPartTagDef, float>>();

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
            if (injuries == null || injuries.Count == 0)
                return null;

            Pawn pawn = injuries[0].pawn;
            capacityTagWeightsCache = new Dictionary<PawnCapacityDef, Dictionary<BodyPartTagDef, float>>();

            // Если у пешки есть кровотечение, в первую очередь лечим его
            if (pawn.health.CanBleed && pawn.health.hediffSet.BleedRateTotal >= 0.1f)
            {
                Hediff_Injury mostBleedingHediff = FindMostBleedingHediff(injuries);
                if (mostBleedingHediff != null)
                    return mostBleedingHediff;
            }

            // Если у пешки есть заражения/болезни, пытаемся вылечить травмы влияющие на фильтрацию крови, чтобы успеть выработать иммунитет до смерти
            if (!WillSurviveFromDisease(pawn))
            {
                Hediff_Injury bloodFiltrationInjury = MostImpactfulInjuryOnCapacity(pawn, injuries, PawnCapacityDefOf.BloodFiltration); // Фильтрация крови
                if (bloodFiltrationInjury != null)
                {
                    //Log.Message($"PAWN {pawn.LabelShort} ");
                    //Log.Message($"bloodFiltrationInjury {bloodFiltrationInjury.Label} on {bloodFiltrationInjury.Part?.Label ?? "null"}");
                    return bloodFiltrationInjury;
                }
            }

            // Восстанавливаем части тела, которые могут привести к смерти при дальнейших повреждениях до 10%
            Hediff_Injury closestToDeathInjury = GetClosestToDeathPartOfTheBody(pawn, injuries, 0.1f);
            if (closestToDeathInjury != null)
                return closestToDeathInjury;

            // Первоочередно после кровотечения и фильтрации крови лечим Кровообращение, Дыхание или Сознание до минимально безопасных значений в 10%
            Hediff_Injury lifeThreatingCapacityInjury = GetMostDangerousLifeThreatingCapacityInjury(pawn, injuries, 0.1f);
            if (lifeThreatingCapacityInjury != null)
                return lifeThreatingCapacityInjury;

            // Восстанавливаем части тела, которые могут привести к смерти при дальнейших повреждениях до 20%
            closestToDeathInjury = GetClosestToDeathPartOfTheBody(pawn, injuries, 0.2f);
            if (closestToDeathInjury != null)
                return closestToDeathInjury;

            // Первоочередно после кровотечения и фильтрации крови лечим Кровообращение, Дыхание или Сознание до минимально безопасных значений в 20%
            lifeThreatingCapacityInjury = GetMostDangerousLifeThreatingCapacityInjury(pawn, injuries, 0.2f);
            if (lifeThreatingCapacityInjury != null)
                return lifeThreatingCapacityInjury;

            // Восстанавливаем части тела, которые могут привести к смерти при дальнейших повреждениях до 30%
            closestToDeathInjury = GetClosestToDeathPartOfTheBody(pawn, injuries, 0.3f);
            if (closestToDeathInjury != null)
                return closestToDeathInjury;

            // Первоочередно после кровотечения и фильтрации крови лечим Кровообращение, Дыхание или Сознание до минимально безопасных значений в 30%
            lifeThreatingCapacityInjury = GetMostDangerousLifeThreatingCapacityInjury(pawn, injuries, 0.3f);
            if (lifeThreatingCapacityInjury != null)
                return lifeThreatingCapacityInjury;




            // Восстанавливаем способность ходить (до минимального значениядо 16%)
            Hediff_Injury injuryThatPreventsWalking = GetWorstCapacityInjuryToHeal(pawn, injuries, PawnCapacityDefOf.Moving, 0.16f);
            if (injuryThatPreventsWalking != null)
                Log.Message($"!!!!! Самая опасная травма для движения '{injuryThatPreventsWalking.Label}' на '{injuryThatPreventsWalking.Part.Label}'");
            if (injuryThatPreventsWalking != null)
                return injuryThatPreventsWalking;

            // Восстанавливаем способность взаимодействия с предметами (до минимального значениядо 1%)
            Hediff_Injury injuryThatPreventsManipulation = GetWorstCapacityInjuryToHeal(pawn, injuries, PawnCapacityDefOf.Manipulation, 0.01f);
            if (injuryThatPreventsManipulation != null)
                Log.Message($"!!!!! Самая опасная травма для работы руками '{injuryThatPreventsManipulation.Label}' на '{injuryThatPreventsManipulation.Part.Label}'");
            if (injuryThatPreventsManipulation != null)
                return injuryThatPreventsManipulation;





            // Восстанавливаем части тела, которые могут привести к смерти при дальнейших повреждениях до 40%
            closestToDeathInjury = GetClosestToDeathPartOfTheBody(pawn, injuries, 0.4f);
            if (closestToDeathInjury != null)
                return closestToDeathInjury;

            // Первоочередно после кровотечения и фильтрации крови лечим Кровообращение, Дыхание или Сознание до минимально безопасных значений в 40%
            lifeThreatingCapacityInjury = GetMostDangerousLifeThreatingCapacityInjury(pawn, injuries, 0.4f);
            if (lifeThreatingCapacityInjury != null)
                return lifeThreatingCapacityInjury;

            // Добавить проверку боли


            // Лечим травмы жизненной важных параметров до максимума
            lifeThreatingCapacityInjury = GetMostDangerousLifeThreatingCapacityInjury(pawn, injuries);
            if (lifeThreatingCapacityInjury != null)
                return lifeThreatingCapacityInjury;

            // Лечим травмы важных частей тела до максимума
            closestToDeathInjury = GetClosestToDeathPartOfTheBody(pawn, injuries);
            if (closestToDeathInjury != null)
                return closestToDeathInjury;


            MostImpactfulInjuryOnCapacity(pawn, injuries, PawnCapacityDefOf.Manipulation); // Работа (До 25%?)

            MostImpactfulInjuryOnCapacity(pawn, injuries, PawnCapacityDefOf.Sight); // Зрение
            MostImpactfulInjuryOnCapacity(pawn, injuries, PawnCapacityDefOf.Hearing); // Слух
            MostImpactfulInjuryOnCapacity(pawn, injuries, PawnCapacityDefOf.Talking); // Общение (не важно)


            return injuries.RandomElement();
        }

        // Не забыть учесть что у бессмертных при впадании в смертосон должен восстанавливаться первым потерянный жизненно важный орган (ДОРАБОТКА ГЕНА РЕГЕНЕРАЦИИ КОНЕЧНОСТЕЙ)


        // ---------------------- БЛОК С КРОВОТЕЧЕНИЕМ ---------------------------

        // Найти самую кровоточащую травму
        private static Hediff_Injury FindMostBleedingHediff(IList<Hediff_Injury> injuries)
        {
            float num = 0f;
            Hediff_Injury hediff = null;

            foreach (var injury in injuries)
            {
                float bleedRate = injury.BleedRate / injury.Severity;
                if (bleedRate > 0f && (bleedRate > num || hediff == null))
                {
                    num = bleedRate;
                    hediff = injury;
                }
            }
            //Log.Message($"PAWN {pawn.LabelShort} ");
            //Log.Message($"Injury BloodLoss -  {pawn.health.hediffSet.BleedRateTotal}");
            //Log.Message($"mostBleeding Injury {hediff.Label} on {hediff.Part?.Label ?? "null"} get max priority");

            return hediff;
        }


        // ---------------------- БЛОК С ИММУНИТЕТОМ ---------------------------

        // Проверяем, сможет ли пешка выработать иммунитет до того как умрет
        public static bool WillSurviveFromDisease(Pawn pawn)
        {
            if (pawn.Dead || pawn.health?.hediffSet == null)
                return true;

            HediffWithComps disease = FindMostSevereHediff(pawn);
            if (disease == null)
                return true;

            var immComp = disease.TryGetComp<HediffComp_Immunizable>();
            if (immComp == null)
                return true;

            // Выработка иммунитета в день
            float immunityPerDay = pawn.health.immunity
                .GetImmunityRecord(disease.def)?
                .ImmunityChangePerTick(pawn, true, disease) * GenDate.TicksPerDay ?? 0f;

            // Прирост болезни в день
            float severityPerDay = AccessTools.Method(typeof(HediffComp_Immunizable), "SeverityChangePerDay")
                .Invoke(immComp, null) as float? ?? 0f;

            // Доп. эффект от лечения (если есть)
            float tendEffect = 0f;
            var tendComp = disease.TryGetComp<HediffComp_TendDuration>();
            if (tendComp?.IsTended == true)
                tendEffect = tendComp.TProps.severityPerDayTended * tendComp.tendQuality;

            // Сравнение: если иммунитет растёт быстрее, чем болезнь — пешка выживет
            //Log.Message($"PAWN {pawn.LabelShort} имеет Immunity Gain Speed: {pawn.GetStatValue(StatDefOf.ImmunityGainSpeed)}");
            //Log.Message($"PAWN {pawn.LabelShort} WillSurviveFromDisease {immunityPerDay > (severityPerDay + tendEffect)}");
            return immunityPerDay > (severityPerDay + tendEffect);
        }


        // Ищем у пешки самую опасную болезнь, излечимую иммунитетом
        private static HediffWithComps FindMostSevereHediff(Pawn pawn)
        {
            IEnumerable<HediffWithComps> hediffs =
                pawn.health.hediffSet.hediffs
                    .Where(h => h.Visible && h is HediffWithComps && !h.FullyImmune())
                    .Cast<HediffWithComps>();

            HediffWithComps mostSevere = null;
            float worstDelta = float.MinValue;

            foreach (var h in hediffs)
            {
                var immComp = h.TryGetComp<HediffComp_Immunizable>();
                if (immComp == null) continue;

                float delta = immComp.Immunity - h.Severity;
                if (delta > worstDelta)
                {
                    worstDelta = delta;
                    mostSevere = h;
                }
            }

            return mostSevere;
        }



        // ---------------------- БЛОК ЖИЗНЕННО ВАЖНЫХ ПАРАМЕТРОВ ПЕШЕК ---------------------------

        // Получить травму, которая сильнее всего сафектила жизненно важные параметры пешки. Чтобы вылечить ее до уровня levelToCure
        public static Hediff_Injury GetMostDangerousLifeThreatingCapacityInjury(Pawn pawn, IList<Hediff_Injury> injuries, float levelToCure = 1f)
        {
            float lowestCapacityValue = levelToCure;
            Hediff_Injury mostDangerousInjury = null;

            // Получаем список всех возможных параметров пешек
            List<PawnCapacityDef> allDefsListForReading = DefDatabase<PawnCapacityDef>.AllDefsListForReading;
            PawnCapacitiesHandler capacities = new PawnCapacitiesHandler(pawn);

            for (int i = 0; i < allDefsListForReading.Count; i++)
            {
                PawnCapacityDef pawnCapacityDef = allDefsListForReading[i];
                // Если для текущего вида пешки параметр смертельно важен, делаем для него расчет
                if (pawn.RaceProps.IsFlesh ? pawnCapacityDef.lethalFlesh : pawnCapacityDef.lethalMechanoids)
                {
                    float capacityValue = GetCapacityValue(pawn, pawnCapacityDef);
                    Hediff_Injury capacityInjury = MostImpactfulInjuryOnCapacity(pawn, injuries, pawnCapacityDef);

                    if (capacityInjury != null && lowestCapacityValue > capacityValue)
                    {
                        mostDangerousInjury = capacityInjury;
                        lowestCapacityValue = capacityValue;
                    }
                }
            }

            return mostDangerousInjury;
        }

        // Получить самую сиьную травму, которая влияет на параметр (pawnCapacity), чтобы поднять уровень параметра до значения levelToCure
        public static Hediff_Injury GetWorstCapacityInjuryToHeal(Pawn pawn, IList<Hediff_Injury> injuries, PawnCapacityDef pawnCapacity, float levelToCure = 1f)
        {
            float capacityValue = GetCapacityValue(pawn, pawnCapacity);

            if (capacityValue >= levelToCure)
                return null;

            return MostImpactfulInjuryOnCapacity(pawn, injuries, pawnCapacity);
        }

        // Получить текущий уровень параметра пешки
        private static float GetCapacityValue(Pawn pawn, PawnCapacityDef capacity)
        {
            return PawnCapacityUtility.CalculateCapacityLevel(pawn.health.hediffSet, capacity);
        }

        // Получить травму которая больше всего влияет на параметр пешки
        public static Hediff_Injury MostImpactfulInjuryOnCapacity(Pawn pawn, IList<Hediff_Injury> injuries, PawnCapacityDef capacity)
        {
            // Получаем текущее значение показателя (с поверждениями)
            float oldCapacityVal = PawnCapacityUtility.CalculateCapacityLevel(pawn.health.hediffSet, capacity);

            Hediff_Injury worstInjury = null;
            float maxReduction = 0f;

            foreach (var injury in injuries)
            {
                // Копируем текущие хеддифы
                var simulatedHediffs = new List<Hediff>(pawn.health.hediffSet.hediffs);
                simulatedHediffs.Remove(injury); // Симулируем "удаление"

                // Создаём временный HediffSet (внутренне он не делает побочных эффектов)
                var simulatedHediffSet = new HediffSet(pawn);
                simulatedHediffSet.hediffs = simulatedHediffs;

                // Считаем, как бы изменилось значение BloodFiltration без этой травмы
                float newCapacityVal = PawnCapacityUtility.CalculateCapacityLevel(simulatedHediffSet, capacity);
                float reduction = newCapacityVal - oldCapacityVal;

                if (reduction > maxReduction)
                {
                    maxReduction = reduction;
                    worstInjury = injury;
                }
            }

            return worstInjury;
        }



        // ---------------------- БЛОК С ЖИЗНЕННО ВАЖНЫМИ ЧАСТЯМИ ТЕЛА ---------------------------

        // Получить самую тяжелую травму, которая находится на части тела, от которой зависит жизнь пешки
        public static Hediff_Injury GetClosestToDeathPartOfTheBody(Pawn pawn, IList<Hediff_Injury> injuries, float levelToCure = 1f)
        {
            Log.Message($"=================== PAWN {pawn.LabelShort} ");
            float partDeathFactor = 0f;
            Hediff_Injury closestToDeathInjury = null;

            List<PawnCapacityDef> allDefsListForReading = DefDatabase<PawnCapacityDef>.AllDefsListForReading;
            PawnCapacitiesHandler capacities = new PawnCapacitiesHandler(pawn);

            List<PawnCapacityDef> vitalCapacitiesList = new List<PawnCapacityDef>();
            for (int i = 0; i < allDefsListForReading.Count; i++)
            {
                PawnCapacityDef pawnCapacityDef = allDefsListForReading[i];
                // Если для текущего вида пешки параметр смертельно важен, делаем для него расчет
                if (pawn.RaceProps.IsFlesh ? pawnCapacityDef.lethalFlesh : pawnCapacityDef.lethalMechanoids)
                {
                    vitalCapacitiesList.Add(pawnCapacityDef);
                }
            }

            List<BodyPartRecord> injuredParts = GetPartsByInjuries(injuries);
            BodyPartRecord closestToDeathPart = null;
            foreach (var part in injuredParts)
            {
                float partMaxHp = part.def.GetMaxHealth(pawn);
                float currentHP = pawn.health.hediffSet.GetPartHealth(part);
                float partDamagedPercent = currentHP / partMaxHp;
                float restorePriorityFactor = 1f - partDamagedPercent;
                Log.Message($"__ЧАСТЬ {part.LabelCap}, процент повреждения {partDamagedPercent}, Допустимый уровень {levelToCure}");

                if (levelToCure < partDamagedPercent)
                    continue;

                float capacityImpactWeight = 0f;

                foreach (var vitalCapacity in vitalCapacitiesList)
                {
                    //Log.Message($"- Влияние {part.LabelCap} на {vitalCapacity.defName}-------------------------");
                    capacityImpactWeight += BodyPartAffectsCapacity(vitalCapacity, part, pawn.health.hediffSet);
                    Log.Message($"-- Влияние {part.LabelCap} на {vitalCapacity.defName}: {BodyPartAffectsCapacity(vitalCapacity, part, pawn.health.hediffSet)}");
                }
                Log.Message($"++ Итоговый вес {part.LabelCap} на vitalCapacity :{capacityImpactWeight}");

                if (capacityImpactWeight > partDeathFactor)
                {
                    partDeathFactor = capacityImpactWeight;
                    closestToDeathPart = part;
                }
            }

            if (closestToDeathPart != null)
                closestToDeathInjury = GetWorstInjuryOfPart(pawn, closestToDeathPart, injuries);

            if (closestToDeathInjury != null)
                Log.Message($"-- Самая опасная травма '{closestToDeathInjury.Label}' на '{closestToDeathPart.Label}'");

            return closestToDeathInjury;

            //foreach (var injury in injuries)
            //{
            //    var part = injury.Part;
            //    if (part == null || pawn.health.hediffSet.PartIsMissing(part))
            //        continue;

            //    float partMaxHp = part.def.GetMaxHealth(pawn);
            //    float currentHP = pawn.health.hediffSet.GetPartHealth(part);
            //    if (partMaxHp <= 0f)
            //        continue;

            //    if (levelToCure > currentHP / partMaxHp)
            //        continue;

            //    float damageFactor = partMaxHp / injury.Severity;
            //    float capacityImpactWeight = 0f;

            //    foreach (var vitalCapacity in vitalCapacitiesList)
            //    {
            //        //Log.Message($"==== Влияние ОСНОВНОЙ ЧАСТИ {part.LabelCap} на {vitalCapacity.defName} ====");
            //        capacityImpactWeight += BodyPartAffectsCapacity(vitalCapacity, part, pawn.health.hediffSet);
            //    }

            //    if (capacityImpactWeight > partDeathFactor)
            //    {
            //        partDeathFactor = capacityImpactWeight;
            //        closestToDeathInjury = injury;
            //    }

            //    Log.Message($"Вес влияния {part.LabelCap} на все жизненно важные параметры: {capacityImpactWeight}");
            //}

            //if (closestToDeathInjury != null)
            //    Log.Message($"-- Самая опасная травма '{closestToDeathInjury.Label}' на '{closestToDeathInjury.Part?.Label ?? "null"}'");
            //return closestToDeathInjury;
        }


        public static List<BodyPartRecord> GetPartsByInjuries(IList<Hediff_Injury> injuries)
        {
            List<BodyPartRecord> injuredParts = new List<BodyPartRecord>();
            foreach (Hediff_Injury injury in injuries)
            {
                if (!injuredParts.Contains(injury.Part))
                {
                    injuredParts.Add(injury.Part);
                }
            }
            return injuredParts;
        }

        public static Hediff_Injury GetWorstInjuryOfPart(Pawn pawn, BodyPartRecord part, IList<Hediff_Injury> injuries)
        {
            return injuries
                .Where(injury => injury.Part == part)
                .OrderByDescending(injury => injury.Severity)
                .FirstOrDefault();
        }

        public static float BodyPartAffectsCapacity(PawnCapacityDef capacityDef, BodyPartRecord part, HediffSet diffSet)
        {
            float partCapacityFactor = 0f;
            //float weightMultiplier = 1f;
            //if (part.def.tags == null || part.def.tags.Count == 0)
            //    return partCapacityFactor;

            if (!capacityTagWeightsCache.TryGetValue(capacityDef, out var dict))
            {
                dict = GetTagsUsedByCapacityWithWeights(capacityDef, diffSet);
                capacityTagWeightsCache[capacityDef] = dict;
            }
            if (dict == null || dict.Count == 0)
                return partCapacityFactor;

            foreach (var tag in dict.Keys)
            {
                partCapacityFactor += CalculatePartCapacityFactorByTag(part, dict, tag);

                foreach (BodyPartRecord childPart in GetChildPartsByTag(part, tag))
                {
                    //Log.Message($"{childPart.LabelCap} дочерняя часть {part.LabelCap}");
                    partCapacityFactor += CalculatePartCapacityFactorByTag(childPart, dict, tag);
                }
            }

            return partCapacityFactor;

            //foreach (var partTag in part.def.tags)
            //{
            //    partCapacityFactor += dict.TryGetValue(partTag, out float weight) ? weight : 0f;
            //}

            //return partCapacityFactor

            // Если часть тела не имеет прямых тегов, ее вес зависит от степени поврежденности (т.к. учитываются теги вложенных частей)
            //if (part.def.tags == null || part.def.tags.Count == 0)
            //{
            //    float partMaxHp = part.def.GetMaxHealth(diffSet.pawn);
            //    float currentHP = diffSet.GetPartHealth(part);
            //    weightMultiplier = 1 - (currentHP / partMaxHp);
            //}

            //return CalculatePartCapacityFactorByTag(part, diffSet, dict, weightMultiplier);
        }

        public static IEnumerable<BodyPartRecord> GetChildPartsByTag(BodyPartRecord part, BodyPartTagDef tag)
        {
            foreach (var child in part.parts)
            {
                if (child.def.tags != null && child.def.tags.Contains(tag))
                {
                    yield return child;
                }

                foreach (var descendant in GetChildPartsByTag(child, tag))
                {
                    yield return descendant;
                }
            }
        }

        private static float CalculatePartCapacityFactorByTag(BodyPartRecord part, Dictionary<BodyPartTagDef, float> tagWeights, BodyPartTagDef tag)
        {
            float partCapacityFactor = 0f;

            if (part.def?.tags != null && part.def.tags.Contains(tag))
            {
                if (tagWeights.TryGetValue(tag, out float baseWeight))
                {
                    partCapacityFactor += baseWeight;
                    //Log.Message($"{part.LabelCap} влияет через {tag.defName}: {baseWeight}");
                }
            }

            return partCapacityFactor;
        }


        public static Dictionary<BodyPartTagDef, float> GetTagsUsedByCapacityWithWeights(PawnCapacityDef def, HediffSet diffSet)
        {
            var tags = new Dictionary<BodyPartTagDef, float>();
            // Сознание
            float ConsciousnessSourceFactor = CalculateBodyPartTagEfficiencyFactor(diffSet, BodyPartTagDefOf.ConsciousnessSource, float.MaxValue);
            // Дыхание
            float BreathingSourceFactor = CalculateBodyPartTagEfficiencyFactor(diffSet, BodyPartTagDefOf.BreathingSource, float.MaxValue);
            float BreathingPathwayFactor = CalculateBodyPartTagEfficiencyFactor(diffSet, BodyPartTagDefOf.BreathingPathway, 1f);
            float BreathingSourceCageFactor = CalculateBodyPartTagEfficiencyFactor(diffSet, BodyPartTagDefOf.BreathingSourceCage, 1f);
            // Кровообращение
            float BloodPumpingSourceFactor = CalculateBodyPartTagEfficiencyFactor(diffSet, BodyPartTagDefOf.BloodPumpingSource, float.MaxValue);
            // Фильтрация крови
            float BloodFiltrationKidneyFactor = 0f;
            float BloodFiltrationLiverFactor = 0f;
            float BloodFiltrationSourceFactor = 0f;
            if (diffSet.pawn.RaceProps.body.HasPartWithTag(BodyPartTagDefOf.BloodFiltrationKidney))
            {
                BloodFiltrationKidneyFactor = CalculateBodyPartTagEfficiencyFactor(diffSet, BodyPartTagDefOf.BloodFiltrationKidney, float.MaxValue);
                BloodFiltrationLiverFactor = CalculateBodyPartTagEfficiencyFactor(diffSet, BodyPartTagDefOf.BloodFiltrationLiver, float.MaxValue);
            }
            else
            {
                BloodFiltrationSourceFactor = CalculateBodyPartTagEfficiencyFactor(diffSet, BodyPartTagDefOf.BloodFiltrationSource, float.MaxValue);
            }
            // Метаболизм
            float MetabolismSourceFactor = CalculateBodyPartTagEfficiencyFactor(diffSet, BodyPartTagDefOf.MetabolismSource, float.MaxValue);
            // Слух
            float HearingSourceFactor = CalculateBodyPartTagEfficiencyFactor(diffSet, BodyPartTagDefOf.MetabolismSource, float.MaxValue, 0.75f);
            // Зрение
            float SightSourceFactor = CalculateBodyPartTagEfficiencyFactor(diffSet, BodyPartTagDefOf.SightSource, float.MaxValue, 0.75f);
            // Речь
            float TalkingSourceFactor = CalculateBodyPartTagEfficiencyFactor(diffSet, BodyPartTagDefOf.TalkingSource, float.MaxValue);
            float TalkingPathwayFactor = CalculateBodyPartTagEfficiencyFactor(diffSet, BodyPartTagDefOf.TalkingPathway, 1f);
            float TongueFactor = CalculateBodyPartTagEfficiencyFactor(diffSet, BodyPartTagDefOf.Tongue, 1f);
            // Питание
            float EatingSourceFactor = CalculateBodyPartTagEfficiencyFactor(diffSet, BodyPartTagDefOf.EatingSource, float.MaxValue);
            float EatingPathwayFactor = CalculateBodyPartTagEfficiencyFactor(diffSet, BodyPartTagDefOf.EatingPathway, 1f);
            // Работа
            float functionalPercentageManipulation = 0f;
            float ManipulationFactor = PawnCapacityUtility.CalculateLimbEfficiency(diffSet, BodyPartTagDefOf.ManipulationLimbCore, BodyPartTagDefOf.ManipulationLimbSegment, BodyPartTagDefOf.ManipulationLimbDigit, 0.8f, out functionalPercentageManipulation, null);
            if (ManipulationFactor > 1f)
            {
                ManipulationFactor = 1f;
            }
            ManipulationFactor = 1f - ManipulationFactor;
            // Движение
            float functionalPercentageMoving = 0f;
            float MovingFactor = PawnCapacityUtility.CalculateLimbEfficiency(diffSet, BodyPartTagDefOf.MovingLimbCore, BodyPartTagDefOf.MovingLimbSegment, BodyPartTagDefOf.MovingLimbDigit, 0.4f, out functionalPercentageMoving, null);
            // Если у пешки уничтодено больше половины ног, она не может ходить (Для человека 2 ноги из 2, для пауков 4 ноги из 6 и т.д.)
            if (functionalPercentageMoving < 0.4999f)
            {
                MovingFactor = 0f;
            }
            if (MovingFactor > 1f)
            {
                MovingFactor = 1f;
            }
            MovingFactor = 1f - MovingFactor;
            float PelvisFactor = CalculateBodyPartTagEfficiencyFactor(diffSet, BodyPartTagDefOf.Pelvis, float.MaxValue);
            float SpineFactor = CalculateBodyPartTagEfficiencyFactor(diffSet, BodyPartTagDefOf.Spine, float.MaxValue);


            //Log.Message($"ConsciousnessSourceFactor: {ConsciousnessSourceFactor}");

            //Log.Message($"BreathingSourceFactor: {BreathingSourceFactor}");
            //Log.Message($"BreathingPathwayFactor: {BreathingPathwayFactor}");
            //Log.Message($"BreathingSourceCageFactor: {BreathingSourceCageFactor}");

            //Log.Message($"BloodPumpingSourceFactor: {BloodPumpingSourceFactor}");

            //Log.Message($"BloodFiltrationKidneyFactor: {BloodFiltrationKidneyFactor}");
            //Log.Message($"BloodFiltrationLiverFactor: {BloodFiltrationLiverFactor}");
            //Log.Message($"BloodFiltrationSourceFactor: {BloodFiltrationSourceFactor}");

            //Log.Message($"MetabolismSourceFactor: {MetabolismSourceFactor}");

            //Log.Message($"HearingSourceFactor: {HearingSourceFactor}");

            //Log.Message($"SightSourceFactor: {SightSourceFactor}");

            //Log.Message($"TalkingPathwayFactor: {TalkingPathwayFactor}");
            //Log.Message($"TalkingSourceFactor: {TalkingSourceFactor}");
            //Log.Message($"TongueFactor: {TongueFactor}");

            //Log.Message($"EatingSourceFactor: {EatingSourceFactor}");
            //Log.Message($"EatingPathwayFactor: {EatingPathwayFactor}");

            //Log.Message($"ManipulationFactor: {ManipulationFactor}");

            //Log.Message($"MovingFactor: {MovingFactor}");
            //Log.Message($"PelvisFactor: {PelvisFactor}");
            //Log.Message($"SpineFactor: {SpineFactor}");

            switch (def.defName)
            {
                case "Consciousness":
                    tags[BodyPartTagDefOf.ConsciousnessSource] = ConsciousnessSourceFactor;

                    // side effects:
                    tags[BodyPartTagDefOf.BloodPumpingSource] = BloodPumpingSourceFactor * 0.2f;

                    tags[BodyPartTagDefOf.BreathingSource] = BreathingSourceFactor * 0.2f;
                    tags[BodyPartTagDefOf.BreathingPathway] = BreathingPathwayFactor * 0.2f;
                    tags[BodyPartTagDefOf.BreathingSourceCage] = BreathingSourceCageFactor * 0.2f;

                    tags[BodyPartTagDefOf.BloodFiltrationKidney] = BloodFiltrationKidneyFactor * 0.1f;
                    tags[BodyPartTagDefOf.BloodFiltrationLiver] = BloodFiltrationLiverFactor * 0.1f;
                    tags[BodyPartTagDefOf.BloodFiltrationSource] = BloodFiltrationSourceFactor * 0.1f;
                    break;

                case "Breathing":
                    tags[BodyPartTagDefOf.BreathingSource] = BreathingSourceFactor;
                    tags[BodyPartTagDefOf.BreathingPathway] = BreathingPathwayFactor;
                    tags[BodyPartTagDefOf.BreathingSourceCage] = BreathingSourceCageFactor;
                    break;

                case "BloodPumping":
                    tags[BodyPartTagDefOf.BloodPumpingSource] = BloodPumpingSourceFactor;
                    break;

                case "BloodFiltration":
                    tags[BodyPartTagDefOf.BloodFiltrationKidney] = BloodFiltrationKidneyFactor;
                    tags[BodyPartTagDefOf.BloodFiltrationLiver] = BloodFiltrationLiverFactor;
                    tags[BodyPartTagDefOf.BloodFiltrationSource] = BloodFiltrationSourceFactor;
                    break;

                case "Metabolism":
                    tags[BodyPartTagDefOf.MetabolismSource] = MetabolismSourceFactor;
                    break;

                case "Hearing":
                    tags[BodyPartTagDefOf.HearingSource] = HearingSourceFactor;
                    break;

                case "Sight":
                    tags[BodyPartTagDefOf.SightSource] = SightSourceFactor;
                    break;

                case "Talking":
                    tags[BodyPartTagDefOf.TalkingSource] = TalkingSourceFactor;
                    tags[BodyPartTagDefOf.TalkingPathway] = TalkingPathwayFactor;
                    tags[BodyPartTagDefOf.Tongue] = TongueFactor;

                    // side effects:
                    tags[BodyPartTagDefOf.ConsciousnessSource] = ConsciousnessSourceFactor;
                    break;

                case "Eating":
                    tags[BodyPartTagDefOf.EatingSource] = EatingSourceFactor;
                    tags[BodyPartTagDefOf.EatingPathway] = EatingPathwayFactor;
                    tags[BodyPartTagDefOf.Tongue] = TongueFactor * 0.5f;

                    // side effects:
                    tags[BodyPartTagDefOf.ConsciousnessSource] = ConsciousnessSourceFactor;
                    break;

                case "Manipulation":
                    tags[BodyPartTagDefOf.ManipulationLimbCore] = ManipulationFactor;
                    tags[BodyPartTagDefOf.ManipulationLimbSegment] = ManipulationFactor;
                    tags[BodyPartTagDefOf.ManipulationLimbDigit] = ManipulationFactor * 0.8f;
                     
                    // side effects:
                    tags[BodyPartTagDefOf.ConsciousnessSource] = ConsciousnessSourceFactor;
                    break;

                case "Moving":
                    tags[BodyPartTagDefOf.MovingLimbCore] = MovingFactor;
                    tags[BodyPartTagDefOf.MovingLimbSegment] = MovingFactor;
                    tags[BodyPartTagDefOf.MovingLimbDigit] = MovingFactor * 0.4f;
                    tags[BodyPartTagDefOf.Pelvis] = PelvisFactor;
                    tags[BodyPartTagDefOf.Spine] = SpineFactor;

                    // side effects:
                    tags[BodyPartTagDefOf.BloodPumpingSource] = BloodPumpingSourceFactor * 0.2f;

                    tags[BodyPartTagDefOf.BreathingSource] = BreathingSourceFactor * 0.2f;
                    tags[BodyPartTagDefOf.BreathingPathway] = BreathingPathwayFactor * 0.2f;
                    tags[BodyPartTagDefOf.BreathingSourceCage] = BreathingSourceCageFactor * 0.2f;

                    tags[BodyPartTagDefOf.ConsciousnessSource] = ConsciousnessSourceFactor;
                    break;
            }

            return tags;
        }
        public static float CalculateBodyPartTagEfficiencyFactor(HediffSet diffSet, BodyPartTagDef tag, float maximum, float bestPartEfficiencySpecialWeight = -1f)
        {
            float factor = PawnCapacityUtility.CalculateTagEfficiency(diffSet, tag, maximum, default(FloatRange), null, bestPartEfficiencySpecialWeight);
            if (factor > 1f)
                factor = 1f;

            factor = 1f - factor;

            return factor;
        }

    }
}