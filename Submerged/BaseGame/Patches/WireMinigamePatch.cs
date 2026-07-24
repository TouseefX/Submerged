using HarmonyLib;
using UnityEngine;
using Submerged.Extensions;
using System;

namespace Submerged.BaseGame.Patches
{
    #if ANDROID
    
    // Patching the Close method. 
    // Both overloads basically do the same thing, so I'm just pointing them both to the same cleanup method.
    [HarmonyPatch(typeof(Minigame), nameof(Minigame.Close), new Type[] { })]
    public static class WireClosePatch
    {
        public static void Prefix(Minigame __instance) => WirePatchHelper.Cleanup(__instance);
    }

    [HarmonyPatch(typeof(Minigame), nameof(Minigame.Close), new Type[] { typeof(bool) })]
    public static class WireCloseBoolPatch
    {
        public static void Prefix(Minigame __instance) => WirePatchHelper.Cleanup(__instance);
    }

    [HarmonyPatch(typeof(WireMinigame), nameof(WireMinigame.Update))]
    public static class WireUpdatePatch
    {
        [HarmonyPrefix]
        public static bool Prefix(WireMinigame __instance)
        {
            // If we aren't submerged, just let the game handle it. 
            // Only need this patch for the specific Android submerged mode.
            if (!(ShipStatus.Instance != null && ShipStatus.Instance.IsSubmerged()))
                return true;

            // If the game is closing, clean up our mess.
            if (!__instance.isActiveAndEnabled || __instance.amClosing != Minigame.CloseState.None)
            {
                WirePatchHelper.Cleanup(__instance);
                return false;
            }

            // The original Update is totally broken on Android, so I'm overriding it entirely.
            WirePatchHelper.EnsureSetup(__instance);
            WirePatchHelper.UpdateAndroid(__instance);
            __instance.UpdateLights();

            return false;
        }
    }

    public static class WirePatchHelper
    {
        private static int selectedIdx = -1;
        private static bool isDragging = false;
        private static bool setupDone = false;
        private static Camera cam; // Cached so we don't call Camera.main every frame

        public static void Cleanup(Minigame minigame)
        {
            if (ShipStatus.Instance != null && ShipStatus.Instance.IsSubmerged() && minigame is WireMinigame wire)
                ForceCleanup(wire);

            Reset();
        }

        public static void EnsureSetup(WireMinigame instance)
        {
            if (setupDone) return;

            // Note: I remember that calling Begin() with a null task can cause issues, 
            // so we check for the task first.
            var task = instance.MyTask ?? (instance.MyNormTask as PlayerTask);
            if (task != null) instance.Begin(task);

            // Sometimes the nodes aren't ready yet, so just bail and try again next frame
            if (instance.LeftNodes == null || instance.RightNodes == null) return;

            ReRandomizeWires(instance);

            var colors = WireMinigame.colors;
            var symbols = instance.Symbols;
            var leftNodes = instance.LeftNodes;
            var rightNodes = instance.RightNodes;
            var expected = instance.ExpectedWires;

            // Loop through and set up the wire colors/symbols
            for (int i = 0; i < leftNodes.Length; i++)
            {
                Wire lWire = leftNodes[i];
                int rIdx = expected[i];
                WireNode rNode = (rIdx >= 0 && rIdx < rightNodes.Length) ? rightNodes[rIdx] : null;

                Color c = (colors != null && colors.Length > 0) ? colors[i % colors.Length] : Color.white;
                Sprite s = (symbols != null && symbols.Length > 0) ? symbols[i % symbols.Length] : null;

                lWire?.SetColor(c, s);
                rNode?.SetColor(c, s);

                if (lWire != null)
                {
                    lWire.ResetLine(lWire.BaseWorldPos, true);
                    if (lWire.Liner != null) lWire.Liner.color = Color.white;
                }
            }

            instance.myController = null;
            selectedIdx = -1;
            isDragging = false;

            // Resetting the glyphs
            if (instance.selectingWireGlyphs != null)
                foreach (var g in instance.selectingWireGlyphs) g?.SetActive(true);

            if (instance.movingWireGlyphs != null)
                foreach (var g in instance.movingWireGlyphs) g?.SetActive(false);

            setupDone = true;
        }

        private static void ReRandomizeWires(WireMinigame instance)
        {
            int count = instance.LeftNodes.Length;
            if (count == 0) return;

            instance.ExpectedWires = new sbyte[count];
            instance.ActualWires = new sbyte[count];

            sbyte[] rightOrder = new sbyte[count];
            for (sbyte i = 0; i < count; i++) rightOrder[i] = i;

            // Standard shuffle
            for (int i = count - 1; i > 0; i--)
            {
                int j = UnityEngine.Random.Range(0, i + 1);
                sbyte temp = rightOrder[i];
                rightOrder[i] = rightOrder[j];
                rightOrder[j] = temp;
            }

            for (int i = 0; i < count; i++)
            {
                instance.ExpectedWires[i] = rightOrder[i];
                instance.ActualWires[i] = -1;
            }
        }

        public static void UpdateAndroid(WireMinigame instance)
        {
            var leftNodes = instance.LeftNodes;
            var rightNodes = instance.RightNodes;
            if (leftNodes == null || rightNodes == null) return;

            if (cam == null) cam = Camera.main;
            if (cam == null) return;

            bool began = false;
            bool ended = false;
            Vector2 worldPos = default;

            // Handling both touch and mouse because sometimes people use emulators or weird setups
            if (Input.touchCount > 0)
            {
                Touch touch = Input.GetTouch(0);
                began = touch.phase == TouchPhase.Began;
                ended = touch.phase == TouchPhase.Ended || touch.phase == TouchPhase.Canceled;
                if (began || isDragging) worldPos = cam.ScreenToWorldPoint(touch.position);
            }
            else if (Input.GetMouseButton(0) || Input.GetMouseButtonDown(0))
            {
                began = Input.GetMouseButtonDown(0);
                ended = Input.GetMouseButtonUp(0);
                if (began || isDragging) worldPos = cam.ScreenToWorldPoint(Input.mousePosition);
            }

            // --- Logic for grabbing a wire ---
            if (began)
            {
                selectedIdx = -1;
                isDragging = false;

                for (int i = 0; i < leftNodes.Length; i++)
                {
                    Wire wire = leftNodes[i];
                    if (wire?.hitbox != null && wire.hitbox.OverlapPoint(worldPos))
                    {
                        selectedIdx = i;
                        isDragging = true;

                        // If it's already connected, let's detach it so we can re-drag
                        if (i < instance.ActualWires.Length && instance.ActualWires[i] >= 0)
                        {
                            instance.ActualWires[i] = -1;
                            wire.ResetLine(wire.BaseWorldPos, true);
                            if (wire.Liner != null) wire.Liner.color = Color.white;
                        }

                        if (instance.selectedWireUI != null)
                            instance.selectedWireUI.position = wire.transform.position;

                        break;
                    }
                }

                // If we didn't grab a wire, maybe we're tapping a connected one to disconnect it?
                if (!isDragging && instance.ActualWires != null)
                {
                    WireNode rNode = GetRightNodeAt(rightNodes, worldPos);
                    if (rNode != null)
                    {
                        for (int i = 0; i < instance.ActualWires.Length; i++)
                        {
                            if (instance.ActualWires[i] == rNode.WireId)
                            {
                                instance.ActualWires[i] = -1;
                                if (i < leftNodes.Length && leftNodes[i] != null)
                                {
                                    leftNodes[i].ResetLine(leftNodes[i].BaseWorldPos, true);
                                    if (leftNodes[i].Liner != null) leftNodes[i].Liner.color = Color.white;
                                }
                                break;
                            }
                        }
                    }
                }
            }

            // --- Dragging logic ---
            if (isDragging && selectedIdx >= 0 && selectedIdx < leftNodes.Length)
            {
                Wire wire = leftNodes[selectedIdx];
                WireNode rightNode = GetRightNodeAt(rightNodes, worldPos);

                if (rightNode != null && wire != null)
                {
                    // Snap to node
                    if (selectedIdx >= instance.ActualWires.Length || instance.ActualWires[selectedIdx] != rightNode.WireId)
                    {
                        wire.ConnectRight(rightNode);
                        if (selectedIdx < instance.ActualWires.Length)
                            instance.ActualWires[selectedIdx] = rightNode.WireId;

                        if (instance.WireSounds != null && instance.WireSounds.Length > 0)
                        {
                            SoundManager.Instance?.PlaySound(instance.WireSounds[UnityEngine.Random.Range(0, instance.WireSounds.Length)], false);
                        }

                        CheckCompletion(instance);
                    }
                }
                else
                {
                    // Just following the cursor
                    if (wire != null) wire.ResetLine(worldPos, false);
                    if (selectedIdx < instance.ActualWires.Length && instance.ActualWires[selectedIdx] != -1)
                        instance.ActualWires[selectedIdx] = -1;
                }

                if (ended)
                {
                    if (rightNode == null && wire != null)
                    {
                        wire.ResetLine(wire.BaseWorldPos, true);
                        if (wire.Liner != null) wire.Liner.color = Color.white;
                    }
                    selectedIdx = -1;
                    isDragging = false;
                }
            }
        }

        private static WireNode GetRightNodeAt(WireNode[] nodes, Vector2 pos)
        {
            if (nodes == null) return null;
            foreach (var node in nodes)
            {
                if (node?.hitbox != null && node.hitbox.OverlapPoint(pos)) return node;
            }
            return null;
        }

        private static void CheckCompletion(WireMinigame instance)
        {
            for (int i = 0; i < instance.ActualWires.Length; i++)
            {
                if (instance.ActualWires[i] != instance.ExpectedWires[i]) return;
            }

            instance.CheckTask();
            instance.StartCoroutine(instance.CoStartClose());
        }

        public static void ForceCleanup(WireMinigame instance)
        {
            if (instance.LeftNodes == null) return;
            foreach (var wire in instance.LeftNodes)
            {
                if (wire != null) wire.ResetLine(wire.BaseWorldPos, true);
            }
        }

        public static void Reset()
        {
            selectedIdx = -1;
            isDragging = false;
            setupDone = false;
            cam = null; 
        }
    }
    #endif
}
