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

        // Патчим VEF только если мод активен
        if (ModsConfig.IsActive("OskarPotocki.VanillaFactionsExpanded.Core"))
        {
            PatchNamespace(harmony, "RegenerationUpgrade.VEFPatches");
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
