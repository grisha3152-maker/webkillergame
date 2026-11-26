using System.Collections;
using System.IO;
using UnityEngine;
using UnityEngine.Video;

/// <summary>
/// Replaces the Player typing sprite/Animator loop with a keyed writing plate
/// (WebM VP8 preferred for Linux; MP4 fallback). Soft-fails back to SpriteRenderer
/// + Animator only when the video file is truly missing — not on prepare hiccups.
/// Starts after desk-ready so wake-desk still poses stay on the sprite path.
/// </summary>
[DisallowMultipleComponent]
public class TypingVideoHero : MonoBehaviour
{
    const string RelativeArtsWebm = "Arts/Hero with PC/Writing/writing.webm";
    const string RelativeArtsMp4 = "Arts/Hero with PC/Writing/writing.mp4";
    const string StreamingWebm = "writing.webm";
    const string StreamingMp4 = "writing.mp4";
    const string MaterialResourcePath = "Materials/ChromaKeyBlack";
    const int RenderWidth = 1280;
    const int RenderHeight = 704;
    const float PrepareTimeoutSeconds = 12f;

    SpriteRenderer heroSprite;
    Animator heroAnimator;
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
    bool ownsTypingPose;
    string resolvedUrl;

    /// <summary>True when the keyed video plate is driving the typing pose.</summary>
    public bool IsVideoActive => ready && !stopped;

    /// <summary>True once we claimed the carve sprite/Animator (file found).</summary>
    public bool OwnsTypingPose => ownsTypingPose && !stopped;

    void Awake()
    {
        heroSprite = GetComponent<SpriteRenderer>();
        heroAnimator = GetComponent<Animator>();
        if (heroSprite != null)
            spriteWasEnabled = heroSprite.enabled;
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

    void LateUpdate()
    {
        // Keep carve Animator/SpriteRenderer from winning while video owns the pose.
        if (!ownsTypingPose || suspended || stopped) return;
        if (heroSprite != null && heroSprite.enabled)
            heroSprite.enabled = false;
        if (heroAnimator != null && heroAnimator.enabled)
            heroAnimator.enabled = false;
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
        if (!ownsTypingPose && !ready) return;
        suspended = true;
        if (video != null && video.isPlaying)
            video.Pause();
        if (plate != null)
            plate.SetActive(false);
        if (heroSprite != null)
            heroSprite.enabled = true;
        if (heroAnimator != null)
            heroAnimator.enabled = false;
    }

    /// <summary>Restore keyed typing loop after still poses.</summary>
    public void ResumeTyping()
    {
        if (stopped) return;
        suspended = false;

        if (!ownsTypingPose)
        {
            // Intro ended before desk-ready finished wiring — kick start if needed.
            if (!beginRequested && GameManager.Instance != null && GameManager.Instance.GameplayActive)
                HandleDeskReady();
            return;
        }

        HideCarveSpriteAndAnimator();
        if (plate != null && ready)
            plate.SetActive(true);
        if (video != null && ready && video.isPrepared && !video.isPlaying)
            video.Play();
    }

    /// <summary>Stop typing for ending lean / narrative beat.</summary>
    public void StopTyping()
    {
        stopped = true;
        ownsTypingPose = false;
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
        if (!TryResolveVideoUrl(out resolvedUrl))
        {
            Debug.LogWarning("TypingVideoHero: writing.webm/mp4 missing — keeping sprite typing animation.");
            yield break;
        }

        // File exists → claim pose immediately so carve sheet cannot keep driving gameplay.
        ClaimTypingPose();

        if (!BuildPlate(resolvedUrl))
        {
            Debug.LogError("TypingVideoHero: failed to build chromakey plate — carve sprite stays hidden (file present).");
            yield break;
        }

        bool prepared = false;
        while (!stopped && !prepared)
        {
            video.errorReceived -= OnVideoError;
            video.errorReceived += OnVideoError;
            video.Prepare();

            float timeout = PrepareTimeoutSeconds;
            while (video != null && !video.isPrepared && timeout > 0f && !stopped)
            {
                timeout -= Time.unscaledDeltaTime;
                yield return null;
            }

            if (stopped) yield break;

            if (video != null && video.isPrepared)
            {
                prepared = true;
                break;
            }

            Debug.LogWarning(
                "TypingVideoHero: prepare timed out for " + resolvedUrl +
                " — retrying (soft-fail only if file missing).");
            if (video != null)
            {
                video.Stop();
                // Prefer alternate container once if first URL stalls (webm ↔ mp4).
                if (TryResolveAlternateUrl(resolvedUrl, out string alt))
                {
                    resolvedUrl = alt;
                    video.url = resolvedUrl;
                }
            }
            yield return null;
        }

        if (video == null || !video.isPrepared)
            yield break;

        ready = true;
        HideCarveSpriteAndAnimator();
        SyncSortingAndSize();
        if (plate != null)
            plate.SetActive(true);

        if (!stopped && !suspended)
            video.Play();
    }

    void OnVideoError(VideoPlayer source, string message)
    {
        Debug.LogWarning("TypingVideoHero: VideoPlayer error — " + message);
    }

    void ClaimTypingPose()
    {
        ownsTypingPose = true;
        HideCarveSpriteAndAnimator();
    }

    void HideCarveSpriteAndAnimator()
    {
        if (heroSprite != null)
            heroSprite.enabled = false;
        if (heroAnimator != null)
            heroAnimator.enabled = false;
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
            // Sit just above the carve sprite in case it flickers back on.
            plateRenderer.sortingLayerID = heroSprite.sortingLayerID;
            plateRenderer.sortingOrder = heroSprite.sortingOrder + 1;
            if (runtimeMaterial != null)
                runtimeMaterial.color = heroSprite.color;

            if (heroSprite.sprite != null)
            {
                ApplySpriteMatchedPlateTransform(heroSprite);
                return;
            }
        }

        // Fallback: Writing crop at PPU=2 → 596×336, FlipX like SampleScene Player (pivot 0,0).
        plate.transform.localPosition = new Vector3(-298f, 168f, 0f);
        plate.transform.localRotation = Quaternion.identity;
        plate.transform.localScale = new Vector3(596f, 336f, 1f);
    }

    /// <summary>
    /// Match SpriteRenderer visual AABB in local space, including FlipX/Y with non-center pivots
    /// (SampleScene Player uses bottom-left pivot + FlipX).
    /// </summary>
    void ApplySpriteMatchedPlateTransform(SpriteRenderer sr)
    {
        if (plate == null || sr == null || sr.sprite == null) return;

        Sprite spr = sr.sprite;
        float w = spr.rect.width / spr.pixelsPerUnit;
        float h = spr.rect.height / spr.pixelsPerUnit;
        Vector2 pivot = spr.pivot / spr.pixelsPerUnit;

        float minX = sr.flipX ? pivot.x - w : -pivot.x;
        float maxX = sr.flipX ? pivot.x : w - pivot.x;
        float minY = sr.flipY ? pivot.y - h : -pivot.y;
        float maxY = sr.flipY ? pivot.y : h - pivot.y;

        plate.transform.localPosition = new Vector3((minX + maxX) * 0.5f, (minY + maxY) * 0.5f, 0f);
        plate.transform.localRotation = Quaternion.identity;
        plate.transform.localScale = new Vector3(Mathf.Abs(maxX - minX), Mathf.Abs(maxY - minY), 1f);
    }

    void CleanupPartial()
    {
        if (video != null)
        {
            video.errorReceived -= OnVideoError;
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

        // WebM VP8 first — Linux VideoPlayer cannot play H.264 MP4 (same as intro cutscene).
        string[] candidates =
        {
            Path.Combine(Application.streamingAssetsPath, StreamingWebm),
            Path.Combine(Application.dataPath, RelativeArtsWebm),
            Path.Combine(Application.streamingAssetsPath, StreamingMp4),
            Path.Combine(Application.dataPath, RelativeArtsMp4),
        };

        for (int i = 0; i < candidates.Length; i++)
        {
            if (!FileExistsForVideo(candidates[i])) continue;
            url = ToFileUrl(candidates[i]);
            return true;
        }

        return false;
    }

    static bool TryResolveAlternateUrl(string currentUrl, out string altUrl)
    {
        altUrl = null;
        if (string.IsNullOrEmpty(currentUrl)) return false;

        string[] candidates =
        {
            Path.Combine(Application.streamingAssetsPath, StreamingWebm),
            Path.Combine(Application.dataPath, RelativeArtsWebm),
            Path.Combine(Application.streamingAssetsPath, StreamingMp4),
            Path.Combine(Application.dataPath, RelativeArtsMp4),
        };

        string current = currentUrl.Replace("\\", "/");
        for (int i = 0; i < candidates.Length; i++)
        {
            if (!FileExistsForVideo(candidates[i])) continue;
            string asUrl = ToFileUrl(candidates[i]).Replace("\\", "/");
            if (asUrl == current) continue;
            altUrl = asUrl;
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
