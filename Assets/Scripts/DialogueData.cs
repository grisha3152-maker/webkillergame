using System;

/// <summary>
/// Старый формат MessengerSystem (threads) — оставлен для совместимости.
/// </summary>
[Serializable]
public class DialogueDatabase
{
    public DialogueThread[] threads;
}

[Serializable]
public class DialogueThread
{
    public string id;
    public string contactName;
    public float triggerAge;
    public float timeoutSeconds = 25f;
    public string autoIgnoreReply = "I'm busy";
    public DialogueMessage[] messages;
}

[Serializable]
public class DialogueMessage
{
    public string sender;
    public string text;
    public bool removesShadow;
    public DialogueChoice[] choices;
}

[Serializable]
public class DialogueChoice
{
    public string text;
    public bool isPositive;
    public int nextIndex;
}

/// <summary>
/// Форма GDD 15.4 — массив пингов в dialogue.json.
/// Поле JSON "in" мапится в incoming при загрузке (JsonUtility не ест ключ in).
/// ГГ не отвечает текстом: choices обычно пустые; set на пинге — флаги при приходе.
/// </summary>
[Serializable]
public class DialoguePingFile
{
    public DialoguePing[] items;
}

[Serializable]
public class DialoguePing
{
    public string id;
    public string contact;
    public float at;
    /// <summary>Устаревшее для ответов; при отсутствии реплик ГГ всегда false.</summary>
    public bool forcedHit = false;
    public string[] requires;
    /// <summary>Флаги, которые ставятся в момент прихода пинга (прошлое → будущее).</summary>
    public string[] set;
    public string incoming;
    public DialoguePingChoice[] choices;
}

[Serializable]
public class DialoguePingChoice
{
    public string label;
    public bool warm;
    public string reply;
    public string[] set;
}
