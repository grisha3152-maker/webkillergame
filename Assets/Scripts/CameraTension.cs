using UnityEngine;

/// <summary>
/// Зум камеры забега: медленно приближает (ortho ↓), на входящий пинг — резкий чуть zoom out.
/// Возврат после пинга — с скоростью обычного tension-zoom, без рывка за ~0.7 с.
/// Пейсинг: zoom-in только после N-го убийства (balance.zoomAfterKills).
/// Ping zoom-out — только после того, как tension zoom-in уже начался (tension01 &gt; 0);
/// до этого ortho не двигается (никакого kick-back / punch с возвратом).
/// Вешается на Main Camera через PrototypeBootstrap; SampleScene не правим.
/// </summary>
public class CameraTension : MonoBehaviour
{
    [Header("Legacy / инспектор (не удалять)")]
    [SerializeField] float startOrthoSize;
    [SerializeField] float endOrthoSize;
    [SerializeField] float zoomSpeed = 1f;

    Camera cam;
    float startSize;
    float endSize;
    /// <summary>0 = весь кадр, 1 = максимум приближения к концу забега.</summary>
    float tension01;
    float playSeconds = 480f;
    /// <summary>Доля startSize для скачка zoom out на пинг (из balance.cameraPingZoomOut).</summary>
    float pingKick = 0.08f;
    /// <summary>Текущий добавочный ortho (zoom out), сходит со скоростью tension.</summary>
    float pingPunch;
    /// <summary>Скорость схода punch (ortho/с) = темп обычного tension-zoom.</summary>
    float pingRecoverPerSec;
    bool running;

    /// <summary>Счётчик убийств призраков за забег.</summary>
    int killCount;
    /// <summary>Порог: после скольких киллов разрешён tension zoom-in.</summary>
    int zoomAfterKills = 3;
    /// <summary>Elapsed в момент разблокировки зума (3-й килл).</summary>
    float zoomUnlockElapsed = -1f;

    bool ZoomUnlocked => killCount >= zoomAfterKills;
    /// <summary>Ping kick разрешён только когда приближение уже началось.</summary>
    bool TensionZoomStarted => ZoomUnlocked && tension01 > 0f;

    void Awake()
    {
        cam = GetComponent<Camera>();
        if (cam == null) cam = Camera.main;
        if (cam == null) return;

        // Pixel Perfect перезаписывает orthographicSize каждый кадр — для tension-зума гасим.
        DisablePixelPerfectOn(cam.gameObject);

        Balance.EnsureLoaded();
        var bal = Balance.Data;
        playSeconds = Mathf.Max(1f, bal.playSeconds);
        pingKick = Mathf.Clamp(bal.cameraPingZoomOut, 0.02f, 0.35f);
        zoomAfterKills = Mathf.Max(1, bal.zoomAfterKills);

        startSize = startOrthoSize > 1f ? startOrthoSize : cam.orthographicSize;
        float endFactor = bal.cameraEndSizeFactor;
        if (endFactor <= 0f || endFactor >= 1f) endFactor = 0.86f;
        endSize = endOrthoSize > 1f ? endOrthoSize : startSize * endFactor;
        // Конец всегда ближе (меньше ortho), чем старт.
        if (endSize >= startSize)
            endSize = startSize * 0.86f;

        // Раньше: (startSize * pingKick) / 0.7f — полный возврат за ~0.7 с, рывок.
        // Теперь: тот же ortho/с, что у tension (start→end за playSeconds).
        float tensionRange = Mathf.Max(0.0001f, startSize - endSize);
        pingRecoverPerSec = tensionRange * Mathf.Max(0.01f, zoomSpeed) / playSeconds;
        tension01 = 0f;
        pingPunch = 0f;
        killCount = 0;
        zoomUnlockElapsed = -1f;
        ApplySize();
    }

    void OnEnable()
    {
        GameEvents.OnMessageArrived += OnPing;
        GameEvents.OnGhostKilled += OnGhostKilled;
        GameEvents.OnDeskReady += OnDeskReady;
        GameEvents.OnClockStarted += OnClockStarted;
        GameEvents.OnFinaleStarted += OnFinale;
    }

    void OnDisable()
    {
        GameEvents.OnMessageArrived -= OnPing;
        GameEvents.OnGhostKilled -= OnGhostKilled;
        GameEvents.OnDeskReady -= OnDeskReady;
        GameEvents.OnClockStarted -= OnClockStarted;
        GameEvents.OnFinaleStarted -= OnFinale;
    }

    void Start()
    {
        TryCatchUpRunning();
    }

    void LateUpdate()
    {
        if (cam == null) return;
        TryCatchUpRunning();
        if (!running) return;

        var gm = GameManager.Instance;
        if (gm != null && (gm.IsPaused || gm.IsEnded)) return;

        // До 3-го килла: никакого приближения и никакого punch — ortho стоит.
        if (!ZoomUnlocked)
        {
            tension01 = 0f;
            pingPunch = 0f;
            ApplySize();
            return;
        }

        // Tension от времени с разблокировки — к концу забега добираем до 1 (без скачка в момент килла).
        float unlockAt = Mathf.Max(0f, zoomUnlockElapsed);
        float remain = Mathf.Max(1f, playSeconds - unlockAt);
        if (gm != null && gm.ClockRunning)
        {
            float since = Mathf.Max(0f, gm.Elapsed - unlockAt);
            tension01 = Mathf.Clamp01((since / remain) * Mathf.Max(0.01f, zoomSpeed));
        }
        else
            tension01 = Mathf.Clamp01(tension01 + Time.deltaTime * (zoomSpeed / remain));

        // Пока zoom-in ещё не сдвинул кадр — punch запрещён (иначе kick-back «в никуда»).
        if (tension01 <= 0f)
        {
            pingPunch = 0f;
            ApplySize();
            return;
        }

        // Сход punch только темпом tension — без отдельного «догоняющего» рывка.
        if (pingPunch > 0f)
            pingPunch = Mathf.MoveTowards(pingPunch, 0f, pingRecoverPerSec * Time.deltaTime);

        ApplySize();
    }

    void OnDeskReady() => running = true;

    void OnClockStarted() => running = true;

    void OnFinale()
    {
        running = false;
    }

    void OnGhostKilled(int _)
    {
        killCount++;
        if (ZoomUnlocked && zoomUnlockElapsed < 0f)
        {
            var gm = GameManager.Instance;
            zoomUnlockElapsed = gm != null ? gm.Elapsed : 0f;
        }
    }

    /// <summary>Новый входящий пинг — zoom out только после старта tension zoom-in.</summary>
    void OnPing(string contactId)
    {
        if (cam == null || !TensionZoomStarted) return;
        // Раньше вычитали из tension01 — на ранних пингах (at=25s) kick обнулял tension без видимого скачка.
        pingPunch = Mathf.Max(pingPunch, startSize * pingKick);
        ApplySize();
    }

    void ApplySize()
    {
        if (cam == null || !cam.orthographic) return;
        float baseSize = Mathf.Lerp(startSize, endSize, tension01);
        cam.orthographicSize = baseSize + pingPunch;
    }

    void TryCatchUpRunning()
    {
        if (running) return;
        var gm = GameManager.Instance;
        if (gm != null && (gm.GameplayActive || gm.ClockRunning))
            running = true;
    }

    static void DisablePixelPerfectOn(GameObject host)
    {
        if (host == null) return;
        // Без жёсткой ссылки на URP-тип: ищем по имени на этой же камере.
        var behaviours = host.GetComponents<Behaviour>();
        for (int i = 0; i < behaviours.Length; i++)
        {
            var b = behaviours[i];
            if (b == null || !b.enabled) continue;
            if (b.GetType().Name != "PixelPerfectCamera") continue;
            b.enabled = false;
        }
    }
}
