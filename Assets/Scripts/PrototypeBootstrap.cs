using UnityEngine;

public static class PrototypeBootstrap
{
    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
    static void Boot()
    {
        Balance.EnsureLoaded();

        var player = GameObject.FindGameObjectWithTag("Player");
        if (player == null)
        {
            Debug.LogWarning("PrototypeBootstrap: нет объекта с тегом Player — системы не созданы.");
            return;
        }

        EnsureTypingVideoHero(player);

        // Если GameManager уже в сцене, всё равно гарантируем Dialogue (телефон).
        var existing = Object.FindAnyObjectByType<GameManager>();
        if (existing != null)
        {
            EnsureDialogue(existing.gameObject);
            EnsureHeroMutter(existing.gameObject);
            EnsureCameraTension();
            return;
        }

        var root = new GameObject("talk with other");
        root.AddComponent<GameManager>();
        root.AddComponent<ShadowManager>();
        // Dialogue раньше MessengerSystem: Awake у Dialogue успевает до прокси-проверки.
        root.AddComponent<Dialogue>();
        root.AddComponent<MessengerSystem>();
        root.AddComponent<HeroMutterView>();
        root.AddComponent<VisualProgression>();
        root.AddComponent<CutsceneController>();
        root.AddComponent<RadialAimController>();
        EnsureCameraTension();
        Debug.Log("PrototypeBootstrap: создан корень talk with other + Dialogue.");
    }

    /// <summary>Keyed writing.mp4 typing loop on Player (soft-fails if clip missing).</summary>
    static void EnsureTypingVideoHero(GameObject player)
    {
        if (player == null) return;
        if (player.GetComponent<TypingVideoHero>() != null) return;
        player.AddComponent<TypingVideoHero>();
    }

    static void EnsureDialogue(GameObject host)
    {
        if (Object.FindAnyObjectByType<Dialogue>() != null) return;
        host.AddComponent<Dialogue>();
        if (host.GetComponent<MessengerSystem>() == null)
            host.AddComponent<MessengerSystem>();
        Debug.Log("PrototypeBootstrap: Dialogue добавлен на существующий GameManager.");
    }

    static void EnsureHeroMutter(GameObject host)
    {
        if (Object.FindAnyObjectByType<HeroMutterView>() != null) return;
        host.AddComponent<HeroMutterView>();
    }

    /// <summary>Зум на Main Camera без правки SampleScene.</summary>
    static void EnsureCameraTension()
    {
        if (Object.FindAnyObjectByType<CameraTension>() != null) return;
        var cam = Camera.main;
        if (cam == null)
        {
            var tagged = GameObject.FindGameObjectWithTag("MainCamera");
            if (tagged != null) cam = tagged.GetComponent<Camera>();
        }
        if (cam == null) return;
        cam.gameObject.AddComponent<CameraTension>();
    }
}
