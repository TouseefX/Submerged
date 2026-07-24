using System;
using HarmonyLib;
using Submerged.Enums;
using UnityEngine;

namespace Submerged.Minigames.Patches;

[HarmonyPatch(typeof(NormalPlayerTask), nameof(NormalPlayerTask.Initialize))]
public static class NormalPlayerTaskInitializePatches
{
    [HarmonyPrefix]
    [UnityEngine.Scripting.UsedImplicitly]
    public static bool Prefix(NormalPlayerTask __instance)
    {
        if (__instance == null || __instance.gameObject == null) return true;

        // Safely extract the tracking arrow using Unity's mobile-safe layout lookup
        __instance.Arrow = __instance.gameObject.GetComponentInChildren<ArrowBehaviour>(true);

        return true;
    }

    [HarmonyPostfix]
    [UnityEngine.Scripting.UsedImplicitly]
    public static void Postfix(NormalPlayerTask __instance)
    {
        if (__instance == null) return;

        // Custom task handling for Submerged sea plant oxygenation
        if ((int)__instance.TaskType == (int)CustomTaskTypes.OxygenateSeaPlants)
        {
#if ANDROID
            // Use native standard Random engine for cross-platform IL2CPP binary performance
            int randomSeed = new System.Random().Next(0, int.MaxValue);
            __instance.Data = BitConverter.GetBytes(randomSeed);
#else
            // Fallback configuration for PC standalone builds 
            __instance.Data = BitConverter.GetBytes(UnityRandom.RandomRangeInt(0, int.MaxValue));
#endif
        }
    }
}
