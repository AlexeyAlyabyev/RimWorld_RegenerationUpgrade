using Verse;

namespace RegenerationUpgrade.HediffComps;

public class RU_HC_RegenerationProperties : HediffCompProperties
{
    public int rateInTicks = 1000;

    public float healAmount = 0.1f;

    public RU_HC_RegenerationProperties()
    {
        base.compClass = typeof(RU_HC_Regeneration);
    }
}