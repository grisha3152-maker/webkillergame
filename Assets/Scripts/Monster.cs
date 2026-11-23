using System.Text;
using UnityEngine;

public class Monster : MonoBehaviour
{
    [Header("Настройки скорости")]
    [SerializeField] private float startSpeed = 450f; // Быстрый рывок при появлении
    [SerializeField] private float endSpeed = 35f;    // Медленное подкрадывание у игрока
    [SerializeField] private bool digitalTint = true;

    private Transform playerTransform;
    private SpriteRenderer spriteRenderer;
    private Collider2D bodyCollider;
    private float initialDistance;
    private bool resolved;
    /// <summary>Заморожен захватом HitCombo (Space): не летит к герою, пока комбо активно.</summary>
    private bool movementFrozen;
    private TextMesh comboLabel;
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
            if (comboLabel != null && comboNextIndex >= 0)
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
        if (comboLabel != null && comboNextIndex >= 0)
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
        RefreshComboText();
        PlaceComboLabel();
    }

    public void ClearHitCombo()
    {
        comboKeys = null;
        comboNextIndex = -1;
        if (comboLabel != null)
        {
            Destroy(comboLabel.gameObject);
            comboLabel = null;
        }
    }

    void EnsureComboLabel()
    {
        if (comboLabel != null) return;
        var go = new GameObject("HitComboLabel");
        go.transform.SetParent(transform, false);
        comboLabel = go.AddComponent<TextMesh>();
        comboLabel.anchor = TextAnchor.LowerCenter;
        comboLabel.alignment = TextAlignment.Center;
        // Было 22/64 → 11/32 → 5.5/16 — ещё ~вдвое: ~2.75/8.
        comboLabel.characterSize = 2.75f;
        comboLabel.fontSize = 8;
        comboLabel.fontStyle = FontStyle.Bold;
        comboLabel.color = new Color(1f, 0.92f, 0.35f, 1f);
        var font = Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf");
        if (font != null) comboLabel.font = font;
        var renderer = go.GetComponent<MeshRenderer>();
        if (renderer != null) renderer.sortingOrder = 40;
    }

    void RefreshComboText()
    {
        if (comboLabel == null || comboKeys == null || comboKeys.Length == 0)
        {
            if (comboLabel != null) comboLabel.text = string.Empty;
            return;
        }

        // Простой world-space TextMesh: текущая клавиша в [ ], остальные через пробел.
        var sb = new StringBuilder(comboKeys.Length * 4);
        for (int i = 0; i < comboKeys.Length; i++)
        {
            if (i > 0) sb.Append(' ');
            if (i == comboNextIndex)
            {
                sb.Append('[');
                sb.Append(comboKeys[i]);
                sb.Append(']');
            }
            else if (i < comboNextIndex)
            {
                sb.Append('_');
            }
            else
            {
                sb.Append(comboKeys[i]);
            }
        }

        comboLabel.text = sb.ToString();
    }

    void PlaceComboLabel()
    {
        if (comboLabel == null) return;
        float top = spriteRenderer != null ? spriteRenderer.bounds.max.y : transform.position.y + 40f;
        comboLabel.transform.position = new Vector3(transform.position.x, top + 18f, 0f);
    }

    void OnDestroy()
    {
        if (comboLabel != null)
        {
            Destroy(comboLabel.gameObject);
            comboLabel = null;
        }
    }
}
