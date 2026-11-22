using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.UI;

/// <summary>
/// Совместимость с RadialAimController.ChoiceIsOpen.
/// Если в сцене есть Dialogue — только прокси ChoicePending, свой UI не строим.
/// </summary>
public class MessengerSystem : MonoBehaviour
{
    [SerializeField] private TextAsset dialogues;

    DialogueDatabase database;
    readonly Queue<int> pending = new Queue<int>();
    readonly List<bool> started = new List<bool>();

    int active = -1;
    int messageIndex;
    int choiceIndex;
    int token;
    bool waitingForChoice;
    bool panelOpen;
    bool proxyOnly;

    RectTransform panel;
    Text body;
    Text header;
    Text choices;
    Text tab;
    AudioSource notify;

    public bool ChoicePending
    {
        get
        {
            if (proxyOnly)
            {
                var dialogue = FindAnyObjectByType<Dialogue>();
                return dialogue != null && dialogue.ChoicePending;
            }
            return waitingForChoice && panelOpen && active >= 0;
        }
    }

    void Awake()
    {
        // Dialogue добавляется раньше в bootstrap — тогда мы только прокси.
        if (FindAnyObjectByType<Dialogue>() != null)
        {
            proxyOnly = true;
            return;
        }

        if (dialogues == null)
            dialogues = Resources.Load<TextAsset>("dialogues");
        if (dialogues != null)
            database = JsonUtility.FromJson<DialogueDatabase>(dialogues.text);

        if (database != null && database.threads != null)
        {
            for (int i = 0; i < database.threads.Length; i++)
                started.Add(false);
        }

        BuildUi();
        notify = gameObject.AddComponent<AudioSource>();
        notify.playOnAwake = false;
        notify.spatialBlend = 0f;
    }

    void OnEnable()
    {
        if (proxyOnly) return;
        GameEvents.OnAgeChanged += OnAge;
        GameEvents.OnFinaleStarted += HideAll;
    }

    void OnDisable()
    {
        if (proxyOnly) return;
        GameEvents.OnAgeChanged -= OnAge;
        GameEvents.OnFinaleStarted -= HideAll;
    }

    void Update()
    {
        if (proxyOnly) return;
        if (GameManager.Instance != null && (GameManager.Instance.IsPaused || GameManager.Instance.IsEnded))
            return;

        if (GameManager.Instance != null)
            OnAge(GameManager.Instance.Age);

        var keyboard = Keyboard.current;
        if (keyboard == null) return;

        if (keyboard.qKey.wasPressedThisFrame && active >= 0)
        {
            panelOpen = !panelOpen;
            Refresh();
        }

        if (!waitingForChoice || !panelOpen || active < 0) return;

        var thread = database.threads[active];
        var message = thread.messages[messageIndex];
        if (message.choices == null || message.choices.Length == 0) return;

        if (keyboard.upArrowKey.wasPressedThisFrame)
        {
            choiceIndex = (choiceIndex - 1 + message.choices.Length) % message.choices.Length;
            Refresh();
        }
        else if (keyboard.downArrowKey.wasPressedThisFrame)
        {
            choiceIndex = (choiceIndex + 1) % message.choices.Length;
            Refresh();
        }
        else if (keyboard.enterKey.wasPressedThisFrame || keyboard.numpadEnterKey.wasPressedThisFrame)
        {
            Commit(message.choices[choiceIndex]);
        }
    }

    void OnAge(float age)
    {
        if (database == null || database.threads == null) return;
        if (GameManager.Instance != null && !GameManager.Instance.GameplayActive) return;

        for (int i = 0; i < database.threads.Length; i++)
        {
            if (started[i]) continue;
            if (age + 0.05f < database.threads[i].triggerAge) continue;
            started[i] = true;
            pending.Enqueue(i);
        }

        if (active < 0)
            OpenNext();
    }

    void OpenNext()
    {
        if (pending.Count == 0)
        {
            active = -1;
            waitingForChoice = false;
            Refresh();
            return;
        }

        active = pending.Dequeue();
        messageIndex = 0;
        choiceIndex = 0;
        waitingForChoice = HasChoices(active, 0);
        panelOpen = false;
        token++;
        if (notify != null)
        {
            notify.PlayOneShot(GeneratedAudio.Notification, 0.7f);
        }
        var thread = database.threads[active];
        float timeout = thread.timeoutSeconds > 0f ? thread.timeoutSeconds : 25f;
        StartCoroutine(Timeout(token, timeout));
        Refresh();
    }

    IEnumerator Timeout(int myToken, float seconds)
    {
        float t = 0f;
        while (t < seconds)
        {
            if (myToken != token) yield break;
            t += Time.deltaTime;
            yield return null;
        }
        if (myToken != token || active < 0) yield break;
        Ignore();
    }

    void Ignore()
    {
        var thread = database.threads[active];
        token++;
        waitingForChoice = false;
        panelOpen = true;
        body.text = thread.messages[0].text + "\n\nYou: " + thread.autoIgnoreReply;
        header.text = thread.contactName;
        choices.text = string.Empty;
        StartCoroutine(CloseAfter(1.3f, token));
    }

    void Commit(DialogueChoice choice)
    {
        token++;
        waitingForChoice = false;
        var thread = database.threads[active];
        int next = choice.nextIndex;
        string reply = "You: " + choice.text;
        string follow = string.Empty;
        if (thread.messages != null && next >= 0 && next < thread.messages.Length)
        {
            follow = thread.messages[next].sender + ": " + thread.messages[next].text;
            messageIndex = next;
        }

        body.text = thread.messages[0].text + "\n\n" + reply + (string.IsNullOrEmpty(follow) ? string.Empty : "\n\n" + follow);
        choices.text = string.Empty;
        // Тень диалогом не снимаем (GDD).
        GameEvents.MessageReplied(choice.isPositive);
        StartCoroutine(CloseAfter(1.6f, token));
    }

    IEnumerator CloseAfter(float seconds, int myToken)
    {
        yield return new WaitForSeconds(seconds);
        if (myToken != token) yield break;
        active = -1;
        waitingForChoice = false;
        panelOpen = false;
        OpenNext();
    }

    bool HasChoices(int threadIndex, int index)
    {
        var messages = database.threads[threadIndex].messages;
        if (messages == null || index < 0 || index >= messages.Length) return false;
        return messages[index].choices != null && messages[index].choices.Length > 0;
    }

    void HideAll()
    {
        token++;
        active = -1;
        waitingForChoice = false;
        panelOpen = false;
        if (panel != null) panel.gameObject.SetActive(false);
        if (tab != null) tab.gameObject.SetActive(false);
    }

    void Refresh()
    {
        bool has = active >= 0 && database != null;
        if (tab != null)
        {
            tab.gameObject.SetActive(has && !panelOpen);
            if (has) tab.text = database.threads[active].contactName;
        }
        if (panel != null) panel.gameObject.SetActive(has && panelOpen);
        if (!has || !panelOpen) return;

        var thread = database.threads[active];
        header.text = thread.contactName;
        var message = thread.messages[Mathf.Clamp(messageIndex, 0, thread.messages.Length - 1)];
        if (waitingForChoice)
        {
            body.text = message.sender + ": " + message.text;
            System.Text.StringBuilder builder = new System.Text.StringBuilder();
            for (int i = 0; i < message.choices.Length; i++)
            {
                if (i == choiceIndex) builder.Append("> ");
                else builder.Append("  ");
                builder.Append(message.choices[i].text);
                if (i < message.choices.Length - 1) builder.Append('\n');
            }
            choices.text = builder.ToString();
        }
    }

    void BuildUi()
    {
        var canvasGo = new GameObject("MessengerCanvas");
        var canvas = canvasGo.AddComponent<Canvas>();
        canvas.renderMode = RenderMode.ScreenSpaceOverlay;
        canvas.sortingOrder = 20;
        var scaler = canvasGo.AddComponent<CanvasScaler>();
        scaler.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
        scaler.referenceResolution = new Vector2(1920f, 1080f);

        tab = MakeText(canvasGo.transform, "MessageTab", 26, TextAnchor.MiddleCenter);
        var tabRect = tab.rectTransform;
        tabRect.anchorMin = new Vector2(1f, 0.5f);
        tabRect.anchorMax = new Vector2(1f, 0.5f);
        tabRect.pivot = new Vector2(1f, 0.5f);
        tabRect.sizeDelta = new Vector2(220f, 64f);
        tabRect.anchoredPosition = new Vector2(-16f, 0f);
        tab.color = new Color(0.8f, 0.88f, 0.95f, 0.95f);
        tab.gameObject.SetActive(false);

        var panelGo = new GameObject("MessengerPanel");
        panelGo.transform.SetParent(canvasGo.transform, false);
        panel = panelGo.AddComponent<RectTransform>();
        panel.anchorMin = new Vector2(1f, 0.5f);
        panel.anchorMax = new Vector2(1f, 0.5f);
        panel.pivot = new Vector2(1f, 0.5f);
        panel.sizeDelta = new Vector2(520f, 640f);
        panel.anchoredPosition = new Vector2(-28f, 0f);
        var background = panelGo.AddComponent<Image>();
        background.color = new Color(0.03f, 0.04f, 0.06f, 0.82f);
        background.raycastTarget = false;

        header = MakeText(panel, "Header", 28, TextAnchor.UpperLeft);
        Stretch(header.rectTransform, 24f, 24f, 24f, 560f);
        header.color = new Color(0.75f, 0.84f, 0.92f);
        header.fontStyle = FontStyle.Bold;

        body = MakeText(panel, "Body", 24, TextAnchor.UpperLeft);
        Stretch(body.rectTransform, 24f, 90f, 24f, 220f);
        body.color = new Color(0.9f, 0.9f, 0.88f);
        body.horizontalOverflow = HorizontalWrapMode.Wrap;
        body.verticalOverflow = VerticalWrapMode.Overflow;

        choices = MakeText(panel, "Choices", 24, TextAnchor.UpperLeft);
        Stretch(choices.rectTransform, 24f, 400f, 24f, 24f);
        choices.color = new Color(0.8f, 0.86f, 0.78f);
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
}
