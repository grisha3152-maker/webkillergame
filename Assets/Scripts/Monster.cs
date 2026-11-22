using UnityEngine;

public class Monster : MonoBehaviour
{
    [Header("Настройки скорости")]
    [SerializeField] private float startSpeed = 450f; // Быстрый рывок при появлении
    [SerializeField] private float endSpeed = 35f;    // Медленное подкрадывание у игрока
    [SerializeField] private bool digitalTint = true;

    private Transform playerTransform;
    private SpriteRenderer spriteRenderer;
    private float initialDistance;
    private bool resolved;

    public bool Alive => !resolved;

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

        var hitbox = GetComponent<Collider2D>();
        if (hitbox != null) hitbox.isTrigger = true;
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
        GameEvents.HeroHit();
        Destroy(gameObject);
    }

    public void Kill(int payout)
    {
        if (resolved) return;
        resolved = true;
        PixelBurst.Spawn(transform.position);
        FloatingIncome.Spawn(transform.position, payout);
        GameEvents.GhostKilled(payout);
        Destroy(gameObject);
    }
}