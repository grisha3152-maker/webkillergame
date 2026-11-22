using UnityEngine;
using UnityEngine.InputSystem;

public class RadialAimController : MonoBehaviour
{
    [SerializeField] private float baseTurnSpeed = 240f;
    [SerializeField] private float sectorHalfAngle = 28f;
    [SerializeField] private float radiusMultiplier = 0.85f;
    [SerializeField] private int killsToRestoreFlow = 5;

    float currentAngle = 90f;
    float drag = 1f;
    float radius = 400f;
    int streak;
    Transform player;
    Collider2D playerCollider;
    LineRenderer ring;
    LineRenderer cursor;
    LineRenderer sector;
    bool sectorVisible;
    float sectorTimer;

    void Start()
    {
        player = GameObject.FindGameObjectWithTag("Player")?.transform;
        if (player != null) playerCollider = player.GetComponent<Collider2D>();
        radius = MeasureRadius();
        ring = MakeLine("AimRing", 10, 2.8f);
        cursor = MakeLine("AimCursor", 12, 4.2f);
        sector = MakeLine("AimSector", 11, 5f);
        DrawRing();
    }

    void OnEnable()
    {
        GameEvents.OnHeroHit += OnHeroHit;
        GameEvents.OnGhostKilled += OnGhostKilled;
        GameEvents.OnMessageReplied += OnMessageReplied;
    }

    void OnDisable()
    {
        GameEvents.OnHeroHit -= OnHeroHit;
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
            DrawSector(origin, new Color(0.65f, 0.82f, 1f, Mathf.Clamp01(sectorTimer / 0.18f)));
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

        Vector2 wish = Vector2.zero;
        if (keyboard.aKey.isPressed) wish += Vector2.left;
        if (keyboard.dKey.isPressed) wish += Vector2.right;
        if (keyboard.wKey.isPressed) wish += Vector2.up;
        if (wish.sqrMagnitude < 0.01f) return;

        float target = Mathf.Atan2(wish.y, wish.x) * Mathf.Rad2Deg;
        target = Mathf.Clamp(target, 0f, 180f);
        float speed = baseTurnSpeed * drag * (ChoiceIsOpen() ? 0.62f : 1f);
        currentAngle = Mathf.MoveTowards(currentAngle, target, speed * Time.deltaTime);
    }

    void Attack(Vector2 origin)
    {
        Monster best = null;
        float bestScore = float.MaxValue;
        var monsters = FindObjectsByType<Monster>(FindObjectsSortMode.None);
        for (int i = 0; i < monsters.Length; i++)
        {
            Monster monster = monsters[i];
            if (monster == null || !monster.Alive) continue;
            Vector2 to = (Vector2)monster.transform.position - origin;
            float dist = to.magnitude;
            if (dist > radius || dist < 0.01f) continue;
            float angle = Mathf.Atan2(to.y, to.x) * Mathf.Rad2Deg;
            float delta = Mathf.Abs(Mathf.DeltaAngle(currentAngle, angle));
            if (delta > sectorHalfAngle) continue;
            float score = delta * 4f + dist;
            if (score < bestScore)
            {
                bestScore = score;
                best = monster;
            }
        }

        if (best != null)
        {
            int payout = GameManager.Instance != null ? GameManager.Instance.PayoutForCurrentAge() : 10;
            best.Kill(payout);
        }
        else
        {
            FlashSector();
        }
    }

    void OnHeroHit()
    {
        streak = 0;
        drag = Mathf.Max(0.24f, drag * 0.74f);
    }

    void OnGhostKilled(int _)
    {
        streak++;
        if (streak >= killsToRestoreFlow)
        {
            streak = 0;
            drag = 1f;
        }
    }

    void OnMessageReplied(bool positive)
    {
        if (!positive) return;
        streak = 0;
        drag = Mathf.Max(0.24f, drag * 0.7f);
    }

    bool ChoiceIsOpen()
    {
        var messenger = FindAnyObjectByType<MessengerSystem>();
        return messenger != null && messenger.ChoicePending;
    }

    void FlashSector()
    {
        sectorVisible = true;
        sectorTimer = 0.18f;
    }

    float MeasureRadius()
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
            float angle = Mathf.Lerp(0f, 180f, i / (float)steps) * Mathf.Deg2Rad;
            Vector3 point = origin + new Vector2(Mathf.Cos(angle), Mathf.Sin(angle)) * radius;
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
        cursor.SetPosition(0, origin + dir * (radius * 0.78f));
        cursor.SetPosition(1, origin + dir * (radius * 1.04f));
    }

    void DrawSector(Vector2 origin, Color color)
    {
        if (sector == null) return;
        sector.enabled = true;
        sector.startColor = color;
        sector.endColor = color;
        sector.positionCount = 3;
        float a0 = (currentAngle - sectorHalfAngle) * Mathf.Deg2Rad;
        float a1 = (currentAngle + sectorHalfAngle) * Mathf.Deg2Rad;
        sector.SetPosition(0, origin);
        sector.SetPosition(1, (Vector3)origin + new Vector3(Mathf.Cos(a0), Mathf.Sin(a0), 0f) * radius);
        sector.SetPosition(2, (Vector3)origin + new Vector3(Mathf.Cos(a1), Mathf.Sin(a1), 0f) * radius);
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
