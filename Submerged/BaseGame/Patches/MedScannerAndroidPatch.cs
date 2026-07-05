using System;
using System.Linq;
using System.Reflection;
using HarmonyLib;
using UnityEngine;
using Submerged.Enums;
using Submerged.Floors;
using Submerged.SpawnIn;
using Submerged.SpawnIn.Enums;
using Submerged.Extensions;
using TMPro;
using Hazel;

public static class MedScanMapChecker
{
    public static bool IsSubmerged()
    {
        return ShipStatus.Instance != null && ShipStatus.Instance.IsSubmerged();
    }
}

[HarmonyPatch(typeof(PlayerControl), nameof(PlayerControl.HandleRpc))]
public static class HandleScanSoundRpcPatch
{
    private static AudioClip cachedScanSound;

    public static bool Prefix(PlayerControl __instance, byte callId, MessageReader reader)
    {
        if (!MedScanMapChecker.IsSubmerged())
            return true;

        if (callId != 215)
            return true;

        byte scanningPlayerId = reader.ReadByte();

        var gameDataPlayer = GameData.Instance.GetPlayerById(scanningPlayerId);
        if (gameDataPlayer?.Object == null)
            return false;

        PlayerControl scanningPlayer = gameDataPlayer.Object;
        PlayerControl localPlayer = PlayerControl.LocalPlayer;

        if (localPlayer == null)
            return false;

        var scanningHandler = FloorHandler.GetFloorHandler(scanningPlayer);
        var localHandler = FloorHandler.GetFloorHandler(localPlayer);

        if (scanningHandler == null || localHandler == null ||
            scanningHandler.onUpper != localHandler.onUpper)
            return false;

        // FindObjectsOfType is expensive; only do it once and only when we actually
        // need the sound (callId == 215 already gated above).
        if (cachedScanSound == null)
        {
            var minigame = UnityEngine.Object.FindObjectsOfType<MedScanMinigame>(true).FirstOrDefault();
            if (minigame != null)
                cachedScanSound = minigame.ScanSound;
        }

        if (cachedScanSound == null || SoundManager.Instance == null)
            return false;

        float distance = Vector3.Distance(localPlayer.transform.position, scanningPlayer.transform.position);
        const float maxHearingDistance = 14f;

        if (distance > maxHearingDistance)
            return false;

        float volumeModifier = 1f - (distance / maxHearingDistance);
        float finalVolume = Mathf.Lerp(0.25f, 0.95f, volumeModifier);

        SoundManager.Instance.PlaySound(cachedScanSound, false, finalVolume);

        return false;
    }
}

#if ANDROID
[HarmonyPatch(typeof(ShipStatus), nameof(ShipStatus.Start))]
public static class ShipStatusStartPatch
{
    [HarmonyPostfix]
    public static void Postfix(ShipStatus __instance)
    {
        MedScanMinigamePatch.Reset();
    }
}

[HarmonyPatch(typeof(Minigame), nameof(Minigame.Close), new Type[] { })]
public static class MedScanClosePatch
{
    public static void Prefix(Minigame __instance)
    {
        if (!MedScanMapChecker.IsSubmerged()) return;
        if (__instance is not MedScanMinigame medScan) return;
        MedScanMinigamePatch.ForceCleanup(medScan);
        MedScanMinigamePatch.Reset();
    }
}

[HarmonyPatch(typeof(Minigame), nameof(Minigame.Close), new Type[] { typeof(bool) })]
public static class MedScanCloseBoolPatch
{
    public static void Prefix(Minigame __instance)
    {
        if (!MedScanMapChecker.IsSubmerged()) return;
        if (__instance is not MedScanMinigame medScan) return;
        MedScanMinigamePatch.ForceCleanup(medScan);
        MedScanMinigamePatch.Reset();
    }
}

[HarmonyPatch(typeof(MedScanMinigame), nameof(MedScanMinigame.FixedUpdate))]
public static class MedScanMinigamePatch
{
    private const float WalkSpeed   = 1.5f;
    private const float ArrivalDist = 0.15f;

    public static bool walkDone     = false;
    public static bool taskComplete = false;
    public static bool soundPlaying = false;
    public static bool scannerIsOn  = false;

    private static Vector3   targetPosition;
    private static bool      targetSet       = false;
    private static AudioClip cachedScanSound = null;

    // Typing variables
    private static float  typeTimer    = 0f;
    private static int    charIndex    = 0;
    private static string fullVitals   = "";
    private static bool   vitalsDone   = false;
    private static string cachedBloodType = "";
    private static float  typeInterval = 0.025f;

    // ---- Caches to avoid per-frame IL2CPP lookups ----
    private static TranslationController cachedTranslator;
    private static TextMeshPro          cachedStatusText;
    private static bool                 statusTextResolved;
    private static TextMeshPro          cachedVitalsText;
    private static bool                 vitalsTextResolved;
    private static string               lastStatusText;
    private static float                lastProgress = -1f;

    private static readonly string[] BloodTypes = { "A+", "A-", "B+", "B-", "AB+", "AB-", "O+", "O-" };

    private static TranslationController GetTranslator()
    {
        if (cachedTranslator == null)
            cachedTranslator = DestroyableSingleton<TranslationController>.Instance;
        return cachedTranslator;
    }

    private static string GetMedScanColorString(int colorId)
    {
        var translator = GetTranslator();
        return colorId switch
        {
            0  => translator.GetString(StringNames.ColorRed),
            1  => translator.GetString(StringNames.ColorBlue),
            2  => translator.GetString(StringNames.ColorGreen),
            3  => translator.GetString(StringNames.ColorPink),
            4  => translator.GetString(StringNames.ColorOrange),
            5  => translator.GetString(StringNames.ColorYellow),
            6  => translator.GetString(StringNames.ColorBlack),
            7  => translator.GetString(StringNames.ColorWhite),
            8  => translator.GetString(StringNames.ColorPurple),
            9  => translator.GetString(StringNames.ColorBrown),
            10 => translator.GetString(StringNames.ColorCyan),
            11 => translator.GetString(StringNames.ColorLime),
            12 => translator.GetString(StringNames.ColorMaroon),
            13 => translator.GetString(StringNames.ColorRose),
            14 => translator.GetString(StringNames.ColorBanana),
            15 => translator.GetString(StringNames.ColorGray),
            16 => translator.GetString(StringNames.ColorTan),
            17 => translator.GetString(StringNames.ColorCoral),
            _  => "???"
        };
    }

    private static void UpdateTypingEffect(MedScanMinigame __instance)
    {
        if (vitalsDone) return;

        if (string.IsNullOrEmpty(fullVitals))
        {
            var player     = PlayerControl.LocalPlayer;
            var translator = GetTranslator();

            if (string.IsNullOrEmpty(cachedBloodType))
                cachedBloodType = BloodTypes[UnityEngine.Random.Range(0, BloodTypes.Length)];

            string colorName   = GetMedScanColorString(player.CurrentOutfit.ColorId).ToUpper();
            string colorPrefix = colorName.Length >= 3 ? colorName.Substring(0, 3) : colorName;
            string medicalId   = $"{colorPrefix}P{player.PlayerId}";
            int    etaSeconds  = Mathf.CeilToInt(__instance.ScanTimer);
            string etaLine     = string.Format(translator.GetString(StringNames.MedETA), etaSeconds);

            fullVitals = $"{translator.GetString(StringNames.MedID)} {medicalId}      " +
                         $"{translator.GetString(StringNames.MedHT)} 3' 6\"      " +
                         $"{translator.GetString(StringNames.MedWT)} 92lb\n" +
                         $"{translator.GetString(StringNames.MedC)} {colorName}      " +
                         $"{translator.GetString(StringNames.MedBT)} {cachedBloodType}         " +
                         etaLine;

            typeInterval = (__instance.ScanTimer - 1f) / Mathf.Max(1, fullVitals.Length);
        }

        typeTimer += Time.fixedDeltaTime;
        if (typeTimer < typeInterval) return;

        typeTimer = 0f;

        if (charIndex < fullVitals.Length)
        {
            char currentChar = fullVitals[charIndex];
            charIndex++;

            // Cached: was GetComponentsInChildren<TextMeshPro>(true) on every tick (allocates!).
            TextMeshPro vitals = GetVitalsText(__instance);
            if (vitals != null)
            {
                vitals.text = fullVitals.Substring(0, charIndex);

                if (currentChar != ' ' && __instance.TextSound != null && SoundManager.Instance != null)
                    SoundManager.Instance.PlaySound(__instance.TextSound, false, 0.4f);
            }
        }
        else vitalsDone = true;
    }

    public static void Reset()
    {
        walkDone        = false;
        taskComplete    = false;
        targetSet       = false;
        scannerIsOn     = false;
        soundPlaying    = false;
        cachedScanSound = null;
        typeTimer       = 0f;
        charIndex       = 0;
        fullVitals      = "";
        vitalsDone      = false;
        cachedBloodType = "";
        typeInterval    = 0.025f;
        targetPosition  = Vector3.zero;

        // Invalidate cached Unity-object references for the next minigame instance.
        cachedStatusText   = null;
        statusTextResolved = false;
        cachedVitalsText   = null;
        vitalsTextResolved = false;
        lastStatusText     = null;
        lastProgress       = -1f;
    }

    public static void ForceCleanup(MedScanMinigame instance)
    {
        var player = PlayerControl.LocalPlayer;
        if (player != null && !taskComplete)
        {
            bool visualTasks = GameOptionsManager.Instance.CurrentGameOptions.GetBool(AmongUs.GameOptions.BoolOptionNames.VisualTasks);
            if (visualTasks) player.RpcSetScanner(false);
            else             player.SetScanner(false, 0);
        }

        AudioClip clip = (instance != null ? instance.ScanSound : null) ?? cachedScanSound;
        if (soundPlaying && clip != null && SoundManager.Instance != null && !taskComplete)
            SoundManager.Instance.StopSound(clip);
    }

    public static bool Prefix(MedScanMinigame __instance)
    {
        if (!MedScanMapChecker.IsSubmerged()) return true;
        if (__instance.ScanSound != null)
            cachedScanSound = __instance.ScanSound;

        // Fresh open detected / closing: tear down state and skip the original.
        if (!__instance.isActiveAndEnabled || __instance.amClosing != Minigame.CloseState.None)
        {
            ForceCleanup(__instance);
            Reset();
            return false;
        }

        if (taskComplete) return false;
        if (SubmarineSpawnInSystem.Instance == null) return false;

        var player       = PlayerControl.LocalPlayer;
        var translator   = GetTranslator();
        bool visualTasks = GameOptionsManager.Instance.CurrentGameOptions.GetBool(AmongUs.GameOptions.BoolOptionNames.VisualTasks);

        if (!targetSet)
        {
            var console = ShipStatus.Instance.AllConsoles
                .FirstOrDefault(c => c.TaskTypes.Contains(TaskTypes.SubmitScan));

            targetPosition = console != null
                ? console.transform.position + new Vector3(0.1f, 0.42f, 0f)
                : player.transform.position;

            targetSet = true;
        }

        // â”€â”€ Walk phase â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€
        if (!walkDone)
        {
            __instance.ScanTimer = __instance.ScanDuration;
            UpdateProgressBar(__instance, 0f);

            string playerName = player?.Data?.PlayerName ?? "Player";
            UpdateStatusText(__instance, string.Format(translator.GetString(StringNames.MedscanWaitingFor), playerName));

            if (player != null)
            {
                Vector3 delta = targetPosition - player.transform.position;
                if (delta.magnitude > ArrivalDist)
                    player.MyPhysics.body.velocity = (Vector2)delta.normalized * WalkSpeed;
                else
                {
                    player.MyPhysics.body.velocity = Vector2.zero;
                    walkDone = true;
                }
            }
            else walkDone = true;

            return false;
        }

        // â”€â”€ Scan phase â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€
        if (__instance.ScanTimer > 0f)
        {
            __instance.ScanTimer -= Time.fixedDeltaTime;
            UpdateTypingEffect(__instance);

            float progress    = 1f - (__instance.ScanTimer / __instance.ScanDuration);
            int   secondsLeft = Mathf.CeilToInt(__instance.ScanTimer);

            UpdateStatusText(__instance, string.Format(translator.GetString(StringNames.MedscanCompleteIn), secondsLeft));
            UpdateProgressBar(__instance, progress);

            if (!scannerIsOn)
            {
                if (visualTasks) player.RpcSetScanner(true);
                else             player.SetScanner(true, 0);
                scannerIsOn = true;
            }

            if (!soundPlaying && __instance.ScanSound != null)
            {
                if (visualTasks)
                {
                    var writer = AmongUsClient.Instance.StartRpcImmediately(player.NetId, 215, SendOption.Reliable, -1);
                    writer.Write(player.PlayerId);
                    AmongUsClient.Instance.FinishRpcImmediately(writer);
                }

                SoundManager.Instance.PlaySound(__instance.ScanSound, false, 0.8f);
                soundPlaying = true;
            }

            return false;
        }

        // â”€â”€ Completion â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€
        taskComplete = true;
        UpdateStatusText(__instance, translator.GetString(StringNames.MedscanCompleted));

        if (visualTasks) player.RpcSetScanner(false);
        else             player.SetScanner(false, 0);

        SubmarineSpawnInSystem.Instance.currentState = SpawnInState.Done;
        SubmarineSpawnInSystem.Instance.IsDirty      = true;

        if (__instance.MyNormTask != null) __instance.MyNormTask.NextStep();
        else if (__instance.MyTask != null) __instance.MyTask.Complete();

        __instance.StartCoroutine(__instance.CoStartClose());
        return false;
    }

    private static void UpdateProgressBar(MedScanMinigame __instance, float progress)
    {
        // Avoid redundant gauge writes (the setter rebuilds the gauge each call).
        if (Mathf.Abs(progress - lastProgress) < 0.001f)
            return;
        lastProgress = progress;

        if (__instance.gauge == null) return;
        __instance.gauge.Value = Mathf.Clamp(progress, 0f, __instance.gauge.MaxValue);
    }

    private static TextMeshPro GetStatusText(MedScanMinigame instance)
    {
        if (statusTextResolved) return cachedStatusText;
        statusTextResolved = true;

        Transform statusTransform = instance.transform.Find("Parent/StatusText")
                                 ?? instance.transform.Find("StatusText");

        if (statusTransform != null)
        {
            cachedStatusText = statusTransform.GetComponent<TextMeshPro>();
            if (cachedStatusText != null) return cachedStatusText;
        }

        // Fallback: scan children once for a "status" (non-"vitals") TextMeshPro.
        var allText = instance.GetComponentsInChildren<TextMeshPro>(true);
        if (allText != null)
        {
            foreach (var tm in allText)
            {
                string objName = tm.gameObject.name.ToLower();
                if (objName.Contains("status") && !objName.Contains("vitals"))
                {
                    cachedStatusText = tm;
                    break;
                }
            }
        }
        return cachedStatusText;
    }

    private static void UpdateStatusText(MedScanMinigame __instance, string text)
    {
        // Skip redundant writes: assigning TMP.text forces a full mesh rebuild.
        if (text == lastStatusText) return;

        TextMeshPro tm = GetStatusText(__instance);
        if (tm != null)
        {
            tm.text = text;
            lastStatusText = text;
        }
    }

    private static TextMeshPro GetVitalsText(MedScanMinigame instance)
    {
        if (vitalsTextResolved) return cachedVitalsText;
        vitalsTextResolved = true;

        var allText = instance.GetComponentsInChildren<TextMeshPro>(true);
        if (allText != null && allText.Length > 0)
            cachedVitalsText = allText[0];

        return cachedVitalsText;
    }
}
#endif
