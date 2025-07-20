using HarmonyLib;
using Verse;

public class RegenerationUpgrade_Mod : Mod
{
    public RegenerationUpgrade_Mod(ModContentPack content)
        : base(content)
    {
        var harmony = new Harmony("AlexeyAlyabev.RegenerationUpgrade");
        harmony.PatchAll();
    }
}
