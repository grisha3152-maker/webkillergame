using System.Collections;
using System.IO;
using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.SceneManagement;
using UnityEngine.UI;
using UnityEngine.Video;

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
    // Полноэкранное интро после wipe, до EnableCombat
    GameObject introVideoRoot;
    RawImage introVideoImage;
    VideoPlayer introVideoPlayer;
    RenderTexture introVideoRt;
    // Narrative ending (Lev «are you even alive?»): cascade / black / space BR
    GameObject aliveRoot;
    Text aliveCascade;
    Text aliveLine;
    Text aliveLineBelow;
    Text aliveSpacePlaque;
    Image aliveBlack;
    AudioSource titleAudio;
    bool ending;
    // true после wipe+видео / пропуска по U
    bool openingDone;
    // true, пока титул/wipe/интро-видео ещё на экране
    bool cutscenePlaying;
    /// <summary>Флаг «cutscene playing» — титул, wipe или intro video; бой ещё не включён.</summary>
    public bool IsCutscenePlaying => cutscenePlaying;
    int kills;

    // Задержка до появления PRESS SPACE
    const float PressSpaceDelay = 2f;
    // Длительность горизонтального разъезда половинок
    const float WipeSeconds = 0.9f;
    // Linux Editor не играет H.264 URL/MP4 (LinuxVideoMedia format 0) — только VP8 WebM.
    const string IntroVideoStreamingName = "first_scene.webm";
    // Optional VideoClip fallback (same WebM under Resources/Videos/).
    const string IntroVideoResourcesPath = "Videos/first_scene";
    const float IntroVideoPrepareTimeout = 8f;

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
        StopIntroVideo();
        HideTitleSplash();
        StartCoroutine(Ending(quiet));
    }

    /// <summary>Ending после открытия последнего пинга Лева — слово-каскад и Space-цепочка.</summary>
    public void PlayAliveEnding()
    {
        if (ending) return;
        ending = true;
        openingDone = true;
        cutscenePlaying = false;
        StopAllCoroutines();
        StopIntroVideo();
        HideTitleSplash();
        StartCoroutine(AliveEnding());
    }

    void OnDestroy()
    {
        if (introVideoPlayer != null)
            introVideoPlayer.Stop();
        if (introVideoRt != null)
        {
            introVideoRt.Release();
            Destroy(introVideoRt);
            introVideoRt = null;
        }
    }

    // Скрыть титул/видео и продолжить игру как после полного интро
    void SkipOpening()
    {
        if (openingDone || ending) return;
        openingDone = true;
        cutscenePlaying = false;
        StopAllCoroutines();
        StopIntroVideo();
        FinishOpening();
    }

    // Общий хвост после титула+видео: часы, бой, подсказка, overlays скрыты
    void FinishOpening()
    {
        HideTitleSplash();
        HideIntroVideo();

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
        if (titleRoot != null) titleRoot.SetActive(false);
    }

    void HideIntroVideo()
    {
        cutscenePlaying = false;
        if (introVideoRoot != null) introVideoRoot.SetActive(false);
        if (introVideoImage != null) introVideoImage.texture = null;
    }

    // Титул: чёрный экран → «ghost clicker» → через 2с PRESS SPACE → Space → wipe → intro video → бой.
    // Старые плашки/дисклеймер здесь не показываются — игрок не застревает за ними.
    IEnumerator Opening()
    {
        cutscenePlaying = true;
        if (captionRoot != null) captionRoot.SetActive(false);
        if (caption != null) caption.text = string.Empty;
        if (titleRoot != null) titleRoot.SetActive(true);
        if (introVideoRoot != null) introVideoRoot.SetActive(false);
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
                // После wipe — ролик, потом бой. Combat/спавнеры пока выключены.
                yield return PlayIntroVideo();
                if (openingDone) yield break;
                openingDone = true;
                FinishOpening();
                yield break;
            }
            yield return null;
        }
    }

    // Полноэкранный VideoPlayer; бой после loopPointReached или soft-fail (без soft-lock).
    IEnumerator PlayIntroVideo()
    {
        cutscenePlaying = true;
        HideTitleSplash();

        EnsureIntroVideoUi();
        if (introVideoRoot == null || introVideoPlayer == null)
        {
            Debug.LogError("CutsceneController: intro VideoPlayer UI missing — skipping to gameplay.");
            yield break;
        }

        introVideoRoot.SetActive(true);
        introVideoPlayer.isLooping = false;
        introVideoPlayer.playOnAwake = false;
        introVideoPlayer.skipOnDrop = true;
        introVideoPlayer.audioOutputMode = VideoAudioOutputMode.Direct;
        introVideoPlayer.clip = null;
        introVideoPlayer.url = string.Empty;

        if (!TryBindIntroVideoSource())
        {
            StopIntroVideo();
            yield break;
        }

        bool prepareError = false;
        string errorMsg = null;
        void OnError(VideoPlayer _, string message)
        {
            prepareError = true;
            errorMsg = message;
        }

        introVideoPlayer.errorReceived += OnError;
        introVideoPlayer.Prepare();
        float wait = 0f;
        while (!introVideoPlayer.isPrepared && !prepareError && wait < IntroVideoPrepareTimeout)
        {
            if (openingDone)
            {
                introVideoPlayer.errorReceived -= OnError;
                yield break;
            }
            wait += Time.deltaTime;
            yield return null;
        }

        if (prepareError || !introVideoPlayer.isPrepared)
        {
            Debug.LogError(
                "CutsceneController: intro video prepare failed" +
                (string.IsNullOrEmpty(errorMsg) ? " (timeout/" + IntroVideoPrepareTimeout + "s)" : ": " + errorMsg) +
                " — skipping to gameplay.");
            introVideoPlayer.errorReceived -= OnError;
            StopIntroVideo();
            yield break;
        }

        if (introVideoPlayer.audioTrackCount > 0)
        {
            introVideoPlayer.EnableAudioTrack(0, true);
            introVideoPlayer.SetDirectAudioMute(0, false);
            introVideoPlayer.SetDirectAudioVolume(0, 1f);
        }

        if (introVideoImage != null && introVideoRt != null)
            introVideoImage.texture = introVideoRt;

        bool ended = false;
        void OnLoopPoint(VideoPlayer _) => ended = true;
        introVideoPlayer.loopPointReached += OnLoopPoint;
        introVideoPlayer.Play();

        while (!ended && !openingDone && !prepareError)
            yield return null;

        introVideoPlayer.loopPointReached -= OnLoopPoint;
        introVideoPlayer.errorReceived -= OnError;

        if (prepareError)
        {
            Debug.LogError(
                "CutsceneController: intro video playback error" +
                (string.IsNullOrEmpty(errorMsg) ? "" : ": " + errorMsg) +
                " — skipping to gameplay.");
        }

        StopIntroVideo();
    }

    // 1) StreamingAssets WebM URL (VP8+Vorbis)  2) Resources VideoClip WebM — никогда Assets/*.mp4 URL.
    bool TryBindIntroVideoSource()
    {
        string streamingPath = Path.Combine(Application.streamingAssetsPath, IntroVideoStreamingName);
        if (File.Exists(streamingPath))
        {
            string url = streamingPath;
            if (!url.Contains("://"))
                url = "file://" + streamingPath;
            introVideoPlayer.source = VideoSource.Url;
            introVideoPlayer.url = url;
            Debug.Log("CutsceneController: intro from StreamingAssets WebM '" + url + "'.");
            return true;
        }

        var clip = Resources.Load<VideoClip>(IntroVideoResourcesPath);
        if (clip != null)
        {
            introVideoPlayer.source = VideoSource.VideoClip;
            introVideoPlayer.clip = clip;
            Debug.Log("CutsceneController: intro from Resources VideoClip '" + IntroVideoResourcesPath + "'.");
            return true;
        }

        Debug.LogError(
            "CutsceneController: intro WebM missing — StreamingAssets '" + streamingPath +
            "' and Resources '" + IntroVideoResourcesPath + "' — skipping to gameplay.");
        return false;
    }

    void StopIntroVideo()
    {
        if (introVideoPlayer != null && introVideoPlayer.isPlaying)
            introVideoPlayer.Stop();
        HideIntroVideo();
    }

    void EnsureIntroVideoUi()
    {
        if (introVideoRoot != null) return;
        var canvas = GameObject.Find("CutsceneCanvas");
        if (canvas == null) return;
        BuildIntroVideoUi(canvas.transform);
    }

    // Верхняя половина уезжает вверх, нижняя — вниз, под ними уже SampleScene / бой.
    IEnumerator WipeReveal()
    {
        if (titleLabel != null) titleLabel.gameObject.SetActive(false);
        if (pressSpaceLabel != null) pressSpaceLabel.gameObject.SetActive(false);

        // Высота уже задана якорями 50/50 — travel берём из реальной высоты панели.
        Canvas.ForceUpdateCanvases();
        float halfH = 540f;
        if (titleTopHalf != null && titleTopHalf.rect.height > 1f)
            halfH = titleTopHalf.rect.height;
        else if (titleBottomHalf != null && titleBottomHalf.rect.height > 1f)
            halfH = titleBottomHalf.rect.height;

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
        ClearMonsters();

        if (!quiet)
        {
            var shadows = FindAnyObjectByType<ShadowManager>();
            if (shadows != null) shadows.PlayCollapse();
            yield return LeanPlayerBack(player, 2.2f);

            yield return Show("Was it worth it?", 2.4f);
            yield return Show("Was it worth it?\n\nPress R", 0f, hold: true);
        }
        else
        {
            yield return Show("The room is quiet. Nothing is asking him to stay.\n\nThis path continues in the full story.\n\nPress R", 0f, hold: true);
        }
    }

    /// <summary>
    /// Каскад слов → пауза → стоп typing / откинуться → «хороший вопрос.» → Space-цепочка.
    /// 5с с открытым телефоном уже отсчитаны в Dialogue до вызова PlayAliveEnding.
    /// </summary>
    IEnumerator AliveEnding()
    {
        ClearMonsters();
        if (captionRoot != null) captionRoot.SetActive(false);
        if (hint != null) hint.gameObject.SetActive(false);
        if (aliveRoot != null) aliveRoot.SetActive(true);
        SetAliveBlack(false);
        HideAliveLines();
        if (aliveSpacePlaque != null) aliveSpacePlaque.gameObject.SetActive(false);

        // Слова по центру, по 2с каждое.
        string[] cascade = { "ты", "там", "вообще", "живой?" };
        for (int i = 0; i < cascade.Length; i++)
        {
            if (aliveCascade != null)
            {
                aliveCascade.gameObject.SetActive(true);
                aliveCascade.text = cascade[i];
            }
            yield return new WaitForSeconds(2f);
            if (aliveCascade != null)
            {
                aliveCascade.text = string.Empty;
                aliveCascade.gameObject.SetActive(false);
            }
        }

        // После последнего слова — 10с тишины.
        yield return new WaitForSeconds(10f);

        var player = GameObject.FindGameObjectWithTag("Player");
        StopTypingAnim(player);
        yield return LeanPlayerBack(player, 2.2f);

        if (aliveLine != null)
        {
            aliveLine.gameObject.SetActive(true);
            aliveLine.text = "хороший вопрос.";
        }
        yield return new WaitForSeconds(3f);

        // Плашка space справа снизу.
        if (aliveSpacePlaque != null)
        {
            aliveSpacePlaque.text = "space";
            aliveSpacePlaque.gameObject.SetActive(true);
            PlayPressSpaceSound();
        }
        yield return WaitForSpace();

        if (aliveSpacePlaque != null) aliveSpacePlaque.gameObject.SetActive(false);
        if (aliveLine != null) aliveLine.text = "был ли я жив.. на самом деле?";
        yield return new WaitForSeconds(2.2f);

        if (aliveLine != null) aliveLine.text = "и";
        yield return new WaitForSeconds(1.6f);

        // Чёрный экран → was / it / worth / it?
        HideAliveLines();
        SetAliveBlack(true);
        yield return new WaitForSeconds(0.4f);

        if (aliveLine != null)
        {
            aliveLine.gameObject.SetActive(true);
            aliveLine.text = "was";
        }
        if (aliveSpacePlaque != null)
        {
            aliveSpacePlaque.text = "space";
            aliveSpacePlaque.gameObject.SetActive(true);
        }
        yield return WaitForSpace();

        if (aliveLineBelow != null)
        {
            aliveLineBelow.gameObject.SetActive(true);
            aliveLineBelow.text = "it";
        }
        yield return WaitForSpace();

        if (aliveLineBelow != null)
            aliveLineBelow.text = "it\nworth";
        yield return WaitForSpace();

        // «it?» under «worth»; Space no longer needed.
        if (aliveLineBelow != null)
            aliveLineBelow.text = "it\nworth\nit?";
        if (aliveSpacePlaque != null)
        {
            aliveSpacePlaque.text = "Press R";
            aliveSpacePlaque.gameObject.SetActive(true);
        }
        // Держим кадр; R — рестарт (Update).
        while (true)
            yield return null;
    }

    static void ClearMonsters()
    {
        // Unity 6: без FindObjectsSortMode (устаревший параметр → CS0618).
        var monsters = FindObjectsByType<Monster>();
        for (int i = 0; i < monsters.Length; i++)
        {
            if (monsters[i] != null) Destroy(monsters[i].gameObject);
        }
    }

    static void StopTypingAnim(GameObject player)
    {
        if (player == null) return;
        var anim = player.GetComponent<Animator>();
        if (anim != null)
            anim.enabled = false;
    }

    static IEnumerator LeanPlayerBack(GameObject player, float seconds)
    {
        if (player == null)
        {
            yield return new WaitForSeconds(seconds);
            yield break;
        }
        Quaternion from = player.transform.rotation;
        Quaternion to = Quaternion.Euler(0f, 0f, 11f);
        float t = 0f;
        while (t < seconds)
        {
            t += Time.deltaTime;
            player.transform.rotation = Quaternion.Slerp(from, to, Mathf.Clamp01(t / seconds));
            yield return null;
        }
    }

    IEnumerator WaitForSpace()
    {
        // Сброс кадра, чтобы не схлопнуть цепочку одним удержанием Space.
        yield return null;
        while (true)
        {
            var keyboard = Keyboard.current;
            if (keyboard != null && keyboard.spaceKey.wasPressedThisFrame)
                yield break;
            yield return null;
        }
    }

    void SetAliveBlack(bool on)
    {
        if (aliveBlack != null)
            aliveBlack.gameObject.SetActive(on);
    }

    void HideAliveLines()
    {
        if (aliveCascade != null)
        {
            aliveCascade.text = string.Empty;
            aliveCascade.gameObject.SetActive(false);
        }
        if (aliveLine != null)
        {
            aliveLine.text = string.Empty;
            aliveLine.gameObject.SetActive(false);
        }
        if (aliveLineBelow != null)
        {
            aliveLineBelow.text = string.Empty;
            aliveLineBelow.gameObject.SetActive(false);
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
        BuildIntroVideoUi(canvasGo.transform);
        BuildAliveEndingUi(canvasGo.transform);

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

    // Полноэкранный слой под VideoPlayer (RenderTexture → RawImage), выше титула.
    void BuildIntroVideoUi(Transform canvas)
    {
        introVideoRoot = new GameObject("IntroVideo");
        introVideoRoot.transform.SetParent(canvas, false);
        var rootRect = introVideoRoot.AddComponent<RectTransform>();
        rootRect.anchorMin = Vector2.zero;
        rootRect.anchorMax = Vector2.one;
        rootRect.offsetMin = Vector2.zero;
        rootRect.offsetMax = Vector2.zero;

        var blackGo = new GameObject("Black");
        blackGo.transform.SetParent(introVideoRoot.transform, false);
        var blackRect = blackGo.AddComponent<RectTransform>();
        blackRect.anchorMin = Vector2.zero;
        blackRect.anchorMax = Vector2.one;
        blackRect.offsetMin = Vector2.zero;
        blackRect.offsetMax = Vector2.zero;
        var black = blackGo.AddComponent<Image>();
        black.color = Color.black;
        black.raycastTarget = false;

        var imageGo = new GameObject("VideoImage");
        imageGo.transform.SetParent(introVideoRoot.transform, false);
        var imageRect = imageGo.AddComponent<RectTransform>();
        imageRect.anchorMin = Vector2.zero;
        imageRect.anchorMax = Vector2.one;
        imageRect.offsetMin = Vector2.zero;
        imageRect.offsetMax = Vector2.zero;
        introVideoImage = imageGo.AddComponent<RawImage>();
        introVideoImage.color = Color.white;
        introVideoImage.raycastTarget = false;

        introVideoRt = new RenderTexture(1920, 1080, 0, RenderTextureFormat.ARGB32);
        introVideoRt.name = "IntroVideoRT";
        introVideoRt.Create();
        introVideoImage.texture = introVideoRt;

        introVideoPlayer = introVideoRoot.AddComponent<VideoPlayer>();
        introVideoPlayer.playOnAwake = false;
        introVideoPlayer.isLooping = false;
        introVideoPlayer.renderMode = VideoRenderMode.RenderTexture;
        introVideoPlayer.targetTexture = introVideoRt;
        introVideoPlayer.audioOutputMode = VideoAudioOutputMode.Direct;
        introVideoPlayer.source = VideoSource.Url;

        introVideoRoot.SetActive(false);
    }

    // UI для Lev alive-ending: каскад по центру, линии, чёрный фон, space справа снизу.
    void BuildAliveEndingUi(Transform canvas)
    {
        aliveRoot = new GameObject("AliveEnding");
        aliveRoot.transform.SetParent(canvas, false);
        var rootRect = aliveRoot.AddComponent<RectTransform>();
        rootRect.anchorMin = Vector2.zero;
        rootRect.anchorMax = Vector2.one;
        rootRect.offsetMin = Vector2.zero;
        rootRect.offsetMax = Vector2.zero;

        var blackGo = new GameObject("Black");
        blackGo.transform.SetParent(aliveRoot.transform, false);
        var blackRect = blackGo.AddComponent<RectTransform>();
        blackRect.anchorMin = Vector2.zero;
        blackRect.anchorMax = Vector2.one;
        blackRect.offsetMin = Vector2.zero;
        blackRect.offsetMax = Vector2.zero;
        aliveBlack = blackGo.AddComponent<Image>();
        aliveBlack.color = Color.black;
        aliveBlack.raycastTarget = false;
        blackGo.SetActive(false);

        aliveCascade = MakeAliveLabel(aliveRoot.transform, "Cascade", 72, new Vector2(0f, 20f), new Vector2(1200f, 160f));
        aliveCascade.gameObject.SetActive(false);

        aliveLine = MakeAliveLabel(aliveRoot.transform, "Line", 40, new Vector2(0f, 36f), new Vector2(1400f, 200f));
        aliveLine.gameObject.SetActive(false);

        aliveLineBelow = MakeAliveLabel(aliveRoot.transform, "LineBelow", 40, new Vector2(0f, -48f), new Vector2(1400f, 200f));
        aliveLineBelow.gameObject.SetActive(false);

        var spaceGo = new GameObject("SpacePlaque");
        spaceGo.transform.SetParent(aliveRoot.transform, false);
        var spaceRect = spaceGo.AddComponent<RectTransform>();
        spaceRect.anchorMin = new Vector2(1f, 0f);
        spaceRect.anchorMax = new Vector2(1f, 0f);
        spaceRect.pivot = new Vector2(1f, 0f);
        spaceRect.sizeDelta = new Vector2(220f, 56f);
        spaceRect.anchoredPosition = new Vector2(-36f, 28f);
        aliveSpacePlaque = spaceGo.AddComponent<Text>();
        PixelFonts.Apply(aliveSpacePlaque);
        aliveSpacePlaque.fontSize = 28;
        aliveSpacePlaque.alignment = TextAnchor.MiddleRight;
        aliveSpacePlaque.color = new Color(0.85f, 0.85f, 0.82f, 0.95f);
        aliveSpacePlaque.text = "space";
        aliveSpacePlaque.raycastTarget = false;
        spaceGo.SetActive(false);

        aliveRoot.SetActive(false);
    }

    static Text MakeAliveLabel(Transform parent, string name, int size, Vector2 anchored, Vector2 sizeDelta)
    {
        var go = new GameObject(name);
        go.transform.SetParent(parent, false);
        var rect = go.AddComponent<RectTransform>();
        rect.anchorMin = new Vector2(0.5f, 0.5f);
        rect.anchorMax = new Vector2(0.5f, 0.5f);
        rect.pivot = new Vector2(0.5f, 0.5f);
        rect.sizeDelta = sizeDelta;
        rect.anchoredPosition = anchored;
        var text = go.AddComponent<Text>();
        PixelFonts.Apply(text);
        text.fontSize = size;
        text.alignment = TextAnchor.MiddleCenter;
        text.color = new Color(0.94f, 0.94f, 0.92f);
        text.horizontalOverflow = HorizontalWrapMode.Wrap;
        text.verticalOverflow = VerticalWrapMode.Overflow;
        text.raycastTarget = false;
        return text;
    }

    // Половинки на 50% высоты с швом по центру; пивот у внешнего края — wipe через anchoredPosition.
    // Фиксированные 540px оставляли щель при canvas выше ref 1080 (match width / узкий экран).
    static RectTransform MakeHalfPanel(Transform parent, string name, bool top)
    {
        // Небольшой нахлёст на шве — без щели при нечётной высоте / субпикселях.
        const float seamOverlap = 2f;

        var go = new GameObject(name);
        go.transform.SetParent(parent, false);
        var rect = go.AddComponent<RectTransform>();
        if (top)
        {
            // Верхняя: от центра до верха; пивот у верха (уезжает вверх при wipe).
            rect.anchorMin = new Vector2(0f, 0.5f);
            rect.anchorMax = new Vector2(1f, 1f);
            rect.pivot = new Vector2(0.5f, 1f);
            // offsetMin.y < 0 — панель чуть ниже центра (нахлёст на шов).
            rect.offsetMin = new Vector2(0f, -seamOverlap);
            rect.offsetMax = Vector2.zero;
        }
        else
        {
            // Нижняя: от низа до центра; пивот у низа (уезжает вниз при wipe).
            rect.anchorMin = new Vector2(0f, 0f);
            rect.anchorMax = new Vector2(1f, 0.5f);
            rect.pivot = new Vector2(0.5f, 0f);
            // offsetMax.y > 0 — панель чуть выше центра (нахлёст на шов).
            rect.offsetMin = Vector2.zero;
            rect.offsetMax = new Vector2(0f, seamOverlap);
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
