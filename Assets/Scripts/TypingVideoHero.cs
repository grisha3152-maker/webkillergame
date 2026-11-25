using System.Collections;
using System.IO;
using UnityEngine;
using UnityEngine.Video;

/// <summary>
/// Replaces the Player typing sprite/Animator loop with a keyed writing.mp4 plate.
/// Soft-fails (keeps existing SpriteRenderer + Animator) if the video file is missing.
/// Starts after desk-ready so wake-desk still poses stay on the sprite path.
/// </summary>
[DisallowMultipleComponent]
public class TypingVideoHero : MonoBehaviour
{
    const string RelativeArtsPath = "Arts/Hero with PC/Writing/writing.mp4";
    const string StreamingFileName = "writing.mp4";
    const string MaterialResourcePath = "Materials/ChromaKeyBlack";
    const int RenderWidth = 1280;
    const int RenderHeight = 704;

    SpriteRenderer heroSprite;
    Animator heroAnimator;
    bool savedAnimatorEnabled = true;
    bool spriteWasEnabled = true;

    VideoPlayer video;
    RenderTexture target;
    MeshRenderer plateRenderer;
    GameObject plate;
    Material runtimeMaterial;

    bool beginRequested;
    bool ready;
    bool suspended;
    bool stopped;

    /// <summary>True when the keyed video plate is driving the typing pose.</summary>
    public bool IsVideoActive => ready && !stopped;

    void Awake()
    {
        heroSprite = GetComponent<SpriteRenderer>();
        heroAnimator = GetComponent<Animator>();
        if (heroSprite != null)
            spriteWasEnabled = heroSprite.enabled;
        if (heroAnimator != null)
            savedAnimatorEnabled = heroAnimator.enabled;
    }

    void OnEnable()
    {
        GameEvents.OnDeskReady += HandleDeskReady;
    }

    void OnDisable()
    {
        GameEvents.OnDeskReady -= HandleDeskReady;
    }

    void Start()
    {
        // U-skip / already in combat when this component mounts.
        if (GameManager.Instance != null && GameManager.Instance.GameplayActive)
            HandleDeskReady();
    }

    void OnDestroy()
    {
        CleanupPartial();
    }

    void HandleDeskReady()
    {
        if (beginRequested || stopped) return;
        beginRequested = true;
        StartCoroutine(BeginTypingVideo());
    }

    /// <summary>Hide video and show SpriteRenderer for still wake poses (sleep/headdown).</summary>
    public void SuspendForStillPose()
    {
        if (!ready) return;
        suspended = true;
        if (video != null && video.isPlaying)
            video.Pause();
        if (plate != null)
            plate.SetActive(false);
        if (heroSprite != null)
            heroSprite.enabled = true;
    }

    /// <summary>Restore keyed typing loop after still poses.</summary>
    public void ResumeTyping()
    {
        if (!ready || stopped) return;
        suspended = false;
        if (heroSprite != null)
            heroSprite.enabled = false;
        if (heroAnimator != null)
            heroAnimator.enabled = false;
        if (plate != null)
            plate.SetActive(true);
        if (video != null && video.isPrepared && !video.isPlaying)
            video.Play();
    }

    /// <summary>Stop typing for ending lean / narrative beat.</summary>
    public void StopTyping()
    {
        stopped = true;
        if (video != null)
            video.Pause();
        if (plate != null)
            plate.SetActive(false);
        if (heroAnimator != null)
            heroAnimator.enabled = false;
        if (heroSprite != null)
            heroSprite.enabled = spriteWasEnabled;
    }

    IEnumerator BeginTypingVideo()
    {
        if (!TryResolveVideoUrl(out string url))
        {
            Debug.LogWarning("TypingVideoHero: writing.mp4 missing — keeping sprite typing animation.");
            yield break;
        }

        if (!BuildPlate(url))
        {
            Debug.LogWarning("TypingVideoHero: failed to build chromakey plate — keeping sprite typing animation.");
            CleanupPartial();
            RestoreSpriteFallback();
            yield break;
        }

        video.Prepare();
        float timeout = 8f;
        while (video != null && !video.isPrepared && timeout > 0f)
        {
            timeout -= Time.unscaledDeltaTime;
            yield return null;
        }

        if (video == null || !video.isPrepared)
        {
            Debug.LogWarning("TypingVideoHero: video prepare timed out — reverting to sprite.");
            CleanupPartial();
            RestoreSpriteFallback();
            yield break;
        }

        ready = true;
        if (heroSprite != null)
            heroSprite.enabled = false;
        if (heroAnimator != null)
            heroAnimator.enabled = false;
        if (plate != null)
            plate.SetActive(true);

        if (!stopped && !suspended)
            video.Play();
    }

    bool BuildPlate(string url)
    {
        Material sourceMat = Resources.Load<Material>(MaterialResourcePath);
        Shader shader = sourceMat != null ? sourceMat.shader : Shader.Find("GhostClicker/ChromaKeyBlack");
        if (shader == null)
            return false;

        runtimeMaterial = sourceMat != null ? new Material(sourceMat) : new Material(shader);
        runtimeMaterial.name = "ChromaKeyWritingRuntime";

        target = new RenderTexture(RenderWidth, RenderHeight, 0, RenderTextureFormat.ARGB32)
        {
            name = "WritingTypingRT",
            filterMode = FilterMode.Bilinear,
            wrapMode = TextureWrapMode.Clamp
        };
        target.Create();
        runtimeMaterial.mainTexture = target;
        runtimeMaterial.SetTexture("_MainTex", target);

        plate = GameObject.CreatePrimitive(PrimitiveType.Quad);
        plate.name = "WritingChromakeyPlate";
        plate.transform.SetParent(transform, false);
        plate.SetActive(false);

        var col = plate.GetComponent<Collider>();
        if (col != null)
            Destroy(col);

        plateRenderer = plate.GetComponent<MeshRenderer>();
        plateRenderer.sharedMaterial = runtimeMaterial;
        plateRenderer.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
        plateRenderer.receiveShadows = false;
        SyncSortingAndSize();

        video = gameObject.AddComponent<VideoPlayer>();
        video.playOnAwake = false;
        video.isLooping = true;
        video.audioOutputMode = VideoAudioOutputMode.None;
        video.renderMode = VideoRenderMode.RenderTexture;
        video.targetTexture = target;
        video.source = VideoSource.Url;
        video.url = url;
        video.skipOnDrop = true;
        video.waitForFirstFrame = true;
        return true;
    }

    void SyncSortingAndSize()
    {
        if (plate == null || plateRenderer == null) return;

        if (heroSprite != null)
        {
            plateRenderer.sortingLayerID = heroSprite.sortingLayerID;
            plateRenderer.sortingOrder = heroSprite.sortingOrder;
            if (runtimeMaterial != null)
                runtimeMaterial.color = heroSprite.color;

            if (heroSprite.sprite != null)
            {
                Bounds b = heroSprite.sprite.bounds;
                float sx = b.size.x;
                float sy = b.size.y;
                // Player FlipX mirrors the desk hero; keep the same facing.
                if (heroSprite.flipX)
                    sx = -Mathf.Abs(sx);
                else
                    sx = Mathf.Abs(sx);
                plate.transform.localPosition = b.center;
                plate.transform.localRotation = Quaternion.identity;
                plate.transform.localScale = new Vector3(sx, sy, 1f);
                return;
            }
        }

        // Fallback: Writing crop at PPU=2 → ~596×336 local units, flipped like the scene sprite.
        plate.transform.localPosition = Vector3.zero;
        plate.transform.localScale = new Vector3(-596f, 336f, 1f);
    }

    void RestoreSpriteFallback()
    {
        ready = false;
        if (heroSprite != null)
            heroSprite.enabled = spriteWasEnabled;
        if (heroAnimator != null)
            heroAnimator.enabled = savedAnimatorEnabled;
    }

    void CleanupPartial()
    {
        if (video != null)
        {
            video.Stop();
            Destroy(video);
            video = null;
        }
        if (plate != null)
        {
            Destroy(plate);
            plate = null;
            plateRenderer = null;
        }
        if (runtimeMaterial != null)
        {
            Destroy(runtimeMaterial);
            runtimeMaterial = null;
        }
        if (target != null)
        {
            target.Release();
            Destroy(target);
            target = null;
        }
    }

    static bool TryResolveVideoUrl(out string url)
    {
        url = null;

        string streaming = Path.Combine(Application.streamingAssetsPath, StreamingFileName);
        if (FileExistsForVideo(streaming))
        {
            url = ToFileUrl(streaming);
            return true;
        }

        string arts = Path.Combine(Application.dataPath, RelativeArtsPath);
        if (FileExistsForVideo(arts))
        {
            url = ToFileUrl(arts);
            return true;
        }

        return false;
    }

    static bool FileExistsForVideo(string path)
    {
        if (string.IsNullOrEmpty(path)) return false;
        if (path.Contains("://") || path.Contains("jar:"))
            return true;
        try
        {
            return File.Exists(path);
        }
        catch
        {
            return false;
        }
    }

    static string ToFileUrl(string path)
    {
        if (path.Contains("://"))
            return path;
        return "file://" + path.Replace("\\", "/");
    }
}
