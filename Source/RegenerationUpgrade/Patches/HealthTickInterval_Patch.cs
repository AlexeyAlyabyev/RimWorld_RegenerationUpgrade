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
        public static List<PawnCapacityDef> pawnLethalCapacities;
        private static Dictionary<(BodyPartRecord part, BodyPartTagDef tag), List<BodyPartRecord>> childPartsByTagCache;

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

        // Получить список летальных параметров для пешки
        public static void SetPawnLethalCapacities(Pawn pawn)
        {
            pawnLethalCapacities = new List<PawnCapacityDef>();

            // Получаем список всех возможных параметров пешек
            List<PawnCapacityDef> allDefsListForReading = DefDatabase<PawnCapacityDef>.AllDefsListForReading;
            for (int i = 0; i < allDefsListForReading.Count; i++)
            {
                PawnCapacityDef pawnCapacityDef = allDefsListForReading[i];
                // Если для текущего вида пешки параметр смертельно важен, делаем для него расчет
                if (pawn.RaceProps.IsFlesh ? pawnCapacityDef.lethalFlesh : pawnCapacityDef.lethalMechanoids)
                {
                    pawnLethalCapacities.Add(pawnCapacityDef);
                }
            }
        }

        public static Hediff_Injury GetMostDangerousInjury(IList<Hediff_Injury> injuries)
        {
            if (injuries == null || injuries.Count == 0)
                return null;

            Pawn pawn = injuries[0].pawn;
            SetPawnLethalCapacities(pawn);
            var capacityTagWeightsCache = new Dictionary<PawnCapacityDef, Dictionary<BodyPartTagDef, float>>();
            var capacityValueCache = new Dictionary<PawnCapacityDef, float>();
            childPartsByTagCache = new Dictionary<(BodyPartRecord, BodyPartTagDef), List<BodyPartRecord>>();

            // Если есть кровотечение, в первую очередь лечим его
            if (pawn.health.CanBleed && pawn.health.hediffSet.BleedRateTotal >= 0.1f)
            {
                Hediff_Injury mostBleedingHediff = FindMostBleedingHediff(injuries);
                if (mostBleedingHediff != null)
                    return mostBleedingHediff;
            }

            // Восстанавливаем критически важные части тела до 10%
            Hediff_Injury lifeThreatingInjury = GetDangerousInjuryToRecoverInInterval(pawn, injuries, capacityTagWeightsCache, capacityValueCache, 0.02f, 0.1f);
            if (lifeThreatingInjury != null)
                return lifeThreatingInjury;

            // Если есть заражения/болезни, пытаемся вылечить травмы влияющие на фильтрацию крови, чтобы успеть выработать иммунитет до смерти
            if (!WillSurviveFromDisease(pawn))
            {
                Hediff_Injury bloodFiltrationInjury = MostImpactfulInjuryOnCapacity(pawn, injuries, PawnCapacityDefOf.BloodFiltration, capacityValueCache); // Фильтрация крови
                if (bloodFiltrationInjury != null)
                {
                    //Log.Message($"PAWN {pawn.LabelShort} ");
                    //Log.Message($"bloodFiltrationInjury {bloodFiltrationInjury.Label} on {bloodFiltrationInjury.Part?.Label ?? "null"}");
                    return bloodFiltrationInjury;
                }
            }

            // Восстанавливаем критически важные части тела до 30% и возвращаем возможность передвигаться
            lifeThreatingInjury = GetDangerousInjuryToRecoverInInterval(pawn, injuries, capacityTagWeightsCache, capacityValueCache, 0.1f, 0.3f);
            if (lifeThreatingInjury != null)
                return lifeThreatingInjury;

            // Восстанавливаем способность взаимодействия
            Hediff_Injury manipulationinjury = GetWorstCapacityInjuryToHeal(pawn, injuries, PawnCapacityDefOf.Manipulation, capacityValueCache, 0.01f);
            if (manipulationinjury != null)
                return manipulationinjury;

            // Восстанавливаем критически важные части тела до 70%, попутно лечим травмы влияющие на передвижение
            lifeThreatingInjury = GetDangerousInjuryToRecoverInInterval(pawn, injuries, capacityTagWeightsCache, capacityValueCache, 0.3f, 0.7f);
            if (lifeThreatingInjury != null)
                return lifeThreatingInjury;

            // Восстанавливаем манипуляцию до 50%
            manipulationinjury = GetWorstCapacityInjuryToHeal(pawn, injuries, PawnCapacityDefOf.Manipulation, capacityValueCache, 0.5f);
            if (manipulationinjury != null)
                return manipulationinjury;


            //MostImpactfulInjuryOnCapacity(pawn, injuries, PawnCapacityDefOf.Manipulation, capacityValueCache); // Работа (До 25%?)

            //MostImpactfulInjuryOnCapacity(pawn, injuries, PawnCapacityDefOf.Sight, capacityValueCache); // Зрение
            //MostImpactfulInjuryOnCapacity(pawn, injuries, PawnCapacityDefOf.Hearing, capacityValueCache); // Слух
            //MostImpactfulInjuryOnCapacity(pawn, injuries, PawnCapacityDefOf.Talking, capacityValueCache); // Общение (не важно)



            // Не забыть учесть что у бессмертных при впадании в смертосон должен восстанавливаться первым потерянный жизненно важный орган (ДОРАБОТКА ГЕНА РЕГЕНЕРАЦИИ КОНЕЧНОСТЕЙ)


            return injuries.RandomElement();
        }

        public static Hediff_Injury GetDangerousInjuryToRecoverInInterval(
            Pawn pawn, 
            IList<Hediff_Injury> injuries, 
            Dictionary<PawnCapacityDef, Dictionary<BodyPartTagDef, float>> capacityTagWeightsCache,
            Dictionary<PawnCapacityDef, float> capacityValueCache,
            float min = 0.01f, 
            float max = 1f)
        {
            Hediff_Injury injuryToHeal = null;
            float step = 0.02f;

            if (min <= 0f)
                min = step;

            if (max <= 0f)
                max = 1f;

            while (min <= max)
            {
                // Восстанавливаем части тела, которые могут привести к смерти при дальнейших повреждениях до min%
                injuryToHeal = GetClosestToDeathPartOfTheBody(pawn, injuries, capacityTagWeightsCache, min);
                if (injuryToHeal != null)
                    return injuryToHeal;

                // Первоочередно после кровотечения и фильтрации крови лечим Кровообращение, Дыхание или Сознание до минимально безопасных значений в min%
                injuryToHeal = GetMostDangerousLifeThreatingCapacityInjury(pawn, injuries, capacityValueCache, min);
                if (injuryToHeal != null)
                    return injuryToHeal;

                // Восстанавливаем способность ходить, но только после того как вылечили основные части/параметры тела до 30%
                if (min > 0.3f)
                {
                    injuryToHeal = GetWorstCapacityInjuryToHeal(pawn, injuries, PawnCapacityDefOf.Moving, capacityValueCache, min - 0.14f);
                    if (injuryToHeal != null)
                        return injuryToHeal;
                }

                min += step;
            }

            return injuryToHeal;
        }

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
            HediffWithComps mostSevere = null;
            float worstDelta = float.MinValue;

            foreach (var h in pawn.health.hediffSet.hediffs)
            {
                if (!h.Visible || h.FullyImmune()) continue;

                if (h is HediffWithComps comp)
                {
                    var immComp = comp.TryGetComp<HediffComp_Immunizable>();
                    if (immComp == null) continue;

                    float delta = immComp.Immunity - h.Severity;
                    if (delta > worstDelta)
                    {
                        worstDelta = delta;
                        mostSevere = comp;
                    }
                }
            }

            return mostSevere;
        }

        // ---------------------- БЛОК ЖИЗНЕННО ВАЖНЫХ ПАРАМЕТРОВ ПЕШЕК ---------------------------
        // Получить травму, которая сильнее всего сафектила жизненно важные параметры пешки. Чтобы вылечить ее до уровня levelToCure
        public static Hediff_Injury GetMostDangerousLifeThreatingCapacityInjury(
            Pawn pawn, 
            IList<Hediff_Injury> injuries,
            Dictionary<PawnCapacityDef, float> capacityValueCache,
            float levelToCure = 1f
        )
        {
            float lowestCapacityValue = levelToCure;
            Hediff_Injury mostDangerousInjury = null;

            foreach (PawnCapacityDef pawnCapacityDef in pawnLethalCapacities)
            {
                if (!capacityValueCache.TryGetValue(pawnCapacityDef, out var capacityValue))
                {
                    capacityValue = GetCapacityValue(pawn, pawnCapacityDef);
                    capacityValueCache[pawnCapacityDef] = capacityValue;
                }
                Hediff_Injury capacityInjury = MostImpactfulInjuryOnCapacity(pawn, injuries, pawnCapacityDef, capacityValueCache);

                if (capacityInjury != null && lowestCapacityValue > capacityValue)
                {
                    mostDangerousInjury = capacityInjury;
                    lowestCapacityValue = capacityValue;
                }
            }

            return mostDangerousInjury;
        }

        // Получить самую сиьную травму, которая влияет на параметр (pawnCapacity), чтобы поднять уровень параметра до значения levelToCure
        public static Hediff_Injury GetWorstCapacityInjuryToHeal(
            Pawn pawn, 
            IList<Hediff_Injury> injuries, 
            PawnCapacityDef pawnCapacity,
            Dictionary<PawnCapacityDef, float> capacityValueCache,
            float levelToCure = 1f
        )
        {
            if (!capacityValueCache.TryGetValue(pawnCapacity, out var capacityValue))
            {
                capacityValue = GetCapacityValue(pawn, pawnCapacity);
                capacityValueCache[pawnCapacity] = capacityValue;
            }

            if (capacityValue >= levelToCure)
                return null;

            return MostImpactfulInjuryOnCapacity(pawn, injuries, pawnCapacity, capacityValueCache);
        }

        // Получить текущий уровень параметра пешки
        private static float GetCapacityValue(Pawn pawn, PawnCapacityDef capacity)
        {
            return PawnCapacityUtility.CalculateCapacityLevel(pawn.health.hediffSet, capacity);
        }

        // Получить травму которая больше всего влияет на параметр пешки
        public static Hediff_Injury MostImpactfulInjuryOnCapacity(
            Pawn pawn, 
            IList<Hediff_Injury> injuries, 
            PawnCapacityDef capacity,
            Dictionary<PawnCapacityDef, float> capacityValueCache
        )
        {
            // Получаем текущее значение показателя (с поверждениями)
            if (!capacityValueCache.TryGetValue(capacity, out var oldCapacityVal))
            {
                oldCapacityVal = GetCapacityValue(pawn, capacity);
                capacityValueCache[capacity] = oldCapacityVal;
            }

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
        public static Hediff_Injury GetClosestToDeathPartOfTheBody(
            Pawn pawn, 
            IList<Hediff_Injury> injuries,
            Dictionary<PawnCapacityDef, Dictionary<BodyPartTagDef, float>> capacityTagWeightsCache,
            float levelToCure = 1f)
        {
            //Log.Message($"=================== PAWN {pawn.LabelShort} ");
            float partDeathFactor = 0f;
            Hediff_Injury closestToDeathInjury = null;

            List<BodyPartRecord> injuredParts = GetPartsByInjuries(injuries);
            BodyPartRecord closestToDeathPart = null;
            foreach (var part in injuredParts)
            {
                float partMaxHp = part.def.GetMaxHealth(pawn);
                float currentHP = pawn.health.hediffSet.GetPartHealth(part);
                float partDamagedPercent = currentHP / partMaxHp;
                float restorePriorityFactor = 1f - partDamagedPercent;
                //Log.Message($"__ЧАСТЬ {part.LabelCap}, процент повреждения {partDamagedPercent}, Допустимый уровень {levelToCure}");

                if (levelToCure < partDamagedPercent)
                    continue;

                float capacityImpactWeight = 0f;

                foreach (var vitalCapacity in pawnLethalCapacities)
                {
                    //Log.Message($"- Влияние {part.LabelCap} на {vitalCapacity.defName}-------------------------");
                    capacityImpactWeight += BodyPartAffectsCapacity(vitalCapacity, part, pawn.health.hediffSet, capacityTagWeightsCache);
                    //Log.Message($"-- Влияние {part.LabelCap} на {vitalCapacity.defName}: {BodyPartAffectsCapacity(vitalCapacity, part, pawn.health.hediffSet)}");
                }
                //Log.Message($"++ Итоговый вес {part.LabelCap} на vitalCapacity :{capacityImpactWeight}");

                if (capacityImpactWeight > partDeathFactor)
                {
                    partDeathFactor = capacityImpactWeight;
                    closestToDeathPart = part;
                }
            }

            if (closestToDeathPart != null)
                closestToDeathInjury = GetWorstInjuryOfPart(pawn, closestToDeathPart, injuries);

            //if (closestToDeathInjury != null)
                //Log.Message($"-- Самая опасная травма '{closestToDeathInjury.Label}' на '{closestToDeathPart.Label}'");

            return closestToDeathInjury;
        }

        // Получить список поврежденных частей тела по списку всех повреждений
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

        // Получить самую сильную травму части тела из переданных травм
        public static Hediff_Injury GetWorstInjuryOfPart(Pawn pawn, BodyPartRecord part, IList<Hediff_Injury> injuries)
        {
            return injuries
                .Where(injury => injury.Part == part)
                .OrderByDescending(injury => injury.Severity)
                .FirstOrDefault();
        }

        // Получение влияния части тела на параметры пешки, с учетом дочерних/вложенных частей тела
        public static float BodyPartAffectsCapacity(
            PawnCapacityDef capacityDef, 
            BodyPartRecord part, 
            HediffSet diffSet,
            Dictionary<PawnCapacityDef, Dictionary<BodyPartTagDef, float>> capacityTagWeightsCache
        )
        {
            float partCapacityFactor = 0f;

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

                foreach (BodyPartRecord childPart in GetChildPartsByTagCached(part, tag))
                {
                    //Log.Message($"{childPart.LabelCap} дочерняя часть {part.LabelCap}");
                    partCapacityFactor += CalculatePartCapacityFactorByTag(childPart, dict, tag);
                }
            }

            return partCapacityFactor;
        }

        // Получить из кэша дочерние части тела от текущей, у которых есть заданный тег
        public static IEnumerable<BodyPartRecord> GetChildPartsByTagCached(BodyPartRecord part, BodyPartTagDef tag)
        {
            var key = (part, tag);
            if (childPartsByTagCache.TryGetValue(key, out var cached))
                return cached;

            var list = new List<BodyPartRecord>();
            FillChildParts(part, tag, list);
            childPartsByTagCache[key] = list;
            return list;
        }

        // Записать в кэш дочерние части тела от текущей, у которых есть заданный тег
        private static void FillChildParts(BodyPartRecord part, BodyPartTagDef tag, List<BodyPartRecord> acc)
        {
            foreach (var child in part.parts)
            {
                if (child.def.tags?.Contains(tag) == true)
                    acc.Add(child);

                FillChildParts(child, tag, acc);
            }
        }

        // Подсчет влияния части тела на параметры пешки, если эта часть имеет переданный тег
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

        // Словарь соотношения веса тега части на важность параметра пешки
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
            // Если у пешки уничтожено больше половины ног, она не может ходить (Для человека 2 ноги из 2, для пауков 4 ноги из 6 и т.д.)
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

        // Расчет множителя эффективности части тела с тегом на переданный параметр пешки
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