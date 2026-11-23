using UnityEngine;

[DefaultExecutionOrder(-50)]
public class GameManager : MonoBehaviour
{
    public static GameManager Instance { get; private set; }

    [SerializeField] private float runSeconds = 480f;
    [SerializeField] private int basePayout = 10;

    public float Age { get; private set; } = 14f;
    public int Money { get; private set; }
    public int ShadowCount { get; private set; }
    public bool ClockRunning { get; private set; }
    public bool GameplayActive { get; private set; }
    public bool IsPaused { get; private set; }
    public bool IsEnded { get; private set; }
    public float Age01 => Mathf.InverseLerp(14f, 40f, Age);
    public float Elapsed { get; private set; }

    public bool AllowCombat => GameplayActive && !IsPaused && !IsEnded;

    float lastAgeNotified = -1f;
    /// <summary>Игрок открыл последнее сообщение Лева — предпочитаем narrative ending.</summary>
    bool aliveEndingArmed;

    void Awake()
    {
        if (Instance != null && Instance != this)
        {
            Destroy(gameObject);
            return;
        }

        Instance = this;
        Balance.EnsureLoaded();
        runSeconds = Balance.Data.playSeconds;
        basePayout = Balance.Data.dollarsPerKill;
    }

    void OnEnable()
    {
        GameEvents.OnGhostKilled += AddPayout;
        GameEvents.OnShadowCountChanged += OnShadows;
    }

    void OnDisable()
    {
        GameEvents.OnGhostKilled -= AddPayout;
        GameEvents.OnShadowCountChanged -= OnShadows;
        if (Instance == this) Instance = null;
    }

    void Update()
    {
        var keyboard = UnityEngine.InputSystem.Keyboard.current;
        if (keyboard != null && keyboard.escapeKey.wasPressedThisFrame && GameplayActive && !IsEnded)
            SetPaused(!IsPaused);

        if (!ClockRunning || IsPaused || IsEnded) return;

        Elapsed += Time.deltaTime;
        float nextAge = AgeFromElapsed(Elapsed);
        if (!Mathf.Approximately(nextAge, Age))
        {
            Age = nextAge;
            if (Mathf.Abs(Age - lastAgeNotified) >= 0.2f || Age >= 40f)
            {
                lastAgeNotified = Age;
                GameEvents.AgeChanged(Age);
            }
        }

        if (Elapsed >= 435f || Age >= 40f)
            ResolveForty();
    }

    public void StartClock()
    {
        if (ClockRunning || IsEnded) return;
        ClockRunning = true;
        Age = 14f;
        GameEvents.AgeChanged(Age);
        // Телефон может стартовать таймер и от часов (fallback, если DeskReady задержится).
        GameEvents.ClockStarted();
    }

    public void EnableCombat()
    {
        if (IsEnded) return;
        bool first = !GameplayActive;
        GameplayActive = true;
        // Конец opening / посадка за стол — основной сигнал для Dialogue.
        if (first)
            GameEvents.DeskReady();
    }

    public void SetPaused(bool paused)
    {
        if (IsEnded) return;
        IsPaused = paused;
        Time.timeScale = paused ? 0f : 1f;
    }

    public int PayoutForCurrentAge()
    {
        return Mathf.Max(1, Mathf.RoundToInt(basePayout * Mathf.Lerp(1f, 2.4f, Age01)));
    }

    public void EndBecauseOfShadows()
    {
        if (IsEnded) return;
        // Уже открыли финальный пинг Лева — ждём narrative path.
        if (aliveEndingArmed) return;
        BeginEnding(quiet: false);
    }

    /// <summary>Пометить: открыт unread lev_alive — блокируем 5-shadow / age-40 до старта катсцены.</summary>
    public void ArmAliveEnding()
    {
        if (IsEnded) return;
        aliveEndingArmed = true;
    }

    /// <summary>Телефон закрыли до 5с — снова разрешаем обычные endings.</summary>
    public void DisarmAliveEnding()
    {
        if (IsEnded) return;
        aliveEndingArmed = false;
    }

    /// <summary>Старт ending после 5с с открытым телефоном на «are you even alive?».</summary>
    public void BeginAliveEnding()
    {
        if (IsEnded) return;
        aliveEndingArmed = true;
        IsEnded = true;
        GameplayActive = false;
        IsPaused = false;
        Time.timeScale = 1f;
        GameEvents.FinaleStarted();

        var cutscene = FindAnyObjectByType<CutsceneController>();
        if (cutscene != null)
            cutscene.PlayAliveEnding();
    }

    void AddPayout(int amount)
    {
        if (IsEnded) return;
        Money += amount;
        GameEvents.MoneyChanged(Money);
    }

    void OnShadows(int count)
    {
        ShadowCount = count;
        if (GameplayActive && count >= 5 && !aliveEndingArmed)
            BeginEnding(quiet: false);
    }

    void ResolveForty()
    {
        if (IsEnded) return;
        // Пока ждём/ведём Lev alive-ending — age-40 не перехватывает.
        if (aliveEndingArmed) return;
        BeginEnding(quiet: ShadowCount <= 0);
    }

    void BeginEnding(bool quiet)
    {
        if (IsEnded) return;
        IsEnded = true;
        GameplayActive = false;
        IsPaused = false;
        Time.timeScale = 1f;
        GameEvents.FinaleStarted();

        var cutscene = FindAnyObjectByType<CutsceneController>();
        if (cutscene != null)
            cutscene.PlayEnding(quiet);
    }

    static float AgeFromElapsed(float t)
    {
        if (t <= 45f) return 14f;
        if (t <= 150f) return Mathf.Lerp(14f, 20f, (t - 45f) / 105f);
        if (t <= 285f) return Mathf.Lerp(20f, 30f, (t - 150f) / 135f);
        if (t <= 435f) return Mathf.Lerp(30f, 40f, (t - 285f) / 150f);
        return 40f;
    }
}
