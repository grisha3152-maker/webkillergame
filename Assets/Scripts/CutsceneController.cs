using System.Collections;
using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.SceneManagement;
using UnityEngine.UI;

public class CutsceneController : MonoBehaviour
{
    Text caption;
    Text hint;
    Text paused;
    GameObject captionRoot;
    bool ending;
    int kills;

    void Start()
    {
        BuildUi();
        StartCoroutine(Opening());
    }

    void OnEnable()
    {
        GameEvents.OnGhostKilled += CountKill;
    }

    void OnDisable()
    {
        GameEvents.OnGhostKilled -= CountKill;
    }

    void Update()
    {
        if (paused != null && GameManager.Instance != null)
            paused.gameObject.SetActive(GameManager.Instance.IsPaused && !ending);

        if (!ending) return;
        var keyboard = Keyboard.current;
        if (keyboard != null && keyboard.rKey.wasPressedThisFrame)
            SceneManager.LoadScene(SceneManager.GetActiveScene().buildIndex);
    }

    public void PlayEnding(bool quiet)
    {
        if (ending) return;
        ending = true;
        StopAllCoroutines();
        StartCoroutine(Ending(quiet));
    }

    IEnumerator Opening()
    {
        yield return Show("This story deals with burnout, loneliness, and depression.\n\nPress Enter", waitForEnter: true);
        if (GameManager.Instance != null) GameManager.Instance.StartClock();

        var crack = gameObject.AddComponent<AudioSource>();
        crack.spatialBlend = 0f;
        crack.PlayOneShot(GeneratedAudio.Crack, 0.5f);

        yield return Show("He closes the door.", 3.2f);
        yield return Show("Behind it, his mother is shouting.", 3.4f);
        yield return Show("His father answers, drunk.", 3.2f);
        yield return Show("Glass breaks.", 2.6f);
        yield return Show("He sits down. He does not turn around.", 3.4f);
        yield return Show("When your server is under attack, you must know how to defend it. To destroy the [****] attacks, aim at them and use your defense (press Space). Remember, the most important thing is our servers. May the grind be with you.", 11f);

        if (captionRoot != null) captionRoot.SetActive(false);
        if (GameManager.Instance != null) GameManager.Instance.EnableCombat();
        if (hint != null)
        {
            hint.gameObject.SetActive(true);
            hint.text = "W / A / D  —  Aim\nSpace  —  Defend\nE  —  Phone";
        }
    }

    void CountKill(int _)
    {
        if (hint == null || !hint.gameObject.activeSelf) return;
        kills++;
        if (kills >= 3) StartCoroutine(FadeHint());
    }

    IEnumerator FadeHint()
    {
        float t = 0f;
        Color color = hint.color;
        while (t < 1.4f)
        {
            t += Time.deltaTime;
            color.a = Mathf.Lerp(1f, 0f, t / 1.4f);
            hint.color = color;
            yield return null;
        }
        hint.gameObject.SetActive(false);
    }

    IEnumerator Ending(bool quiet)
    {
        if (captionRoot != null) captionRoot.SetActive(true);
        var player = GameObject.FindGameObjectWithTag("Player");
        var monsters = FindObjectsByType<Monster>(FindObjectsSortMode.None);
        for (int i = 0; i < monsters.Length; i++)
        {
            if (monsters[i] != null) Destroy(monsters[i].gameObject);
        }

        if (!quiet)
        {
            var shadows = FindAnyObjectByType<ShadowManager>();
            if (shadows != null) shadows.PlayCollapse();
            if (player != null)
            {
                Quaternion from = player.transform.rotation;
                Quaternion to = Quaternion.Euler(0f, 0f, 11f);
                float t = 0f;
                while (t < 2.2f)
                {
                    t += Time.deltaTime;
                    player.transform.rotation = Quaternion.Slerp(from, to, Mathf.Clamp01(t / 2.2f));
                    yield return null;
                }
            }
            else yield return new WaitForSeconds(2.2f);

            yield return Show("Was it worth it?", 2.4f);
            yield return Show("Was it worth it?\n\nPress R", 0f, hold: true);
        }
        else
        {
            yield return Show("The room is quiet. Nothing is asking him to stay.\n\nThis path continues in the full story.\n\nPress R", 0f, hold: true);
        }
    }

    IEnumerator Show(string text, float seconds = 0f, bool waitForEnter = false, bool hold = false)
    {
        if (captionRoot != null) captionRoot.SetActive(true);
        if (caption != null) caption.text = text;
        if (hold) yield break;
        if (waitForEnter)
        {
            while (true)
            {
                var keyboard = Keyboard.current;
                if (keyboard != null && (keyboard.enterKey.wasPressedThisFrame || keyboard.numpadEnterKey.wasPressedThisFrame || keyboard.spaceKey.wasPressedThisFrame))
                    yield break;
                yield return null;
            }
        }
        yield return new WaitForSeconds(seconds);
    }

    void BuildUi()
    {
        var canvasGo = new GameObject("CutsceneCanvas");
        var canvas = canvasGo.AddComponent<Canvas>();
        canvas.renderMode = RenderMode.ScreenSpaceOverlay;
        canvas.sortingOrder = 40;
        var scaler = canvasGo.AddComponent<CanvasScaler>();
        scaler.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
        scaler.referenceResolution = new Vector2(1920f, 1080f);

        captionRoot = new GameObject("Caption");
        captionRoot.transform.SetParent(canvasGo.transform, false);
        var rect = captionRoot.AddComponent<RectTransform>();
        rect.anchorMin = new Vector2(0.12f, 0.08f);
        rect.anchorMax = new Vector2(0.88f, 0.42f);
        rect.offsetMin = Vector2.zero;
        rect.offsetMax = Vector2.zero;
        var back = captionRoot.AddComponent<Image>();
        back.color = new Color(0f, 0f, 0f, 0.55f);
        back.raycastTarget = false;
        caption = CreateText(captionRoot.transform, 30);

        var hintGo = new GameObject("AimHint");
        hintGo.transform.SetParent(canvasGo.transform, false);
        var hintRect = hintGo.AddComponent<RectTransform>();
        hintRect.anchorMin = new Vector2(0.5f, 1f);
        hintRect.anchorMax = new Vector2(0.5f, 1f);
        hintRect.pivot = new Vector2(0.5f, 1f);
        hintRect.sizeDelta = new Vector2(700f, 120f);
        hintRect.anchoredPosition = new Vector2(0f, -28f);
        hint = hintGo.AddComponent<Text>();
        hint.font = Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf");
        hint.fontSize = 26;
        hint.alignment = TextAnchor.UpperCenter;
        hint.color = new Color(1f, 1f, 1f, 0.78f);
        hint.raycastTarget = false;
        hintGo.SetActive(false);

        var pauseGo = new GameObject("Paused");
        pauseGo.transform.SetParent(canvasGo.transform, false);
        var pauseRect = pauseGo.AddComponent<RectTransform>();
        pauseRect.anchorMin = new Vector2(0.5f, 0.5f);
        pauseRect.anchorMax = new Vector2(0.5f, 0.5f);
        pauseRect.sizeDelta = new Vector2(400f, 80f);
        paused = pauseGo.AddComponent<Text>();
        paused.font = Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf");
        paused.fontSize = 36;
        paused.alignment = TextAnchor.MiddleCenter;
        paused.color = new Color(0.9f, 0.9f, 0.88f, 0.9f);
        paused.text = "Paused";
        paused.raycastTarget = false;
        pauseGo.SetActive(false);
    }

    static Text CreateText(Transform parent, int size)
    {
        var go = new GameObject("Text");
        go.transform.SetParent(parent, false);
        var rect = go.AddComponent<RectTransform>();
        rect.anchorMin = Vector2.zero;
        rect.anchorMax = Vector2.one;
        rect.offsetMin = new Vector2(24f, 16f);
        rect.offsetMax = new Vector2(-24f, -16f);
        var text = go.AddComponent<Text>();
        text.font = Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf");
        text.fontSize = size;
        text.alignment = TextAnchor.MiddleCenter;
        text.color = new Color(0.92f, 0.92f, 0.9f);
        text.horizontalOverflow = HorizontalWrapMode.Wrap;
        text.verticalOverflow = VerticalWrapMode.Overflow;
        text.raycastTarget = false;
        return text;
    }
}
