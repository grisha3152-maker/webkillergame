using UnityEngine;
using UnityEngine.UI;

public class ShadowManager : MonoBehaviour
{
    [SerializeField] private int maxShadows = 5;
    [SerializeField] private SpriteRenderer[] cornerShadows;

    int count;
    Image vignette;
    AudioSource hum;
    AudioSource drone;
    AudioSource[] whispers;
    Camera cam;
    Vector3 camBase;
    Transform[] runtimeShadows;
    bool finale;
    bool silenced;

    public int Count => count;

    void Start()
    {
        cam = Camera.main;
        if (cam != null) camBase = cam.transform.position;
        BuildVignette();
        BuildAudio();
        if (cornerShadows == null || cornerShadows.Length == 0)
            BuildRuntimeShadows();
        ApplyVisuals(immediate: true);
    }

    void OnEnable()
    {
        GameEvents.OnHeroHit += AddShadow;
    }

    void OnDisable()
    {
        GameEvents.OnHeroHit -= AddShadow;
    }

    void Update()
    {
        float age01 = GameManager.Instance != null ? GameManager.Instance.Age01 : 0f;
        if (drone != null && !finale)
            drone.volume = Mathf.MoveTowards(drone.volume, Mathf.Lerp(0.08f, 0f, age01), Time.deltaTime * 0.05f);

        float pressure = finale ? 1f : count / (float)maxShadows;
        if (vignette != null)
        {
            float target = Mathf.Lerp(0f, 0.72f, pressure);
            var c = vignette.color;
            c.a = Mathf.MoveTowards(c.a, target, Time.deltaTime * (finale ? 0.8f : 0.35f));
            vignette.color = c;
        }

        if (cam != null && !finale)
        {
            float amp = pressure * 7f;
            Vector3 shake = Vector3.zero;
            if (amp > 0.01f)
            {
                shake.x = (Mathf.PerlinNoise(Time.time * 8f, 0.2f) - 0.5f) * 2f * amp;
                shake.y = (Mathf.PerlinNoise(0.2f, Time.time * 8f) - 0.5f) * 2f * amp;
            }
            cam.transform.position = camBase + shake;
        }

        FadeWhispers();
    }

    public void AddShadow()
    {
        if (finale) return;
        if (GameManager.Instance != null && !GameManager.Instance.AllowCombat) return;
        if (count >= maxShadows) return;
        count++;
        GameEvents.ShadowCountChanged(count);
        ApplyVisuals(immediate: false);
    }

    public void RemoveShadow()
    {
        if (finale || count <= 0) return;
        count--;
        GameEvents.ShadowCountChanged(count);
        ApplyVisuals(immediate: false);
    }

    public void PlayCollapse()
    {
        finale = true;
        count = maxShadows;
        ApplyVisuals(immediate: false);
        if (drone != null) drone.volume = 0f;
        StartCoroutine(SwellThenCut());
    }

    System.Collections.IEnumerator SwellThenCut()
    {
        float t = 0f;
        while (t < 2.4f)
        {
            t += Time.deltaTime;
            if (cam != null)
            {
                float amp = Mathf.Lerp(6f, 22f, t / 2.4f);
                Vector3 shake = new Vector3(Mathf.Sin(Time.time * 30f) * amp, Mathf.Cos(Time.time * 27f) * amp, 0f);
                cam.transform.position = camBase + shake;
            }
            yield return null;
        }

        silenced = true;
        if (whispers != null)
        {
            for (int i = 0; i < whispers.Length; i++)
                if (whispers[i] != null) whispers[i].volume = 0f;
        }
        if (hum != null) hum.volume = 0f;
        if (cam != null) cam.transform.position = camBase;
    }

    void ApplyVisuals(bool immediate)
    {
        SetShadowVisible(count);
    }

    void SetShadowVisible(int visible)
    {
        if (cornerShadows != null)
        {
            for (int i = 0; i < cornerShadows.Length; i++)
            {
                if (cornerShadows[i] == null) continue;
                cornerShadows[i].enabled = i < visible;
            }
        }

        if (runtimeShadows == null) return;
        for (int i = 0; i < runtimeShadows.Length; i++)
        {
            if (runtimeShadows[i] == null) continue;
            runtimeShadows[i].gameObject.SetActive(i < visible);
        }
    }

    void FadeWhispers()
    {
        if (silenced || whispers == null) return;
        for (int i = 0; i < whispers.Length; i++)
        {
            if (whispers[i] == null) continue;
            float target = finale ? 0.2f : (i < count ? 0.11f : 0f);
            whispers[i].volume = Mathf.MoveTowards(whispers[i].volume, target, Time.deltaTime * 0.25f);
        }
    }

    void BuildVignette()
    {
        var canvasGo = new GameObject("Vignette");
        var canvas = canvasGo.AddComponent<Canvas>();
        canvas.renderMode = RenderMode.ScreenSpaceOverlay;
        canvas.sortingOrder = 10;
        var imageGo = new GameObject("VignetteImage");
        imageGo.transform.SetParent(canvasGo.transform, false);
        var rect = imageGo.AddComponent<RectTransform>();
        rect.anchorMin = Vector2.zero;
        rect.anchorMax = Vector2.one;
        rect.offsetMin = Vector2.zero;
        rect.offsetMax = Vector2.zero;
        vignette = imageGo.AddComponent<Image>();
        vignette.sprite = VignetteSprite();
        vignette.color = new Color(0f, 0f, 0f, 0f);
        vignette.raycastTarget = false;
    }

    void BuildAudio()
    {
        hum = MakeSource("Hum", GeneratedAudio.Hum, 0.045f, true);
        drone = MakeSource("Drone", GeneratedAudio.Drone, 0.08f, true);
        whispers = new AudioSource[maxShadows];
        for (int i = 0; i < maxShadows; i++)
            whispers[i] = MakeSource("Whisper" + i, GeneratedAudio.Whisper, 0f, true);
    }

    AudioSource MakeSource(string name, AudioClip clip, float volume, bool loop)
    {
        var go = new GameObject(name);
        go.transform.SetParent(transform, false);
        var source = go.AddComponent<AudioSource>();
        source.clip = clip;
        source.loop = loop;
        source.volume = volume;
        source.playOnAwake = false;
        source.spatialBlend = 0f;
        source.Play();
        return source;
    }

    void BuildRuntimeShadows()
    {
        runtimeShadows = new Transform[maxShadows];
        Vector2[] view =
        {
            new Vector2(0.08f, 0.14f),
            new Vector2(0.92f, 0.14f),
            new Vector2(0.1f, 0.86f),
            new Vector2(0.9f, 0.86f),
            new Vector2(0.5f, 0.93f)
        };

        for (int i = 0; i < maxShadows; i++)
        {
            var go = new GameObject("Shadow" + i);
            var renderer = go.AddComponent<SpriteRenderer>();
            renderer.sprite = BlobSprite();
            renderer.color = new Color(0.02f, 0.03f, 0.05f, 0.9f);
            renderer.sortingOrder = 4;
            float size = cam != null ? cam.orthographicSize * 0.55f : 200f;
            go.transform.localScale = Vector3.one * size;
            if (cam != null)
            {
                float depth = Mathf.Abs(cam.transform.position.z);
                Vector3 world = cam.ViewportToWorldPoint(new Vector3(view[i].x, view[i].y, depth));
                world.z = 0f;
                go.transform.position = world;
            }
            AddEye(go.transform, -0.08f);
            AddEye(go.transform, 0.08f);
            go.SetActive(false);
            runtimeShadows[i] = go.transform;
        }
    }

    void AddEye(Transform parent, float x)
    {
        var eye = new GameObject("Eye");
        eye.transform.SetParent(parent, false);
        eye.transform.localPosition = new Vector3(x, 0.05f, 0f);
        eye.transform.localScale = Vector3.one * 0.08f;
        var renderer = eye.AddComponent<SpriteRenderer>();
        renderer.sprite = BlobSprite();
        renderer.color = new Color(0.75f, 0.86f, 0.75f, 0.85f);
        renderer.sortingOrder = 5;
    }

    static Sprite blob;

    static Sprite BlobSprite()
    {
        if (blob != null) return blob;
        const int n = 64;
        var tex = new Texture2D(n, n, TextureFormat.RGBA32, false);
        var pixels = new Color[n * n];
        var center = new Vector2((n - 1) * 0.5f, (n - 1) * 0.5f);
        for (int y = 0; y < n; y++)
        {
            for (int x = 0; x < n; x++)
            {
                float d = Vector2.Distance(new Vector2(x, y), center) / (n * 0.5f);
                float a = Mathf.Clamp01(1f - d);
                a = a * a;
                pixels[y * n + x] = new Color(1f, 1f, 1f, a);
            }
        }
        tex.SetPixels(pixels);
        tex.Apply();
        blob = Sprite.Create(tex, new Rect(0, 0, n, n), new Vector2(0.5f, 0.5f), n);
        return blob;
    }

    static Sprite vignetteSprite;

    static Sprite VignetteSprite()
    {
        if (vignetteSprite != null) return vignetteSprite;
        const int n = 128;
        var tex = new Texture2D(n, n, TextureFormat.RGBA32, false);
        var pixels = new Color[n * n];
        var center = new Vector2((n - 1) * 0.5f, (n - 1) * 0.5f);
        for (int y = 0; y < n; y++)
        {
            for (int x = 0; x < n; x++)
            {
                float d = Vector2.Distance(new Vector2(x, y), center) / (n * 0.5f);
                float a = Mathf.Clamp01(Mathf.InverseLerp(0.35f, 1.05f, d));
                a = a * a;
                pixels[y * n + x] = new Color(0f, 0f, 0f, a);
            }
        }
        tex.SetPixels(pixels);
        tex.Apply();
        vignetteSprite = Sprite.Create(tex, new Rect(0, 0, n, n), new Vector2(0.5f, 0.5f), n);
        return vignetteSprite;
    }
}
