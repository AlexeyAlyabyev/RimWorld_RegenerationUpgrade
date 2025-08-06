using HarmonyLib;
using System;
using System.Reflection;
using Verse;
using System.Linq;

public class RegenerationUpgrade_Mod : Mod
{
    public RegenerationUpgrade_Mod(ModContentPack content)
        : base(content)
    {
        var harmony = new Harmony("AlexeyAlyabev.RegenerationUpgrade");

        // Патчим все из основного неймспейса
        PatchNamespace(harmony, "RegenerationUpgrade.Patches");
        RegenerationUpgrade.Patches.HealthTickInterval_Patch.ApplyPatch(harmony);

        // Патчим VEF только если мод активен
        if (ModsConfig.IsActive("OskarPotocki.VanillaFactionsExpanded.Core"))
        {
            //PatchNamespace(harmony, "RegenerationUpgrade.VEFPatches");
            RegenerationUpgrade.VEFPatches.CompPostTickInterval_Patch.ApplyPatch(harmony);
            RegenerationUpgrade.VEFPatches.CompTickInterval_Patch.ApplyPatch(harmony);
            RegenerationUpgrade.VEFPatches.GetInjuries_Patch.ApplyPatch(harmony);
        }
        //harmony.PatchAll();
    }


    private void PatchNamespace(Harmony harmony, string @namespace)
    {
        var types = Assembly.GetExecutingAssembly()
            .GetTypes()
            .Where(t => t.IsClass && t.Namespace == @namespace);

        foreach (var type in types)
        {
            try
            {
                harmony.CreateClassProcessor(type).Patch();
            }
            catch (Exception ex)
            {
                Log.Error($"[RegenerationUpgrade] Error while patching {type.FullName}: {ex}");
            }
        }
    }
}
