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
            if (injuries == null || injuries.Count == 0)
                return null;

            Pawn pawn = injuries[0].pawn;

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

            // Первоочередно после кровотечения и фильтрации крови лечим Кровообращение, Дыхание или Сознание до относительно безопасных значений в 50%
            Hediff_Injury lifeThreatingCapacityInjury = GetMostDangerousLifeThreatingCapacityInjury(pawn, injuries, 0.5f);
            if (lifeThreatingCapacityInjury != null)
                return lifeThreatingCapacityInjury;


            // Восстанавливаем части тела, которые могут привести к смерти при дальнейших повреждениях
            Hediff_Injury closestToDeathInjury = GetClosestToDeathPartOfTheBody(pawn, injuries);
            if (closestToDeathInjury != null)
                return closestToDeathInjury;


            // Добавить проверку боли


            // Восстанавливаем способность ходить (до минимального значениядо 16%)
            Hediff_Injury injuryThatPreventsWalking = GetWorstCapacityInjuryToHeal(pawn, injuries, PawnCapacityDefOf.Moving, 0.16f);
            if (injuryThatPreventsWalking != null)
                return injuryThatPreventsWalking;




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
        public static Hediff_Injury GetClosestToDeathPartOfTheBody(Pawn pawn, IList<Hediff_Injury> injuries)
        {
            //float partDeathFactor = 0f;
            Hediff_Injury closestToDeathInjury = null;

            List<PawnCapacityDef> allDefsListForReading = DefDatabase<PawnCapacityDef>.AllDefsListForReading;
            PawnCapacitiesHandler capacities = new PawnCapacitiesHandler(pawn);

            Log.Message($"PAWN {pawn.LabelShort} ");
            List<PawnCapacityDef> vitalCapacitiesList = new List<PawnCapacityDef>();
            for (int i = 0; i < allDefsListForReading.Count; i++)
            {
                PawnCapacityDef pawnCapacityDef = allDefsListForReading[i];
                Log.Message($"{i + 1}. {allDefsListForReading[i].defName} ({allDefsListForReading[i].label}), isLethal {(pawn.RaceProps.IsFlesh ? pawnCapacityDef.lethalFlesh : pawnCapacityDef.lethalMechanoids)}");
                // Если для текущего вида пешки параметр смертельно важен, делаем для него расчет
                if (pawn.RaceProps.IsFlesh ? pawnCapacityDef.lethalFlesh : pawnCapacityDef.lethalMechanoids)
                {
                    vitalCapacitiesList.Add(pawnCapacityDef);
                }
            }
            Log.Message("=== Vital capacities list for " + pawn.LabelShort + " ===");
            for (int i = 0; i < vitalCapacitiesList.Count; i++)
            {
                Log.Message($"{i + 1}. {vitalCapacitiesList[i].defName} ({vitalCapacitiesList[i].label})");
            }
            foreach (var injury in injuries)
            {
                var part = injury.Part;
                Log.Message($"Part {part.LabelShort}, PartIsMissing: {pawn.health.hediffSet.PartIsMissing(part)} ");
                if (part == null || pawn.health.hediffSet.PartIsMissing(part))
                    continue;

                float partMaxHp = part.def.GetMaxHealth(pawn);
                Log.Message($"partMaxHp {partMaxHp}");
                if (partMaxHp <= 0f)
                    continue;

                float damageFactor = partMaxHp / injury.Severity;
                float capacityImpactWeight = 0f;

                foreach (var vitalCapacity in vitalCapacitiesList)
                {
                    capacityImpactWeight += BodyPartAffectsCapacity(vitalCapacity, part);
                    //if (BodyPartAffectsCapacity(vitalCapacity, part))
                    //{
                    //    Log.Message($"{part.LabelCap} влияет на {vitalCapacity.LabelCap}.");
                    //}

                    //var worker = vitalCapacity.Worker;
                    //var directImpact = vitalCapacity.bodyPartGroups?.Any(g => part.groups.Contains(g)) == true;
                    //if (directImpact)
                    //{
                    //    totalImpactWeight += 1.0f;
                    //    continue;
                    //}
                }
            }


            return closestToDeathInjury;
        }

        public static float BodyPartAffectsCapacity(PawnCapacityDef capacityDef, BodyPartRecord part)
        {
            var dict = GetTagsUsedByCapacityWithWeights(capacityDef);
            float partCapacityFactor = 0f;

            // Логируем все теги, если включена разработка или диагностика
            Log.Message($"Проверка части '{part.LabelCap}' на влияние на способность '{capacityDef.defName}'");

            if (dict == null || dict.Count == 0)
            {
                Log.Message("  Способность не использует BodyPartTagDef (tags == null или пусто).");
                return partCapacityFactor;
            }

            if (part.def.tags == null || part.def.tags.Count == 0)
            {
                Log.Message("  У части тела нет тегов (part.def.tags == null или пусто).");
                return partCapacityFactor;
            }

            foreach (var partTag in part.def.tags)
            {
                partCapacityFactor += dict.TryGetValue(partTag, out float weight) ? weight : 0f;
            }
            Log.Message($"'{part.LabelCap}', partCapacityFactor {partCapacityFactor}");

            return partCapacityFactor;

            //// Логируем теги, используемые способностью
            //Log.Message($"  Теги, используемые способностью: [{string.Join(", ", tags.Select(t => t.defName))}]");

            //// Логируем теги части тела
            //Log.Message($"  Теги части тела: [{string.Join(", ", part.def.tags.Select(t => t.defName))}]");

            //// Пересечение
            //var matchingTags = part.def.tags.Where(tag => tags.Contains(tag)).ToList();
            //Log.Message($"  Совпадающие теги: [{string.Join(", ", matchingTags.Select(t => t.defName))}]");

            //return matchingTags.Count > 0;
        }

        public static Dictionary<BodyPartTagDef, float> GetTagsUsedByCapacityWithWeights(PawnCapacityDef def)
        {
            var tags = new Dictionary<BodyPartTagDef, float>();

            switch (def.defName)
            {
                case "Consciousness":
                    tags[BodyPartTagDefOf.ConsciousnessSource] = 1.0f;
                    break;

                case "Breathing":
                    tags[BodyPartTagDefOf.BreathingSource] = 1.0f;
                    tags[BodyPartTagDefOf.BreathingPathway] = 0.5f;
                    tags[BodyPartTagDefOf.BreathingSourceCage] = 0.5f;
                    break;

                case "BloodPumping":
                    tags[BodyPartTagDefOf.BloodPumpingSource] = 1.0f;
                    break;

                case "BloodFiltration":
                    tags[BodyPartTagDefOf.BloodFiltrationKidney] = 0.7f;
                    tags[BodyPartTagDefOf.BloodFiltrationLiver] = 0.7f;
                    tags[BodyPartTagDefOf.BloodFiltrationSource] = 1.0f; // fallback
                    break;

                case "Metabolism":
                    tags[BodyPartTagDefOf.MetabolismSource] = 1.0f;
                    break;

                case "Hearing":
                    tags[BodyPartTagDefOf.HearingSource] = 0.75f;
                    break;

                case "Sight":
                    tags[BodyPartTagDefOf.SightSource] = PawnCapacityWorker_Sight.PartEfficiencySpecialWeight; // 0.75f
                    break;

                case "Talking":
                    tags[BodyPartTagDefOf.TalkingSource] = 1.0f;
                    tags[BodyPartTagDefOf.TalkingPathway] = 0.5f;
                    tags[BodyPartTagDefOf.Tongue] = 0.5f;
                    break;

                case "Eating":
                    tags[BodyPartTagDefOf.EatingSource] = 1.0f;
                    tags[BodyPartTagDefOf.EatingPathway] = 0.5f;
                    tags[BodyPartTagDefOf.Tongue] = 0.5f;
                    break;

                case "Manipulation":
                    tags[BodyPartTagDefOf.ManipulationLimbCore] = 1.0f;
                    tags[BodyPartTagDefOf.ManipulationLimbSegment] = 0.8f;
                    tags[BodyPartTagDefOf.ManipulationLimbDigit] = 0.5f;
                    break;

                case "Moving":
                    tags[BodyPartTagDefOf.MovingLimbCore] = 1.0f;
                    tags[BodyPartTagDefOf.MovingLimbSegment] = 0.8f;
                    tags[BodyPartTagDefOf.MovingLimbDigit] = 0.4f;
                    tags[BodyPartTagDefOf.Pelvis] = 0.5f;
                    tags[BodyPartTagDefOf.Spine] = 0.5f;
                    break;
            }

            return tags;
        }

    }
}