using HarmonyLib;
using UnityEngine;
using Submerged.Extensions;
using System;

namespace Submerged.BaseGame.Patches
{
    #if ANDROID
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
        private static int  selectedWireIndex = -1;
        private static bool isDragging        = false;
        private static bool isSetupDone       = false;

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

            // Randomize FIRST so ExpectedWires is populated before color assignment.
            ReRandomizeWires(instance);

            // Assign colors using the shuffled mapping: left[i] and right[ExpectedWires[i]]
            // share the same color/symbol, making the right side appear visually scrambled.
            int colorCount  = WireMinigame.colors != null ? WireMinigame.colors.Length : 0;
            int symbolCount = instance.Symbols    != null ? instance.Symbols.Length    : 0;

            for (int i = 0; i < instance.LeftNodes.Length; i++)
            {
                Wire     leftWire  = instance.LeftNodes[i];
                int      rightIdx  = instance.ExpectedWires[i];
                WireNode rightNode = (rightIdx >= 0 && rightIdx < instance.RightNodes.Length)
                                     ? instance.RightNodes[rightIdx]
                                     : null;

                Color  color  = colorCount  > 0 ? WireMinigame.colors[i % colorCount]  : Color.white;
                Sprite symbol = symbolCount > 0 ? instance.Symbols[i   % symbolCount]  : null;

                leftWire?.SetColor(color, symbol);
                rightNode?.SetColor(color, symbol);

                if (leftWire != null)
                {
                    leftWire.ResetLine(leftWire.BaseWorldPos, true);
                    if (leftWire.Liner != null)
                        leftWire.Liner.color = Color.white;
                }
            }

            instance.myController = null;
            selectedWireIndex     = -1;
            isDragging            = false;

            if (instance.selectingWireGlyphs != null)
                foreach (var g in instance.selectingWireGlyphs) if (g != null) g.SetActive(true);

            if (instance.movingWireGlyphs != null)
                foreach (var g in instance.movingWireGlyphs) if (g != null) g.SetActive(false);

            isSetupDone = true;
        }

        private static void ReRandomizeWires(WireMinigame instance)
        {
            int count = instance.LeftNodes.Length;
            if (count == 0) return;

            instance.ExpectedWires = new sbyte[count];
            instance.ActualWires   = new sbyte[count];

            sbyte[] rightOrder = new sbyte[count];
            for (sbyte i = 0; i < count; i++) rightOrder[i] = i;

            // Fisher-Yates shuffle
            for (int i = count - 1; i > 0; i--)
            {
                int j = UnityEngine.Random.Range(0, i + 1);
                (rightOrder[i], rightOrder[j]) = (rightOrder[j], rightOrder[i]);
            }

            for (int i = 0; i < count; i++)
            {
                instance.ExpectedWires[i] = rightOrder[i];
                instance.ActualWires[i]   = -1;
            }
        }

        public static void UpdateAndroid(WireMinigame instance)
        {
            if (instance.LeftNodes == null || instance.RightNodes == null) return;

            Vector2 worldPos = Vector2.zero;
            bool    began    = false;
            bool    ended    = false;

            // Read began/ended as one-shot events but do NOT overwrite the isDragging
            // state field from Input.GetMouseButton.  GetMouseButton returns false on the
            // very frame the button is released, so overwriting isDragging with it caused
            // the if (isDragging) block to be skipped on every release – wires phased through.
            if (Input.touchCount > 0)
            {
                Touch touch = Input.GetTouch(0);
                worldPos = Camera.main.ScreenToWorldPoint(touch.position);
                began    = touch.phase == TouchPhase.Began;
                ended    = touch.phase == TouchPhase.Ended || touch.phase == TouchPhase.Canceled;
            }
            else
            {
                worldPos = Camera.main.ScreenToWorldPoint(Input.mousePosition);
                began    = Input.GetMouseButtonDown(0);
                ended    = Input.GetMouseButtonUp(0);
            }

            // ---- Began: try to pick up a left wire ----
            if (began)
            {
                selectedWireIndex = -1;
                isDragging        = false;

                for (int i = 0; i < instance.LeftNodes.Length; i++)
                {
                    Wire wire = instance.LeftNodes[i];
                    if (wire?.hitbox != null && wire.hitbox.OverlapPoint(worldPos))
                    {
                        selectedWireIndex = i;
                        isDragging        = true;

                        if (instance.selectedWireUI != null)
                            instance.selectedWireUI.position = wire.transform.position;

                        break;
                    }
                }
            }

            // ---- While dragging: stretch line; attempt connection on hover OR release ----
            if (isDragging && selectedWireIndex >= 0 && selectedWireIndex < instance.LeftNodes.Length)
            {
                Wire wire = instance.LeftNodes[selectedWireIndex];

                if (wire != null)
                    wire.ResetLine(worldPos, false);
                
                WireNode rightNode = GetRightNodeAt(instance, worldPos);

                if (rightNode != null && wire != null)
                {
                    wire.ConnectRight(rightNode);

                    if (selectedWireIndex < instance.ActualWires.Length)
                        instance.ActualWires[selectedWireIndex] = rightNode.WireId;

                    if (instance.WireSounds != null && instance.WireSounds.Length > 0)
                    {
                        int idx = UnityEngine.Random.Range(0, instance.WireSounds.Length);
                        SoundManager.Instance?.PlaySound(instance.WireSounds[idx], false);
                    }
                    
                    wire.ResetLine(rightNode.transform.position, true);

                    selectedWireIndex = -1;
                    isDragging        = false;

                    TryCompleteTask(instance);
                }
                else if (ended)
                {
                    if (wire != null)
                        wire.ResetLine(wire.BaseWorldPos, true);

                    selectedWireIndex = -1;
                    isDragging        = false;
                }
            }
        }

        private static WireNode GetRightNodeAt(WireMinigame instance, Vector2 pos)
        {
            if (instance.RightNodes == null) return null;

            foreach (var node in instance.RightNodes)
            {
                if (node?.hitbox != null && node.hitbox.OverlapPoint(pos))
                    return node;
            }
            return null;
        }

        /// <summary>
        /// Only completes and closes the minigame once every wire matches its expected target.
        /// </summary>
        private static void TryCompleteTask(WireMinigame instance)
        {
            for (int i = 0; i < instance.ActualWires.Length; i++)
            {
                if (instance.ActualWires[i] != instance.ExpectedWires[i])
                    return;   // Still wires left to connect.
            }

            // CheckTask() already calls NextStep/Complete internally when all wires match.
            // Calling NextStep/Complete again here would complete the task twice.
            instance.CheckTask();
            instance.StartCoroutine(instance.CoStartClose());
        }

        public static void ForceCleanup(WireMinigame instance)
        {
            if (instance.LeftNodes != null)
            {
                foreach (var wire in instance.LeftNodes)
                    if (wire != null) wire.ResetLine(wire.BaseWorldPos, true);
            }
        }

        public static void Reset()
        {
            selectedWireIndex = -1;
            isDragging        = false;
            isSetupDone       = false;
        }
    }
    #endif
}
