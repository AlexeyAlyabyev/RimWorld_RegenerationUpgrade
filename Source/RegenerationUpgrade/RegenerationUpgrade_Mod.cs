using HarmonyLib;
using Verse;

public class RegenerationUpgrade_Mod : Mod
{
    public RegenerationUpgrade_Mod(ModContentPack content)
        : base(content)
    {
        var harmony = new Harmony("AlexeyAlyabev.RegenerationUpgrade");

        // Основные патчи
        harmony.PatchAll();
        RegenerationUpgrade.Patches.HealthTickInterval_Patch.ApplyPatch(harmony);

        // Патчим VEF только если мод активен
        if (ModsConfig.IsActive("OskarPotocki.VanillaFactionsExpanded.Core"))
        {
            RegenerationUpgrade.VEFPatches.CompPostTickInterval_Patch.ApplyPatch(harmony);
            RegenerationUpgrade.VEFPatches.CompTickInterval_Patch.ApplyPatch(harmony);
            RegenerationUpgrade.VEFPatches.GetInjuries_Patch.ApplyPatch(harmony);
        }
    }
}
