using System;
using HarmonyLib;
using JetBrains.Annotations;
using Submerged.Enums;
using UnityEngine;

namespace Submerged.Minigames.Patches;

[HarmonyPatch(typeof(NormalPlayerTask), nameof(NormalPlayerTask.Initialize))]
public static class NormalPlayerTaskInitializePatches
{
    [HarmonyPrefix]
    [UsedImplicitly]
    public static bool Prefix(NormalPlayerTask __instance)
    {
        if (__instance == null || __instance.gameObject == null) return true;
        
        __instance.Arrow = __instance.gameObject.GetComponentInChildren<ArrowBehaviour>(true);

        return true;
    }

    [HarmonyPostfix]
    [UsedImplicitly]
    public static void Postfix(NormalPlayerTask __instance)
    {
        if (__instance == null) return;
        
        if ((int)__instance.TaskType == (int)CustomTaskTypes.OxygenateSeaPlants)
        {
#if ANDROID
            // Use native standard Random engine for cross-platform IL2CPP binary performance
            int randomSeed = new System.Random().Next(0, int.MaxValue);
            __instance.Data = BitConverter.GetBytes(randomSeed);
#else 
            __instance.Data = BitConverter.GetBytes(UnityRandom.RandomRangeInt(0, int.MaxValue));
#endif
        }
    }
}
