using UnityEngine;
using UnityEngine.Rendering.Universal;

public class VisualProgression : MonoBehaviour
{
    [Header("Hero age sprites (optional)")]
    [SerializeField] private Sprite age14;
    [SerializeField] private Sprite age20;
    [SerializeField] private Sprite age30;
    [SerializeField] private Sprite age40;

    [Header("Room upgrades, enabled as earnings rise")]
    [SerializeField] private GameObject[] upgrades;
    [SerializeField] private int[] upgradeCosts = { 60, 180, 420, 900 };

    [SerializeField] private bool dimRoomLight = true;
    [SerializeField] private bool screenGlow = true;
    [SerializeField] private bool contactShadow = true;

    SpriteRenderer hero;
    Light2D glow;
    int shownAgeBand = 14;

    void Start()
    {
        var player = GameObject.FindGameObjectWithTag("Player");
        if (player != null) hero = player.GetComponent<SpriteRenderer>();
        if (dimRoomLight) DimGlobalLight();
        if (player != null && screenGlow) CreateGlow(player);
        if (player != null && contactShadow) CreateContactShadow(player);
        ApplyAge(14f);
        ApplyMoney(0);
    }

    void OnEnable()
    {
        GameEvents.OnAgeChanged += ApplyAge;
        GameEvents.OnMoneyChanged += ApplyMoney;
    }

    void OnDisable()
    {
        GameEvents.OnAgeChanged -= ApplyAge;
        GameEvents.OnMoneyChanged -= ApplyMoney;
    }

    void Update()
    {
        if (glow == null || GameManager.Instance == null) return;
        float pulse = 0.75f + Mathf.Sin(Time.time * (2.2f + GameManager.Instance.Age01 * 3f)) * 0.12f;
        float wealth = Mathf.Clamp01(GameManager.Instance.Money / 900f);
        glow.intensity = Mathf.Lerp(0.55f, 1.35f, wealth) * pulse;
    }

    void ApplyAge(float age)
    {
        int band = 14;
        if (age >= 40f) band = 40;
        else if (age >= 30f) band = 30;
        else if (age >= 20f) band = 20;
        if (band == shownAgeBand && hero != null && hero.sprite != null) return;
        shownAgeBand = band;
        if (hero == null) return;
        Sprite next = band switch
        {
            20 => age20,
            30 => age30,
            40 => age40,
            _ => age14
        };
        if (next != null) hero.sprite = next;
    }

    void ApplyMoney(int money)
    {
        if (upgrades == null) return;
        for (int i = 0; i < upgrades.Length; i++)
        {
            if (upgrades[i] == null) continue;
            int cost = upgradeCosts != null && i < upgradeCosts.Length ? upgradeCosts[i] : (i + 1) * 150;
            upgrades[i].SetActive(money >= cost);
        }
    }

    void DimGlobalLight()
    {
        var lights = FindObjectsByType<Light2D>(FindObjectsSortMode.None);
        for (int i = 0; i < lights.Length; i++)
        {
            if (lights[i] == null || lights[i].lightType != Light2D.LightType.Global) continue;
            lights[i].intensity = 0.4f;
            lights[i].color = new Color(0.62f, 0.7f, 0.8f, 1f);
        }
    }

    void CreateGlow(GameObject player)
    {
        var go = new GameObject("ScreenGlow");
        go.transform.SetParent(player.transform, false);
        var col = player.GetComponent<Collider2D>();
        if (col != null)
        {
            Vector3 local = player.transform.InverseTransformPoint(col.bounds.center);
            go.transform.localPosition = local + new Vector3(col.bounds.extents.x * 0.05f, 0.1f, 0f);
        }
        glow = go.AddComponent<Light2D>();
        glow.lightType = Light2D.LightType.Point;
        glow.color = new Color(0.55f, 0.78f, 1f, 1f);
        glow.intensity = 0.7f;
        float radius = col != null ? col.bounds.size.y * 0.9f : 300f;
        glow.pointLightInnerRadius = radius * 0.15f;
        glow.pointLightOuterRadius = radius;
    }

    void CreateContactShadow(GameObject player)
    {
        var go = new GameObject("ContactShadow");
        go.transform.SetParent(player.transform, false);
        var renderer = go.AddComponent<SpriteRenderer>();
        renderer.sprite = ShadowManagerBlob();
        renderer.color = new Color(0f, 0f, 0f, 0.38f);
        renderer.sortingOrder = -5;
        var col = player.GetComponent<Collider2D>();
        float width = col != null ? col.bounds.size.x * 0.7f : 200f;
        float height = col != null ? col.bounds.size.y * 0.12f : 40f;
        Vector3 lossy = player.transform.lossyScale;
        go.transform.localScale = new Vector3(
            width / Mathf.Max(0.001f, lossy.x),
            height / Mathf.Max(0.001f, lossy.y),
            1f);
        if (col != null)
        {
            Vector3 local = player.transform.InverseTransformPoint(col.bounds.center);
            local.y -= col.bounds.extents.y * 0.92f;
            go.transform.localPosition = local;
        }
    }

    static Sprite oval;

    static Sprite ShadowManagerBlob()
    {
        if (oval != null) return oval;
        const int n = 32;
        var tex = new Texture2D(n, n, TextureFormat.RGBA32, false);
        var pixels = new Color[n * n];
        var center = new Vector2((n - 1) * 0.5f, (n - 1) * 0.5f);
        for (int y = 0; y < n; y++)
        {
            for (int x = 0; x < n; x++)
            {
                float dx = (x - center.x) / center.x;
                float dy = (y - center.y) / center.y;
                float a = Mathf.Clamp01(1f - Mathf.Sqrt(dx * dx + dy * dy));
                pixels[y * n + x] = new Color(1f, 1f, 1f, a * a);
            }
        }
        tex.SetPixels(pixels);
        tex.Apply();
        oval = Sprite.Create(tex, new Rect(0, 0, n, n), new Vector2(0.5f, 0.5f), n);
        return oval;
    }
}
