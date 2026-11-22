using UnityEngine;
using UnityEngine.InputSystem;

public class RadialAimController : MonoBehaviour
{
    [SerializeField] private float radiusMultiplier = 0.85f;

    // Углы верхней дуги: 0 = право, 90 = верх, 180 = лево
    const float AngleRight = 0f;
    const float AngleTop = 90f;
    const float AngleLeft = 180f;

    float currentAngle = AngleTop;
    float targetAngle = AngleTop;
    float drag = 1f;
    float aimRadius = 400f;
    int streak;
    Transform player;
    Collider2D playerCollider;
    SpriteRenderer playerSprite;
    LineRenderer ring;
    LineRenderer cursor;
    LineRenderer sector;
    bool sectorVisible;
    float sectorTimer;
    float sectorDrawAngle = AngleTop;

    void Start()
    {
        Balance.EnsureLoaded();
        player = GameObject.FindGameObjectWithTag("Player")?.transform;
        if (player != null)
        {
            playerCollider = player.GetComponent<Collider2D>();
            playerSprite = player.GetComponent<SpriteRenderer>();
        }
        aimRadius = MeasureAimRadius();
        ring = MakeLine("AimRing", 10, 2.8f);
        cursor = MakeLine("AimCursor", 12, 4.2f);
        sector = MakeLine("AimSector", 11, 5f);
        DrawRing();
    }

    void OnEnable()
    {
        GameEvents.OnHeroHit += OnHeroHit;
        GameEvents.OnGhostReachedHero += OnGhostReachedHero;
        GameEvents.OnGhostKilled += OnGhostKilled;
        GameEvents.OnMessageReplied += OnMessageReplied;
    }

    void OnDisable()
    {
        GameEvents.OnHeroHit -= OnHeroHit;
        GameEvents.OnGhostReachedHero -= OnGhostReachedHero;
        GameEvents.OnGhostKilled -= OnGhostKilled;
        GameEvents.OnMessageReplied -= OnMessageReplied;
    }

    void Update()
    {
        bool combat = GameManager.Instance == null || GameManager.Instance.AllowCombat;
        if (ring != null) ring.enabled = combat;
        if (cursor != null) cursor.enabled = combat;
        if (!combat)
        {
            if (sector != null) sector.enabled = false;
            return;
        }

        Vector2 origin = Origin();
        ReadAim();
        DrawRing();
        DrawCursor(origin);

        if (sectorVisible)
        {
            sectorTimer -= Time.deltaTime;
            DrawSector(origin, sectorDrawAngle, new Color(0.65f, 0.82f, 1f, Mathf.Clamp01(sectorTimer / 0.18f)));
            if (sectorTimer <= 0f)
            {
                sectorVisible = false;
                sector.enabled = false;
            }
        }

        var keyboard = Keyboard.current;
        if (keyboard != null && keyboard.spaceKey.wasPressedThisFrame)
            Attack(origin);
    }

    void ReadAim()
    {
        var keyboard = Keyboard.current;
        if (keyboard == null) return;

        // Нажатие задаёт целевой угол; курсор едет по дуге, не телепортируется.
        if (keyboard.aKey.wasPressedThisFrame)
        {
            targetAngle = AngleLeft;
            FlashSector(AngleLeft);
        }
        if (keyboard.wKey.wasPressedThisFrame)
        {
            targetAngle = AngleTop;
            FlashSector(AngleTop);
        }
        if (keyboard.dKey.wasPressedThisFrame)
        {
            targetAngle = AngleRight;
            FlashSector(AngleRight);
        }

        float speed = Balance.Data.baseTurnDegreesPerSecond * drag * (ChoiceIsOpen() ? Balance.Data.turnAfterPhone : 1f);
        // Углы 0..180: MoveTowards всегда идёт через верх, низа нет.
        currentAngle = Mathf.MoveTowards(currentAngle, targetAngle, speed * Time.deltaTime);
    }

    void Attack(Vector2 origin)
    {
        float hitRadius = HitRadius();
        float halfCone = Balance.Data.SectorHalfAngle;
        bool clearAll = Balance.Data.hitClearsAllInCone;
        int payout = Balance.Data.dollarsPerKill;
        if (GameManager.Instance != null)
            payout = GameManager.Instance.PayoutForCurrentAge();

        Monster best = null;
        float bestScore = float.MaxValue;
        int killed = 0;
        var monsters = FindObjectsByType<Monster>(FindObjectsSortMode.None);
        for (int i = 0; i < monsters.Length; i++)
        {
            Monster monster = monsters[i];
            if (monster == null || !monster.Alive) continue;
            Vector2 to = (Vector2)monster.transform.position - origin;
            float dist = to.magnitude;
            // Удар только внутри радиуса 1.5 ширины героя, не по всей дуге прицела.
            if (dist > hitRadius || dist < 0.01f) continue;
            float angle = Mathf.Atan2(to.y, to.x) * Mathf.Rad2Deg;
            float delta = Mathf.Abs(Mathf.DeltaAngle(currentAngle, angle));
            if (delta > halfCone) continue;

            if (clearAll)
            {
                monster.Kill(payout);
                killed++;
            }
            else
            {
                float score = delta * 4f + dist;
                if (score < bestScore)
                {
                    bestScore = score;
                    best = monster;
                }
            }
        }

        if (!clearAll && best != null)
        {
            best.Kill(payout);
            killed = 1;
        }

        // Промах — только подсветка сектора, без штрафа.
        if (killed == 0)
            FlashSector(currentAngle);
    }

    void OnHeroHit()
    {
        ApplyTurnPenalty(Balance.Data.turnAfterHit);
    }

    void OnGhostReachedHero()
    {
        // Шаг 3: событие есть, штраф поворота подключим на шаге 5 через ту же цепочку.
    }

    void OnGhostKilled(int _)
    {
        streak++;
        if (streak >= Balance.Data.killsToRestoreTurn)
        {
            streak = 0;
            drag = 1f;
            GameEvents.ComboRestored();
        }
    }

    void OnMessageReplied(bool positive)
    {
        if (!positive) return;
        ApplyTurnPenalty(Balance.Data.turnAfterPhone);
    }

    void ApplyTurnPenalty(float multiplier)
    {
        streak = 0;
        drag = Mathf.Max(Balance.Data.turnFloor, drag * multiplier);
    }

    bool ChoiceIsOpen()
    {
        var messenger = FindAnyObjectByType<MessengerSystem>();
        return messenger != null && messenger.ChoicePending;
    }

    void FlashSector(float centerAngle)
    {
        sectorDrawAngle = centerAngle;
        sectorVisible = true;
        sectorTimer = 0.18f;
    }

    float HeroWidth()
    {
        if (playerCollider != null) return Mathf.Max(1f, playerCollider.bounds.size.x);
        if (playerSprite != null) return Mathf.Max(1f, playerSprite.bounds.size.x);
        return 100f;
    }

    float HitRadius()
    {
        return HeroWidth() * Balance.Data.hitRadiusInHeroWidths;
    }

    float MeasureAimRadius()
    {
        if (playerCollider != null)
            return Mathf.Max(180f, playerCollider.bounds.size.y * radiusMultiplier);
        if (Camera.main != null)
            return Camera.main.orthographicSize * 0.7f;
        return 420f;
    }

    Vector2 Origin()
    {
        if (playerCollider != null) return playerCollider.bounds.center;
        if (player != null) return player.position;
        return Vector2.zero;
    }

    void DrawRing()
    {
        if (ring == null) return;
        const int steps = 28;
        ring.positionCount = steps + 1;
        Vector2 origin = Origin();
        for (int i = 0; i <= steps; i++)
        {
            float angle = Mathf.Lerp(AngleRight, AngleLeft, i / (float)steps) * Mathf.Deg2Rad;
            Vector3 point = origin + new Vector2(Mathf.Cos(angle), Mathf.Sin(angle)) * aimRadius;
            point.z = 0f;
            ring.SetPosition(i, point);
        }
    }

    void DrawCursor(Vector2 origin)
    {
        if (cursor == null) return;
        float rad = currentAngle * Mathf.Deg2Rad;
        Vector2 dir = new Vector2(Mathf.Cos(rad), Mathf.Sin(rad));
        cursor.positionCount = 2;
        cursor.SetPosition(0, origin + dir * (aimRadius * 0.78f));
        cursor.SetPosition(1, origin + dir * (aimRadius * 1.04f));
    }

    void DrawSector(Vector2 origin, float centerAngle, Color color)
    {
        if (sector == null) return;
        float half = Balance.Data.SectorHalfAngle;
        sector.enabled = true;
        sector.startColor = color;
        sector.endColor = color;
        sector.positionCount = 3;
        float a0 = (centerAngle - half) * Mathf.Deg2Rad;
        float a1 = (centerAngle + half) * Mathf.Deg2Rad;
        sector.SetPosition(0, origin);
        sector.SetPosition(1, (Vector3)origin + new Vector3(Mathf.Cos(a0), Mathf.Sin(a0), 0f) * aimRadius);
        sector.SetPosition(2, (Vector3)origin + new Vector3(Mathf.Cos(a1), Mathf.Sin(a1), 0f) * aimRadius);
    }

    LineRenderer MakeLine(string name, int order, float width)
    {
        var go = new GameObject(name);
        go.transform.SetParent(transform, false);
        var line = go.AddComponent<LineRenderer>();
        line.useWorldSpace = true;
        line.loop = false;
        line.widthMultiplier = width;
        line.numCapVertices = 4;
        line.sortingOrder = order;
        line.material = LineMaterial();
        var tint = new Color(0.62f, 0.8f, 0.95f, 0.45f);
        line.startColor = tint;
        line.endColor = tint;
        line.positionCount = 2;
        return line;
    }

    static Material lineMaterial;

    static Material LineMaterial()
    {
        if (lineMaterial != null) return lineMaterial;
        Shader shader = Shader.Find("Sprites/Default");
        if (shader == null) shader = Shader.Find("Universal Render Pipeline/2D/Sprite-Unlit-Default");
        lineMaterial = shader != null ? new Material(shader) : new Material(Shader.Find("Hidden/InternalErrorShader"));
        return lineMaterial;
    }
}
