using UnityEngine;

public class Monster : MonoBehaviour
{
    [Header("Настройки скорости")]
    [SerializeField] private float startSpeed = 450f; // Быстрый рывок при появлении
    [SerializeField] private float endSpeed = 35f;    // Медленное подкрадывание у игрока
    [SerializeField] private bool digitalTint = true;

    /// <summary>Базовый масштаб букв комбо над головой.</summary>
    const float ComboLetterBaseScale = 1f;
    /// <summary>Текущая клавиша чуть крупнее остальных.</summary>
    const float ComboActiveScaleMul = 1.28f;
    /// <summary>Шаг между центрами букв (world units при PPU 16).</summary>
    const float ComboLetterSpacing = 34f;
    const int ComboSortingOrder = 40;

    private Transform playerTransform;
    private SpriteRenderer spriteRenderer;
    private Collider2D bodyCollider;
    private float initialDistance;
    private bool resolved;
    /// <summary>Заморожен захватом HitCombo (Space): не летит к герою, пока комбо активно.</summary>
    private bool movementFrozen;

    /// <summary>Корень ряда спрайтов Z/X/C над головой (вместо Legacy TextMesh).</summary>
    private Transform comboRoot;
    private SpriteRenderer[] comboLetterRenderers;
    private char[] comboKeys;
    private int comboNextIndex = -1;

    public bool Alive => !resolved;

    /// <summary>
    /// Forced-призрак (GDD 11.1.1): конус / splash комбо его не убивают.
    /// Спавн Forced пока не подключён — флаг готов к использованию.
    /// </summary>
    public bool IsForced { get; private set; }

    /// <summary>Ширина тела (renderer/bounds) для радиуса splash после успешного комбо.</summary>
    public float BodyWidth
    {
        get
        {
            if (spriteRenderer != null) return Mathf.Max(1f, spriteRenderer.bounds.size.x);
            if (bodyCollider != null) return Mathf.Max(1f, bodyCollider.bounds.size.x);
            return 40f;
        }
    }

    private void Awake()
    {
        spriteRenderer = GetComponent<SpriteRenderer>();
        if (spriteRenderer != null && digitalTint)
            spriteRenderer.color = new Color(0.62f, 0.82f, 1f, spriteRenderer.color.a);

        var body = GetComponent<Rigidbody2D>();
        if (body != null)
        {
            body.gravityScale = 0f;
            body.bodyType = RigidbodyType2D.Kinematic;
        }

        bodyCollider = GetComponent<Collider2D>();
        if (bodyCollider != null) bodyCollider.isTrigger = true;
    }

    public void Initialize(Transform target)
    {
        playerTransform = target;
        if (playerTransform != null)
        {
            // Запоминаем точную дистанцию, на которой заспавнился монстр
            initialDistance = Vector2.Distance(transform.position, playerTransform.position);
        }
    }

    /// <summary>Пометить как forced (SpawnForced). Пока флаг true — не умирает от конуса/splash.</summary>
    public void MarkForced(bool forced = true)
    {
        IsForced = forced;
    }

    /// <summary>Остановить / возобновить полёт к герою на время HitCombo.</summary>
    public void SetMovementFrozen(bool frozen)
    {
        movementFrozen = frozen;
    }

    private void Update()
    {
        if (GameManager.Instance != null && !GameManager.Instance.AllowCombat) return;

        if (playerTransform == null)
        {
            GameObject playerObj = GameObject.FindGameObjectWithTag("Player");
            if (playerObj != null)
            {
                playerTransform = playerObj.transform;
                initialDistance = Vector2.Distance(transform.position, playerTransform.position);
            }
            else
            {
                return;
            }
        }

        // Захват комбо: стоим на месте, лейбл обновляем, до героя не летим.
        if (movementFrozen)
        {
            if (comboRoot != null && comboNextIndex >= 0)
                PlaceComboLabel();
            TryReach();
            return;
        }

        // Текущая дистанция до героя
        float currentDistance = Vector2.Distance(transform.position, playerTransform.position);

        // Доля оставшегося пути от 1 (на старте) до 0 (вплотную к герою)
        float t = initialDistance > 0f ? Mathf.Clamp01(currentDistance / initialDistance) : 0f;

        // Возводим в квадрат (t * t), чтобы замедление ощущалось резко и наглядно
        float ageMul = GameManager.Instance != null ? Mathf.Lerp(1f, 1.85f, GameManager.Instance.Age01) : 1f;
        float currentSpeed = Mathf.Lerp(endSpeed, startSpeed, t * t) * ageMul;

        transform.position = Vector2.MoveTowards(
            transform.position,
            playerTransform.position,
            currentSpeed * Time.deltaTime
        );

        if (spriteRenderer != null)
        {
            spriteRenderer.flipX = playerTransform.position.x > transform.position.x;
        }

        // Лейбл комбо держим над головой, пока удар активен.
        if (comboRoot != null && comboNextIndex >= 0)
            PlaceComboLabel();

        TryReach();
    }

    private void OnTriggerEnter2D(Collider2D other)
    {
        if (!other.CompareTag("Player")) return;
        TryReach();
    }

    private void TryReach()
    {
        if (resolved || playerTransform == null) return;
        if (GameManager.Instance != null && !GameManager.Instance.AllowCombat) return;

        Vector2 center = playerTransform.position;
        float reach = 80f;
        var col = playerTransform.GetComponent<Collider2D>();
        if (col != null)
        {
            center = col.bounds.center;
            reach = col.bounds.size.y * 0.22f;
        }

        if (Vector2.Distance(transform.position, center) > reach) return;
        resolved = true;
        ClearHitCombo();
        // Попадание по гг: GhostReachedHero → HeroHit → ShadowManager.AddShadow.
        GameEvents.GhostReachedHero();
        Destroy(gameObject);
    }

    /// <summary>
    /// Убийство только после успешного комбо Z/X/C.
    /// fromHitCombo должен быть true — вызов с Space / без комбо игнорируется (защита от старого InstantHit).
    /// </summary>
    public void Kill(int payout, bool fromHitCombo = false)
    {
        if (resolved) return;
        if (!fromHitCombo)
        {
            Debug.LogWarning("Monster.Kill без комбо Z/X/C запрещён — Space только стартует удар.");
            return;
        }

        resolved = true;
        ClearHitCombo();
        PixelBurst.Spawn(transform.position);
        FloatingIncome.Spawn(transform.position, payout);
        GameEvents.GhostKilled(payout);
        Destroy(gameObject);
    }

    /// <summary>Проверка hitRadius из balance (ширина героя × hitRadiusInHeroWidths).</summary>
    public bool IsWithinHitRadius(Vector2 origin, float hitRadius)
    {
        return Vector2.Distance(transform.position, origin) <= hitRadius;
    }

    /// <summary>Показать последовательность Z/X/C над головой; nextIndex — текущая ожидаемая клавиша.</summary>
    public void ShowHitCombo(char[] keys, int nextIndex)
    {
        comboKeys = keys;
        comboNextIndex = nextIndex;
        EnsureComboLabel();
        RefreshComboSprites();
        PlaceComboLabel();
    }

    public void ClearHitCombo()
    {
        comboKeys = null;
        comboNextIndex = -1;
        if (comboRoot != null)
        {
            Destroy(comboRoot.gameObject);
            comboRoot = null;
            comboLetterRenderers = null;
        }
    }

    void EnsureComboLabel()
    {
        if (comboRoot != null) return;

        var go = new GameObject("HitComboLetters");
        go.transform.SetParent(transform, false);
        comboRoot = go.transform;
        comboLetterRenderers = null;
    }

    /// <summary>
    /// Обновить спрайты: текущая клавиша — полный цвет + крупнее;
    /// остальные — grayscale / низкая насыщенность, обычный масштаб.
    /// </summary>
    void RefreshComboSprites()
    {
        if (comboRoot == null) return;

        if (comboKeys == null || comboKeys.Length == 0)
        {
            if (comboLetterRenderers != null)
            {
                for (int i = 0; i < comboLetterRenderers.Length; i++)
                {
                    if (comboLetterRenderers[i] != null)
                        comboLetterRenderers[i].enabled = false;
                }
            }
            return;
        }

        EnsureLetterRendererCount(comboKeys.Length);

        float totalWidth = (comboKeys.Length - 1) * ComboLetterSpacing;
        float startX = -totalWidth * 0.5f;

        for (int i = 0; i < comboKeys.Length; i++)
        {
            SpriteRenderer sr = comboLetterRenderers[i];
            bool isNext = i == comboNextIndex;
            Sprite sprite = ComboLetterSprites.Get(comboKeys[i], saturated: isNext);

            // Если grayscale-текстура недоступна, Get всё равно вернёт цветной — гасим насыщенность тинтом.
            if (sprite == null)
            {
                sr.enabled = false;
                continue;
            }

            sr.enabled = true;
            sr.sprite = sprite;
            sr.sortingOrder = ComboSortingOrder + (isNext ? 1 : 0);
            // Grayscale уже в текстуре; тинт — только запасной путь, если серый ассет = цветной.
            Sprite colorSprite = ComboLetterSprites.Get(comboKeys[i], saturated: true);
            bool grayFallback = !isNext && ReferenceEquals(sprite, colorSprite);
            sr.color = grayFallback
                ? new Color(0.55f, 0.55f, 0.55f, 0.9f)
                : Color.white;

            float scale = ComboLetterBaseScale * (isNext ? ComboActiveScaleMul : 1f);
            sr.transform.localScale = new Vector3(scale, scale, 1f);
            sr.transform.localPosition = new Vector3(startX + i * ComboLetterSpacing, 0f, 0f);
        }
    }

    void EnsureLetterRendererCount(int count)
    {
        if (comboLetterRenderers != null && comboLetterRenderers.Length == count)
            return;

        // Пересоздаём дочерние SpriteRenderer под длину ряда (2–4).
        if (comboLetterRenderers != null)
        {
            for (int i = 0; i < comboLetterRenderers.Length; i++)
            {
                if (comboLetterRenderers[i] != null)
                    Destroy(comboLetterRenderers[i].gameObject);
            }
        }

        comboLetterRenderers = new SpriteRenderer[count];
        for (int i = 0; i < count; i++)
        {
            var letterGo = new GameObject("ComboLetter_" + i);
            letterGo.transform.SetParent(comboRoot, false);
            var sr = letterGo.AddComponent<SpriteRenderer>();
            sr.sortingOrder = ComboSortingOrder;
            comboLetterRenderers[i] = sr;
        }
    }

    void PlaceComboLabel()
    {
        if (comboRoot == null) return;
        float top = spriteRenderer != null ? spriteRenderer.bounds.max.y : transform.position.y + 40f;
        // Ряд букв чуть выше макушки призрака (как прежний TextMesh +18).
        comboRoot.position = new Vector3(transform.position.x, top + 18f, 0f);
    }

    void OnDestroy()
    {
        if (comboRoot != null)
        {
            Destroy(comboRoot.gameObject);
            comboRoot = null;
            comboLetterRenderers = null;
        }
    }
}
