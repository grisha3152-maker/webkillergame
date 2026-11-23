using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Text.RegularExpressions;
using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.UI;

/// <summary>
/// Телефон: пинги из dialogue.json по at (сек после посадки).
/// ГГ никогда не отвечает текстом — только чтение входящих.
/// E открывает историю (накопленные входящие) и/или активный пинг; timeScale не трогаем.
/// Игнор по таймеру: −25 к отношениям / закрытие контакта, без исходящей «I'm busy».
/// forcedHit на открытие E не ставится (ответов нет).
/// При приходе пинга с heroMutter — GameEvents.HeroMutter (подпись под ГГ, не в чате).
/// </summary>
public class Dialogue : MonoBehaviour
{
    [SerializeField] TextAsset dialogueJson;

    DialoguePing[] pings;
    readonly HashSet<string> flags = new HashSet<string>();
    readonly HashSet<string> closed = new HashSet<string>();
    readonly HashSet<string> offered = new HashSet<string>();
    readonly Dictionary<string, int> relations = new Dictionary<string, int>();
    readonly Dictionary<string, int> coldCounts = new Dictionary<string, int>();
    readonly Queue<DialoguePing> waiting = new Queue<DialoguePing>();
    /// <summary>Накопленные входящие сверху вниз; при закрытии панели не сбрасывается.</summary>
    readonly List<HistoryLine> history = new List<HistoryLine>();

    DialoguePing active;
    int token;
    bool panelOpen;
    /// <summary>Есть неотвеченный пинг (таймер игнора ещё тикает).</summary>
    bool pendingIgnore;
    bool timingStarted;
    bool deskSeated;
    bool firstPingLogged;
    float playTime;
    float ignoreSeconds = 8f;
    int relationWarm = 15;
    int relationCold = -25;
    int farewellStrikes = 2;
    int scrollLines;

    RectTransform panel;
    Text body;
    Text header;
    Text footer;
    Text tab;
    AudioSource notify;

    /// <summary>
    /// Есть неотвеченный пинг — курсор тупеет уже с приходом сообщения (GDD), не только при открытой панели.
    /// </summary>
    public bool ChoicePending => pendingIgnore && active != null;

    /// <summary>Есть активное входящее (вкладка / таймер игнора).</summary>
    public bool HasActiveMessage => active != null;

    /// <summary>Можно открыть телефон: активный пинг или уже есть история.</summary>
    public bool CanOpenPhone => active != null || history.Count > 0;

    /// <summary>Ответов нет — forcedHit с телефона никогда не выставляется.</summary>
    public bool LastReplyForcedHit { get; private set; }

    void Awake()
    {
        InitRelationDefaults();
        LoadBalanceHints();
        LoadPings();
        BuildUi();
        notify = gameObject.AddComponent<AudioSource>();
        notify.playOnAwake = false;
        notify.spatialBlend = 0f;
        Debug.Log($"Dialogue: Awake OK, пингов={pings?.Length ?? 0}, UI={(panel != null ? "есть" : "нет")}");
    }

    void OnEnable()
    {
        GameEvents.OnFinaleStarted += HideAll;
        GameEvents.OnClockStarted += OnClockStarted;
        GameEvents.OnDeskReady += OnDeskReady;
    }

    void OnDisable()
    {
        GameEvents.OnFinaleStarted -= HideAll;
        GameEvents.OnClockStarted -= OnClockStarted;
        GameEvents.OnDeskReady -= OnDeskReady;
    }

    void Start()
    {
        // Если подписка опоздала к уже прошедшим сигналам — подхватить состояние GameManager.
        var gm = GameManager.Instance;
        if (gm == null) return;
        if (gm.GameplayActive)
            OnDeskReady();
        else if (gm.ClockRunning)
            OnClockStarted();
    }

    void OnClockStarted()
    {
        // Fallback: часы тикают. Полноценный старт пингов — на DeskReady; здесь только страховка.
        if (deskSeated) return;
        BeginTiming("ClockStarted", resetPlayTime: true, seated: false);
    }

    void OnDeskReady()
    {
        // Конец opening / посадка — основной старт. Сбрасываем преждевременный ClockStarted.
        BeginTiming("DeskReady", resetPlayTime: true, seated: true);
    }

    void BeginTiming(string reason, bool resetPlayTime, bool seated)
    {
        if (timingStarted && deskSeated && seated)
            return; // Уже сидим за столом — повторный DeskReady игнор.

        if (timingStarted && !resetPlayTime)
            return;

        if (resetPlayTime)
        {
            // Сброс преждевременных пингов (таймер шёл от ClockStarted до посадки).
            if (timingStarted && !deskSeated && seated)
            {
                token++;
                active = null;
                waiting.Clear();
                offered.Clear();
                history.Clear();
                pendingIgnore = false;
                panelOpen = false;
                scrollLines = 0;
            }
            playTime = 0f;
            firstPingLogged = false;
        }

        if (!timingStarted)
            Debug.Log($"Dialogue: таймер пингов стартовал ({reason})");
        else if (seated)
            Debug.Log($"Dialogue: таймер пингов сброшен к посадке ({reason})");

        timingStarted = true;
        if (seated)
            deskSeated = true;
        Refresh();
    }

    void Update()
    {
        var gm = GameManager.Instance;
        if (gm != null && gm.IsEnded)
            return;

        // Пауза Esc сама ставит timeScale=0; телефон timeScale не трогает.
        if (gm != null && gm.IsPaused)
            return;

        // Подхват, если события прошли мимо подписки.
        if (!timingStarted && gm != null)
        {
            if (gm.GameplayActive) OnDeskReady();
            else if (gm.ClockRunning) OnClockStarted();
        }

        // Пинги тикают после старта; автоигнор — после посадки (deskSeated),
        // чтобы mom@25s не сгорел во время плашек интро.
        if (timingStarted && deskSeated)
        {
            playTime += Time.deltaTime;
            PollArrivals();
        }
        else if (timingStarted && !deskSeated && gm != null && gm.Elapsed >= 35f)
        {
            // Opening завис / EnableCombat не вызван — всё равно сажаем за стол.
            Debug.LogWarning("Dialogue: DeskReady не пришёл за 35с после часов — принудительный старт.");
            OnDeskReady();
        }

        bool pressedE = false;
        var keyboard = Keyboard.current;
        if (keyboard != null && keyboard.eKey.wasPressedThisFrame)
            pressedE = true;
#if ENABLE_LEGACY_INPUT_MANAGER
        else if (Input.GetKeyDown(KeyCode.E))
            pressedE = true;
#endif

        // E: открыть/закрыть историю или активный пинг. Без истории и пинга — ничего.
        if (pressedE)
            TogglePhone();

        if (!panelOpen || history.Count == 0) return;
        if (keyboard == null) return;

        // Скролл истории стрелками (без выбора реплик).
        int visible = VisibleHistoryLines();
        int maxScroll = Mathf.Max(0, history.Count - visible);
        if (keyboard.upArrowKey.wasPressedThisFrame)
        {
            scrollLines = Mathf.Max(0, scrollLines - 1);
            Refresh();
        }
        else if (keyboard.downArrowKey.wasPressedThisFrame)
        {
            scrollLines = Mathf.Min(maxScroll, scrollLines + 1);
            Refresh();
        }
    }

    void TogglePhone()
    {
        if (!CanOpenPhone)
        {
            Debug.Log("Dialogue: E нажат, но нет истории и активного пинга.");
            return;
        }

        panelOpen = !panelOpen;
        if (panelOpen)
        {
            // Новое внизу: при открытии прокрутить к концу.
            scrollLines = Mathf.Max(0, history.Count - VisibleHistoryLines());
            string who = active != null ? active.id : "история";
            Debug.Log($"Dialogue: E → панель открыта ({who}, строк={history.Count})");
        }
        else
        {
            Debug.Log("Dialogue: E → панель закрыта (история сохранена)");
        }
        // Открытие E только чтение — forcedHit / MessageReplied не вызываем.
        LastReplyForcedHit = false;
        Refresh();
    }

    void PollArrivals()
    {
        if (pings == null) return;

        SyncDerivedFlags();
        for (int i = 0; i < pings.Length; i++)
        {
            var ping = pings[i];
            if (ping == null || string.IsNullOrEmpty(ping.id)) continue;
            if (offered.Contains(ping.id)) continue;
            if (playTime + 0.05f < ping.at) continue;
            if (closed.Contains(ping.contact)) continue;
            if (!RequiresMet(ping)) continue;

            offered.Add(ping.id);
            waiting.Enqueue(ping);
        }

        if (active == null)
            OpenNext();
    }

    void OpenNext()
    {
        while (waiting.Count > 0)
        {
            var next = waiting.Dequeue();
            // Опоздавший пинг сгорает, если висел в очереди слишком долго.
            if (playTime > next.at + ignoreSeconds)
                continue;
            if (closed.Contains(next.contact))
                continue;
            SyncDerivedFlags();
            if (!RequiresMet(next))
                continue;

            active = next;
            pendingIgnore = true;
            // Панель сама не всплывает — только вкладка, пока игрок не нажмёт E.
            // Исключение: прощание без ожидания ответа — сразу в историю и закрыть контакт.
            token++;
            AppendHistory(next);
            ApplyFlags(next.set);

            if (!firstPingLogged)
            {
                firstPingLogged = true;
                Debug.Log($"Dialogue: первый пинг «{next.id}» от {next.contact} at={next.at:0}s (playTime={playTime:0.0}s). Жми E.");
            }
            else
            {
                Debug.Log($"Dialogue: пинг «{next.id}» ({next.contact}), E открывает чтение.");
            }

            if (notify != null && GeneratedAudio.Notification != null)
                notify.PlayOneShot(GeneratedAudio.Notification, 0.7f);

            // Сигнал камере и прочим слушателям: пришло новое входящее.
            GameEvents.MessageArrived(next.contact);

            // Бормотание под ГГ (пинги 1–9); с 10-го heroMutter пустой — тишина.
            if (!string.IsNullOrEmpty(next.heroMutter))
                GameEvents.HeroMutter(next.heroMutter);

            // Прощание (явный bye): закрываем контакт сразу, без штрафа «I'm busy».
            bool farewell = IsFarewellPing(next);
            if (farewell)
            {
                CloseContact(next.contact);
                pendingIgnore = false;
                // Показать вкладку, чтобы игрок мог открыть; таймер только убирает active.
                StartCoroutine(TimeoutOrIgnore(token, ignoreSeconds));
                Refresh();
                return;
            }

            StartCoroutine(TimeoutOrIgnore(token, ignoreSeconds));
            Refresh();
            return;
        }

        active = null;
        pendingIgnore = false;
        Refresh();
    }

    static bool IsFarewellPing(DialoguePing ping)
    {
        if (ping == null) return false;
        // Явные прощания без таймерного штрафа отношений.
        return ping.id == "lev_farewell";
    }

    IEnumerator TimeoutOrIgnore(int myToken, float seconds)
    {
        float t = 0f;
        while (t < seconds)
        {
            if (myToken != token) yield break;
            t += Time.deltaTime;
            yield return null;
        }
        if (myToken != token || active == null) yield break;

        if (IsFarewellPing(active))
        {
            DismissActive();
            yield break;
        }

        Ignore();
    }

    /// <summary>
    /// Автоигнор messageIgnoreSeconds: −25 к отношениям, без исходящей реплики и без forcedHit.
    /// Сообщение уже в истории — только штраф и снятие активного пинга.
    /// </summary>
    void Ignore()
    {
        if (active == null) return;
        var ping = active;
        string contact = ping.contact;
        token++;
        pendingIgnore = false;
        LastReplyForcedHit = false;

        // Игнор = холодный удар по отношениям; удара-призрака и текста «I'm busy» нет.
        if (contact != "desk")
        {
            ApplyRelation(contact, false);
            RegisterCold(contact);
            MaybeCloseAfterStrikes(contact);
        }

        active = null;
        // Панель не закрываем насильно: если открыта — просто обновить историю.
        Refresh();
        OpenNext();
    }

    void DismissActive()
    {
        token++;
        pendingIgnore = false;
        LastReplyForcedHit = false;
        active = null;
        Refresh();
        OpenNext();
    }

    void AppendHistory(DialoguePing ping)
    {
        if (ping == null) return;
        history.Add(new HistoryLine
        {
            contact = ping.contact,
            text = ping.incoming
        });
        if (panelOpen)
            scrollLines = Mathf.Max(0, history.Count - VisibleHistoryLines());
    }

    void RegisterCold(string contact)
    {
        if (!coldCounts.ContainsKey(contact)) coldCounts[contact] = 0;
        coldCounts[contact]++;
    }

    void MaybeCloseAfterStrikes(string contact)
    {
        // Мама и Аня продолжают писать в молчание до поздних битов; desk без замка.
        if (contact == "desk" || contact == "mom" || contact == "anya") return;
        if (!coldCounts.TryGetValue(contact, out int n) || n < farewellStrikes) return;
        if (closed.Contains(contact)) return;
        CloseContact(contact);
        // Прощание сразу в историю (пинг lev_farewell мог уже не успеть).
        if (contact == "lev")
        {
            history.Add(new HistoryLine
            {
                contact = "lev",
                text = "ok. i get it. i'm not going to be the guy who writes into a mute chat. bye."
            });
            if (panelOpen)
                scrollLines = Mathf.Max(0, history.Count - VisibleHistoryLines());
        }
        Debug.Log($"Dialogue: контакт «{contact}» закрыт после {n} игноров.");
    }

    void ApplyRelation(string contact, bool warm)
    {
        if (!relations.ContainsKey(contact)) return;
        relations[contact] = Mathf.Clamp(
            relations[contact] + (warm ? relationWarm : relationCold),
            0,
            100);
    }

    void ApplyFlags(string[] set)
    {
        if (set == null) return;
        for (int i = 0; i < set.Length; i++)
        {
            if (!string.IsNullOrEmpty(set[i]))
                flags.Add(set[i]);
        }
    }

    void CloseContact(string contact)
    {
        if (string.IsNullOrEmpty(contact) || contact == "desk") return;
        closed.Add(contact);
    }

    bool RequiresMet(DialoguePing ping)
    {
        if (ping.requires == null || ping.requires.Length == 0) return true;
        for (int i = 0; i < ping.requires.Length; i++)
        {
            string req = ping.requires[i];
            if (string.IsNullOrEmpty(req)) continue;
            if (!flags.Contains(req)) return false;
        }
        return true;
    }

    void SyncDerivedFlags()
    {
        flags.Remove("lev_relation_gt_25");
        flags.Remove("lev_relation_lte_25");

        int levRel = relations.TryGetValue("lev", out int lr) ? lr : 45;
        if (levRel > 25) flags.Add("lev_relation_gt_25");
        else flags.Add("lev_relation_lte_25");
    }

    void HideAll()
    {
        token++;
        active = null;
        pendingIgnore = false;
        panelOpen = false;
        waiting.Clear();
        if (panel != null) panel.gameObject.SetActive(false);
        if (tab != null) tab.gameObject.SetActive(false);
    }

    void Refresh()
    {
        bool hasActive = active != null;
        bool showPanel = panelOpen && CanOpenPhone;
        // Вкладка «входящее», если есть активный пинг, а панель ещё не открыта.
        if (tab != null)
        {
            tab.gameObject.SetActive(hasActive && !panelOpen && pendingIgnore);
            if (hasActive) tab.text = ContactTitle(active.contact) + "\n[E]";
        }
        if (panel != null) panel.gameObject.SetActive(showPanel);
        if (!showPanel) return;

        if (hasActive)
            header.text = ContactTitle(active.contact);
        else if (history.Count > 0)
            header.text = ContactTitle(history[history.Count - 1].contact);
        else
            header.text = "Phone";

        body.text = BuildHistoryText();
        footer.text = history.Count > VisibleHistoryLines()
            ? "↑↓ scroll  ·  [E] close"
            : "[E] close";
    }

    string BuildHistoryText()
    {
        if (history.Count == 0) return string.Empty;

        int visible = VisibleHistoryLines();
        int maxScroll = Mathf.Max(0, history.Count - visible);
        scrollLines = Mathf.Clamp(scrollLines, 0, maxScroll);
        int start = scrollLines;
        int end = Mathf.Min(history.Count, start + visible);

        var builder = new System.Text.StringBuilder();
        for (int i = start; i < end; i++)
        {
            var line = history[i];
            builder.Append(ContactTitle(line.contact));
            builder.Append(": ");
            builder.Append(line.text);
            if (i < end - 1) builder.Append("\n\n");
        }
        return builder.ToString();
    }

    static int VisibleHistoryLines() => 6;

    void InitRelationDefaults()
    {
        relations["mom"] = 60;
        relations["anya"] = 40;
        relations["lev"] = 45;
        relations["mila"] = 50;
    }

    void LoadBalanceHints()
    {
        // Без зависимости от Balance.cs: читаем только нужные поля, если файл есть.
        TextAsset asset = Resources.Load<TextAsset>("balance");
#if UNITY_EDITOR
        if (asset == null)
            asset = UnityEditor.AssetDatabase.LoadAssetAtPath<TextAsset>("Assets/Data/balance.json");
#endif
        if (asset == null)
        {
            string path = Path.Combine(Application.dataPath, "Data", "balance.json");
            if (File.Exists(path))
                asset = new TextAsset(File.ReadAllText(path));
        }
        if (asset == null || string.IsNullOrWhiteSpace(asset.text)) return;

        var hint = JsonUtility.FromJson<DialogueBalanceHint>(asset.text);
        if (hint == null) return;
        if (hint.messageIgnoreSeconds > 0f) ignoreSeconds = hint.messageIgnoreSeconds;
        if (hint.relationWarm != 0) relationWarm = hint.relationWarm;
        if (hint.relationCold != 0) relationCold = hint.relationCold;
        if (hint.farewellStrikes > 0) farewellStrikes = hint.farewellStrikes;
        if (hint.relationStart != null)
        {
            relations["mom"] = hint.relationStart.mom;
            relations["anya"] = hint.relationStart.anya;
            relations["lev"] = hint.relationStart.lev;
            relations["mila"] = hint.relationStart.mila;
        }
    }

    void LoadPings()
    {
        if (dialogueJson == null)
            dialogueJson = Resources.Load<TextAsset>("dialogue");
#if UNITY_EDITOR
        if (dialogueJson == null)
            dialogueJson = UnityEditor.AssetDatabase.LoadAssetAtPath<TextAsset>("Assets/Data/dialogue.json");
#endif
        if (dialogueJson == null)
        {
            string path = Path.Combine(Application.dataPath, "Data", "dialogue.json");
            if (File.Exists(path))
                dialogueJson = new TextAsset(File.ReadAllText(path));
        }

        if (dialogueJson == null || string.IsNullOrWhiteSpace(dialogueJson.text))
        {
            Debug.LogWarning("Dialogue: dialogue.json не найден.");
            pings = Array.Empty<DialoguePing>();
            return;
        }

        // JsonUtility не ест корневой массив и ключ "in".
        string raw = dialogueJson.text.Trim();
        raw = Regex.Replace(raw, "\"in\"\\s*:", "\"incoming\":");
        string wrapped = "{\"items\":" + raw + "}";
        var file = JsonUtility.FromJson<DialoguePingFile>(wrapped);
        pings = file != null && file.items != null ? file.items : Array.Empty<DialoguePing>();

        // Ответов ГГ нет → forcedHit с телефона всегда false (desk и семья одинаково).
        for (int i = 0; i < pings.Length; i++)
        {
            var p = pings[i];
            if (p == null) continue;
            p.forcedHit = false;
        }

        Debug.Log($"Dialogue: загружено пингов: {pings.Length}");
    }

    void BuildUi()
    {
        var canvasGo = new GameObject("PhoneCanvas");
        canvasGo.transform.SetParent(transform, false);
        var canvas = canvasGo.AddComponent<Canvas>();
        canvas.renderMode = RenderMode.ScreenSpaceOverlay;
        // Выше CutsceneCanvas (40), чтобы E/панель не прятались под плашками.
        canvas.sortingOrder = 60;
        var scaler = canvasGo.AddComponent<CanvasScaler>();
        scaler.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
        scaler.referenceResolution = new Vector2(1920f, 1080f);
        canvasGo.AddComponent<GraphicRaycaster>();

        tab = MakeText(canvasGo.transform, "IncomingTab", 22, TextAnchor.MiddleCenter);
        var tabRect = tab.rectTransform;
        tabRect.anchorMin = new Vector2(1f, 0.5f);
        tabRect.anchorMax = new Vector2(1f, 0.5f);
        tabRect.pivot = new Vector2(1f, 0.5f);
        tabRect.sizeDelta = new Vector2(160f, 72f);
        tabRect.anchoredPosition = new Vector2(-16f, 0f);
        tab.color = new Color(0.85f, 0.9f, 0.95f, 0.95f);
        tab.gameObject.SetActive(false);

        var panelGo = new GameObject("PhonePanel");
        panelGo.transform.SetParent(canvasGo.transform, false);
        panel = panelGo.AddComponent<RectTransform>();
        // Тёмный прямоугольник справа — один UI телефона на всю игру.
        panel.anchorMin = new Vector2(1f, 0.5f);
        panel.anchorMax = new Vector2(1f, 0.5f);
        panel.pivot = new Vector2(1f, 0.5f);
        panel.sizeDelta = new Vector2(480f, 560f);
        panel.anchoredPosition = new Vector2(-24f, 0f);
        var background = panelGo.AddComponent<Image>();
        background.color = new Color(0.05f, 0.05f, 0.07f, 0.92f);
        background.raycastTarget = false;

        header = MakeText(panel, "Header", 26, TextAnchor.UpperLeft);
        Stretch(header.rectTransform, 20f, 18f, 20f, 500f);
        header.color = new Color(0.78f, 0.84f, 0.9f);
        header.fontStyle = FontStyle.Bold;

        body = MakeText(panel, "Body", 22, TextAnchor.UpperLeft);
        Stretch(body.rectTransform, 20f, 70f, 20f, 56f);
        body.color = new Color(0.9f, 0.9f, 0.88f);
        body.horizontalOverflow = HorizontalWrapMode.Wrap;
        body.verticalOverflow = VerticalWrapMode.Truncate;

        footer = MakeText(panel, "Footer", 18, TextAnchor.LowerLeft);
        Stretch(footer.rectTransform, 20f, 520f, 20f, 16f);
        footer.color = new Color(0.55f, 0.6f, 0.58f);

        panel.gameObject.SetActive(false);
    }

    static Text MakeText(Transform parent, string name, int size, TextAnchor anchor)
    {
        var go = new GameObject(name);
        go.transform.SetParent(parent, false);
        var text = go.AddComponent<Text>();
        text.font = Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf");
        text.fontSize = size;
        text.alignment = anchor;
        text.raycastTarget = false;
        text.color = Color.white;
        return text;
    }

    static void Stretch(RectTransform rect, float left, float top, float right, float bottom)
    {
        rect.anchorMin = Vector2.zero;
        rect.anchorMax = Vector2.one;
        rect.offsetMin = new Vector2(left, bottom);
        rect.offsetMax = new Vector2(-right, -top);
    }

    static string ContactTitle(string contact)
    {
        if (string.IsNullOrEmpty(contact)) return "?";
        switch (contact)
        {
            case "mom": return "Mom";
            case "anya": return "Anya";
            case "lev": return "Lev";
            case "mila": return "Mila";
            case "desk": return "DESK";
            default: return contact;
        }
    }

    struct HistoryLine
    {
        public string contact;
        public string text;
    }

    [Serializable]
    class DialogueBalanceHint
    {
        public float messageIgnoreSeconds;
        public int relationWarm;
        public int relationCold;
        public int farewellStrikes;
        public RelationStartHint relationStart;
    }

    [Serializable]
    class RelationStartHint
    {
        public int mom = 60;
        public int anya = 40;
        public int lev = 45;
        public int mila = 50;
    }
}
