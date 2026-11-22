using System;

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
    public string autoIgnoreReply = "I'm busy.";
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
