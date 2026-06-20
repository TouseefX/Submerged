using HarmonyLib;
using Submerged.Extensions;
using Submerged.Map;
using UnityEngine;

namespace Submerged.Floors.Patches;

[HarmonyPatch]
public static class RoleTeleportFloorPatches
{
    [HarmonyPatch(typeof(CustomNetworkTransform), nameof(CustomNetworkTransform.SnapTo), typeof(Vector2), typeof(ushort))]
    [HarmonyPostfix]
    public static void SnapToSeqPatch(CustomNetworkTransform __instance, [HarmonyArgument(0)] Vector2 position)
        => UpdateFloorForSnap(__instance, position);

    [HarmonyPatch(typeof(CustomNetworkTransform), nameof(CustomNetworkTransform.SnapTo), typeof(Vector2))]
    [HarmonyPostfix]
    public static void SnapToPatch(CustomNetworkTransform __instance, [HarmonyArgument(0)] Vector2 position)
        => UpdateFloorForSnap(__instance, position);

    private static void UpdateFloorForSnap(CustomNetworkTransform cnt, Vector2 position)
    {
        if (!ShipStatus.Instance || !ShipStatus.Instance.IsSubmerged()) return;
        if (!SubmarineStatus.instance || SubmarinePlayerFloorSystem.Instance == null) return;

        PlayerControl player = cnt.myPlayer;
        if (!player || !player.AmOwner) return;

        bool destUpper = position.y > FloorHandler.FLOOR_CUTOFF;
        FloorHandler handler = FloorHandler.GetFloorHandler(player);
        if (!handler || handler.onUpper == destUpper) return;

        handler.RpcRequestChangeFloor(destUpper);
        handler.RegisterFloorOverride(destUpper);
    }
}
