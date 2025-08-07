using HarmonyLib;
using RimWorld;
using System.Reflection;
using Verse;

namespace RegenerationUpgrade.Patches
{
    [HarmonyPatch(typeof(HediffGiver_Bleeding), "OnIntervalPassed")]
    public static class HediffGiver_Bleeding_Patch
    {
        public const float BASE_RECOVERY_RATE       = -0.00033333333f;
        public const float MIN_BLOOD_REGEN_SCALE    = -0.99f;
        public const float MIN_FACTOR_VAL           = 1f;
        public const float BASE_HEAL_PER_DAY        = 8f;
        public const float FOOD_TO_BLOOD_MULTIPLIER = 4f;
        public const float TICKS_IN_DAY             = 60000f;

        private static bool? _vefModActive = null;
        private static bool VefModActive
        {
            get
            {
                if (!_vefModActive.HasValue)
                {
                    _vefModActive = ModsConfig.IsActive("OskarPotocki.VanillaFactionsExpanded.Core");
                }
                return _vefModActive.Value;
            }
        }

        [HarmonyPostfix]
        public static void ScaleBloodRecovery(Pawn pawn, Hediff cause, HediffGiver_Bleeding __instance)
        {
            // if bleed rate more then 10%, blood will not recover
            if (pawn.health.hediffSet.BleedRateTotal >= 0.1f)
                return;

            Hediff firstHediffOfDef = pawn.health.hediffSet.GetFirstHediffOfDef(__instance.hediff);
            if (firstHediffOfDef == null || firstHediffOfDef.Severity <= 0f)
                return;

            float bloodRegenScale = GetNaturalHealingFactor(pawn)
                * pawn.GetStatValue(StatDefOf.InjuryHealingFactor); // Множитель от скорости лечения ран

            var regenerationField = typeof(HediffStage).GetField("regeneration", BindingFlags.Public | BindingFlags.Instance);
            if (regenerationField != null) {
                bloodRegenScale *= GetRegenerationFactor(pawn);
            }


            if (VefModActive)
            {
                bloodRegenScale *= GetCompRegenerationFactor(pawn)
                * GetHediffCompRegenerationFactor(pawn);
            }

            // if pawn has standart healing factor, no need for further operations
            if (bloodRegenScale == MIN_FACTOR_VAL)
                return;

            bloodRegenScale = (bloodRegenScale < MIN_BLOOD_REGEN_SCALE) ? MIN_BLOOD_REGEN_SCALE : bloodRegenScale;

            // Учитываем базовое восставновление здоровья
            bloodRegenScale -= 1f;

            float severityOffset = BASE_RECOVERY_RATE * bloodRegenScale;

            // Если пешка зависима от еды, то для ускоренной регенрации будет расходоваться сытость
            if (bloodRegenScale > 0 && pawn.needs.food != null) {
                float metabolismFactor     = GetPawnMetabolismFactor(pawn);
                float needFoodToBloodRegen = -BASE_RECOVERY_RATE * FOOD_TO_BLOOD_MULTIPLIER * bloodRegenScale * metabolismFactor;

                if (pawn.needs.food.CurLevel >= needFoodToBloodRegen) {
                    pawn.needs.food.CurLevel -= needFoodToBloodRegen;
                    HealthUtility.AdjustSeverity(pawn, __instance.hediff, severityOffset);
                }

            } else
            {
                HealthUtility.AdjustSeverity(pawn, __instance.hediff, severityOffset);
            }
        }

        // Множитель заживления (стакается от значения всех состояний)
        private static float GetNaturalHealingFactor(Pawn pawn)
        {
            float factor = MIN_FACTOR_VAL;

            foreach (Hediff hediff in pawn.health.hediffSet.hediffs)
            {
                HediffStage stage = hediff.CurStage;
                if (stage != null && stage.naturalHealingFactor != -1f)
                {
                    factor *= stage.naturalHealingFactor;
                }
            }

            return factor;
        }

        // Множитель ускорения восполнения крови от regeneration (лечит 100 ОЗ в день)
        private static float GetRegenerationFactor(Pawn pawn)
        {
            float regeneration = 0f;
            float factor       = MIN_FACTOR_VAL;

            foreach (Hediff hediff in pawn.health.hediffSet.hediffs)
            {
                HediffStage stage = hediff.CurStage;
                if (stage != null && stage.regeneration > 0f) {
                    regeneration += stage.regeneration;
                }
            }

            if (regeneration != 0f)
            {
                if (pawn.GetPosture() != PawnPosture.Standing) {
                    float healPerDay          = BASE_HEAL_PER_DAY + 4f;
                    Building_Bed building_Bed = pawn.CurrentBed();

                    if (building_Bed != null) {
                        healPerDay += building_Bed.def.building.bed_healPerDay;
                    }

                    factor += (regeneration / BASE_HEAL_PER_DAY / pawn.HealthScale) * (healPerDay / BASE_HEAL_PER_DAY);
                } else {
                    factor += (regeneration / BASE_HEAL_PER_DAY / pawn.HealthScale);
                }
            }


            return factor;
        }

        // Множитель восстановления от встроенной в пешку регенерации VEF
        private static float GetCompRegenerationFactor(Pawn pawn)
        {
            float factor = MIN_FACTOR_VAL;

            ThingComp compNew = pawn.AllComps?.FirstOrDefault(c => c.GetType().FullName == "VEF.AnimalBehaviours.CompRegeneration");
            ThingComp compOld = pawn.AllComps?.FirstOrDefault(c => c.GetType().FullName == "AnimalBehaviours.CompRegeneration");
            ThingComp comp    = compNew ?? compOld;
            if (comp != null)
            {
                var props = comp.props;

                // Получаем свойства через рефлексию
                var rateInTicksField = props.GetType().GetField("rateInTicks", BindingFlags.Public | BindingFlags.Instance);
                var healAmountField  = props.GetType().GetField("healAmount",  BindingFlags.Public | BindingFlags.Instance);
                var needsSunField    = props.GetType().GetField("needsSun",    BindingFlags.Public | BindingFlags.Instance);
                var needsWaterField  = props.GetType().GetField("needsWater",  BindingFlags.Public | BindingFlags.Instance);

                if (rateInTicksField != null && healAmountField != null)
                {
                    int rateInTicks  = (int)rateInTicksField.GetValue(props);
                    float healAmount = (float)healAmountField.GetValue(props);

                    if (needsSunField != null && needsWaterField != null) {
                        bool needsSun    = (bool)needsSunField.GetValue(props);
                        bool needsWater  = (bool)needsWaterField.GetValue(props);

                        // Если не выполняются условия регенерации, не учитываем ее
                        if (!IsVefRegenerationAvailable(pawn, needsSun, needsWater))
                            return factor;
                    }

                    float healPerDay = (TICKS_IN_DAY / rateInTicks) * healAmount;

                    factor += healPerDay / BASE_HEAL_PER_DAY / pawn.HealthScale;
                }
            }

            return factor;
        }

        // Множитель ускорения восстановления от состояния регенерации VEF
        private static float GetHediffCompRegenerationFactor(Pawn pawn)
        {
            float factor = MIN_FACTOR_VAL;

            foreach (Hediff hediff in pawn.health.hediffSet.hediffs)
            {
                if (hediff is HediffWithComps hediffWithComps)
                {
                    foreach (HediffComp comp in hediffWithComps.comps)
                    {
                        if (comp.GetType().FullName == "VEF.AnimalBehaviours.HediffComp_Regeneration" || comp.GetType().FullName == "AnimalBehaviours.HediffComp_Regeneration")
                        {
                            var props = comp.props;

                            // Получаем свойства через рефлексию
                            var rateInTicksField = props.GetType().GetField("rateInTicks", BindingFlags.Public | BindingFlags.Instance);
                            var healAmountField  = props.GetType().GetField("healAmount",  BindingFlags.Public | BindingFlags.Instance);
                            var needsSunField    = props.GetType().GetField("needsSun",    BindingFlags.Public | BindingFlags.Instance);
                            var needsWaterField  = props.GetType().GetField("needsWater",  BindingFlags.Public | BindingFlags.Instance);

                            if (rateInTicksField != null && healAmountField != null && needsSunField != null && needsWaterField != null)
                            {
                                int rateInTicks  = (int)rateInTicksField.GetValue(props);
                                float healAmount = (float)healAmountField.GetValue(props);
                                bool needsSun    = (bool)needsSunField.GetValue(props);
                                bool needsWater  = (bool)needsWaterField.GetValue(props);

                                // Если не выполняются условия регенерации, не учитываем ее
                                if (!IsVefRegenerationAvailable(pawn, needsSun, needsWater))
                                    continue;

                                // Кол-во исцеления в день
                                float healPerDay = (TICKS_IN_DAY / rateInTicks) * healAmount;

                                factor += healPerDay / BASE_HEAL_PER_DAY / pawn.HealthScale;
                            }
                        }
                    }
                }
            }

            return factor;
        }

        // Доступны ли специфичные виды регенерации, которые зависят от наличия воды или солнца
        private static bool IsVefRegenerationAvailable(Pawn pawn, bool needsSun, bool needsWater)
        {
            if ((needsSun && (pawn.Map == null || !SanguophageUtility.InSunlight(pawn.Position, pawn.Map))) ||
                (needsWater && (pawn.Map == null || !GridsUtility.GetTerrain(pawn.Position, pawn.Map).IsWater)))
            {
                return false;
            }

            return true;
        }

        // Получить множитель эффективности метаболизма (возвращает отличные от 1f результаты только для пешек с генами)
        // TODO: Учитывать множитель от стата пищеварения пешки (если он больше 100%, тратим меньше еды, если меньше 100% тратим больше еды)
        private static float GetPawnMetabolismFactor(Pawn pawn)
        {
            float factor = MIN_FACTOR_VAL;

            if (ModsConfig.BiotechActive && pawn.genes != null) {
                int biostatMet = 0;
                foreach (Gene item in pawn.genes.GenesListForReading) {
                    if (!item.Overridden) {
                        biostatMet += item.def.biostatMet;
                    }
                }
                factor *= GeneTuning.MetabolismToFoodConsumptionFactorCurve.Evaluate(biostatMet);
            }

            return factor;
        }
    }
}