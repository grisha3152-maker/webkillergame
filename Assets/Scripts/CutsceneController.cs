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
    // Плашки captionRoot — только для ending; стартовый дисклеймер/тексты убраны
    GameObject captionRoot;
    // Титульный сплэш: чёрный экран + «ghost clicker» + wipe в бой
    GameObject titleRoot;
    RectTransform titleTopHalf;
    RectTransform titleBottomHalf;
    Text titleLabel;
    Text pressSpaceLabel;
    AudioSource titleAudio;
    bool ending;
    // true после wipe / пропуска по U
    bool openingDone;
    // true, пока титул/wipe ещё на экране
    bool cutscenePlaying;
    /// <summary>Флаг «cutscene playing» — титул или wipe, бой ещё не включён.</summary>
    public bool IsCutscenePlaying => cutscenePlaying;
    int kills;

    // Задержка до появления PRESS SPACE
    const float PressSpaceDelay = 2f;
    // Длительность горизонтального разъезда половинок
    const float WipeSeconds = 0.9f;

    void Start()
    {
        BuildUi();
        // Титул с первого кадра — без вспышки комнаты до Opening().
        if (titleRoot != null) titleRoot.SetActive(true);
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

        var keyboard = Keyboard.current;
        if (keyboard == null) return;

        // Пропуск титула по U — сразу в геймплей без wipe (отладка / старый хоткей)
        if (!openingDone && !ending && keyboard.uKey.wasPressedThisFrame)
        {
            SkipOpening();
            return;
        }

        if (!ending) return;
        if (keyboard.rKey.wasPressedThisFrame)
            SceneManager.LoadScene(SceneManager.GetActiveScene().buildIndex);
    }

    public void PlayEnding(bool quiet)
    {
        if (ending) return;
        ending = true;
        openingDone = true;
        cutscenePlaying = false;
        StopAllCoroutines();
        HideTitleSplash();
        StartCoroutine(Ending(quiet));
    }

    // Скрыть титул и продолжить игру как после полного интро
    void SkipOpening()
    {
        if (openingDone || ending) return;
        openingDone = true;
        cutscenePlaying = false;
        StopAllCoroutines();
        FinishOpening();
    }

    // Общий хвост после титула: часы, бой, подсказка, сплэш скрыт
    void FinishOpening()
    {
        HideTitleSplash();

        if (GameManager.Instance != null)
        {
            GameManager.Instance.StartClock();
            GameManager.Instance.EnableCombat();
        }

        if (captionRoot != null) captionRoot.SetActive(false);
        // Дубль сигнала посадки: даже если EnableCombat уже был true, Dialogue получит DeskReady.
        GameEvents.DeskReady();
        if (hint != null)
        {
            hint.gameObject.SetActive(true);
            hint.text = "W / A / D  —  Aim\nSpace  —  Defend\nE  —  Phone";
        }
    }

    void HideTitleSplash()
    {
        cutscenePlaying = false;
        if (titleRoot != null) titleRoot.SetActive(false);
    }

    // Титул: чёрный экран → «ghost clicker» → через 2с PRESS SPACE (+клик) → Space → wipe → бой.
    // Старые плашки/дисклеймер здесь не показываются — игрок не застревает за ними.
    IEnumerator Opening()
    {
        cutscenePlaying = true;
        if (captionRoot != null) captionRoot.SetActive(false);
        if (caption != null) caption.text = string.Empty;
        if (titleRoot != null) titleRoot.SetActive(true);
        if (pressSpaceLabel != null) pressSpaceLabel.gameObject.SetActive(false);

        // Ждём появления подсказки
        float t = 0f;
        while (t < PressSpaceDelay)
        {
            if (openingDone) yield break;
            t += Time.deltaTime;
            yield return null;
        }

        if (openingDone) yield break;

        if (pressSpaceLabel != null)
            pressSpaceLabel.gameObject.SetActive(true);
        PlayPressSpaceSound();

        // Ждём Space (бой ещё выключен — RadialAim Space не перехватит)
        while (!openingDone)
        {
            var keyboard = Keyboard.current;
            if (keyboard != null && keyboard.spaceKey.wasPressedThisFrame)
            {
                yield return WipeReveal();
                if (openingDone) yield break;
                openingDone = true;
                FinishOpening();
                yield break;
            }
            yield return null;
        }
    }

    // Верхняя половина уезжает вверх, нижняя — вниз, под ними уже SampleScene / бой.
    IEnumerator WipeReveal()
    {
        if (titleLabel != null) titleLabel.gameObject.SetActive(false);
        if (pressSpaceLabel != null) pressSpaceLabel.gameObject.SetActive(false);

        // Подгоняем высоту половинок под реальный canvas (ref 1080 → half 540).
        float halfH = 540f;
        if (titleRoot != null)
        {
            var rootRect = titleRoot.GetComponent<RectTransform>();
            if (rootRect != null && rootRect.rect.height > 1f)
                halfH = rootRect.rect.height * 0.5f;
        }
        if (titleTopHalf != null)
            titleTopHalf.sizeDelta = new Vector2(titleTopHalf.sizeDelta.x, halfH);
        if (titleBottomHalf != null)
            titleBottomHalf.sizeDelta = new Vector2(titleBottomHalf.sizeDelta.x, halfH);

        float travel = halfH + 8f;
        Vector2 topStart = titleTopHalf != null ? titleTopHalf.anchoredPosition : Vector2.zero;
        Vector2 bottomStart = titleBottomHalf != null ? titleBottomHalf.anchoredPosition : Vector2.zero;

        float t = 0f;
        while (t < WipeSeconds)
        {
            if (openingDone) yield break;
            t += Time.deltaTime;
            float u = Mathf.SmoothStep(0f, 1f, Mathf.Clamp01(t / WipeSeconds));
            // Top: пивот у верха — +Y уезжает за край; Bottom: пивот у низа — −Y вниз.
            if (titleTopHalf != null)
                titleTopHalf.anchoredPosition = topStart + new Vector2(0f, travel * u);
            if (titleBottomHalf != null)
                titleBottomHalf.anchoredPosition = bottomStart + new Vector2(0f, -travel * u);
            yield return null;
        }
    }

    void PlayPressSpaceSound()
    {
        if (titleAudio == null) return;
        titleAudio.PlayOneShot(GeneratedAudio.Click, 0.7f);
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

    // Плашки captionRoot — только ending. На старте/титле Show не вызывается.
    IEnumerator Show(string text, float seconds = 0f, bool hold = false)
    {
        if (!ending) yield break;
        if (captionRoot != null) captionRoot.SetActive(true);
        if (caption != null) caption.text = text;
        if (hold) yield break;
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

        titleAudio = canvasGo.AddComponent<AudioSource>();
        titleAudio.playOnAwake = false;
        titleAudio.spatialBlend = 0f;

        BuildTitleSplash(canvasGo.transform);

        // Сразу выключен: не должен мелькать на старте до Opening()
        captionRoot = new GameObject("Caption");
        captionRoot.SetActive(false);
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
        PixelFonts.Apply(hint);
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
        PixelFonts.Apply(paused);
        paused.fontSize = 36;
        paused.alignment = TextAnchor.MiddleCenter;
        paused.color = new Color(0.9f, 0.9f, 0.88f, 0.9f);
        paused.text = "Paused";
        paused.raycastTarget = false;
        pauseGo.SetActive(false);
    }

    // Чёрный полноэкранный титул: две половины для wipe + заголовок и PRESS SPACE.
    void BuildTitleSplash(Transform canvas)
    {
        titleRoot = new GameObject("TitleSplash");
        titleRoot.transform.SetParent(canvas, false);
        var rootRect = titleRoot.AddComponent<RectTransform>();
        rootRect.anchorMin = Vector2.zero;
        rootRect.anchorMax = Vector2.one;
        rootRect.offsetMin = Vector2.zero;
        rootRect.offsetMax = Vector2.zero;

        titleTopHalf = MakeHalfPanel(titleRoot.transform, "TopHalf", top: true);
        titleBottomHalf = MakeHalfPanel(titleRoot.transform, "BottomHalf", top: false);

        // Текст поверх половинок (sibling после панелей), скрываем в начале wipe.
        var titleGo = new GameObject("TitleLabel");
        titleGo.transform.SetParent(titleRoot.transform, false);
        var titleRect = titleGo.AddComponent<RectTransform>();
        titleRect.anchorMin = new Vector2(0.5f, 0.5f);
        titleRect.anchorMax = new Vector2(0.5f, 0.5f);
        titleRect.pivot = new Vector2(0.5f, 0.5f);
        titleRect.sizeDelta = new Vector2(900f, 120f);
        titleRect.anchoredPosition = new Vector2(0f, 36f);
        titleLabel = titleGo.AddComponent<Text>();
        PixelFonts.Apply(titleLabel);
        titleLabel.fontSize = 64;
        titleLabel.fontStyle = FontStyle.Bold;
        titleLabel.alignment = TextAnchor.MiddleCenter;
        titleLabel.color = Color.white;
        titleLabel.text = "ghost clicker";
        titleLabel.raycastTarget = false;

        var pressGo = new GameObject("PressSpace");
        pressGo.transform.SetParent(titleRoot.transform, false);
        var pressRect = pressGo.AddComponent<RectTransform>();
        pressRect.anchorMin = new Vector2(0.5f, 0.5f);
        pressRect.anchorMax = new Vector2(0.5f, 0.5f);
        pressRect.pivot = new Vector2(0.5f, 0.5f);
        pressRect.sizeDelta = new Vector2(700f, 60f);
        pressRect.anchoredPosition = new Vector2(0f, -48f);
        pressSpaceLabel = pressGo.AddComponent<Text>();
        PixelFonts.Apply(pressSpaceLabel);
        pressSpaceLabel.fontSize = 28;
        pressSpaceLabel.alignment = TextAnchor.MiddleCenter;
        pressSpaceLabel.color = new Color(0.85f, 0.85f, 0.82f, 0.95f);
        pressSpaceLabel.text = "PRESS SPACE";
        pressSpaceLabel.raycastTarget = false;
        pressGo.SetActive(false);

        titleRoot.SetActive(false);
    }

    // Половинки с пивотом у линии разреза — сдвиг anchoredPosition даёт чистый wipe.
    static RectTransform MakeHalfPanel(Transform parent, string name, bool top)
    {
        var go = new GameObject(name);
        go.transform.SetParent(parent, false);
        var rect = go.AddComponent<RectTransform>();
        if (top)
        {
            // Верхняя: якорь по верху экрана, пивот у нижнего края панели.
            rect.anchorMin = new Vector2(0f, 1f);
            rect.anchorMax = new Vector2(1f, 1f);
            rect.pivot = new Vector2(0.5f, 1f);
            rect.sizeDelta = new Vector2(0f, 540f);
            rect.anchoredPosition = Vector2.zero;
        }
        else
        {
            // Нижняя: якорь по низу, пивот у верхнего края панели.
            rect.anchorMin = new Vector2(0f, 0f);
            rect.anchorMax = new Vector2(1f, 0f);
            rect.pivot = new Vector2(0.5f, 0f);
            rect.sizeDelta = new Vector2(0f, 540f);
            rect.anchoredPosition = Vector2.zero;
        }
        var image = go.AddComponent<Image>();
        image.color = Color.black;
        image.raycastTarget = false;
        return rect;
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
        PixelFonts.Apply(text);
        text.fontSize = size;
        text.alignment = TextAnchor.MiddleCenter;
        text.color = new Color(0.92f, 0.92f, 0.9f);
        text.horizontalOverflow = HorizontalWrapMode.Wrap;
        text.verticalOverflow = VerticalWrapMode.Overflow;
        text.raycastTarget = false;
        return text;
    }
}
