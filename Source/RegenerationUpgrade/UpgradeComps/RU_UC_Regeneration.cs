using MU;
using RegenerationUpgrade.Components;
using System.Collections.Generic;
using Verse;

namespace RegenerationUpgrade.UpgradeComps;

public class RU_UC_Regeneration : UpgradeComp
{
    public RU_UC_RegenerationProperties Props => (RU_UC_RegenerationProperties)props;

    private List<Hediff_Injury> injuries = new();

    public override void CompTick()
    {
        if (!Gen.IsHashIntervalTick(Mech, Props.rateInTicks, 1))
            return;

        if (Mech.health == null)
            return;
        Mech.health.hediffSet.GetHediffs(ref injuries, (Hediff_Injury h) => h.CanHealNaturally());
        if (injuries.Count <= 0)
            return;

        Hediff_Injury mostDangerousInjury = HealLogic_Component.GetMostDangerousInjury(injuries, true);
        Log.Message($"Самая опасная травма {mostDangerousInjury.Label} на {mostDangerousInjury.Part.Label}");
        if (mostDangerousInjury == null)
            return;

        mostDangerousInjury.Heal(Props.healAmount);
    }
}
