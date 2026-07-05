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

            // This is a full recode of WireMinigame: the game's native Update is broken, so we
            // run our own touch/mouse/gamepad logic (with the detach-reconnect cooldown) and
            // never hand control back to the original Update.
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
        private static bool   grabbedWasConnected = false;
        private static Camera cachedCamera;

        // Detach cooldown: after detaching from a right node, don't immediately
        // reconnect to that same node. Prevents the "detach -> snaps back" bug.
        private const float DetachCooldown       = 0.35f;
        private static float detachCooldownTimer = 0f;
        private static sbyte lastDetachedRightNode = -1;

        // ---- Controller (Xbox) support ----
        // Left stick = move cursor, hold X to grab/move a wire,
        // release over a right node to connect or over empty space to detach if connected.
        // The stick is read from the game's NATIVE Controller API (reliable on Android IL2CPP);
        // raw UnityEngine.Input is only used as a desktop fallback. Button = Joystick Button 2 (X).
        private const float ControllerSpeed     = 9f;
        private static Vector2 controllerCursor    = Vector2.zero;
        private static bool   controllerCursorInit = false;
        private static bool   controllerEngaged    = false;

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

            // Detach cooldown ticks down every frame.
            if (detachCooldownTimer > 0f)
                detachCooldownTimer -= Time.deltaTime;

            // ---- Controller (Xbox) input ----
            // Gamepad is routed through Rewired. We read it at the low level
            // (axis 0/1 = left stick, button 2 = Xbox X) so it works regardless of whether
            // Rewired feeds Unity's Input. Falls back to raw UnityEngine.Input if Rewired
            // isn't reachable. (AmongUs.Controller is only the TOUCH handler â€” no gamepad API,
            // and the game's native Update is intentionally replaced, so we self-contain it.)
            Vector2 stick = ReadGamepadStick(out bool xDown, out bool xUp);
            bool ctrlActivity = stick.sqrMagnitude > 1e-4f || xDown || xUp;

            bool touchActive = Input.touchCount > 0;
            bool mouseActive = Input.GetMouseButton(0) || Input.GetMouseButtonDown(0);

            if (touchActive)
            {
                controllerEngaged = false;
                Touch touch = Input.GetTouch(0);
                began = touch.phase == TouchPhase.Began;
                ended = touch.phase == TouchPhase.Ended || touch.phase == TouchPhase.Canceled;
                if (began || isDragging)
                    worldPos = cam.ScreenToWorldPoint(touch.position);
            }
            else if (mouseActive)
            {
                controllerEngaged = false;
                began = Input.GetMouseButtonDown(0);
                ended = Input.GetMouseButtonUp(0);
                if (began || isDragging)
                    worldPos = cam.ScreenToWorldPoint(Input.mousePosition);
            }
            else if (ctrlActivity || controllerEngaged)
            {
                controllerEngaged = true;

                if (!controllerCursorInit && leftNodes.Length > 0 && leftNodes[0] != null)
                {
                    controllerCursor     = leftNodes[0].transform.position;
                    controllerCursorInit = true;
                }

                // Free cursor driven by the left stick.
                controllerCursor += new Vector2(stick.x, stick.y) * ControllerSpeed * Time.deltaTime;
                worldPos = controllerCursor;

                began = xDown; // grab on X press
                ended  = xUp;  // drop on X release

                // Highlight the wire currently under the cursor (when not dragging).
                if (!isDragging)
                {
                    int hover = GetLeftWireAt(leftNodes, worldPos);
                    selectedWireIndex = hover;
                    if (hover >= 0 && instance.selectedWireUI != null && leftNodes[hover] != null)
                        instance.selectedWireUI.position = leftNodes[hover].transform.position;
                }
            }
            else
            {
                began = false;
                ended = false;
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
                        // Remember if this wire was already connected, so we can detach it later.
                        grabbedWasConnected = (i < instance.ActualWires.Length) && instance.ActualWires[i] >= 0;

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

                // Don't reconnect to the node we just detached from until the cooldown elapses.
                bool blocked = detachCooldownTimer > 0f && rightNode != null && rightNode.WireId == lastDetachedRightNode;

                if (rightNode != null && wire != null && !blocked)
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
                    // Released on empty space. If this wire was already connected,
                    // detach it so a wrong connection can be undone. Otherwise just
                    // cancel the drag (no connection was ever made).
                    if (grabbedWasConnected && selectedWireIndex < instance.ActualWires.Length)
                    {
                        // Capture the node we're leaving BEFORE clearing the wire.
                        lastDetachedRightNode = instance.ActualWires[selectedWireIndex];
                        instance.ActualWires[selectedWireIndex] = -1;
                        detachCooldownTimer = DetachCooldown;

                        if (wire != null)
                        {
                            wire.ResetLine(wire.BaseWorldPos, true);
                            if (wire.Liner != null)
                                wire.Liner.color = Color.white;
                        }
                    }
                    else if (wire != null)
                    {
                        wire.ResetLine(wire.BaseWorldPos, true);
                    }

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

        private static int GetLeftWireAt(Wire[] nodes, Vector2 pos)
        {
            if (nodes == null)
                return -1;

            for (int i = 0; i < nodes.Length; i++)
            {
                Wire node = nodes[i];
                if (node?.hitbox != null && node.hitbox.OverlapPoint(pos))
                    return i;
            }
            return -1;
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
            grabbedWasConnected = false;
            cachedCamera     = null; // force a fresh Camera.main lookup on next open
            detachCooldownTimer  = 0f;
            lastDetachedRightNode = -1;
            controllerCursor    = Vector2.zero;
            controllerCursorInit = false;
            controllerEngaged   = false;
        }

        private static Camera GetCamera()
        {
            if (cachedCamera == null)
                cachedCamera = Camera.main;
            return cachedCamera;
        }

        // Read the gamepad through Rewired's low-level API (no reliance on action names,
        // which are stripped from the IL2CPP dump). Axis 0/1 = left stick, button 2 = Xbox X.
        // Falls back to raw UnityEngine.Input if Rewired isn't reachable. Edge-detects the
        // X button so began/ended (grab/drop) fire once per press.
        private static bool prevXButton = false;
        private static Vector2 ReadGamepadStick(out bool xDown, out bool xUp)
        {
            xDown = false;
            xUp   = false;
            Vector2 stick = Vector2.zero;
            bool xNow = false;
            bool read  = false;

            try
            {
                var player = Rewired.ReInput.players.GetPlayer(0);
                if (player != null && player.controllers.Joysticks.Count > 0)
                {
                    var js = player.controllers.Joysticks[0];
                    stick = new Vector2(js.GetAxisValue(0), js.GetAxisValue(1));
                    xNow = js.GetButtonValue(2);
                    read  = true;
                }
            }
            catch { }

            if (!read)
            {
                stick = new Vector2(Input.GetAxis("Horizontal"), Input.GetAxis("Vertical"));
                xNow = Input.GetKey(KeyCode.JoystickButton2) || Input.GetKey(KeyCode.Joystick1Button2);
            }

            xDown = xNow && !prevXButton;
            xUp   = !xNow && prevXButton;
            prevXButton = xNow;
            return stick;
        }
    }
    #endif
}
