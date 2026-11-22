using UnityEngine;

public static class PrototypeBootstrap
{
    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
    static void Boot()
    {
        if (GameObject.FindGameObjectWithTag("Player") == null) return;
        if (Object.FindAnyObjectByType<GameManager>() != null) return;

        var root = new GameObject("talk with other");
        root.AddComponent<GameManager>();
        root.AddComponent<ShadowManager>();
        root.AddComponent<Dialogue>();
        root.AddComponent<MessengerSystem>();
        root.AddComponent<VisualProgression>();
        root.AddComponent<CutsceneController>();
        root.AddComponent<RadialAimController>();
    }
}
