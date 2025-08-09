using MU;
using Verse;

namespace RegenerationUpgrade.UpgradeComps;

public class RU_UC_RegenerationProperties : UpgradeCompProperties
{
    public int rateInTicks = 1000;

    public float healAmount = 0.1f;

    public RU_UC_RegenerationProperties()
    {
        base.compClass = typeof(RU_UC_Regeneration);
    }
}