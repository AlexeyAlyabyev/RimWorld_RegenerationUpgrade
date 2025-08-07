using HarmonyLib;
using RimWorld;
using System.Collections.Generic;
using System.Linq;
using Verse;

namespace RegenerationUpgrade.Replacers
{
    public static class HealLogic_Replacer
    {
        // кэш жизненно важных параметров по типу пешки
        public static Dictionary<ThingDef, List<PawnCapacityDef>> lethalCapsCacheByRace = new Dictionary<ThingDef, List<PawnCapacityDef>>();
        public static List<PawnCapacityDef> pawnLethalCapacities;

        //кэш вложенностей частей тела
        public static Dictionary<BodyDef, List<BodyPartRecord>> sortedPartsByBodyDef = new Dictionary<BodyDef, List<BodyPartRecord>>();
        public static Dictionary<(BodyDef, BodyPartRecord), int> bodyPartDepthCache = new Dictionary<(BodyDef, BodyPartRecord), int>();
        //кэш веса части тела на жизненно важные параметры
        public static Dictionary<(PawnCapacityDef, BodyPartDef), float> partCapacityWeightCache = new Dictionary<(PawnCapacityDef, BodyPartDef), float>();
        public static Dictionary<BodyPartRecord, (float healthPercent, float capacityImpactSum)> pawnPartData;

        public static Dictionary<PawnCapacityDef, float> capacityValueCache;

        // Имеет ли пешка оптимизатор регенерации для применения патча
        private static bool HasHealingUpgrade(Pawn pawn)
        {
            // Проверка на ген
            return HasActiveGene(pawn, DefDatabase<GeneDef>.GetNamed("RU_HealingPriorityGene")) == true
                // или на имплант
                || pawn.health?.hediffSet?.hediffs?.Any(h => h.def == HediffDef.Named("RU_HealingPriorityImplant")) == true;
        }

        public static bool HasActiveGene(Pawn pawn, GeneDef geneDef)
        {
            if (pawn?.genes == null || geneDef == null)
            {
                return false;
            }

            List<Gene> genesListForReading = pawn.genes.GenesListForReading;
            for (int i = 0; i < genesListForReading.Count; i++)
            {
                if (genesListForReading[i].def == geneDef && genesListForReading[i].Active)
                {
                    return true;
                }
            }

            return false;
        }

        public static void SortHediffList(List<Hediff_Injury> injuries)
        {
            if (injuries == null || injuries.Count <= 1)
                return;

            Pawn pawn = injuries.First().pawn;
            if (!HasHealingUpgrade(pawn))
                return;

            float regenAmount = GetRegenerationHealAmount(injuries.First().pawn);
            if (regenAmount <= 0f)
                return;

            var remaining = new HashSet<Hediff_Injury>(injuries);
            injuries.Clear();

            while (remaining.Count > 0)
            {
                Hediff_Injury mostImportantInjury = GetMostDangerousInjury(remaining);
                if (mostImportantInjury == null)
                    break;

                injuries.Add(mostImportantInjury);
                remaining.Remove(mostImportantInjury);

                regenAmount -= mostImportantInjury.Severity;
                if (regenAmount <= 0f)
                    break;
            }

            // Добавляем оставшиеся элементы
            injuries.AddRange(remaining);
        }

        public static void SortHediffListVEF(List<Hediff_Injury> injuries)
        {
            if (injuries == null || injuries.Count <= 1)
                return;

            Pawn pawn = injuries.First().pawn;
            if (!HasHealingUpgrade(pawn))
                return;

            Hediff_Injury mostImportantInjury = GetMostDangerousInjury(injuries);
            if (mostImportantInjury == null)
                return;

            var remaining = new HashSet<Hediff_Injury>(injuries);
            injuries.Clear();

            injuries.Add(mostImportantInjury);
            remaining.Remove(mostImportantInjury);

            // Добавляем оставшиеся элементы
            injuries.AddRange(remaining);
        }

        public static float GetRegenerationHealAmount(Pawn pawn)
        {
            if (!pawn.health.hediffSet.HasRegeneration) return 0f;

            float regenValue = 0f;
            foreach (Hediff h in pawn.health.hediffSet.hediffs)
            {
                if (h.CurStage != null)
                    regenValue += h.CurStage.regeneration;
            }
            return regenValue * 0.00025f;
        }

        // Получить список летальных параметров для пешки
        public static void SetPawnLethalCapacities(Pawn pawn)
        {
            var race = pawn.def;
            if (!lethalCapsCacheByRace.TryGetValue(race, out var cachedList))
            {
                cachedList = new List<PawnCapacityDef>();

                List<PawnCapacityDef> allDefsListForReading = DefDatabase<PawnCapacityDef>.AllDefsListForReading;
                for (int i = 0; i < allDefsListForReading.Count; i++)
                {
                    PawnCapacityDef pawnCapacityDef = allDefsListForReading[i];
                    // Если для текущего вида пешки параметр смертельно важен, делаем для него расчет
                    if (pawn.RaceProps.IsFlesh ? pawnCapacityDef.lethalFlesh : pawnCapacityDef.lethalMechanoids)
                    {
                        cachedList.Add(pawnCapacityDef);
                    }
                }

                lethalCapsCacheByRace[race] = cachedList;
            }

            // Сохраняем ссылку на локальную переменную
            pawnLethalCapacities = cachedList;
        }

        public static void SetPawnCapacitiesCache(Pawn pawn)
        {
            foreach (var lethalCapacity in pawnLethalCapacities)
            {
                capacityValueCache[lethalCapacity] = GetCapacityValue(pawn, lethalCapacity);
            }
            capacityValueCache[PawnCapacityDefOf.Moving] = GetCapacityValue(pawn, PawnCapacityDefOf.Moving);
        }

        public static Hediff_Injury GetMostDangerousInjury(IEnumerable<Hediff_Injury> injuries)
        {
            if (injuries == null || !injuries.Any())
                return null;

            Pawn pawn = injuries.First().pawn;
            // Если отсутствует имплант/ген берем как и по стандарту рандомную травму
            if (!HasHealingUpgrade(pawn))
                return injuries.RandomElement();

            SetPawnLethalCapacities(pawn);
            capacityValueCache = new Dictionary<PawnCapacityDef, float>();
            SetPawnCapacitiesCache(pawn);
            pawnPartData = new Dictionary<BodyPartRecord, (float healthPercent, float capacityImpactSum)>();
            SetCapacityValueForBodyParts(pawn, pawnPartData);


            // Если есть кровотечение, в первую очередь лечим его
            if (pawn.health.hediffSet.BleedRateTotal >= 0.1f)
            {
                Hediff_Injury mostBleedingHediff = FindMostBleedingHediff(injuries);
                if (mostBleedingHediff != null)
                    return mostBleedingHediff;
            }

            // Восстанавливаем критически важные части тела до 10%
            Hediff_Injury lifeThreatingInjury = GetDangerousInjuryToRecoverInInterval(pawn, injuries, 0.01f, 0.1f);
            if (lifeThreatingInjury != null)
                return lifeThreatingInjury;

            // Если есть заражения/болезни, пытаемся вылечить травмы влияющие на фильтрацию крови, чтобы успеть выработать иммунитет до смерти
            if (!WillSurviveFromDisease(pawn))
            {
                Hediff_Injury bloodFiltrationInjury = MostImpactfulInjuryOnCapacity(pawn, injuries, PawnCapacityDefOf.BloodFiltration); // Фильтрация крови
                if (bloodFiltrationInjury != null)
                    return bloodFiltrationInjury;
            }

            // Восстанавливаем критически важные части тела до 70% и возвращаем возможность передвигаться
            lifeThreatingInjury = GetDangerousInjuryToRecoverInInterval(pawn, injuries, 0.1f, 0.7f);
            if (lifeThreatingInjury != null)
                return lifeThreatingInjury;

            // Восстанавливаем манипуляцию до 50%
            Hediff_Injury manipulationinjury = GetWorstCapacityInjuryToHeal(pawn, injuries, PawnCapacityDefOf.Manipulation, 0.5f);
            if (manipulationinjury != null)
                return manipulationinjury;

            //MostImpactfulInjuryOnCapacity(pawn, injuries, PawnCapacityDefOf.Sight); // Зрение
            //MostImpactfulInjuryOnCapacity(pawn, injuries, PawnCapacityDefOf.Hearing); // Слух
            //MostImpactfulInjuryOnCapacity(pawn, injuries, PawnCapacityDefOf.Talking); // Общение (не важно)
            // Не забыть учесть что у бессмертных при впадании в смертосон должен восстанавливаться первым потерянный жизненно важный орган (ДОРАБОТКА ГЕНА РЕГЕНЕРАЦИИ КОНЕЧНОСТЕЙ)

            return injuries.RandomElement();
        }

        public static Hediff_Injury GetDangerousInjuryToRecoverInInterval(Pawn pawn, IEnumerable<Hediff_Injury> injuries, float min = 0.01f, float max = 1f)
        {
            Hediff_Injury injuryToHeal = null;
            float step = 0.05f;

            float pawnMostLethalCapacityValue = GetMostLethalCapacityValue();

            if (min <= 0f)
                min = step;

            if (max <= 0f)
                max = 1f;

            while (min <= max)
            {
                injuryToHeal = GetWorstCriticalPartInjury(injuries, pawnPartData, min);
                if (injuryToHeal != null)
                    return injuryToHeal;

                if (min >= pawnMostLethalCapacityValue) {
                    // Лечим травмы, влияющие на основные параметры до минимально безопасных значений в min%
                    injuryToHeal = GetMostDangerousCapacityInjury(pawn, injuries, min);
                    if (injuryToHeal != null)
                        return injuryToHeal;
                } 

                // Восстанавливаем способность ходить, но только после того как вылечили основные части/параметры тела до 30%
                if (min > 0.3f && min > capacityValueCache[PawnCapacityDefOf.Moving])
                {
                    injuryToHeal = GetWorstCapacityInjuryToHeal(pawn, injuries, PawnCapacityDefOf.Moving, min - 0.14f);
                    if (injuryToHeal != null)
                        return injuryToHeal;
                }

                min += step;
            }

            return injuryToHeal;
        }

        // ---------------------- БЛОК С КРОВОТЕЧЕНИЕМ ---------------------------
        // Найти самую кровоточащую травму
        private static Hediff_Injury FindMostBleedingHediff(IEnumerable<Hediff_Injury> injuries)
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
        public static Hediff_Injury GetMostDangerousCapacityInjury(Pawn pawn, IEnumerable<Hediff_Injury> injuries, float levelToCure = 1f)
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

                if (lowestCapacityValue > capacityValue)
                {
                    Hediff_Injury capacityInjury = MostImpactfulInjuryOnCapacity(pawn, injuries, pawnCapacityDef);
                    if (capacityInjury != null)
                    {
                        mostDangerousInjury = capacityInjury;
                        lowestCapacityValue = capacityValue;
                    }
                }
            }

            return mostDangerousInjury;
        }

        // Получить самую сиьную травму, которая влияет на параметр (pawnCapacity), чтобы поднять уровень параметра до значения levelToCure
        public static Hediff_Injury GetWorstCapacityInjuryToHeal(Pawn pawn, IEnumerable<Hediff_Injury> injuries, PawnCapacityDef pawnCapacity, float levelToCure = 1f        )
        {
            if (!capacityValueCache.TryGetValue(pawnCapacity, out var capacityValue))
            {
                capacityValue = GetCapacityValue(pawn, pawnCapacity);
                capacityValueCache[pawnCapacity] = capacityValue;
            }

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
        public static Hediff_Injury MostImpactfulInjuryOnCapacity(Pawn pawn, IEnumerable<Hediff_Injury> injuries, PawnCapacityDef capacity)
        {
            // Получаем текущее значение показателя (с поверждениями)
            if (!capacityValueCache.TryGetValue(capacity, out var oldCapacityVal))
            {
                oldCapacityVal = GetCapacityValue(pawn, capacity);
                capacityValueCache[capacity] = oldCapacityVal;
            }

            Hediff_Injury worstInjury = null;
            float maxReduction = 0f;
            List<Hediff> tempHediffs = new List<Hediff>();
            var simulatedHediffSet = new HediffSet(pawn);

            foreach (var injury in injuries)
            {
                // Копируем текущие хеддифы
                tempHediffs.Clear();
                tempHediffs.AddRange(pawn.health.hediffSet.hediffs);
                tempHediffs.Remove(injury); // Симулируем "удаление"

                // Создаём временный HediffSet (внутренне он не делает побочных эффектов)
                simulatedHediffSet.hediffs = tempHediffs;

                // Считаем, как бы изменилось значение capacity без этой травмы
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

        public static float GetMostLethalCapacityValue()
        {
            float minCapacityValue = 1f;
            foreach (var lethalCapacity in pawnLethalCapacities)
            {
                if (minCapacityValue > capacityValueCache[lethalCapacity])
                    minCapacityValue = capacityValueCache[lethalCapacity];
            }

            return minCapacityValue;
        }

        // ---------------------- БЛОК С ЖИЗНЕННО ВАЖНЫМИ ЧАСТЯМИ ТЕЛА ---------------------------
        // Получить самую тяжелую травму на жизненно важной части тела с наибольшими повреждениями
        private static Hediff_Injury GetWorstCriticalPartInjury(
            IEnumerable<Hediff_Injury> injuries,
            Dictionary<BodyPartRecord, (float healthPercent, float capacityImpactSum)> pawnPartData,
            float minPartHealthPercent
        )
        {
            Hediff_Injury mostCriticalPartInjury = null;
            BodyPartRecord mostCriticalPart = GetWorstCriticalPart(injuries, pawnPartData, minPartHealthPercent);
            if (mostCriticalPart != null)
            {
                mostCriticalPartInjury = GetWorstInjuryOfPart(mostCriticalPart, injuries);
            }

            return mostCriticalPartInjury;
        }

        // Получить жизненно важную часть тела с наибольшими повреждениями
        private static BodyPartRecord GetWorstCriticalPart(
            IEnumerable<Hediff_Injury> injuries,
            Dictionary<BodyPartRecord, (float healthPercent, float capacityImpactSum)> pawnPartData,
            float minHealthPercent
        )
        {
            List<BodyPartRecord> injuredParts = GetPartsByInjuries(injuries);
            // Эффективная фильтрация по injuredParts
            var injuredSet = new HashSet<BodyPartRecord>(injuredParts);

            var filtered = pawnPartData
                .Where(kvp => 
                    injuredSet.Contains(kvp.Key)
                    && kvp.Value.healthPercent < minHealthPercent
                    && kvp.Value.capacityImpactSum > 0f);

            // Найти часть с максимальным capacityImpactSum
            BodyPartRecord worstPart = null;
            float maxImpact = -1f;
            foreach (var kvp in filtered)
            {
                if (kvp.Value.capacityImpactSum > maxImpact)
                {
                    maxImpact = kvp.Value.capacityImpactSum;
                    worstPart = kvp.Key;
                }
            }

            return worstPart;
        }

        // Получить список поврежденных частей тела по списку всех повреждений
        public static List<BodyPartRecord> GetPartsByInjuries(IEnumerable<Hediff_Injury> injuries)
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
        public static Hediff_Injury GetWorstInjuryOfPart(BodyPartRecord part, IEnumerable<Hediff_Injury> injuries)
        {
            return injuries
                .Where(injury => injury.Part == part)
                .OrderByDescending(injury => injury.Severity)
                .FirstOrDefault();
        }

        // Словарь соотношения веса тега части на важность параметра пешки
        public static Dictionary<BodyPartTagDef, float> GetTagsWeightsByCapacity(PawnCapacityDef def)
        {
            var tags = new Dictionary<BodyPartTagDef, float>();

            switch (def.defName)
            {
                case "Consciousness":
                    tags[BodyPartTagDefOf.ConsciousnessSource] = 1f;
                    tags[BodyPartTagDefOf.BloodPumpingSource] = 0.2f;
                    tags[BodyPartTagDefOf.BreathingSource] = 0.2f;
                    tags[BodyPartTagDefOf.BreathingPathway] = 0.2f;
                    tags[BodyPartTagDefOf.BreathingSourceCage] = 0.2f;
                    tags[BodyPartTagDefOf.BloodFiltrationKidney] = 0.1f;
                    tags[BodyPartTagDefOf.BloodFiltrationLiver] = 0.1f;
                    tags[BodyPartTagDefOf.BloodFiltrationSource] = 0.1f;
                    break;

                case "Breathing":
                    tags[BodyPartTagDefOf.BreathingSource] = 1f;
                    tags[BodyPartTagDefOf.BreathingPathway] = 1f;
                    tags[BodyPartTagDefOf.BreathingSourceCage] = 1f;
                    break;

                case "BloodPumping":
                    tags[BodyPartTagDefOf.BloodPumpingSource] = 1f;
                    break;

                case "BloodFiltration":
                    tags[BodyPartTagDefOf.BloodFiltrationKidney] = 1f;
                    tags[BodyPartTagDefOf.BloodFiltrationLiver] = 1f;
                    tags[BodyPartTagDefOf.BloodFiltrationSource] = 1f;
                    break;

                case "Metabolism":
                    tags[BodyPartTagDefOf.MetabolismSource] = 1f;
                    break;

                case "Hearing":
                    tags[BodyPartTagDefOf.HearingSource] = 1f;
                    break;

                case "Sight":
                    tags[BodyPartTagDefOf.SightSource] = 1f;
                    break;

                case "Talking":
                    tags[BodyPartTagDefOf.TalkingSource] = 1f;
                    tags[BodyPartTagDefOf.TalkingPathway] = 1f;
                    tags[BodyPartTagDefOf.Tongue] = 1f;
                    tags[BodyPartTagDefOf.ConsciousnessSource] = 1f;
                    break;

                case "Eating":
                    tags[BodyPartTagDefOf.EatingSource] = 1f;
                    tags[BodyPartTagDefOf.EatingPathway] = 1f;
                    tags[BodyPartTagDefOf.Tongue] = 0.5f;
                    tags[BodyPartTagDefOf.ConsciousnessSource] = 1f;
                    break;

                case "Manipulation":
                    tags[BodyPartTagDefOf.ManipulationLimbCore] = 1f;
                    tags[BodyPartTagDefOf.ManipulationLimbSegment] = 0.9f;
                    tags[BodyPartTagDefOf.ManipulationLimbDigit] = 0.8f;
                    tags[BodyPartTagDefOf.ConsciousnessSource] = 1f;
                    break;

                case "Moving":
                    tags[BodyPartTagDefOf.MovingLimbCore] = 1f;
                    tags[BodyPartTagDefOf.MovingLimbSegment] = 0.9f;
                    tags[BodyPartTagDefOf.MovingLimbDigit] = 0.4f;
                    tags[BodyPartTagDefOf.Pelvis] = 1f;
                    tags[BodyPartTagDefOf.Spine] = 1f;
                    tags[BodyPartTagDefOf.BloodPumpingSource] = 0.2f;
                    tags[BodyPartTagDefOf.BreathingSource] = 0.2f;
                    tags[BodyPartTagDefOf.BreathingPathway] = 0.2f;
                    tags[BodyPartTagDefOf.BreathingSourceCage] = 0.2f;
                    tags[BodyPartTagDefOf.ConsciousnessSource] = 1f;
                    break;
            }

            return tags;
        }

        // Задаем для каждой части тела ее текущий процент прочности и насколько она важна для жизненно важных параметров
        private static void SetCapacityValueForBodyParts(Pawn pawn, Dictionary<BodyPartRecord, (float healthPercent, float capacityImpactSum)> pawnPartData)
        {
            // Пытаемся взять отсортированный порядок по расе персонажа из кэша
            var bodyDef = pawn.RaceProps.body;
            if (!sortedPartsByBodyDef.TryGetValue(bodyDef, out var sortedParts))
            {
                sortedParts = bodyDef.AllParts.OrderByDescending(p => GetBodyPartDepth(p, bodyDef)).ToList();

                sortedPartsByBodyDef[bodyDef] = sortedParts;
            }

            // Проходимся по всем частям тела от снизу вверх по структуре
            foreach (var part in sortedParts)
            {
                float partMaxHp = part.def.GetMaxHealth(pawn);
                float currentHP = pawn.health.hediffSet.GetPartHealth(part);
                float healthPercent = currentHP / partMaxHp;

                float capacityImpactSum = 0f;
                foreach (var capacity in pawnLethalCapacities)
                {
                    capacityImpactSum += GetPartCapacityWeight(part, capacity);
                }

                if (part.parts != null)
                {
                    foreach (var childPart in part.parts)
                    {
                        capacityImpactSum += pawnPartData[childPart].capacityImpactSum;
                    }
                }

                pawnPartData[part] = (healthPercent, capacityImpactSum);
            }
        }

        // Вспомогательный метод, для сортировки частей тела пешки по глубине
        private static int GetBodyPartDepth(BodyPartRecord part, BodyDef bodyDef)
        {
            var key = (bodyDef, part);
            if (bodyPartDepthCache.TryGetValue(key, out int cachedDepth))
                return cachedDepth;

            int depth = 0;
            while (part.parent != null)
            {
                part = part.parent;
                depth++;
            }
            bodyPartDepthCache[key] = depth;

            return depth;
        }

        // Получение веса важности части тела на параметр
        private static float GetPartCapacityWeight(BodyPartRecord part, PawnCapacityDef capacity)
        {
            var key = (capacity, part.def);
            if (partCapacityWeightCache.TryGetValue(key, out var cachedWeight))
                return cachedWeight;

            float capacitiesImpact = 0f;
            if (part.def?.tags != null)
            {
                var capacityTags = GetTagsWeightsByCapacity(capacity);
                foreach (var tag in capacityTags.Keys)
                {
                    if (part.def.tags.Contains(tag) && capacityTags.TryGetValue(tag, out float tagWeight))
                    {
                        capacitiesImpact += tagWeight;
                    }
                }
            }
            partCapacityWeightCache[key] = capacitiesImpact;

            return capacitiesImpact;
        }
    }
}