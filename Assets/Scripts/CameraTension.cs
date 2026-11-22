using UnityEngine;

/// <summary>
/// Зум камеры забега: медленно приближает (ortho ↓), на входящий пинг — резкий чуть zoom out.
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
    float pingKick = 0.08f;
    bool running;

    void Awake()
    {
        cam = GetComponent<Camera>();
        if (cam == null) cam = Camera.main;
        if (cam == null) return;

        Balance.EnsureLoaded();
        var bal = Balance.Data;
        playSeconds = Mathf.Max(1f, bal.playSeconds);
        pingKick = Mathf.Clamp(bal.cameraPingZoomOut, 0.02f, 0.35f);

        startSize = startOrthoSize > 1f ? startOrthoSize : cam.orthographicSize;
        float endFactor = bal.cameraEndSizeFactor;
        if (endFactor <= 0f || endFactor >= 1f) endFactor = 0.86f;
        endSize = endOrthoSize > 1f ? endOrthoSize : startSize * endFactor;
        // Конец всегда ближе (меньше ortho), чем старт.
        if (endSize >= startSize)
            endSize = startSize * 0.86f;

        tension01 = 0f;
        ApplySize();
    }

    void OnEnable()
    {
        GameEvents.OnMessageArrived += OnPing;
        GameEvents.OnDeskReady += OnDeskReady;
        GameEvents.OnClockStarted += OnClockStarted;
        GameEvents.OnFinaleStarted += OnFinale;
    }

    void OnDisable()
    {
        GameEvents.OnMessageArrived -= OnPing;
        GameEvents.OnDeskReady -= OnDeskReady;
        GameEvents.OnClockStarted -= OnClockStarted;
        GameEvents.OnFinaleStarted -= OnFinale;
    }

    void Start()
    {
        var gm = GameManager.Instance;
        if (gm == null) return;
        if (gm.GameplayActive || gm.ClockRunning)
            running = true;
    }

    void Update()
    {
        if (!running || cam == null) return;
        var gm = GameManager.Instance;
        if (gm != null && (gm.IsPaused || gm.IsEnded)) return;

        // Медленное приближение пропорционально длине забега.
        float rate = zoomSpeed / playSeconds;
        tension01 = Mathf.Clamp01(tension01 + Time.deltaTime * rate);
        ApplySize();
    }

    void OnDeskReady() => running = true;

    void OnClockStarted() => running = true;

    void OnFinale()
    {
        running = false;
    }

    /// <summary>Новый входящий пинг — скачок zoom out, затем снова медленно внутрь.</summary>
    void OnPing(string contactId)
    {
        if (cam == null) return;
        tension01 = Mathf.Max(0f, tension01 - pingKick);
        ApplySize();
    }

    void ApplySize()
    {
        if (cam == null || !cam.orthographic) return;
        cam.orthographicSize = Mathf.Lerp(startSize, endSize, tension01);
    }
}
