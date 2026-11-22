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
/// E открывает панель только если есть активное входящее. timeScale не трогаем, тень не снимаем.
/// </summary>
public class Dialogue : MonoBehaviour
{
    const string AutoIgnoreReply = "I'm busy";

    [SerializeField] TextAsset dialogueJson;

    DialoguePing[] pings;
    readonly HashSet<string> flags = new HashSet<string>();
    readonly HashSet<string> closed = new HashSet<string>();
    readonly HashSet<string> offered = new HashSet<string>();
    readonly Dictionary<string, int> relations = new Dictionary<string, int>();
    readonly Dictionary<string, int> warmCounts = new Dictionary<string, int>();
    readonly Dictionary<string, int> coldCounts = new Dictionary<string, int>();
    readonly Queue<DialoguePing> waiting = new Queue<DialoguePing>();

    DialoguePing active;
    int choiceIndex;
    int token;
    bool panelOpen;
    bool waitingForChoice;
    bool timingStarted;
    float playTime;
    float ignoreSeconds = 8f;
    int relationWarm = 15;
    int relationCold = -25;
    int farewellStrikes = 2;

    RectTransform panel;
    Text body;
    Text header;
    Text choices;
    AudioSource notify;

    /// <summary>Открыт выбор на телефоне — курсор чуть тупеет (MessengerSystem проксирует).</summary>
    public bool ChoicePending => waitingForChoice && panelOpen && active != null;

    /// <summary>Есть непрочитанное/активное входящее (для отладки и индикаторов).</summary>
    public bool HasActiveMessage => active != null;

    /// <summary>Последний ответ требовал forced-удар (для боевого агента). Игнор всегда false.</summary>
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
    }

    void OnEnable()
    {
        GameEvents.OnFinaleStarted += HideAll;
    }

    void OnDisable()
    {
        GameEvents.OnFinaleStarted -= HideAll;
    }

    void Update()
    {
        var gm = GameManager.Instance;
        if (gm != null && gm.IsEnded)
            return;

        // Пауза Esc сама ставит timeScale=0; телефон timeScale не трогает.
        if (gm != null && gm.IsPaused)
            return;

        // Таймер пингов — секунды после посадки (EnableCombat), не от StartClock.
        if (gm != null && gm.GameplayActive)
        {
            if (!timingStarted)
                timingStarted = true;
            playTime += Time.deltaTime;
            PollArrivals();
        }

        var keyboard = Keyboard.current;
        if (keyboard == null) return;

        // E: открыть/закрыть только если есть входящее. Без сообщения — ничего.
        if (keyboard.eKey.wasPressedThisFrame)
            TogglePhone();

        if (!waitingForChoice || !panelOpen || active == null) return;
        if (active.choices == null || active.choices.Length == 0)
        {
            if (keyboard.enterKey.wasPressedThisFrame || keyboard.numpadEnterKey.wasPressedThisFrame)
                DismissFarewell();
            return;
        }

        if (keyboard.upArrowKey.wasPressedThisFrame)
        {
            choiceIndex = (choiceIndex - 1 + active.choices.Length) % active.choices.Length;
            Refresh();
        }
        else if (keyboard.downArrowKey.wasPressedThisFrame)
        {
            choiceIndex = (choiceIndex + 1) % active.choices.Length;
            Refresh();
        }
        else if (keyboard.enterKey.wasPressedThisFrame || keyboard.numpadEnterKey.wasPressedThisFrame)
        {
            Commit(active.choices[choiceIndex]);
        }
    }

    void TogglePhone()
    {
        if (active == null)
            return;

        panelOpen = !panelOpen;
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
            choiceIndex = 0;
            waitingForChoice = next.choices != null && next.choices.Length > 0;
            panelOpen = false;
            token++;
            if (notify != null && GeneratedAudio.Notification != null)
                notify.PlayOneShot(GeneratedAudio.Notification, 0.7f);

            // Прощание без выбора — сразу показать и закрыть контакт (удара нет).
            if (!waitingForChoice)
            {
                panelOpen = true;
                CloseContact(next.contact);
                Refresh();
                StartCoroutine(TimeoutOrIgnore(token, ignoreSeconds));
                return;
            }

            StartCoroutine(TimeoutOrIgnore(token, ignoreSeconds));
            Refresh();
            return;
        }

        active = null;
        waitingForChoice = false;
        panelOpen = false;
        Refresh();
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
        // Прощание без выбора — просто закрыть, без «I'm busy».
        if (active.choices == null || active.choices.Length == 0)
        {
            DismissFarewell();
            yield break;
        }
        Ignore();
    }

    /// <summary>
    /// Автоигнор 8 с: ответ «I'm busy», −25 к отношениям, без forcedHit.
    /// </summary>
    void Ignore()
    {
        if (active == null) return;
        var ping = active;
        string contact = ping.contact;
        token++;
        waitingForChoice = false;
        LastReplyForcedHit = false;

        // Игнор = холодный удар по отношениям; удара-призрака нет.
        if (contact != "desk")
        {
            ApplyRelation(contact, false);
            RegisterCold(contact);
            MaybeCloseAfterStrikes(contact);
        }

        // Коротко показать автоответ, даже если панель была закрыта.
        panelOpen = true;
        header.text = ContactTitle(contact);
        body.text = ping.incoming + "\n\nYou: " + AutoIgnoreReply;
        choices.text = string.Empty;
        if (panel != null) panel.gameObject.SetActive(true);

        StartCoroutine(CloseAfter(1.2f, token));
    }

    void DismissFarewell()
    {
        token++;
        waitingForChoice = false;
        LastReplyForcedHit = false;
        active = null;
        panelOpen = false;
        Refresh();
        OpenNext();
    }

    void Commit(DialoguePingChoice choice)
    {
        if (active == null || choice == null) return;

        token++;
        waitingForChoice = false;
        var ping = active;
        string contact = ping.contact;
        bool warm = choice.warm;
        // Desk всегда false; родственники с выбором — true (и поле JSON, и страховка в LoadPings).
        LastReplyForcedHit = contact != "desk" && ping.forcedHit;

        ApplyFlags(choice.set);
        if (contact != "desk")
        {
            ApplyRelation(contact, warm);
            if (warm)
            {
                if (!warmCounts.ContainsKey(contact)) warmCounts[contact] = 0;
                warmCounts[contact]++;
                coldCounts[contact] = 0;
            }
            else
            {
                RegisterCold(contact);
            }
        }

        string reply = choice.reply;
        // Второй холодный маме подряд — особая реплика и замок.
        if (contact == "mom" && !warm && coldCounts.TryGetValue(contact, out int momCold) && momCold >= farewellStrikes)
        {
            reply = "i'll stop knocking. you're grown.";
            CloseContact("mom");
        }
        else if (!warm)
        {
            MaybeCloseAfterStrikes(contact);
        }

        if (HasFlag("anya_closed"))
            CloseContact("anya");
        // Холодный отказ на прогулку — Лев на время замолкает (не пишет в 470).
        if (contact == "lev" && !warm && ping.id == "lev_walk")
            CloseContact("lev");

        panelOpen = true;
        header.text = ContactTitle(contact);
        body.text = ping.incoming + "\n\nYou: " + choice.label + "\n\n" + ContactTitle(contact) + ": " + reply;
        choices.text = string.Empty;
        if (panel != null) panel.gameObject.SetActive(true);

        // Тень диалогом не снимаем. Forced-спавн — зона боя, только флаг + событие ответа.
        GameEvents.MessageReplied(warm);

        StartCoroutine(CloseAfter(1.5f, token));
    }

    IEnumerator CloseAfter(float seconds, int myToken)
    {
        yield return new WaitForSeconds(seconds);
        if (myToken != token) yield break;
        active = null;
        waitingForChoice = false;
        panelOpen = false;
        Refresh();
        OpenNext();
    }

    void RegisterCold(string contact)
    {
        if (!coldCounts.ContainsKey(contact)) coldCounts[contact] = 0;
        coldCounts[contact]++;
    }

    void MaybeCloseAfterStrikes(string contact)
    {
        if (contact == "desk" || contact == "mom") return;
        if (!coldCounts.TryGetValue(contact, out int n) || n < farewellStrikes) return;
        // Аня закрывается явными флагами anya_closed; общий замок — для остальных близких.
        if (contact == "anya") return;
        CloseContact(contact);
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

    bool HasFlag(string id) => flags.Contains(id);

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
        flags.Remove("anya_warm_ge_2");
        flags.Remove("anya_warm_lt_2");

        int levRel = relations.TryGetValue("lev", out int lr) ? lr : 45;
        if (levRel > 25) flags.Add("lev_relation_gt_25");
        else flags.Add("lev_relation_lte_25");

        int anyaWarm = warmCounts.TryGetValue("anya", out int aw) ? aw : 0;
        // Ветка «почти написала» — только если Аня жива и тёплых < 2.
        if (!closed.Contains("anya"))
        {
            if (anyaWarm >= 2) flags.Add("anya_warm_ge_2");
            else flags.Add("anya_warm_lt_2");
        }
    }

    void HideAll()
    {
        token++;
        active = null;
        waitingForChoice = false;
        panelOpen = false;
        waiting.Clear();
        if (panel != null) panel.gameObject.SetActive(false);
    }

    void Refresh()
    {
        bool show = active != null && panelOpen;
        if (panel != null) panel.gameObject.SetActive(show);
        if (!show || active == null) return;

        header.text = ContactTitle(active.contact);
        if (waitingForChoice && active.choices != null && active.choices.Length > 0)
        {
            // Текст сразу блоком, без печатной машинки.
            body.text = active.incoming;
            var builder = new System.Text.StringBuilder();
            for (int i = 0; i < active.choices.Length; i++)
            {
                builder.Append(i == choiceIndex ? "> " : "  ");
                builder.Append(active.choices[i].label);
                if (i < active.choices.Length - 1) builder.Append('\n');
            }
            choices.text = builder.ToString();
        }
        else if (active.choices == null || active.choices.Length == 0)
        {
            body.text = active.incoming;
            choices.text = "(Enter)";
        }
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

        // Desk никогда не forced; у родственника пустое поле JsonUtility читает как false — чиним.
        for (int i = 0; i < pings.Length; i++)
        {
            var p = pings[i];
            if (p == null) continue;
            if (p.contact == "desk")
                p.forcedHit = false;
            else if (p.choices != null && p.choices.Length > 0)
                p.forcedHit = true;
            else
                p.forcedHit = false;
        }

        Debug.Log($"Dialogue: загружено пингов: {pings.Length}");
    }

    void BuildUi()
    {
        var canvasGo = new GameObject("PhoneCanvas");
        var canvas = canvasGo.AddComponent<Canvas>();
        canvas.renderMode = RenderMode.ScreenSpaceOverlay;
        canvas.sortingOrder = 30;
        var scaler = canvasGo.AddComponent<CanvasScaler>();
        scaler.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
        scaler.referenceResolution = new Vector2(1920f, 1080f);
        canvasGo.AddComponent<GraphicRaycaster>();

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
        Stretch(body.rectTransform, 20f, 70f, 20f, 180f);
        body.color = new Color(0.9f, 0.9f, 0.88f);
        body.horizontalOverflow = HorizontalWrapMode.Wrap;
        body.verticalOverflow = VerticalWrapMode.Overflow;

        choices = MakeText(panel, "Choices", 22, TextAnchor.UpperLeft);
        Stretch(choices.rectTransform, 20f, 360f, 20f, 24f);
        choices.color = new Color(0.82f, 0.88f, 0.8f);
        choices.horizontalOverflow = HorizontalWrapMode.Wrap;

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
