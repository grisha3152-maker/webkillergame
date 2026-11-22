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
    // Визуал прицела — только палочка (линия от героя по углу). Без кольца / сектора-заливки.
    LineRenderer cursor;

    // Комбо удара: Space выбирает цель, Z/X/C добивают. Длина 2–4 кидается в момент старта удара (не при спавне).
    static readonly char[] ComboPool = { 'Z', 'X', 'C' };
    Monster comboTarget;
    char[] comboKeys;
    int comboIndex;
    int comboPayout;
    bool comboActive;

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
        cursor = MakeStick("AimCursor", 12, 3.2f);
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
        if (cursor != null) cursor.enabled = combat;
        if (!combat)
        {
            // Бой выключен — активное комбо сбрасываем без вспышки.
            if (comboActive) AbortCombo();
            return;
        }

        Vector2 origin = Origin();
        ReadAim();
        DrawCursor(origin);

        // Единственный обработчик Space: только старт комбо. Мгновенного Kill / Attack / InstantHit нет.
        if (comboActive)
            TickCombo(origin);
        else
        {
            var keyboard = Keyboard.current;
            if (keyboard != null && keyboard.spaceKey.wasPressedThisFrame)
                StartHitCombo(origin);
        }
    }

    void ReadAim()
    {
        var keyboard = Keyboard.current;
        if (keyboard == null) return;

        // Цель задаётся удержанием A/W/D. Без любой из клавиш курсор стоит на месте — не дрейфует к цели.
        bool holdA = keyboard.aKey.isPressed;
        bool holdW = keyboard.wKey.isPressed;
        bool holdD = keyboard.dKey.isPressed;
        bool holding = holdA || holdW || holdD;

        if (!holding)
        {
            targetAngle = currentAngle;
            return;
        }

        // При одновременном удержании побеждает последняя в порядке A → W → D.
        if (holdA) targetAngle = AngleLeft;
        if (holdW) targetAngle = AngleTop;
        if (holdD) targetAngle = AngleRight;

        float speed = Balance.Data.baseTurnDegreesPerSecond * drag * (ChoiceIsOpen() ? Balance.Data.turnAfterPhone : 1f);
        // Углы 0..180: MoveTowards всегда идёт через верх, низа нет.
        currentAngle = Mathf.MoveTowards(currentAngle, targetAngle, speed * Time.deltaTime);
    }

    /// <summary>
    /// Space: выбрать цель в конусе/hitRadius и показать комбо. Не вызывает Kill.
    /// Старый путь Attack()/InstantHit удалён — убийство только в FinishHitCombo после Z/X/C.
    /// </summary>
    void StartHitCombo(Vector2 origin)
    {
        // Пока комбо на одном призраке — новый Space / другая цель игнорируются (см. TickCombo).
        if (comboActive) return;

        Monster best = FindBestTarget(origin);
        if (best == null)
        {
            // Промах — без штрафа (визуал сектора убран).
            return;
        }

        int payout = Balance.Data.dollarsPerKill;
        if (GameManager.Instance != null)
            payout = GameManager.Instance.PayoutForCurrentAge();

        // Длина и буквы комбо выбираются здесь, в момент старта удара (не при спавне призрака).
        comboKeys = RollComboKeys();
        comboIndex = 0;
        comboPayout = payout;
        comboTarget = best;
        comboActive = true;
        // Захват: цель стоит, пока комбо активно (не летит к герою).
        best.SetMovementFrozen(true);
        best.ShowHitCombo(comboKeys, comboIndex);
        Debug.Log("HitCombo start: " + new string(comboKeys) + " (Space не убивает — жми Z/X/C)");
        GameEvents.HitComboStarted();
    }

    void TickCombo(Vector2 origin)
    {
        // Цель умерла / ушла / уничтожена — срыв как промах.
        if (comboTarget == null || !comboTarget.Alive)
        {
            AbortCombo();
            return;
        }

        float hitRadius = HitRadius();
        if (!comboTarget.IsWithinHitRadius(origin, hitRadius))
        {
            AbortCombo();
            return;
        }

        // Space во время комбо не начинает новый удар и не меняет цель — просто игнорируется.
        var keyboard = Keyboard.current;
        if (keyboard == null) return;

        char? pressed = ReadComboKey(keyboard);
        if (pressed == null) return;

        if (pressed.Value != comboKeys[comboIndex])
        {
            AbortCombo();
            return;
        }

        comboIndex++;
        if (comboIndex >= comboKeys.Length)
        {
            FinishHitCombo();
            return;
        }

        comboTarget.ShowHitCombo(comboKeys, comboIndex);
    }

    /// <summary>Единственное место, где удар завершается убийством — после всех клавиш комбо.</summary>
    void FinishHitCombo()
    {
        Monster target = comboTarget;
        int payout = comboPayout;
        // Позицию и ширину тела берём до Kill: splash = BodyWidth × comboSplashRadiusInBodyWidths.
        Vector2 splashCenter = target != null ? (Vector2)target.transform.position : Vector2.zero;
        float bodyWidth = target != null ? target.BodyWidth : 0f;

        ClearComboState(clearLabel: false);
        if (target != null && target.Alive)
        {
            // Соседей ищем до Destroy основной цели (Destroy отложен до конца кадра, но Alive уже false).
            Monster[] splash = FindSplashVictims(splashCenter, bodyWidth, exclude: target);
            target.Kill(payout, fromHitCombo: true);
            // До двух ближайших обычных призраков в радиусе; forced не трогаем (GDD).
            for (int i = 0; i < splash.Length; i++)
            {
                Monster other = splash[i];
                if (other != null && other.Alive)
                    other.Kill(payout, fromHitCombo: true);
            }
        }

        GameEvents.HitComboSucceeded();
    }

    void AbortCombo()
    {
        if (comboTarget != null)
        {
            comboTarget.ClearHitCombo();
            // Сброс комбо (ошибка / уход) — призрак снова летит к герою.
            comboTarget.SetMovementFrozen(false);
        }

        ClearComboState(clearLabel: false);
        GameEvents.HitComboFailed();
    }

    void ClearComboState(bool clearLabel)
    {
        if (comboTarget != null)
        {
            if (clearLabel)
                comboTarget.ClearHitCombo();
            // На случай сброса без AbortCombo (бой выключен и т.п.) — снимаем стоп.
            comboTarget.SetMovementFrozen(false);
        }

        comboActive = false;
        comboTarget = null;
        comboKeys = null;
        comboIndex = 0;
        comboPayout = 0;
    }

    /// <summary>Один лучший призрак в конусе и hitRadius. Комбо всегда на одну цель (hitClearsAllInCone не мульти-килл).</summary>
    Monster FindBestTarget(Vector2 origin)
    {
        float hitRadius = HitRadius();
        float halfCone = Balance.Data.SectorHalfAngle;
        Monster best = null;
        float bestScore = float.MaxValue;
        var monsters = FindObjectsByType<Monster>(FindObjectsSortMode.None);
        for (int i = 0; i < monsters.Length; i++)
        {
            Monster monster = monsters[i];
            if (monster == null || !monster.Alive) continue;
            // Forced не берётся захватом Space / конусом (GDD 11.1.1).
            if (monster.IsForced) continue;
            Vector2 to = (Vector2)monster.transform.position - origin;
            float dist = to.magnitude;
            // Удар только внутри hitRadius из balance.json, не по всей дуге прицела.
            if (dist > hitRadius || dist < 0.01f) continue;
            float angle = Mathf.Atan2(to.y, to.x) * Mathf.Rad2Deg;
            float delta = Mathf.Abs(Mathf.DeltaAngle(currentAngle, angle));
            if (delta > halfCone) continue;
            float score = delta * 4f + dist;
            if (score < bestScore)
            {
                bestScore = score;
                best = monster;
            }
        }

        return best;
    }

    /// <summary>
    /// После успешного Kill — до comboSplashMaxExtraKills ближайших призраков
    /// в радиусе BodyWidth убитого × comboSplashRadiusInBodyWidths (как hitRadius-стиль).
    /// Forced не убиваем splash'ем.
    /// </summary>
    Monster[] FindSplashVictims(Vector2 center, float killedBodyWidth, Monster exclude)
    {
        int maxExtra = Balance.Data.comboSplashMaxExtraKills;
        if (maxExtra <= 0 || killedBodyWidth <= 0f)
            return System.Array.Empty<Monster>();

        float radius = killedBodyWidth * Balance.Data.comboSplashRadiusInBodyWidths;
        var monsters = FindObjectsByType<Monster>(FindObjectsSortMode.None);

        // Два ближайших без сортировки всего списка: держим top-N по дистанции.
        var best = new Monster[maxExtra];
        var bestDist = new float[maxExtra];
        for (int i = 0; i < maxExtra; i++)
            bestDist[i] = float.MaxValue;

        int filled = 0;
        for (int i = 0; i < monsters.Length; i++)
        {
            Monster monster = monsters[i];
            if (monster == null || monster == exclude || !monster.Alive) continue;
            if (monster.IsForced) continue;

            float dist = Vector2.Distance(center, monster.transform.position);
            if (dist > radius) continue;

            // Вставка в упорядоченный top-N (возрастание dist).
            int slot = filled < maxExtra ? filled : maxExtra - 1;
            if (filled >= maxExtra && dist >= bestDist[maxExtra - 1]) continue;

            int insertAt = slot;
            while (insertAt > 0 && dist < bestDist[insertAt - 1])
                insertAt--;

            for (int j = slot; j > insertAt; j--)
            {
                best[j] = best[j - 1];
                bestDist[j] = bestDist[j - 1];
            }

            best[insertAt] = monster;
            bestDist[insertAt] = dist;
            if (filled < maxExtra) filled++;
        }

        if (filled == maxExtra) return best;
        if (filled == 0) return System.Array.Empty<Monster>();

        var trimmed = new Monster[filled];
        for (int i = 0; i < filled; i++)
            trimmed[i] = best[i];
        return trimmed;
    }

    static char[] RollComboKeys()
    {
        int length = Random.Range(2, 5); // 2, 3 или 4
        var keys = new char[length];
        for (int i = 0; i < length; i++)
            keys[i] = ComboPool[Random.Range(0, ComboPool.Length)];
        return keys;
    }

    static char? ReadComboKey(Keyboard keyboard)
    {
        if (keyboard.zKey.wasPressedThisFrame) return 'Z';
        if (keyboard.xKey.wasPressedThisFrame) return 'X';
        if (keyboard.cKey.wasPressedThisFrame) return 'C';
        return null;
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

    void DrawCursor(Vector2 origin)
    {
        if (cursor == null) return;
        float rad = currentAngle * Mathf.Deg2Rad;
        Vector2 dir = new Vector2(Mathf.Cos(rad), Mathf.Sin(rad));
        // Палочка от края героя до радиуса прицела — без дуги и без сектора.
        float inner = Mathf.Max(HeroWidth() * 0.55f, aimRadius * 0.12f);
        cursor.positionCount = 2;
        cursor.SetPosition(0, origin + dir * inner);
        cursor.SetPosition(1, origin + dir * aimRadius);
    }

    LineRenderer MakeStick(string name, int order, float width)
    {
        var go = new GameObject(name);
        go.transform.SetParent(transform, false);
        var line = go.AddComponent<LineRenderer>();
        line.useWorldSpace = true;
        line.loop = false;
        line.widthMultiplier = width;
        line.numCapVertices = 0;
        line.sortingOrder = order;
        line.material = LineMaterial();
        var tint = new Color(0.78f, 0.88f, 0.98f, 0.85f);
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
