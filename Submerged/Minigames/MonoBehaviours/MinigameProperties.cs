using System;
using System.Linq;
using Il2CppInterop.Runtime.Attributes;
using Reactor.Utilities.Attributes;
using UnityEngine;

namespace Submerged.Minigames.MonoBehaviours;

[RegisterInIl2Cpp]
#if ANDROID
public sealed class MinigameProperties : MonoBehaviour
{
    // Native IL2CPP constructor explicitly written out for Android stability
    public MinigameProperties(IntPtr ptr) : base(ptr) { }
#else
public sealed class MinigameProperties(nint ptr) : MonoBehaviour(ptr)
{
#endif

    // --- Serialized / Assigned Fields ---
    public string @string;
    public AudioClip[] audioClips;
    public Collider2D[] colliders;
    public GameObject[] gameObjects;
    public int[] integers;
    public Sprite[] sprites;
    public Vector2[] vector2S;

    public string playerTaskName = "";
    public string minigameName = "";

    public bool dontCloseOnBgClick;

    public void Awake()
    {
        Transform propObj = transform.Find("MinigameProperties");
        if (propObj == null)
        {
            Debug.LogError("[Submerged] MinigameProperties child object not found!");
            return;
        }

        StowArms stowArms = propObj.GetComponent<StowArms>();
        PolishRubyGame polishRubyGame = propObj.GetComponent<PolishRubyGame>();
        TextLink textLink = propObj.GetComponent<TextLink>();
        Tilemap2 tilemap2 = propObj.GetComponent<Tilemap2>();

        if (textLink != null) @string = textLink.targetUrl;
        if (polishRubyGame != null) audioClips = polishRubyGame.rubSounds;
        if (stowArms != null) colliders = stowArms.GunColliders;
        if (stowArms != null) gameObjects = stowArms.selectorSubobjects;
        if (polishRubyGame != null) integers = polishRubyGame.swipes;
        if (tilemap2 != null) sprites = tilemap2.sprites;
        if (polishRubyGame != null) vector2S = polishRubyGame.directions;

        if (!string.IsNullOrEmpty(@string))
        {
#if ANDROID
            // Explicit char array initialization to prevent parsing errors on older mobile runtimes
            string[] splits = @string.Split(new char[] { ';' }, 2);
#else
            string[] splits = @string.Split([';'], 2);
#endif
            if (splits.Length > 0) playerTaskName = splits[0];
            if (splits.Length > 1) minigameName = splits[1];
        }
    }

    public void CloseTask()
    {
        if (dontCloseOnBgClick) return;

        Minigame[] minigames = GetComponents<Minigame>();
        if (minigames == null || minigames.Length == 0) return;

#if ANDROID
        // Strict null and validation checks for Android garbage collection threads
        var activeMinigame = minigames.FirstOrDefault(mg => mg != null && !mg.TryCast<DivertPowerMetagame>());
        if (activeMinigame != null)
            activeMinigame.Close();
        else if (minigames[0] != null)
            minigames[0].Close();
#else
        if (minigames.FirstOrDefault(mg => !mg.TryCast<DivertPowerMetagame>()) is { } m)
            m.Close();
        else
            minigames[0].Close();
#endif
    }

    [HideFromIl2Cpp]
    public (string playerTaskName, string minigameName) GetCustomTypes()
    {
        Transform propObj = transform.Find("MinigameProperties");
        if (propObj == null) return ("", "");

        TextLink textLink = propObj.GetComponent<TextLink>();
        if (textLink == null || string.IsNullOrEmpty(textLink.targetUrl)) return ("", "");

        string p = "";
        string m = "";

#if ANDROID
        string[] splits = textLink.targetUrl.Split(new char[] { ';' }, 2);
#else
        string[] splits = textLink.targetUrl.Split([';'], 2);
#endif
        if (splits.Length > 0) p = splits[0];
        if (splits.Length > 1) m = splits[1];

        return (p, m);
    }
}
