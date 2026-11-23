using UnityEngine;

public class Monster : MonoBehaviour
{
    [Header("Настройки скорости")]
    [SerializeField] private float startSpeed = 450f; // Быстрый рывок при появлении
    [SerializeField] private float endSpeed = 35f;    // Медленное подкрадывание у игрока
    [SerializeField] private bool digitalTint = true;

    /// <summary>
    /// Базовый масштаб букв над головой. PPU букв = 100 (как Pig) → при 0.18 высота ~1.25 wu,
    /// ряд из трёх — компактная «шапка», не гигантские буквы рядом с героем.
    /// </summary>
    const float ComboLetterBaseScale = 0.18f;
    /// <summary>Текущая клавиша чуть крупнее остальных (насыщенная vs grayscale).</summary>
    const float ComboActiveScaleMul = 1.28f;
    /// <summary>
    /// Доля bounds.width с непрозрачным глифом (Combo PNG ~1280×698, глиф ~450px ≈ 0.35).
    /// Шаг ряда — по визуальной ширине, не по полному rect со полями (иначе Z X C разлетаются).
    /// </summary>
    const float ComboLetterOpaqueWidthFrac = 0.36f;
    /// <summary>Плотный зазор между визуальными краями соседних букв (opaque-width шаг).</summary>
    const float ComboLetterEdgeGap = 0.08f;
    /// <summary>Зазор между макушкой призрака и низом букв (pivot по центру → halfH + clearance).</summary>
    const float ComboHeadClearance = 0.35f;
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
    /// Обновить спрайты: всегда весь ряд (Z/X/C…) над призраком.
    /// Текущая клавиша — полный цвет + чуть крупнее; остальные — grayscale, меньше, но видимые.
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

        // Шаг по визуальной ширине глифа × active-mul + edge gap (не полный PNG bounds — поля ~65%).
        float slotWidth = 0f;
        for (int i = 0; i < comboKeys.Length; i++)
        {
            Sprite measure = ComboLetterSprites.Get(comboKeys[i], saturated: true);
            if (measure == null) continue;
            float w = measure.bounds.size.x * ComboLetterOpaqueWidthFrac
                * ComboLetterBaseScale * ComboActiveScaleMul;
            if (w > slotWidth) slotWidth = w;
        }

        if (slotWidth < 0.05f)
            // fallback: 1280/100 × opaque × base × active ≈ 1.06
            slotWidth = (1280f / 100f) * ComboLetterOpaqueWidthFrac
                * ComboLetterBaseScale * ComboActiveScaleMul;

        float spacing = slotWidth + ComboLetterEdgeGap;
        float totalWidth = (comboKeys.Length - 1) * spacing;
        float startX = -totalWidth * 0.5f;

        for (int i = 0; i < comboKeys.Length; i++)
        {
            SpriteRenderer sr = comboLetterRenderers[i];
            bool isNext = i == comboNextIndex;
            Sprite colorSprite = ComboLetterSprites.Get(comboKeys[i], saturated: true);
            Sprite sprite = isNext
                ? colorSprite
                : ComboLetterSprites.Get(comboKeys[i], saturated: false);

            // Нет ассета — всё равно оставляем слот (пустой), не прячем ряд целиком.
            if (sprite == null && colorSprite == null)
            {
                sr.enabled = false;
                sr.sprite = null;
                continue;
            }

            if (sprite == null)
                sprite = colorSprite;

            sr.enabled = true;
            sr.sprite = sprite;
            // Одинаковый sorting у всех: раньше active +1 рисовал поверх и маскировал соседей при overlap.
            sr.sortingOrder = ComboSortingOrder;

            // Grayscale уже в текстуре; тинт — запасной путь + чуть приглушаем неактивные.
            bool grayFallback = !isNext && ReferenceEquals(sprite, colorSprite);
            if (isNext)
                sr.color = Color.white;
            else if (grayFallback)
                sr.color = new Color(0.62f, 0.62f, 0.62f, 1f);
            else
                sr.color = new Color(1f, 1f, 1f, 0.92f);

            float scale = ComboLetterBaseScale * (isNext ? ComboActiveScaleMul : 1f);
            sr.transform.localScale = new Vector3(scale, scale, 1f);
            sr.transform.localPosition = new Vector3(startX + i * spacing, 0f, 0f);
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

        // Центр по bounds призрака — ряд строго над головой, не сбоку у героя.
        float headX = spriteRenderer != null
            ? spriteRenderer.bounds.center.x
            : transform.position.x;
        float top = spriteRenderer != null
            ? spriteRenderer.bounds.max.y
            : transform.position.y + 40f;

        // Pivot букв по центру: поднимаем на halfH + clearance, чтобы низ ряда был над макушкой.
        float halfH = 0f;
        if (comboLetterRenderers != null)
        {
            for (int i = 0; i < comboLetterRenderers.Length; i++)
            {
                SpriteRenderer sr = comboLetterRenderers[i];
                if (sr == null || !sr.enabled || sr.sprite == null) continue;
                float h = sr.sprite.bounds.size.y * Mathf.Abs(sr.transform.localScale.y) * 0.5f;
                if (h > halfH) halfH = h;
            }
        }

        if (halfH < 0.01f)
            halfH = (698f / 100f) * ComboLetterBaseScale * ComboActiveScaleMul * 0.5f;

        comboRoot.position = new Vector3(headX, top + halfH + ComboHeadClearance, 0f);
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
