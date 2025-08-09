using RegenerationUpgrade.Components;
using System.Collections.Generic;
using Verse;

namespace RegenerationUpgrade.HediffComps;

public class RU_HC_Regeneration : HediffComp
{
    public RU_HC_RegenerationProperties Props => (RU_HC_RegenerationProperties)props;

    private List<Hediff_Injury> injuries = new();

    public override void CompPostTickInterval(ref float severityAdjustment, int delta)
    {
        if (!Gen.IsHashIntervalTick(Pawn, Props.rateInTicks, delta))
            return;

        if (Pawn.health == null)
            return;

        Pawn.health.hediffSet.GetHediffs(ref injuries, (Hediff_Injury h) => h.CanHealNaturally());
        if (injuries.Count <= 0)
            return;

        Hediff_Injury mostDangerousInjury = HealLogic_Component.GetMostDangerousInjury(injuries);
        if (mostDangerousInjury == null)
            return;

        mostDangerousInjury.Heal(Props.healAmount);
    }
}
