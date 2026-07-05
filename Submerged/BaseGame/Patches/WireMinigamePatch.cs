using HarmonyLib;
using UnityEngine;
using Submerged.Extensions;
using System;

namespace Submerged.BaseGame.Patches
{
    #if ANDROID
    // ================== CLOSE PATCHES ==================
    // Both Close overloads share the same logic via CustomWireMinigame.Cleanup,
    // so the bodies aren't duplicated.
    [HarmonyPatch(typeof(Minigame), nameof(Minigame.Close), new Type[] { })]
    public static class WireMinigameClosePatch
    {
        public static void Prefix(Minigame __instance) => CustomWireMinigame.Cleanup(__instance);
    }

    [HarmonyPatch(typeof(Minigame), nameof(Minigame.Close), new Type[] { typeof(bool) })]
    public static class WireMinigameCloseBoolPatch
    {
        public static void Prefix(Minigame __instance) => CustomWireMinigame.Cleanup(__instance);
    }

    // ================== UPDATE PATCH ==================
    [HarmonyPatch(typeof(WireMinigame), nameof(WireMinigame.Update))]
    public static class WireMinigameUpdatePatch
    {
        [HarmonyPrefix]
        public static bool Prefix(WireMinigame __instance)
        {
            // Not in submerged mode -> let the original (broken on Android) Update run.
            if (!(ShipStatus.Instance != null && ShipStatus.Instance.IsSubmerged()))
                return true;

            // Minigame is closing / inactive -> tear down our state and skip the original.
            if (!__instance.isActiveAndEnabled || __instance.amClosing != Minigame.CloseState.None)
            {
                CustomWireMinigame.Cleanup(__instance);
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
        private static int    selectedWireIndex = -1;
        private static bool   isDragging        = false;
        private static bool   isSetupDone       = false;
        private static Camera cachedCamera;

        // ---- Close-path entry point (used by the Close patches) ----
        public static void Cleanup(Minigame minigame)
        {
            if (ShipStatus.Instance != null && ShipStatus.Instance.IsSubmerged() && minigame is WireMinigame wire)
                ForceCleanup(wire);

            Reset();
        }

        // ================== SETUP ==================
        public static void EnsureSetup(WireMinigame instance)
        {
            if (isSetupDone)
                return;

            // Begin(task) is the game's own method; guarding the task with null checks
            // avoids the expensive IL2CPP try/catch that was here before.
            var task = instance.MyTask ?? (instance.MyNormTask as PlayerTask);
            if (task != null)
                instance.Begin(task);

            // Nodes may not be initialised yet this frame -> retry next frame.
            if (instance.LeftNodes == null || instance.RightNodes == null)
                return;

            ReRandomizeWires(instance);

            Color[]    colors    = WireMinigame.colors;
            Sprite[]   symbols   = instance.Symbols;
            Wire[]     leftNodes = instance.LeftNodes;
            WireNode[] rightNodes = instance.RightNodes;
            sbyte[]    expected  = instance.ExpectedWires;

            int colorCount  = colors  != null ? colors.Length  : 0;
            int symbolCount = symbols != null ? symbols.Length : 0;

            for (int i = 0; i < leftNodes.Length; i++)
            {
                Wire      leftWire  = leftNodes[i];
                int       rightIdx  = expected[i];
                WireNode  rightNode = (rightIdx >= 0 && rightIdx < rightNodes.Length) ? rightNodes[rightIdx] : null;

                Color  color  = colorCount  > 0 ? colors[i  % colorCount]  : Color.white;
                Sprite symbol = symbolCount > 0 ? symbols[i % symbolCount] : null;

                leftWire?.SetColor(color, symbol);
                rightNode?.SetColor(color, symbol);

                if (leftWire != null)
                {
                    leftWire.ResetLine(leftWire.BaseWorldPos, true);
                    if (leftWire.Liner != null)
                        leftWire.Liner.color = Color.white;
                }
            }

            instance.myController  = null;
            selectedWireIndex      = -1;
            isDragging             = false;

            if (instance.selectingWireGlyphs != null)
                for (int i = 0; i < instance.selectingWireGlyphs.Length; i++)
                    instance.selectingWireGlyphs[i]?.SetActive(true);

            if (instance.movingWireGlyphs != null)
                for (int i = 0; i < instance.movingWireGlyphs.Length; i++)
                    instance.movingWireGlyphs[i]?.SetActive(false);

            isSetupDone = true;
        }

        private static void ReRandomizeWires(WireMinigame instance)
        {
            int count = instance.LeftNodes.Length;
            if (count == 0)
                return;

            instance.ExpectedWires = new sbyte[count];
            instance.ActualWires   = new sbyte[count];

            sbyte[] rightOrder = new sbyte[count];
            for (sbyte i = 0; i < count; i++)
                rightOrder[i] = i;

            // Fisher-Yates shuffle
            for (int i = count - 1; i > 0; i--)
            {
                int j = UnityEngine.Random.Range(0, i + 1);
                (rightOrder[i], rightOrder[j]) = (rightOrder[j], rightOrder[i]);
            }

            for (int i = 0; i < count; i++)
            {
                instance.ExpectedWires[i] = rightOrder[i];
                instance.ActualWires[i]    = -1;
            }
        }

        // ================== PER-FRAME UPDATE ==================
        public static void UpdateAndroid(WireMinigame instance)
        {
            Wire[]     leftNodes  = instance.LeftNodes;
            WireNode[] rightNodes = instance.RightNodes;
            if (leftNodes == null || rightNodes == null)
                return;

            // Cache the main camera ONCE. Camera.main is a tagged FindObjectOfType and is
            // extremely costly to call every frame on IL2CPP. The cached reference auto
            // invalidates on scene change because UnityObject == null is true for destroyed cams.
            Camera cam = GetCamera();
            if (cam == null)
                return;

            bool began = false;
            bool ended = false;
            Vector2 worldPos = default;

            // Read began/ended as one-shot events but do NOT overwrite the isDragging state
            // from Input.GetMouseButton (it returns false on the release frame, which previously
            // made the dragging block skip and let wires phase through).
            if (Input.touchCount > 0)
            {
                Touch touch = Input.GetTouch(0);
                began = touch.phase == TouchPhase.Began;
                ended = touch.phase == TouchPhase.Ended || touch.phase == TouchPhase.Canceled;
                if (began || isDragging)
                    worldPos = cam.ScreenToWorldPoint(touch.position);
            }
            else
            {
                began = Input.GetMouseButtonDown(0);
                ended = Input.GetMouseButtonUp(0);
                if (began || isDragging)
                    worldPos = cam.ScreenToWorldPoint(Input.mousePosition);
            }

            // ---- Began: try to pick up a left wire ----
            if (began)
            {
                selectedWireIndex = -1;
                isDragging        = false;

                for (int i = 0; i < leftNodes.Length; i++)
                {
                    Wire wire = leftNodes[i];
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

            // ---- While dragging: stretch line; connect on hover or release ----
            if (isDragging && selectedWireIndex >= 0 && selectedWireIndex < leftNodes.Length)
            {
                Wire wire = leftNodes[selectedWireIndex];
                if (wire != null)
                    wire.ResetLine(worldPos, false);

                WireNode rightNode = GetRightNodeAt(rightNodes, worldPos);

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

        private static WireNode GetRightNodeAt(WireNode[] nodes, Vector2 pos)
        {
            if (nodes == null)
                return null;

            for (int i = 0; i < nodes.Length; i++)
            {
                WireNode node = nodes[i];
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
                    return; // Still wires left to connect.
            }

            // CheckTask() already calls NextStep/Complete internally when all wires match,
            // so calling them again here would complete the task twice.
            instance.CheckTask();
            instance.StartCoroutine(instance.CoStartClose());
        }

        public static void ForceCleanup(WireMinigame instance)
        {
            Wire[] leftNodes = instance.LeftNodes;
            if (leftNodes == null)
                return;

            for (int i = 0; i < leftNodes.Length; i++)
            {
                Wire wire = leftNodes[i];
                if (wire != null)
                    wire.ResetLine(wire.BaseWorldPos, true);
            }
        }

        public static void Reset()
        {
            selectedWireIndex = -1;
            isDragging        = false;
            isSetupDone       = false;
            cachedCamera     = null; // force a fresh Camera.main lookup on next open
        }

        private static Camera GetCamera()
        {
            if (cachedCamera == null)
                cachedCamera = Camera.main;
            return cachedCamera;
        }
    }
    #endif
}
