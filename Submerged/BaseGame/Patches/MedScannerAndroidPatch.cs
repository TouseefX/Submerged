#if ANDROID
using System;
using System.Linq;
using System.Reflection;
using HarmonyLib;
using UnityEngine;
using Submerged.SpawnIn;
using Submerged.SpawnIn.Enums;
using Submerged.Extensions;
using TMPro;

public static class MedScanMapChecker
{
    public static bool IsSubmerged()
    {
        return ShipStatus.Instance != null && ShipStatus.Instance.IsSubmerged();
    }
}

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

    public static bool  walkDone     = false;
    public static bool  taskComplete = false;
    public static bool  soundPlaying = false;
    public static bool  scannerIsOn  = false;

    private static Vector3   targetPosition;
    private static bool      targetSet       = false;
    private static AudioClip cachedScanSound = null;

    // Typing variables
    private static float  typeTimer      = 0f;
    private static int    charIndex      = 0;
    private static string fullVitals     = "";
    private static bool   vitalsDone     = false;
    private static string cachedBloodType = "";
    private static float  typeInterval   = 0.025f;

    private static readonly string[] BloodTypes = { "A+", "A-", "B+", "B-", "AB+", "AB-", "O+", "O-" };

    private static string GetMedScanColorString(int colorId)
    {
        var translator = DestroyableSingleton<TranslationController>.Instance;
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
            var translator = DestroyableSingleton<TranslationController>.Instance;

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

            var allText = __instance.GetComponentsInChildren<TextMeshPro>(true);
            if (allText != null && allText.Length > 0)
            {
                allText[0].text = fullVitals.Substring(0, charIndex);

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

        // Fresh open detected: Begin reset ScanTimer back to ScanDuration
        if (!__instance.isActiveAndEnabled || __instance.amClosing != Minigame.CloseState.None)
        {
            ForceCleanup(__instance);
            Reset();
            return false;
        }

        if (taskComplete) return false;
        if (SubmarineSpawnInSystem.Instance == null) return false;

        var player       = PlayerControl.LocalPlayer;
        var translator   = DestroyableSingleton<TranslationController>.Instance;
        bool visualTasks = GameOptionsManager.Instance.CurrentGameOptions.GetBool(AmongUs.GameOptions.BoolOptionNames.VisualTasks);

        if (!targetSet)
        {
            var console = ShipStatus.Instance.AllConsoles
                .FirstOrDefault(c => c.TaskTypes.Contains(TaskTypes.SubmitScan));

            targetPosition = console != null
                ? console.transform.position + new Vector3(0.4f, 0.4f, 0f)
                : player.transform.position;

            targetSet = true;
        }

        // ── Walk phase ────────────────────────────────────────────────────
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

        // ── Scan phase ────────────────────────────────────────────────────
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
                SoundManager.Instance.PlaySound(__instance.ScanSound, true, 0.8f);
                soundPlaying = true;
            }

            return false;
        }

        // ── Completion ────────────────────────────────────────────────────
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
        if (__instance.gauge == null) return;
        __instance.gauge.Value = Mathf.Clamp(progress, 0f, __instance.gauge.MaxValue);
    }

    private static void UpdateStatusText(MedScanMinigame __instance, string text)
    {
        Transform statusTransform = __instance.transform.Find("Parent/StatusText")
                                 ?? __instance.transform.Find("StatusText");

        if (statusTransform != null)
        {
            var tm = statusTransform.GetComponent<TextMeshPro>();
            if (tm != null)
            {
                tm.text = text;
                return;
            }
        }

        var allText = __instance.GetComponentsInChildren<TextMeshPro>(true);
        foreach (var tm in allText)
        {
            string objName = tm.gameObject.name.ToLower();
            if (objName.Contains("status") && !objName.Contains("vitals"))
                tm.text = text;
        }
    }
}
#endif