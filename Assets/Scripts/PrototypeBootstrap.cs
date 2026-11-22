using UnityEngine;

public static class PrototypeBootstrap
{
    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
    static void Boot()
    {
        Balance.EnsureLoaded();

        if (GameObject.FindGameObjectWithTag("Player") == null)
        {
            Debug.LogWarning("PrototypeBootstrap: нет объекта с тегом Player — системы не созданы.");
            return;
        }

        // Если GameManager уже в сцене, всё равно гарантируем Dialogue (телефон).
        var existing = Object.FindAnyObjectByType<GameManager>();
        if (existing != null)
        {
            EnsureDialogue(existing.gameObject);
            EnsureCameraTension();
            return;
        }

        var root = new GameObject("talk with other");
        root.AddComponent<GameManager>();
        root.AddComponent<ShadowManager>();
        // Dialogue раньше MessengerSystem: Awake у Dialogue успевает до прокси-проверки.
        root.AddComponent<Dialogue>();
        root.AddComponent<MessengerSystem>();
        root.AddComponent<VisualProgression>();
        root.AddComponent<CutsceneController>();
        root.AddComponent<RadialAimController>();
        EnsureCameraTension();
        Debug.Log("PrototypeBootstrap: создан корень talk with other + Dialogue.");
    }

    static void EnsureDialogue(GameObject host)
    {
        if (Object.FindAnyObjectByType<Dialogue>() != null) return;
        host.AddComponent<Dialogue>();
        if (host.GetComponent<MessengerSystem>() == null)
            host.AddComponent<MessengerSystem>();
        Debug.Log("PrototypeBootstrap: Dialogue добавлен на существующий GameManager.");
    }

    /// <summary>Зум на Main Camera без правки SampleScene.</summary>
    static void EnsureCameraTension()
    {
        if (Object.FindAnyObjectByType<CameraTension>() != null) return;
        var cam = Camera.main;
        if (cam == null) return;
        cam.gameObject.AddComponent<CameraTension>();
    }
}
