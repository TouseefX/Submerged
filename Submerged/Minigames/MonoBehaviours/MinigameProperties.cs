using System;
using System.Linq;
using Il2CppInterop.Runtime.Attributes;
using Reactor.Utilities.Attributes;
using UnityEngine;

namespace Submerged.Minigames.MonoBehaviours;

[RegisterInIl2Cpp]
public sealed class MinigameProperties : MonoBehaviour
{
    // Native IL2CPP constructor pointer requirement
    public MinigameProperties(IntPtr ptr) : base(ptr) { }

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

        // Safely grab the vanilla component data containers
        StowArms stowArms = propObj.GetComponent<StowArms>();
        PolishRubyGame polishRubyGame = propObj.GetComponent<PolishRubyGame>();
        TextLink textLink = propObj.GetComponent<TextLink>();
        Tilemap2 tilemap2 = propObj.GetComponent<Tilemap2>();

        // Reconstruct data mappings
        if (textLink != null) @string = textLink.targetUrl;
        if (polishRubyGame != null) audioClips = polishRubyGame.rubSounds;
        if (stowArms != null) colliders = stowArms.GunColliders;
        if (stowArms != null) gameObjects = stowArms.selectorSubobjects;
        if (polishRubyGame != null) integers = polishRubyGame.swipes;
        if (tilemap2 != null) sprites = tilemap2.sprites;
        if (polishRubyGame != null) vector2S = polishRubyGame.directions;

        // Parse strings out of the hidden TextLink targetUrl
        if (!string.IsNullOrEmpty(@string))
        {
            string[] splits = @string.Split(new char[] { ';' }, 2);
            if (splits.Length > 0) playerTaskName = splits[0];
            if (splits.Length > 1) minigameName = splits[1];
        }
    }

    public void CloseTask()
    {
        if (dontCloseOnBgClick) return;

        Minigame[] minigames = GetComponents<Minigame>();
        if (minigames == null || minigames.Length == 0) return;

        // Find the minigame handler that isn't a DivertPowerMetagame container
        var activeMinigame = minigames.FirstOrDefault(mg => mg != null && !mg.TryCast<DivertPowerMetagame>());
        
        if (activeMinigame != null)
            activeMinigame.Close();
        else if (minigames[0] != null)
            minigames[0].Close();
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

        string[] splits = textLink.targetUrl.Split(new char[] { ';' }, 2);
        if (splits.Length > 0) p = splits[0];
        if (splits.Length > 1) m = splits[1];

        return (p, m);
    }
}
