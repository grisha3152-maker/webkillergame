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
/// E открывает спрайт телефона; внутри экрана — список Messages (без аватаров).
/// Контакты из history (не пустеют после прочтения); справа время или missed.
/// Игнор по таймеру: −25 к отношениям / закрытие контакта, без исходящей «I'm busy».
/// forcedHit на открытие E не ставится (ответов нет).
/// Бормотание под ГГ — при открытии телефона с непрочитанными (последний unread 1–9), ~1с после E.
/// </summary>
public class Dialogue : MonoBehaviour
{
    const float HeroMutterDelaySeconds = 1f;
    /// <summary>Финальный пинг Лева — триггер narrative ending (EN в JSON, RU на экране катсцены).</summary>
    const string AlivePingId = "lev_alive";
    /// <summary>Флаг set[] при доставке lev_alive — переживает mark-read / пустой openInbox.</summary>
    const string AlivePingFlag = "lev_alive_sent";
    /// <summary>Текст пинга в dialogue.json (EN). RU — если когда-то подставят в данные.</summary>
    const string AlivePingTextEn = "are you even alive?";
    const string AlivePingTextRu = "ты там вообще живой?";
    /// <summary>Секунд с открытым телефоном на lev_alive до старта каскада.</summary>
    const float AliveEndingHoldSeconds = 5f;
    const string PhoneSpritePath = "Sprites/Phone/phone";
    /// <summary>Отступы экрана внутри спрайта (доля; спрайт обрезан по корпусу ~515×824).</summary>
    const float ScreenInsetL = 0.110f;
    const float ScreenInsetR = 0.114f;
    const float ScreenInsetT = 0.078f;
    const float ScreenInsetB = 0.078f;
    /// <summary>Ширина строки имени+времени/missed в символах m5x7 (без аватаров — чуть шире).</summary>
    const int InboxLineWidth = 16;
    const int VisibleInboxBlocks = 3;
    /// <summary>Кегль списка чатов; крупнее 16 — иначе Mom/превью нечитаемы в безеле.</summary>
    const int InboxFontSize = 22;
    /// <summary>Заголовок «Messages» в шапке.</summary>
    const int HeaderFontSize = 22;
    /// <summary>Подсказка [E] close.</summary>
    const int FooterFontSize = 18;
    /// <summary>Высота шапки Messages (px в reference resolution панели).</summary>
    const float HeaderBarHeight = 36f;
    /// <summary>Игровые часы: 21:00 + playTime (поздний вечер за столом).</summary>
    const int ClockBaseHour = 21;

    // Палитра списка Messages (как в messages-ui-ref): muted blue-gray шапка, светлый фон.
    static readonly Color ScreenBgColor = new Color(0.94f, 0.94f, 0.95f, 1f);
    static readonly Color HeaderBarColor = new Color(0.38f, 0.44f, 0.50f, 1f);
    static readonly Color HeaderTitleColor = new Color(0.96f, 0.97f, 0.98f, 1f);
    static readonly Color NameTextColor = new Color(0.18f, 0.20f, 0.24f, 1f);
    static readonly Color PreviewTextColor = new Color(0.42f, 0.44f, 0.48f, 1f);
    static readonly Color MetaTextColor = new Color(0.45f, 0.48f, 0.52f, 1f);
    static readonly Color DividerColor = new Color(0.78f, 0.80f, 0.82f, 1f);
    static readonly Color FooterHintColor = new Color(0.50f, 0.54f, 0.58f, 1f);

    [SerializeField] TextAsset dialogueJson;

    DialoguePing[] pings;
    readonly HashSet<string> flags = new HashSet<string>();
    readonly HashSet<string> closed = new HashSet<string>();
    readonly HashSet<string> offered = new HashSet<string>();
    readonly Dictionary<string, int> relations = new Dictionary<string, int>();
    readonly Dictionary<string, int> coldCounts = new Dictionary<string, int>();
    readonly Queue<DialoguePing> waiting = new Queue<DialoguePing>();
    /// <summary>Полная история входящих: список контактов и последний текст на экране телефона.</summary>
    readonly List<HistoryLine> history = new List<HistoryLine>();
    /// <summary>
    /// Непрочитанные при закрытом телефоне. Не истекают по времени и не вытесняются
    /// новыми пингами: пишутся сразу в PollArrivals (до очереди ignore), живут до
    /// открытия E (копия в openInbox + clear) или HideAll. Очистка unread не стирает
    /// контактный список — он строится из history.
    /// </summary>
    readonly List<UnreadEntry> unread = new List<UnreadEntry>();
    /// <summary>
    /// Снимок непрочитанных на время открытой панели (unread на момент открытия + пришедшие
    /// пока смотрим). Даёт счётчик missed; сами блоки контактов — из history.
    /// </summary>
    readonly List<UnreadEntry> openInbox = new List<UnreadEntry>();

    DialoguePing active;
    int token;
    /// <summary>Инвалидирует отложенное бормотание (финал / сброс таймера / новое открытие).</summary>
    int mutterToken;
    Coroutine pendingMutter;
    Coroutine aliveHoldRoutine;
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
    Text headerTitle;
    Text body;
    Text footer;
    Text tab;
    AudioSource notify;
    static Sprite uiWhiteSprite;

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

        if (!panelOpen) return;
        if (keyboard == null) return;

        // Скролл блоков контактов стрелками (без выбора реплик).
        int blockCount = CountInboxBlocks();
        if (blockCount == 0) return;
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
            // Снимок unread → счётчики; список контактов остаётся из history после mark-read.
            BeginInboxSession();
            string who = active != null ? active.id : "inbox";
            Debug.Log(
                $"Dialogue: E → телефон открыт ({who}, unreadSession={openInbox.Count}, history={history.Count}, contacts={CountInboxBlocks()})");
            // После phone-chat-persist контакты живут в history: mark-read чистит unread,
            // поэтому триггер не должен зависеть только от openInbox (см. ShouldArmAliveEnding).
            TryStartAliveEndingHold();
            MutterForLatestUnreadThenMarkRead();
            scrollBlocks = Mathf.Max(0, CountInboxBlocks() - VisibleInboxBlocks);
        }
        else
        {
            CancelAliveEndingHold();
            openInbox.Clear();
            scrollBlocks = 0;
            Debug.Log("Dialogue: E → телефон закрыт");
        }
        // Открытие E только чтение — forcedHit / MessageReplied не вызываем.
        LastReplyForcedHit = false;
        Refresh();
    }

    /// <summary>EN/RU текст финального пинга Лева (id или язык данных).</summary>
    static bool IsAlivePingText(string text)
    {
        if (string.IsNullOrEmpty(text)) return false;
        string t = text.Trim();
        return string.Equals(t, AlivePingTextEn, StringComparison.OrdinalIgnoreCase)
            || string.Equals(t, AlivePingTextRu, StringComparison.Ordinal);
    }

    /// <summary>Есть ли в текущем inbox-снимке пинг lev_alive (id или текст EN/RU).</summary>
    bool OpenInboxHasAlivePing()
    {
        for (int i = 0; i < openInbox.Count; i++)
        {
            UnreadEntry e = openInbox[i];
            if (e.pingId == AlivePingId || IsAlivePingText(e.text))
                return true;
        }
        return false;
    }

    /// <summary>Уже прочитанный lev_alive всё ещё виден в history (после mark-read).</summary>
    bool HistoryHasAlivePing()
    {
        for (int i = 0; i < history.Count; i++)
        {
            HistoryLine line = history[i];
            if (line.contact == "lev" && IsAlivePingText(line.text))
                return true;
        }
        return false;
    }

    /// <summary>
    /// Открыли телефон, пока финальный пинг Лева «на экране»: unread-сессия,
    /// флаг доставки, или строка в history после mark-read / reopen.
    /// </summary>
    bool ShouldArmAliveEnding()
    {
        if (OpenInboxHasAlivePing()) return true;
        if (flags.Contains(AlivePingFlag)) return true;
        return HistoryHasAlivePing();
    }

    void TryStartAliveEndingHold()
    {
        if (!ShouldArmAliveEnding()) return;
        var gm = GameManager.Instance;
        if (gm == null || gm.IsEnded) return;

        CancelAliveEndingHold(disarm: false);
        gm.ArmAliveEnding();
        aliveHoldRoutine = StartCoroutine(AliveEndingHoldThenBegin());
        Debug.Log("Dialogue: lev_alive открыт — через 5с narrative ending (телефон должен остаться открытым).");
    }

    void CancelAliveEndingHold(bool disarm = true)
    {
        if (aliveHoldRoutine == null) return;
        StopCoroutine(aliveHoldRoutine);
        aliveHoldRoutine = null;
        // Закрыли телефон до 5с — не блокируем age-40 / shadows навсегда.
        if (disarm && GameManager.Instance != null)
            GameManager.Instance.DisarmAliveEnding();
    }

    IEnumerator AliveEndingHoldThenBegin()
    {
        float t = 0f;
        while (t < AliveEndingHoldSeconds)
        {
            // Спека: 5с с открытым телефоном и видимым сообщением.
            if (!panelOpen)
            {
                aliveHoldRoutine = null;
                if (GameManager.Instance != null)
                    GameManager.Instance.DisarmAliveEnding();
                yield break;
            }
            var gm = GameManager.Instance;
            if (gm != null && gm.IsEnded)
            {
                aliveHoldRoutine = null;
                yield break;
            }
            t += Time.deltaTime;
            yield return null;
        }

        aliveHoldRoutine = null;
        if (!panelOpen)
        {
            if (GameManager.Instance != null)
                GameManager.Instance.DisarmAliveEnding();
            yield break;
        }
        if (GameManager.Instance == null || GameManager.Instance.IsEnded) yield break;
        GameManager.Instance.BeginAliveEnding();
    }

    /// <summary>
    /// Снимок текущих unread в openInbox (счётчики / lev_alive / бормотание).
    /// Список контактов на экране строится из history — очистка unread его не трогает.
    /// </summary>
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
        bool delivered = false;
        for (int i = 0; i < pings.Length; i++)
        {
            var ping = pings[i];
            if (ping == null || string.IsNullOrEmpty(ping.id)) continue;
            if (offered.Contains(ping.id)) continue;
            if (playTime + 0.05f < ping.at) continue;
            // lev_alive — финальный пинг: доставляем даже если контакт уже закрыт игнорами.
            if (closed.Contains(ping.contact) && ping.id != AlivePingId) continue;
            if (!RequiresMet(ping)) continue;

            offered.Add(ping.id);
            // Сразу в unread/history: очередь ignore больше не должна «съедать» контакт.
            // Старые блоки и счётчики живут рядом с новыми до открытия E.
            DeliverIncoming(ping);
            waiting.Enqueue(ping);
            delivered = true;
        }

        if (active == null)
            OpenNext();
        else if (delivered)
            Refresh(); // Новый unread при уже активном пинге — обновить вкладку/панель.
    }

    /// <summary>
    /// Доставка входящего в inbox в момент due (at), до сериализации ignore-таймера.
    /// Так OpenNext continue (closed / requires / опоздание) не выкидывает контакт из unread.
    /// </summary>
    void DeliverIncoming(DialoguePing ping)
    {
        if (ping == null) return;

        // Флаги до history: lev_alive_sent должен быть доступен сразу в TryStartAliveEndingHold
        // (пинг пришёл при уже открытом телефоне).
        ApplyFlags(ping.set);
        AppendHistory(ping);

        if (!firstPingLogged)
        {
            firstPingLogged = true;
            Debug.Log($"Dialogue: первый пинг «{ping.id}» от {ping.contact} at={ping.at:0}s (playTime={playTime:0.0}s). Жми E.");
        }
        else
        {
            Debug.Log($"Dialogue: пинг «{ping.id}» ({ping.contact}) → unread={unread.Count}, openInbox={openInbox.Count}. E открывает чтение.");
        }

        if (notify != null && GeneratedAudio.Notification != null)
            notify.PlayOneShot(GeneratedAudio.Notification, 0.7f);

        // Сигнал камере и прочим слушателям: пришло новое входящее.
        // Бормотание здесь НЕ шлём — только при открытии телефона с unread.
        GameEvents.MessageArrived(ping.contact);
    }

    void OpenNext()
    {
        while (waiting.Count > 0)
        {
            var next = waiting.Dequeue();
            // Inbox уже пополнен в DeliverIncoming. Здесь только active/ignore-таймер.
            // Опоздавшие / закрытые / requires — НЕ трогаем unread (блоки и счётчики остаются).
            if (closed.Contains(next.contact) && next.id != AlivePingId)
                continue;
            SyncDerivedFlags();
            if (!RequiresMet(next))
                continue;

            active = next;
            pendingIgnore = true;
            // Панель сама не всплывает — только вкладка, пока игрок не нажмёт E.
            token++;

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
            text = ping.incoming,
            clockSeconds = playTime
        });
        var entry = MakeUnread(ping.id, ping.contact, ping.incoming, ping.heroMutter);
        // Уже смотрит в телефон — сразу на экран сессии, без бормотания на приход.
        if (panelOpen)
        {
            openInbox.Add(entry);
            scrollBlocks = Mathf.Max(0, CountInboxBlocks() - VisibleInboxBlocks);
            if (ping.id == AlivePingId || IsAlivePingText(ping.incoming))
                TryStartAliveEndingHold();
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
            history.Add(new HistoryLine { contact = "lev", text = farewell, clockSeconds = playTime });
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
        CancelAliveEndingHold();
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
        // Вкладка: активный пинг ИЛИ накопленный unread — не гасим, когда таймер ignore
        // сменил контакт / закончился: старые непрочитанные всё ещё ждут E.
        if (tab != null)
        {
            bool showTab = !panelOpen && ((hasActive && pendingIgnore) || unread.Count > 0);
            tab.gameObject.SetActive(showTab);
            if (showTab)
            {
                if (hasActive && pendingIgnore)
                    tab.text = ContactTitle(active.contact) + "\n[E]";
                else
                    tab.text = UnreadTabLabel();
            }
        }
        if (panel != null) panel.gameObject.SetActive(showPanel);
        if (!showPanel) return;

        body.text = BuildInboxText();
        int blocks = CountInboxBlocks();
        footer.text = blocks > VisibleInboxBlocks
            ? "↑↓ scroll  ·  [E] close"
            : "[E] close";
    }

    /// <summary>Подпись вкладки, когда active уже снят, но unread ещё не открыт.</summary>
    string UnreadTabLabel()
    {
        int contacts = CountUnreadContacts();
        if (contacts <= 0) return "[E]";
        if (contacts == 1 && unread.Count > 0)
            return ContactTitle(unread[unread.Count - 1].contact) + "\n[E]";
        return contacts + " new\n[E]";
    }

    int CountUnreadContacts()
    {
        var seen = new HashSet<string>();
        for (int i = 0; i < unread.Count; i++)
        {
            string key = unread[i].contact ?? string.Empty;
            if (!seen.Contains(key))
                seen.Add(key);
        }
        return seen.Count;
    }

    /// <summary>
    /// Список Messages: имя слева, время или missed справа, превью под именем, разделители.
    /// Без аватаров. Контакты из history; missed — только openInbox сессии.
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
        // ASCII-разделитель: m5x7 без box-drawing; цвет через rich text.
        string sep = ColorTag(DividerColor, new string('-', InboxLineWidth));

        for (int i = start; i < end; i++)
        {
            InboxBlock block = blocks[i];
            string title = ContactTitle(block.contact);
            string right = FormatRowMeta(block.missed, block.clockSeconds);
            builder.Append(FormatColoredNameLine(title, right));
            builder.Append('\n');
            builder.Append(ColorTag(PreviewTextColor, PreviewLine(block.text ?? string.Empty)));
            if (i < end - 1)
            {
                builder.Append('\n');
                builder.Append(sep);
                builder.Append('\n');
            }
        }

        return builder.ToString();
    }

    /// <summary>Одна строка превью; длинные реплики обрезаем, чтобы не лезли за безель.</summary>
    static string PreviewLine(string text)
    {
        if (string.IsNullOrEmpty(text)) return string.Empty;
        if (text.Length <= InboxLineWidth) return text;
        int keep = Mathf.Max(1, InboxLineWidth - 2);
        return text.Substring(0, keep) + "..";
    }

    int CountInboxBlocks() => BuildInboxBlocks().Count;

    /// <summary>
    /// Блоки: все контакты из history (порядок первого появления, текст/время — последнее сообщение).
    /// missed — сколько непрочитанных в openInbox для контакта (0 после mark-read / без новых).
    /// </summary>
    List<InboxBlock> BuildInboxBlocks()
    {
        var order = new List<string>();
        var map = new Dictionary<string, InboxBlock>();

        for (int i = 0; i < history.Count; i++)
        {
            HistoryLine line = history[i];
            string key = line.contact ?? string.Empty;
            if (!map.TryGetValue(key, out InboxBlock block))
            {
                order.Add(key);
                map[key] = new InboxBlock
                {
                    contact = key,
                    text = line.text ?? string.Empty,
                    clockSeconds = line.clockSeconds,
                    missed = 0
                };
            }
            else
            {
                block.text = line.text ?? string.Empty;
                block.clockSeconds = line.clockSeconds;
                map[key] = block;
            }
        }

        // Счётчик unread/missed: снимок сессии + пришедшие пока телефон открыт.
        for (int i = 0; i < openInbox.Count; i++)
        {
            UnreadEntry entry = openInbox[i];
            string key = entry.contact ?? string.Empty;
            if (!map.TryGetValue(key, out InboxBlock block))
            {
                // Fallback: сообщение должно уже быть в history через AppendHistory.
                order.Add(key);
                map[key] = new InboxBlock
                {
                    contact = key,
                    text = entry.text ?? string.Empty,
                    clockSeconds = playTime,
                    missed = 1
                };
            }
            else
            {
                block.missed++;
                map[key] = block;
            }
        }

        var result = new List<InboxBlock>(order.Count);
        for (int i = 0; i < order.Count; i++)
            result.Add(map[order[i]]);
        return result;
    }

    /// <summary>Справа: missed если есть непрочитанные, иначе часы последнего сообщения.</summary>
    static string FormatRowMeta(int missed, float clockSeconds)
    {
        if (missed > 0) return missed.ToString();
        return FormatClock(clockSeconds);
    }

    static string FormatClock(float secondsFromStart)
    {
        int total = Mathf.Max(0, Mathf.FloorToInt(ClockBaseHour * 3600f + secondsFromStart));
        int h = (total / 3600) % 24;
        int m = (total / 60) % 60;
        return h.ToString() + ":" + m.ToString("00");
    }

    /// <summary>Имя (тёмный) + pad + время/missed (приглушённый), влезает в InboxLineWidth.</summary>
    static string FormatColoredNameLine(string title, string right)
    {
        string left = title ?? string.Empty;
        string meta = right ?? string.Empty;
        if (string.IsNullOrEmpty(meta))
            return ColorTag(NameTextColor, left);

        if (left.Length + 1 + meta.Length > InboxLineWidth)
        {
            int keep = Mathf.Max(1, InboxLineWidth - meta.Length - 1);
            if (left.Length > keep) left = left.Substring(0, keep);
        }
        int pad = InboxLineWidth - left.Length - meta.Length;
        if (pad < 1) pad = 1;
        return ColorTag(NameTextColor, left)
               + new string(' ', pad)
               + ColorTag(MetaTextColor, meta);
    }

    static string ColorTag(Color c, string text)
    {
        return "<color=#" + ColorUtility.ToHtmlStringRGB(c) + ">" + text + "</color>";
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
        // Клип: UI не рисуется поверх безеля/комнаты.
        screenGo.AddComponent<RectMask2D>();

        // Светлый фон списка Messages (корпус phone.png снаружи).
        var screenBg = screenGo.AddComponent<Image>();
        screenBg.sprite = UiWhiteSprite();
        screenBg.type = Image.Type.Simple;
        screenBg.color = ScreenBgColor;
        screenBg.raycastTarget = false;

        // Шапка «Messages» — muted blue-gray как в референсе.
        var headerGo = new GameObject("MessagesHeader");
        headerGo.transform.SetParent(screenArea, false);
        var headerRect = headerGo.AddComponent<RectTransform>();
        headerRect.anchorMin = new Vector2(0f, 1f);
        headerRect.anchorMax = new Vector2(1f, 1f);
        headerRect.pivot = new Vector2(0.5f, 1f);
        headerRect.sizeDelta = new Vector2(0f, HeaderBarHeight);
        headerRect.anchoredPosition = Vector2.zero;
        var headerBg = headerGo.AddComponent<Image>();
        headerBg.sprite = UiWhiteSprite();
        headerBg.type = Image.Type.Simple;
        headerBg.color = HeaderBarColor;
        headerBg.raycastTarget = false;

        headerTitle = MakeText(headerGo.transform, "MessagesTitle", HeaderFontSize, TextAnchor.MiddleLeft);
        Stretch(headerTitle.rectTransform, 10f, 2f, 10f, 2f);
        headerTitle.text = "Messages";
        headerTitle.color = HeaderTitleColor;
        headerTitle.horizontalOverflow = HorizontalWrapMode.Overflow;
        headerTitle.verticalOverflow = VerticalWrapMode.Truncate;

        // Список чатов под шапкой: имя | время/missed, превью, разделители. Без аватаров.
        body = MakeText(screenArea, "InboxBody", InboxFontSize, TextAnchor.UpperLeft);
        Stretch(body.rectTransform, 8f, HeaderBarHeight + 4f, 8f, 34f);
        body.color = NameTextColor;
        body.supportRichText = true;
        body.horizontalOverflow = HorizontalWrapMode.Wrap;
        body.verticalOverflow = VerticalWrapMode.Truncate;
        body.lineSpacing = 1.02f;
        body.resizeTextForBestFit = false;

        footer = MakeText(screenArea, "Footer", FooterFontSize, TextAnchor.LowerCenter);
        var footerRect = footer.rectTransform;
        footerRect.anchorMin = new Vector2(0f, 0f);
        footerRect.anchorMax = new Vector2(1f, 0f);
        footerRect.pivot = new Vector2(0.5f, 0f);
        footerRect.sizeDelta = new Vector2(-12f, 28f);
        footerRect.anchoredPosition = new Vector2(0f, 4f);
        footer.color = FooterHintColor;
        footer.horizontalOverflow = HorizontalWrapMode.Wrap;
        footer.verticalOverflow = VerticalWrapMode.Truncate;

        panel.gameObject.SetActive(false);
    }

    /// <summary>1×1 белый спрайт для Image (фон экрана / шапка).</summary>
    static Sprite UiWhiteSprite()
    {
        if (uiWhiteSprite != null) return uiWhiteSprite;
        var tex = new Texture2D(1, 1, TextureFormat.RGBA32, false);
        tex.filterMode = FilterMode.Point;
        tex.wrapMode = TextureWrapMode.Clamp;
        tex.SetPixel(0, 0, Color.white);
        tex.Apply(false, true);
        uiWhiteSprite = Sprite.Create(tex, new Rect(0f, 0f, 1f, 1f), new Vector2(0.5f, 0.5f), 1f);
        return uiWhiteSprite;
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
        /// <summary>playTime на момент доставки — для часов справа в списке.</summary>
        public float clockSeconds;
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
        public float clockSeconds;
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
