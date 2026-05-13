using HarmonyLib;
using UnityEngine;
using Submerged.Extensions;
using System;

namespace Submerged.BaseGame.Patches
{
    // ================== CLOSE PATCHES ==================
    [HarmonyPatch(typeof(Minigame), nameof(Minigame.Close), new Type[] { })]
    public static class WireMinigameClosePatch
    {
        public static void Prefix(Minigame __instance)
        {
            if (!(ShipStatus.Instance != null && ShipStatus.Instance.IsSubmerged())) return;
            if (__instance is not WireMinigame wire) return;

            CustomWireMinigame.ForceCleanup(wire);
            CustomWireMinigame.Reset();
        }
    }

    [HarmonyPatch(typeof(Minigame), nameof(Minigame.Close), new Type[] { typeof(bool) })]
    public static class WireMinigameCloseBoolPatch
    {
        public static void Prefix(Minigame __instance)
        {
            if (!(ShipStatus.Instance != null && ShipStatus.Instance.IsSubmerged())) return;
            if (__instance is not WireMinigame wire) return;

            CustomWireMinigame.ForceCleanup(wire);
            CustomWireMinigame.Reset();
        }
    }

    // ================== UPDATE PATCH ==================
    [HarmonyPatch(typeof(WireMinigame), nameof(WireMinigame.Update))]
    public static class WireMinigameUpdatePatch
    {
        [HarmonyPrefix]
        public static bool Prefix(WireMinigame __instance)
        {
            if (!(ShipStatus.Instance != null && ShipStatus.Instance.IsSubmerged())) 
                return true;

            if (!__instance.isActiveAndEnabled || __instance.amClosing != Minigame.CloseState.None)
            {
                CustomWireMinigame.ForceCleanup(__instance);
                CustomWireMinigame.Reset();
                return false;
            }

            CustomWireMinigame.EnsureSetup(__instance);
            CustomWireMinigame.UpdateAndroid(__instance);
            __instance.UpdateLights();

            return false;
        }
    }

    public static class CustomWireMinigame
    {
        private static int selectedWireIndex = -1;
        private static bool isDragging = false;
        private static bool isSetupDone = false;

        public static void EnsureSetup(WireMinigame instance)
        {
            if (isSetupDone) return;

            try
            {
                var task = instance.MyTask ?? instance.MyNormTask as PlayerTask;
                if (task != null)
                    instance.Begin(task);
            }
            catch { }

            if (instance.LeftNodes == null || instance.RightNodes == null) return;

            // Different colors + shapes (already working)
            for (int i = 0; i < instance.LeftNodes.Length; i++)
            {
                Wire leftWire = instance.LeftNodes[i];
                WireNode rightNode = i < instance.RightNodes.Length ? instance.RightNodes[i] : null;

                if (leftWire != null)
                {
                    Sprite symbol = instance.Symbols != null && instance.Symbols.Length > 0 
                        ? instance.Symbols[i % instance.Symbols.Length] : null;

                    Color color = WireMinigame.colors != null && WireMinigame.colors.Length > 0 
                        ? WireMinigame.colors[i % WireMinigame.colors.Length] : Color.white;

                    leftWire.SetColor(color, symbol);
                    rightNode?.SetColor(color, symbol);

                    leftWire.ResetLine(leftWire.BaseWorldPos, true);
                    if (leftWire.Liner != null)
                        leftWire.Liner.color = Color.white;
                }
            }

            ReRandomizeWires(instance);

            instance.myController = null;
            selectedWireIndex = -1;
            isDragging = false;

            if (instance.selectingWireGlyphs != null)
                foreach (var g in instance.selectingWireGlyphs) if (g != null) g.SetActive(true);

            if (instance.movingWireGlyphs != null)
                foreach (var g in instance.movingWireGlyphs) if (g != null) g.SetActive(false);

            isSetupDone = true;
        }

        private static void ReRandomizeWires(WireMinigame instance)
        {
            int count = instance.LeftNodes.Length;
            instance.ExpectedWires = new sbyte[count];
            instance.ActualWires = new sbyte[count];

            sbyte[] rightOrder = new sbyte[count];
            for (sbyte i = 0; i < count; i++) rightOrder[i] = i;

            for (int i = count - 1; i > 0; i--)
            {
                int j = UnityEngine.Random.Range(0, i + 1);
                (rightOrder[i], rightOrder[j]) = (rightOrder[j], rightOrder[i]);
            }

            for (int i = 0; i < count; i++)
            {
                instance.ExpectedWires[i] = rightOrder[i];
                instance.ActualWires[i] = -1;
            }
        }

        public static void UpdateAndroid(WireMinigame instance)
        {
            if (instance.LeftNodes == null || instance.RightNodes == null) return;

            Vector2 worldPos = Vector2.zero;
            bool began = false;
            bool ended = false;

            if (Input.touchCount > 0)
            {
                Touch touch = Input.GetTouch(0);
                worldPos = Camera.main.ScreenToWorldPoint(touch.position);
                began = touch.phase == TouchPhase.Began;
                ended = touch.phase == TouchPhase.Ended || touch.phase == TouchPhase.Canceled;
                isDragging = touch.phase == TouchPhase.Moved || touch.phase == TouchPhase.Stationary;
            }
            else
            {
                worldPos = Camera.main.ScreenToWorldPoint(Input.mousePosition);
                began = Input.GetMouseButtonDown(0);
                ended = Input.GetMouseButtonUp(0);
                isDragging = Input.GetMouseButton(0);
            }

            if (began)
            {
                selectedWireIndex = -1;
                for (int i = 0; i < instance.LeftNodes.Length; i++)
                {
                    Wire wire = instance.LeftNodes[i];
                    if (wire?.hitbox != null && wire.hitbox.OverlapPoint(worldPos))
                    {
                        selectedWireIndex = i;
                        isDragging = true;
                        if (instance.selectedWireUI != null)
                            instance.selectedWireUI.position = wire.transform.position;
                        break;
                    }
                }
            }

            if (isDragging && selectedWireIndex >= 0 && selectedWireIndex < instance.LeftNodes.Length)
            {
                Wire wire = instance.LeftNodes[selectedWireIndex];
                if (wire != null)
                {
                    wire.ResetLine(worldPos, false);   // Update line while dragging
                }

                if (ended)
                {
                    WireNode rightNode = GetRightNodeAt(instance, worldPos);

                    if (rightNode != null && wire != null)
                    {
                        wire.ConnectRight(rightNode);        // This is the important call

                        if (instance.WireSounds != null && instance.WireSounds.Length > 0)
                        {
                            int idx = UnityEngine.Random.Range(0, instance.WireSounds.Length);
                            SoundManager.Instance?.PlaySound(instance.WireSounds[idx], false);
                        }

                        CheckTask(instance);
                    }
                    else if (wire != null)
                    {
                        wire.ResetLine(wire.BaseWorldPos, true); // Reset on miss
                    }

                    selectedWireIndex = -1;
                    isDragging = false;
                }
            }
        }

        private static WireNode GetRightNodeAt(WireMinigame instance, Vector2 pos)
        {
            if (instance.RightNodes == null) return null;

            // Slightly larger hitbox for easier connection on Android
            foreach (var node in instance.RightNodes)
            {
                if (node?.hitbox != null)
                {
                    // Optional: Increase tolerance
                    if (node.hitbox.OverlapPoint(pos))
                        return node;
                }
            }
            return null;
        }

        private static void CheckTask(WireMinigame instance)
        {
            instance.CheckTask();

            if (instance.MyNormTask != null)
                instance.MyNormTask.NextStep();
            else if (instance.MyTask != null)
                instance.MyTask.Complete();

            instance.StartCoroutine(instance.CoStartClose());
        }

        public static void ForceCleanup(WireMinigame instance)
        {
            if (instance.LeftNodes != null)
            {
                foreach (var wire in instance.LeftNodes)
                {
                    if (wire != null)
                        wire.ResetLine(wire.BaseWorldPos, true);
                }
            }
        }

        public static void Reset()
        {
            selectedWireIndex = -1;
            isDragging = false;
            isSetupDone = false;
        }
    }
}
