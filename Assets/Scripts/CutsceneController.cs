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
    // Still-frame wake-desk intro после wipe, до EnableCombat (без VideoPlayer)
    // Позы sleep/headdown — in-place на Player (тот же transform/scale, что боевой стол),
    // а не fullscreen Image на чёрной пустоте.
    GameObject introRoot;
    Text introLine;
    Text introSpacePrompt;
    SpriteRenderer wakeHeroRenderer;
    Sprite wakeHeroSavedSprite;
    Animator wakeHeroAnimator;
    bool wakeHeroSavedAnimatorEnabled;
    bool wakePoseBound;
    // Narrative ending (Lev «are you even alive?»): cascade → mid words → darken → end.mp4
    GameObject aliveRoot;
    Text aliveCascade;
    Text aliveLine;
    Text aliveLineBelow;
    Text aliveSpacePlaque;
    Image aliveBlack;
    RawImage aliveEndVideoImage;
    VideoPlayer aliveEndVideo;
    RenderTexture aliveEndRt;
    Material aliveEndMaterial;
    AudioSource aliveEndAudio;
    bool aliveEndVideoPlaying;
    AudioSource titleAudio;
    bool ending;
    // true после wipe+интро / пропуска по U
    bool openingDone;
    // true, пока титул/wipe/интро ещё на экране
    bool cutscenePlaying;
    /// <summary>Флаг «cutscene playing» — титул, wipe или wake-desk intro; бой ещё не включён.</summary>
    public bool IsCutscenePlaying => cutscenePlaying;
    int kills;

    // Задержка до появления PRESS SPACE
    const float PressSpaceDelay = 2f;
    // Длительность горизонтального разъезда половинок
    const float WipeSeconds = 0.9f;
    // sleep.png → headdown.png soft beat
    const float SleepHoldSeconds = 2f;
    // Пауза перед подсказкой «space» под репликой
    const float IntroSpaceDelay = 3f;
    const string IntroSleepPath = "Sprites/Intro/sleep";
    const string IntroHeaddownPath = "Sprites/Intro/headdown";
    const string IntroLineNoise = "that noise again..";
    const string IntroLineDistract = "guess I should distract myself";
    // Герой в SampleScene: PPU=2, crop как у Writing/1 (см. .meta rect).
    const float IntroHeroPixelsPerUnit = 2f;
    const float IntroHeroCropX = 50f;
    const float IntroHeroCropY = 6f;
    const float IntroHeroCropW = 1192f;
    const float IntroHeroCropH = 672f;
    // Lev alive-ending: end.mp4 plate (same size as writing chromakey source).
    const string EndVideoArtsPath = "Arts/Ending/end.mp4";
    const string EndVideoStreamingName = "end.mp4";
    const string ChromaKeyMaterialPath = "Materials/ChromaKeyBlack";
    const int EndVideoWidth = 1280;
    const int EndVideoHeight = 704;
    const float AliveDarkenSeconds = 1.4f;
    const float AliveMidWordSeconds = 1f;
    const float AliveOverlayHoldSeconds = 3f;

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

    void OnDestroy()
    {
        RestoreWakeHeroPose();
        CleanupAliveEndVideo();
    }

    public void PlayEnding(bool quiet)
    {
        if (ending) return;
        ending = true;
        openingDone = true;
        cutscenePlaying = false;
        StopAllCoroutines();
        HideWakeDeskIntro();
        HideTitleSplash();
        StartCoroutine(Ending(quiet));
    }

    /// <summary>Ending после открытия последнего пинга Лева — каскад, mid words, end.mp4.</summary>
    public void PlayAliveEnding()
    {
        if (ending) return;
        ending = true;
        openingDone = true;
        cutscenePlaying = false;
        StopAllCoroutines();
        HideWakeDeskIntro();
        HideTitleSplash();
        StartCoroutine(AliveEnding());
    }

    // Скрыть титул/интро и продолжить игру как после полного интро
    void SkipOpening()
    {
        if (openingDone || ending) return;
        openingDone = true;
        cutscenePlaying = false;
        StopAllCoroutines();
        HideWakeDeskIntro();
        FinishOpening();
    }

    // Общий хвост после титула+интро: часы, бой, подсказка, overlays скрыты
    void FinishOpening()
    {
        HideTitleSplash();
        HideWakeDeskIntro();

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

    void HideWakeDeskIntro()
    {
        cutscenePlaying = false;
        RestoreWakeHeroPose();
        if (introRoot != null) introRoot.SetActive(false);
        if (introLine != null) introLine.text = string.Empty;
        if (introSpacePrompt != null)
        {
            introSpacePrompt.text = string.Empty;
            introSpacePrompt.gameObject.SetActive(false);
        }
    }

    // Титул: чёрный экран → «ghost clicker» → через 2с PRESS SPACE → Space → wipe → wake-desk stills → бой.
    // Старые плашки/дисклеймер здесь не показываются — игрок не застревает за ними.
    IEnumerator Opening()
    {
        cutscenePlaying = true;
        if (captionRoot != null) captionRoot.SetActive(false);
        if (caption != null) caption.text = string.Empty;
        if (titleRoot != null) titleRoot.SetActive(true);
        if (introRoot != null) introRoot.SetActive(false);
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
                // Позу sleep ставим до wipe — при разъезде половинок уже видна комната+стол.
                yield return PlayWakeDeskIntro();
                if (openingDone) yield break;
                openingDone = true;
                FinishOpening();
                yield break;
            }
            yield return null;
        }
    }

    // sleep → headdown на Player + две реплики со Space; missing art → soft-fail в бой (без soft-lock).
    IEnumerator PlayWakeDeskIntro()
    {
        cutscenePlaying = true;

        EnsureWakeDeskIntroUi();
        if (introRoot == null)
        {
            Debug.LogError("CutsceneController: wake-desk intro UI missing — skipping to gameplay.");
            yield break;
        }

        Sprite sleep = LoadIntroHeroPose(IntroSleepPath);
        Sprite headdown = LoadIntroHeroPose(IntroHeaddownPath);
        if (sleep == null && headdown == null)
        {
            Debug.LogError(
                "CutsceneController: wake-desk sprites missing ('" + IntroSleepPath +
                "', '" + IntroHeaddownPath + "') — skipping to gameplay.");
            HideWakeDeskIntro();
            yield break;
        }

        if (!BindWakeHeroPose())
        {
            Debug.LogError("CutsceneController: Player SpriteRenderer missing — skipping wake poses.");
            HideWakeDeskIntro();
            yield break;
        }

        // 1) sleep на герое, затем wipe — комната уже в кадре.
        ApplyWakeHeroPose(sleep != null ? sleep : headdown);
        yield return WipeReveal();
        if (openingDone) yield break;
        HideTitleSplash();

        introRoot.SetActive(true);
        if (introLine != null)
        {
            introLine.text = string.Empty;
            introLine.gameObject.SetActive(false);
        }
        if (introSpacePrompt != null)
        {
            introSpacePrompt.text = string.Empty;
            introSpacePrompt.gameObject.SetActive(false);
        }

        yield return WaitOrOpeningDone(SleepHoldSeconds);
        if (openingDone) yield break;

        // 2) headdown — тот же transform Player
        if (headdown != null)
            ApplyWakeHeroPose(headdown);

        // 3) line under character
        if (introLine != null)
        {
            introLine.gameObject.SetActive(true);
            introLine.text = IntroLineNoise;
        }

        // 4) 3s → "space" → wait Space
        yield return WaitOrOpeningDone(IntroSpaceDelay);
        if (openingDone) yield break;
        yield return ShowIntroSpaceAndWait();
        if (openingDone) yield break;

        // 5) second line
        if (introLine != null)
            introLine.text = IntroLineDistract;
        if (introSpacePrompt != null)
            introSpacePrompt.gameObject.SetActive(false);

        // 6) 3s → "space" → Space → combat
        yield return WaitOrOpeningDone(IntroSpaceDelay);
        if (openingDone) yield break;
        yield return ShowIntroSpaceAndWait();
        if (openingDone) yield break;

        HideWakeDeskIntro();
    }

    /// <summary>
    /// Поза для мира SampleScene: тот же crop/PPU, что у Writing-героя (не UI PPU=100).
    /// Импортированный Sprite.rect игнорируем — иначе desk «плавает» мелким на чёрном.
    /// </summary>
    static Sprite LoadIntroHeroPose(string resourcesPath)
    {
        Texture2D tex = null;
        Sprite imported = Resources.Load<Sprite>(resourcesPath);
        if (imported != null) tex = imported.texture;
        if (tex == null) tex = Resources.Load<Texture2D>(resourcesPath);
        if (tex == null) return null;

        float w = Mathf.Min(IntroHeroCropW, tex.width - IntroHeroCropX);
        float h = Mathf.Min(IntroHeroCropH, tex.height - IntroHeroCropY);
        if (w < 8f || h < 8f)
        {
            w = tex.width;
            h = tex.height;
            return Sprite.Create(
                tex,
                new Rect(0f, 0f, w, h),
                new Vector2(0.5f, 0.5f),
                IntroHeroPixelsPerUnit);
        }

        return Sprite.Create(
            tex,
            new Rect(IntroHeroCropX, IntroHeroCropY, w, h),
            new Vector2(0.5f, 0.5f),
            IntroHeroPixelsPerUnit);
    }

    bool BindWakeHeroPose()
    {
        if (wakePoseBound && wakeHeroRenderer != null) return true;

        var player = GameObject.FindGameObjectWithTag("Player");
        if (player == null) return false;
        wakeHeroRenderer = player.GetComponent<SpriteRenderer>();
        if (wakeHeroRenderer == null) return false;

        // If chromakey typing video already runs, park it and use SpriteRenderer stills.
        var typing = player.GetComponent<TypingVideoHero>();
        if (typing != null)
            typing.SuspendForStillPose();

        wakeHeroSavedSprite = wakeHeroRenderer.sprite;
        wakeHeroAnimator = player.GetComponent<Animator>();
        wakeHeroSavedAnimatorEnabled = wakeHeroAnimator != null && wakeHeroAnimator.enabled;
        if (wakeHeroAnimator != null)
            wakeHeroAnimator.enabled = false;

        wakePoseBound = true;
        return true;
    }

    void ApplyWakeHeroPose(Sprite pose)
    {
        if (wakeHeroRenderer == null || pose == null) return;
        wakeHeroRenderer.sprite = pose;
    }

    void RestoreWakeHeroPose()
    {
        if (!wakePoseBound) return;
        if (wakeHeroRenderer != null && wakeHeroSavedSprite != null)
            wakeHeroRenderer.sprite = wakeHeroSavedSprite;
        if (wakeHeroAnimator != null)
            wakeHeroAnimator.enabled = wakeHeroSavedAnimatorEnabled;

        var player = wakeHeroRenderer != null ? wakeHeroRenderer.gameObject : GameObject.FindGameObjectWithTag("Player");
        var typing = player != null ? player.GetComponent<TypingVideoHero>() : null;
        if (typing != null)
            typing.ResumeTyping();

        wakePoseBound = false;
        wakeHeroRenderer = null;
        wakeHeroSavedSprite = null;
        wakeHeroAnimator = null;
    }

    IEnumerator WaitOrOpeningDone(float seconds)
    {
        float t = 0f;
        while (t < seconds)
        {
            if (openingDone) yield break;
            t += Time.deltaTime;
            yield return null;
        }
    }

    IEnumerator ShowIntroSpaceAndWait()
    {
        if (introSpacePrompt != null)
        {
            introSpacePrompt.text = "space";
            introSpacePrompt.gameObject.SetActive(true);
            PlayPressSpaceSound();
        }
        // Сброс кадра, чтобы удержание Space с титула не схлопнуло шаг.
        yield return null;
        while (!openingDone)
        {
            var keyboard = Keyboard.current;
            if (keyboard != null && keyboard.spaceKey.wasPressedThisFrame)
                yield break;
            yield return null;
        }
    }

    void EnsureWakeDeskIntroUi()
    {
        if (introRoot != null) return;
        var canvas = GameObject.Find("CutsceneCanvas");
        if (canvas == null) return;
        BuildWakeDeskIntroUi(canvas.transform);
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
    /// Каскад → пауза → lean → «хороший вопрос.» → mid words 1s → darken → end.mp4 → overlay.
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
        HideAliveEndVideo();
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

        // Mid beat: words of "was I even alive.. really?" one-by-one (1s), then "and".
        HideAliveLines();
        string[] midWords = { "was", "I", "even", "alive..", "really?", "and" };
        for (int i = 0; i < midWords.Length; i++)
        {
            if (aliveCascade != null)
            {
                aliveCascade.gameObject.SetActive(true);
                aliveCascade.text = midWords[i];
            }
            yield return new WaitForSeconds(AliveMidWordSeconds);
            if (aliveCascade != null)
            {
                aliveCascade.text = string.Empty;
                aliveCascade.gameObject.SetActive(false);
            }
        }

        // Room darkens → end.mp4 (chromakey black; soft-fail if missing) → overlay lines.
        HideAliveLines();
        yield return DarkenAliveRoom(AliveDarkenSeconds);
        yield return BeginAliveEndVideoSoft();

        if (aliveLine != null)
        {
            aliveLine.transform.SetAsLastSibling();
            aliveLine.gameObject.SetActive(true);
            aliveLine.text = "maybe I should've just..";
        }
        yield return new WaitForSeconds(AliveOverlayHoldSeconds);

        if (aliveLineBelow != null)
        {
            aliveLineBelow.transform.SetAsLastSibling();
            aliveLineBelow.gameObject.SetActive(true);
            aliveLineBelow.text = "talked?";
        }
        if (aliveSpacePlaque != null)
            aliveSpacePlaque.transform.SetAsLastSibling();

        yield return WaitAliveEndVideoFinished();

        if (aliveSpacePlaque != null)
        {
            aliveSpacePlaque.text = "Press R";
            aliveSpacePlaque.gameObject.SetActive(true);
        }
        // Держим кадр; R — рестарт (Update).
        while (true)
            yield return null;
    }

    IEnumerator DarkenAliveRoom(float seconds)
    {
        if (aliveBlack == null)
        {
            yield return new WaitForSeconds(seconds);
            yield break;
        }

        aliveBlack.gameObject.SetActive(true);
        Color c = aliveBlack.color;
        c.a = 0f;
        aliveBlack.color = c;
        float t = 0f;
        float dur = Mathf.Max(0.05f, seconds);
        while (t < dur)
        {
            t += Time.deltaTime;
            c.a = Mathf.Clamp01(t / dur);
            aliveBlack.color = c;
            yield return null;
        }
        c.a = 1f;
        aliveBlack.color = c;
    }

    /// <summary>
    /// Starts end.mp4 over the darkened plate with black chromakey.
    /// Soft-fails (logs, continues) if the file is missing or prepare times out.
    /// </summary>
    IEnumerator BeginAliveEndVideoSoft()
    {
        aliveEndVideoPlaying = false;
        if (!TryResolveEndVideoUrl(out string url))
        {
            Debug.LogWarning("CutsceneController: end.mp4 missing — continuing ending without video.");
            yield break;
        }

        if (!EnsureAliveEndVideoUi())
        {
            Debug.LogWarning("CutsceneController: end.mp4 chromakey UI failed — continuing without video.");
            CleanupAliveEndVideo();
            yield break;
        }

        aliveEndVideo.url = url;
        aliveEndVideo.Prepare();
        float timeout = 8f;
        while (aliveEndVideo != null && !aliveEndVideo.isPrepared && timeout > 0f)
        {
            timeout -= Time.unscaledDeltaTime;
            yield return null;
        }

        if (aliveEndVideo == null || !aliveEndVideo.isPrepared)
        {
            Debug.LogWarning("CutsceneController: end.mp4 prepare timed out — continuing without video.");
            CleanupAliveEndVideo();
            yield break;
        }

        if (aliveEndVideoImage != null)
            aliveEndVideoImage.gameObject.SetActive(true);
        aliveEndVideoPlaying = true;
        aliveEndVideo.Play();
    }

    IEnumerator WaitAliveEndVideoFinished()
    {
        if (aliveEndVideo == null || !aliveEndVideoPlaying)
            yield break;

        float wait = 0f;
        float maxWait = (float)aliveEndVideo.length + 1.5f;
        if (maxWait < 2f) maxWait = EndVideoFallbackSeconds;
        while (aliveEndVideo != null && aliveEndVideoPlaying && wait < maxWait)
        {
            if (!aliveEndVideo.isPlaying)
                break;
            wait += Time.deltaTime;
            yield return null;
        }

        if (aliveEndVideo != null && aliveEndVideo.isPlaying)
            aliveEndVideo.Pause();
    }

    const float EndVideoFallbackSeconds = 8.5f;

    bool EnsureAliveEndVideoUi()
    {
        if (aliveRoot == null) return false;

        Material sourceMat = Resources.Load<Material>(ChromaKeyMaterialPath);
        Shader shader = sourceMat != null ? sourceMat.shader : Shader.Find("GhostClicker/ChromaKeyBlack");
        if (shader == null) return false;

        if (aliveEndRt == null)
        {
            aliveEndRt = new RenderTexture(EndVideoWidth, EndVideoHeight, 0, RenderTextureFormat.ARGB32)
            {
                name = "AliveEndRT",
                filterMode = FilterMode.Bilinear,
                wrapMode = TextureWrapMode.Clamp
            };
            aliveEndRt.Create();
        }

        if (aliveEndMaterial == null)
        {
            aliveEndMaterial = sourceMat != null ? new Material(sourceMat) : new Material(shader);
            aliveEndMaterial.name = "ChromaKeyAliveEndRuntime";
        }
        aliveEndMaterial.mainTexture = aliveEndRt;
        aliveEndMaterial.SetTexture("_MainTex", aliveEndRt);

        if (aliveEndVideoImage == null)
        {
            var go = new GameObject("EndVideo");
            go.transform.SetParent(aliveRoot.transform, false);
            // Above black, below cascade/line text (siblings created earlier stay on top if we insert after black).
            go.transform.SetSiblingIndex(aliveBlack != null ? aliveBlack.transform.GetSiblingIndex() + 1 : 0);
            var rect = go.AddComponent<RectTransform>();
            rect.anchorMin = Vector2.zero;
            rect.anchorMax = Vector2.one;
            rect.offsetMin = Vector2.zero;
            rect.offsetMax = Vector2.zero;
            aliveEndVideoImage = go.AddComponent<RawImage>();
            aliveEndVideoImage.raycastTarget = false;
            go.SetActive(false);
        }
        aliveEndVideoImage.texture = aliveEndRt;
        aliveEndVideoImage.material = aliveEndMaterial;
        aliveEndVideoImage.color = Color.white;

        if (aliveEndAudio == null)
        {
            aliveEndAudio = aliveRoot.AddComponent<AudioSource>();
            aliveEndAudio.playOnAwake = false;
            aliveEndAudio.spatialBlend = 0f;
            aliveEndAudio.loop = false;
        }

        if (aliveEndVideo == null)
        {
            aliveEndVideo = aliveRoot.AddComponent<VideoPlayer>();
            aliveEndVideo.playOnAwake = false;
            aliveEndVideo.isLooping = false;
            aliveEndVideo.renderMode = VideoRenderMode.RenderTexture;
            aliveEndVideo.targetTexture = aliveEndRt;
            aliveEndVideo.source = VideoSource.Url;
            aliveEndVideo.skipOnDrop = true;
            aliveEndVideo.waitForFirstFrame = true;
            aliveEndVideo.audioOutputMode = VideoAudioOutputMode.AudioSource;
            aliveEndVideo.SetTargetAudioSource(0, aliveEndAudio);
        }
        else
        {
            aliveEndVideo.targetTexture = aliveEndRt;
            aliveEndVideo.SetTargetAudioSource(0, aliveEndAudio);
        }

        return true;
    }

    void HideAliveEndVideo()
    {
        aliveEndVideoPlaying = false;
        if (aliveEndVideo != null && aliveEndVideo.isPlaying)
            aliveEndVideo.Pause();
        if (aliveEndVideoImage != null)
            aliveEndVideoImage.gameObject.SetActive(false);
    }

    void CleanupAliveEndVideo()
    {
        aliveEndVideoPlaying = false;
        if (aliveEndVideo != null)
        {
            aliveEndVideo.Stop();
            Destroy(aliveEndVideo);
            aliveEndVideo = null;
        }
        if (aliveEndVideoImage != null)
        {
            Destroy(aliveEndVideoImage.gameObject);
            aliveEndVideoImage = null;
        }
        if (aliveEndMaterial != null)
        {
            Destroy(aliveEndMaterial);
            aliveEndMaterial = null;
        }
        if (aliveEndRt != null)
        {
            aliveEndRt.Release();
            Destroy(aliveEndRt);
            aliveEndRt = null;
        }
        if (aliveEndAudio != null)
        {
            Destroy(aliveEndAudio);
            aliveEndAudio = null;
        }
    }

    static bool TryResolveEndVideoUrl(out string url)
    {
        url = null;

        string streaming = Path.Combine(Application.streamingAssetsPath, EndVideoStreamingName);
        if (EndVideoFileExists(streaming))
        {
            url = ToEndVideoFileUrl(streaming);
            return true;
        }

        string arts = Path.Combine(Application.dataPath, EndVideoArtsPath);
        if (EndVideoFileExists(arts))
        {
            url = ToEndVideoFileUrl(arts);
            return true;
        }

        return false;
    }

    static bool EndVideoFileExists(string path)
    {
        if (string.IsNullOrEmpty(path)) return false;
        if (path.Contains("://") || path.Contains("jar:"))
            return true;
        try
        {
            return File.Exists(path);
        }
        catch
        {
            return false;
        }
    }

    static string ToEndVideoFileUrl(string path)
    {
        if (path.Contains("://"))
            return path;
        return "file://" + path.Replace("\\", "/");
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
        var typing = player.GetComponent<TypingVideoHero>();
        if (typing != null)
            typing.StopTyping();
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
        if (aliveBlack == null) return;
        if (on)
        {
            Color c = aliveBlack.color;
            c.a = 1f;
            aliveBlack.color = c;
        }
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
        BuildWakeDeskIntroUi(canvasGo.transform);
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

    // Только текст/space поверх комнаты SampleScene — без чёрной пустоты и fullscreen frame.
    void BuildWakeDeskIntroUi(Transform canvas)
    {
        introRoot = new GameObject("WakeDeskIntro");
        introRoot.transform.SetParent(canvas, false);
        var rootRect = introRoot.AddComponent<RectTransform>();
        rootRect.anchorMin = Vector2.zero;
        rootRect.anchorMax = Vector2.one;
        rootRect.offsetMin = Vector2.zero;
        rootRect.offsetMax = Vector2.zero;

        var lineGo = new GameObject("Line");
        lineGo.transform.SetParent(introRoot.transform, false);
        var lineRect = lineGo.AddComponent<RectTransform>();
        // Under character: lower band of the combat room framing.
        lineRect.anchorMin = new Vector2(0.5f, 0.12f);
        lineRect.anchorMax = new Vector2(0.5f, 0.12f);
        lineRect.pivot = new Vector2(0.5f, 0.5f);
        lineRect.sizeDelta = new Vector2(1200f, 80f);
        lineRect.anchoredPosition = Vector2.zero;
        introLine = lineGo.AddComponent<Text>();
        PixelFonts.Apply(introLine);
        introLine.fontSize = 36;
        introLine.alignment = TextAnchor.MiddleCenter;
        introLine.color = new Color(0.94f, 0.94f, 0.92f);
        introLine.horizontalOverflow = HorizontalWrapMode.Wrap;
        introLine.verticalOverflow = VerticalWrapMode.Overflow;
        introLine.raycastTarget = false;
        lineGo.SetActive(false);

        var spaceGo = new GameObject("SpacePrompt");
        spaceGo.transform.SetParent(introRoot.transform, false);
        var spaceRect = spaceGo.AddComponent<RectTransform>();
        spaceRect.anchorMin = new Vector2(0.5f, 0.12f);
        spaceRect.anchorMax = new Vector2(0.5f, 0.12f);
        spaceRect.pivot = new Vector2(0.5f, 0.5f);
        spaceRect.sizeDelta = new Vector2(400f, 48f);
        spaceRect.anchoredPosition = new Vector2(0f, -52f);
        introSpacePrompt = spaceGo.AddComponent<Text>();
        PixelFonts.Apply(introSpacePrompt);
        introSpacePrompt.fontSize = 28;
        introSpacePrompt.alignment = TextAnchor.MiddleCenter;
        introSpacePrompt.color = new Color(0.85f, 0.85f, 0.82f, 0.95f);
        introSpacePrompt.text = "space";
        introSpacePrompt.raycastTarget = false;
        spaceGo.SetActive(false);

        introRoot.SetActive(false);
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
