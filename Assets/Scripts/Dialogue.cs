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
/// ГГ никогда не отвечает текстом — только чтение входящих (ignore-only).
/// E открывает спрайт телефона; на экране — только непрочитанные (блоки по контакту).
/// Игнор по таймеру: −25 к отношениям / закрытие контакта, без исходящей «I'm busy».
/// forcedHit на открытие E не ставится (ответов нет).
/// Бормотание под ГГ — при открытии телефона с непрочитанными (последний unread 1–9), ~1с после E.
/// </summary>
public class Dialogue : MonoBehaviour
{
    const float HeroMutterDelaySeconds = 1f;
    const string PhoneSpritePath = "Sprites/Phone/phone";
    /// <summary>Отступы тёмного экрана внутри спрайта (доля; спрайт обрезан по корпусу ~515×824).</summary>
    const float ScreenInsetL = 0.110f;
    const float ScreenInsetR = 0.114f;
    const float ScreenInsetT = 0.078f;
    const float ScreenInsetB = 0.078f;
    /// <summary>Ширина строки имени+счётчика в символах m5x7 (после увеличения кегля уже).</summary>
    const int InboxLineWidth = 14;
    const int VisibleInboxBlocks = 3;
    /// <summary>Кегль inbox внутри безеля: 16 было нечитаемо (Mom и т.п.); крупнее ради читаемости.</summary>
    const int InboxFontSize = 26;
    /// <summary>Подсказка [E] close — чуть меньше тела, но тоже крупнее прежних 14.</summary>
    const int FooterFontSize = 20;

    [SerializeField] TextAsset dialogueJson;

    DialoguePing[] pings;
    readonly HashSet<string> flags = new HashSet<string>();
    readonly HashSet<string> closed = new HashSet<string>();
    readonly HashSet<string> offered = new HashSet<string>();
    readonly Dictionary<string, int> relations = new Dictionary<string, int>();
    readonly Dictionary<string, int> coldCounts = new Dictionary<string, int>();
    readonly Queue<DialoguePing> waiting = new Queue<DialoguePing>();
    /// <summary>Полная история входящих (логика отношений); в UI не показываем старые.</summary>
    readonly List<HistoryLine> history = new List<HistoryLine>();
    /// <summary>
    /// Непрочитанные при закрытом телефоне. Не истекают по времени —
    /// живут до открытия E (копия в openInbox + clear) или HideAll.
    /// </summary>
    readonly List<UnreadEntry> unread = new List<UnreadEntry>();
    /// <summary>Снимок inbox на время открытой панели (unread на момент открытия + пришедшие пока смотрим).</summary>
    readonly List<UnreadEntry> openInbox = new List<UnreadEntry>();

    DialoguePing active;
    int token;
    /// <summary>Инвалидирует отложенное бормотание (финал / сброс таймера / новое открытие).</summary>
    int mutterToken;
    Coroutine pendingMutter;
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
    int scrollBlocks;

    RectTransform panel;
    RectTransform screenArea;
    Text body;
    Text footer;
    Text tab;
    AudioSource notify;

    /// <summary>
    /// Есть неотвеченный пинг — курсор тупеет уже с приходом сообщения (GDD), не только при открытой панели.
    /// </summary>
    public bool ChoicePending => pendingIgnore && active != null;

    /// <summary>Есть активное входящее (вкладка / таймер игнора).</summary>
    public bool HasActiveMessage => active != null;

    /// <summary>Можно открыть телефон: активный пинг, история или непрочитанные.</summary>
    public bool CanOpenPhone => active != null || history.Count > 0 || unread.Count > 0;

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
                CancelPendingMutter();
                active = null;
                waiting.Clear();
                offered.Clear();
                history.Clear();
                unread.Clear();
                openInbox.Clear();
                pendingIgnore = false;
                panelOpen = false;
                scrollBlocks = 0;
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

        // E: открыть/закрыть телефон. Без пинга/истории/unread — ничего.
        if (pressedE)
            TogglePhone();

        if (!panelOpen || openInbox.Count == 0) return;
        if (keyboard == null) return;

        // Скролл блоков контактов стрелками (без выбора реплик).
        int blockCount = CountInboxBlocks();
        int maxScroll = Mathf.Max(0, blockCount - VisibleInboxBlocks);
        if (keyboard.upArrowKey.wasPressedThisFrame)
        {
            scrollBlocks = Mathf.Max(0, scrollBlocks - 1);
            Refresh();
        }
        else if (keyboard.downArrowKey.wasPressedThisFrame)
        {
            scrollBlocks = Mathf.Min(maxScroll, scrollBlocks + 1);
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
            // Снимок непрочитанных на экран; бормотание + пометка прочитанным.
            BeginInboxSession();
            string who = active != null ? active.id : "inbox";
            Debug.Log($"Dialogue: E → телефон открыт ({who}, inbox={openInbox.Count}, history={history.Count})");
            MutterForLatestUnreadThenMarkRead();
            scrollBlocks = Mathf.Max(0, CountInboxBlocks() - VisibleInboxBlocks);
        }
        else
        {
            openInbox.Clear();
            scrollBlocks = 0;
            Debug.Log("Dialogue: E → телефон закрыт");
        }
        // Открытие E только чтение — forcedHit / MessageReplied не вызываем.
        LastReplyForcedHit = false;
        Refresh();
    }

    /// <summary>Скопировать текущий unread в openInbox до очистки.</summary>
    void BeginInboxSession()
    {
        openInbox.Clear();
        for (int i = 0; i < unread.Count; i++)
            openInbox.Add(unread[i]);
    }

    /// <summary>
    /// При открытии телефона: если есть непрочитанные — через ~1с бормотать только за последнего
    /// (если у него есть heroMutter, пинги 1–9). С 10-го у последнего — тишина.
    /// Весь текущий unread помечается прочитанным сразу; повторно на том же наборе не спамим.
    /// Закрытие панели до конца задержки бормотание не отменяет (подпись под ГГ, не в чате).
    /// </summary>
    void MutterForLatestUnreadThenMarkRead()
    {
        if (unread.Count == 0) return;

        UnreadEntry latest = unread[unread.Count - 1];
        unread.Clear();

        if (string.IsNullOrEmpty(latest.heroMutter)) return;

        CancelPendingMutter();
        pendingMutter = StartCoroutine(DelayedHeroMutter(mutterToken, latest.heroMutter));
    }

    IEnumerator DelayedHeroMutter(int myToken, string line)
    {
        float t = 0f;
        while (t < HeroMutterDelaySeconds)
        {
            if (myToken != mutterToken) yield break;
            t += Time.deltaTime;
            yield return null;
        }
        if (myToken != mutterToken) yield break;
        pendingMutter = null;
        GameEvents.HeroMutter(line);
    }

    void CancelPendingMutter()
    {
        mutterToken++;
        if (pendingMutter != null)
        {
            StopCoroutine(pendingMutter);
            pendingMutter = null;
        }
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
            // Опоздавшие в очереди НЕ сбрасываем: unread-блок контакта должен
            // дойти до телефона и жить до открытия E (счётчик пропусков тоже).
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
            // Бормотание здесь НЕ шлём — только при открытии телефона с unread.
            GameEvents.MessageArrived(next.contact);

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
        // Счётчик «пропущенных» для виньетки (ShadowManager): только автоигнор по таймеру.
        GameEvents.MessageIgnored();
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
        var entry = MakeUnread(ping.id, ping.contact, ping.incoming, ping.heroMutter);
        // Уже смотрит в телефон — сразу на экран сессии, без бормотания на приход.
        if (panelOpen)
        {
            openInbox.Add(entry);
            scrollBlocks = Mathf.Max(0, CountInboxBlocks() - VisibleInboxBlocks);
        }
        else
            unread.Add(entry);
    }

    void EnqueueUnread(string pingId, string contact, string text, string heroMutter)
    {
        unread.Add(MakeUnread(pingId, contact, text, heroMutter));
    }

    static UnreadEntry MakeUnread(string pingId, string contact, string text, string heroMutter)
    {
        return new UnreadEntry
        {
            pingId = pingId ?? string.Empty,
            contact = contact ?? string.Empty,
            text = text ?? string.Empty,
            heroMutter = heroMutter ?? string.Empty
        };
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
            const string farewell =
                "ok. i get it. i'm not going to be the guy who writes into a mute chat. bye.";
            history.Add(new HistoryLine { contact = "lev", text = farewell });
            var entry = MakeUnread("lev_farewell_auto", "lev", farewell, string.Empty);
            if (panelOpen)
            {
                openInbox.Add(entry);
                scrollBlocks = Mathf.Max(0, CountInboxBlocks() - VisibleInboxBlocks);
            }
            else
                unread.Add(entry);
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
        CancelPendingMutter();
        active = null;
        pendingIgnore = false;
        panelOpen = false;
        waiting.Clear();
        unread.Clear();
        openInbox.Clear();
        scrollBlocks = 0;
        if (panel != null) panel.gameObject.SetActive(false);
        if (tab != null) tab.gameObject.SetActive(false);
    }

    void Refresh()
    {
        bool hasActive = active != null;
        bool showPanel = panelOpen && (CanOpenPhone || openInbox.Count > 0);
        // Вкладка «входящее», если есть активный пинг, а панель ещё не открыта.
        if (tab != null)
        {
            tab.gameObject.SetActive(hasActive && !panelOpen && pendingIgnore);
            if (hasActive) tab.text = ContactTitle(active.contact) + "\n[E]";
        }
        if (panel != null) panel.gameObject.SetActive(showPanel);
        if (!showPanel) return;

        body.text = BuildInboxText();
        int blocks = CountInboxBlocks();
        footer.text = blocks > VisibleInboxBlocks
            ? "↑↓ scroll  ·  [E] close"
            : "[E] close";
    }

    /// <summary>
    /// Только непрочитанные: один блок на контакт (имя + текст + счётчик справа),
    /// между пользователями — горизонтальный разделитель. Старые прочитанные не показываем,
    /// но непрочитанные не выкидываем, пока игрок не откроет телефон.
    /// </summary>
    string BuildInboxText()
    {
        List<InboxBlock> blocks = BuildInboxBlocks();
        if (blocks.Count == 0) return string.Empty;

        int maxScroll = Mathf.Max(0, blocks.Count - VisibleInboxBlocks);
        scrollBlocks = Mathf.Clamp(scrollBlocks, 0, maxScroll);
        int start = scrollBlocks;
        int end = Mathf.Min(blocks.Count, start + VisibleInboxBlocks);

        var builder = new System.Text.StringBuilder();
        // ASCII: m5x7 без box-drawing глифов.
        string sep = new string('-', InboxLineWidth);
        for (int i = start; i < end; i++)
        {
            InboxBlock block = blocks[i];
            builder.Append(FormatNameLine(ContactTitle(block.contact), block.missed));
            builder.Append('\n');
            builder.Append(WrapInboxLine(block.text ?? string.Empty));
            if (i < end - 1)
            {
                builder.Append('\n');
                builder.Append(sep);
                builder.Append('\n');
            }
        }
        return builder.ToString();
    }

    /// <summary>Жёсткий перенос длинных реплик, чтобы не вылезали за безель.</summary>
    static string WrapInboxLine(string text)
    {
        if (string.IsNullOrEmpty(text) || text.Length <= InboxLineWidth)
            return text;

        var builder = new System.Text.StringBuilder();
        int i = 0;
        while (i < text.Length)
        {
            int remaining = text.Length - i;
            int take = Mathf.Min(InboxLineWidth, remaining);
            if (take < remaining)
            {
                // Предпочитаем разрыв по пробелу внутри окна.
                int split = text.LastIndexOf(' ', i + take - 1, take);
                if (split >= i) take = split - i + 1;
            }
            if (take <= 0) take = 1;
            builder.Append(text, i, take);
            i += take;
            while (i < text.Length && text[i] == ' ') i++;
            if (i < text.Length) builder.Append('\n');
        }
        return builder.ToString();
    }

    int CountInboxBlocks() => BuildInboxBlocks().Count;

    List<InboxBlock> BuildInboxBlocks()
    {
        var order = new List<string>();
        var map = new Dictionary<string, InboxBlock>();
        for (int i = 0; i < openInbox.Count; i++)
        {
            UnreadEntry entry = openInbox[i];
            string key = entry.contact ?? string.Empty;
            if (!map.TryGetValue(key, out InboxBlock block))
            {
                order.Add(key);
                map[key] = new InboxBlock
                {
                    contact = key,
                    text = entry.text ?? string.Empty,
                    missed = 1
                };
            }
            else
            {
                block.text = entry.text ?? string.Empty;
                block.missed++;
                map[key] = block;
            }
        }

        var result = new List<InboxBlock>(order.Count);
        for (int i = 0; i < order.Count; i++)
            result.Add(map[order[i]]);
        return result;
    }

    static string FormatNameLine(string title, int missed)
    {
        string right = missed.ToString();
        int pad = InboxLineWidth - title.Length - right.Length;
        if (pad < 1) pad = 1;
        return title + new string(' ', pad) + right;
    }

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

        // Корпус телефона — спрайт; сообщения только в тёмной зоне экрана.
        var panelGo = new GameObject("PhonePanel");
        panelGo.transform.SetParent(canvasGo.transform, false);
        panel = panelGo.AddComponent<RectTransform>();
        panel.anchorMin = new Vector2(1f, 0.5f);
        panel.anchorMax = new Vector2(1f, 0.5f);
        panel.pivot = new Vector2(1f, 0.5f);
        // Спрайт обрезан по корпусу ~515×824 (без пустого квадратного холста).
        panel.sizeDelta = new Vector2(400f, 640f);
        panel.anchoredPosition = new Vector2(-24f, 0f);

        var frame = panelGo.AddComponent<Image>();
        frame.raycastTarget = false;
        frame.preserveAspect = true;
        Sprite phoneSprite = LoadPhoneSprite();
        if (phoneSprite != null)
        {
            frame.sprite = phoneSprite;
            frame.color = Color.white;
            frame.type = Image.Type.Simple;
            // Подгоняем панель под реальный aspect спрайта, чтобы insets совпали с безелем.
            float aspect = phoneSprite.rect.width / Mathf.Max(1f, phoneSprite.rect.height);
            float height = panel.sizeDelta.y;
            panel.sizeDelta = new Vector2(height * aspect, height);
        }
        else
        {
            // Fallback без ассета — тёмная плашка, чтобы телефон всё равно открывался.
            frame.color = new Color(0.05f, 0.05f, 0.1f, 0.94f);
        }

        var screenGo = new GameObject("PhoneScreen");
        screenGo.transform.SetParent(panel, false);
        screenArea = screenGo.AddComponent<RectTransform>();
        StretchFraction(screenArea, ScreenInsetL, ScreenInsetT, ScreenInsetR, ScreenInsetB);
        // Клип: текст не рисуется поверх безеля/комнаты.
        screenGo.AddComponent<RectMask2D>();

        // Текст inbox только в тёмной зоне экрана; кегль крупный — иначе Mom/превью нечитаемы.
        body = MakeText(screenArea, "InboxBody", InboxFontSize, TextAnchor.UpperLeft);
        Stretch(body.rectTransform, 8f, 8f, 8f, 40f);
        body.color = new Color(0.86f, 0.88f, 0.94f);
        body.horizontalOverflow = HorizontalWrapMode.Wrap;
        body.verticalOverflow = VerticalWrapMode.Truncate;
        body.lineSpacing = 1.05f;
        body.resizeTextForBestFit = false;

        footer = MakeText(screenArea, "Footer", FooterFontSize, TextAnchor.LowerCenter);
        var footerRect = footer.rectTransform;
        footerRect.anchorMin = new Vector2(0f, 0f);
        footerRect.anchorMax = new Vector2(1f, 0f);
        footerRect.pivot = new Vector2(0.5f, 0f);
        // Высота футера под FooterFontSize=20, чтобы [E] close не обрезался.
        footerRect.sizeDelta = new Vector2(-16f, 32f);
        footerRect.anchoredPosition = new Vector2(0f, 6f);
        footer.color = new Color(0.55f, 0.58f, 0.7f);
        footer.horizontalOverflow = HorizontalWrapMode.Wrap;
        footer.verticalOverflow = VerticalWrapMode.Truncate;

        panel.gameObject.SetActive(false);
    }

    /// <summary>Спрайт телефона из Resources; Point filter. Fallback — Texture2D → Sprite.Create.</summary>
    static Sprite LoadPhoneSprite()
    {
        Sprite imported = Resources.Load<Sprite>(PhoneSpritePath);
        Texture2D tex = imported != null ? imported.texture : Resources.Load<Texture2D>(PhoneSpritePath);
        if (tex == null)
        {
            Debug.LogWarning("Dialogue: нет ассета Resources/" + PhoneSpritePath);
            return null;
        }

        tex.filterMode = FilterMode.Point;
        tex.wrapMode = TextureWrapMode.Clamp;
        if (imported != null) return imported;

        return Sprite.Create(
            tex,
            new Rect(0f, 0f, tex.width, tex.height),
            new Vector2(0.5f, 0.5f),
            100f);
    }

    static Text MakeText(Transform parent, string name, int size, TextAnchor anchor)
    {
        var go = new GameObject(name);
        go.transform.SetParent(parent, false);
        var text = go.AddComponent<Text>();
        PixelFonts.Apply(text);
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

    /// <summary>Растянуть дочерний Rect долями родителя (экран внутри безеля).</summary>
    static void StretchFraction(RectTransform rect, float left, float top, float right, float bottom)
    {
        rect.anchorMin = new Vector2(left, bottom);
        rect.anchorMax = new Vector2(1f - right, 1f - top);
        rect.offsetMin = Vector2.zero;
        rect.offsetMax = Vector2.zero;
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

    struct UnreadEntry
    {
        public string pingId;
        public string contact;
        public string text;
        /// <summary>Пусто для пингов 10+ — при открытии тишина, даже если раньше в unread были 1–9.</summary>
        public string heroMutter;
    }

    struct InboxBlock
    {
        public string contact;
        public string text;
        public int missed;
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
