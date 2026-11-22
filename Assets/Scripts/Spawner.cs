using UnityEngine;

public class MonsterSpawner : MonoBehaviour
{
    [Header("Ссылки")]
    [SerializeField] private Monster monsterPrefab;
    [SerializeField] private Transform playerTransform;

    [Header("Настройки спавна")]
    [SerializeField] private float spawnDistance = 250f; // Расстояние появления за пределами экрана

    [Header("Направление")]
    [Tooltip("Старый 360° круг. Выключен для прототипа — спавн только слева/справа/сверху.")]
    [SerializeField] private bool useFullCircleSpawn = false;

    private float timer;

    private void Start()
    {
        Balance.EnsureLoaded();
    }

    private void Update()
    {
        if (playerTransform == null || monsterPrefab == null) return;
        if (GameManager.Instance != null && !GameManager.Instance.AllowCombat) return;

        timer += Time.deltaTime;
        if (timer >= CurrentInterval())
        {
            timer = 0f;
            SpawnMonster();
        }
    }

    private float CurrentInterval()
    {
        float elapsed = GameManager.Instance != null ? GameManager.Instance.Elapsed : 0f;
        return Balance.Data.SpawnEveryAt(elapsed);
    }

    private void SpawnMonster()
    {
        Vector2 spawnDirection = GetSpawnDirection();
        Vector2 spawnPosition = (Vector2)playerTransform.position + spawnDirection * spawnDistance;

        Monster newMonster = Instantiate(monsterPrefab, spawnPosition, Quaternion.identity);
        newMonster.Initialize(playerTransform);
    }

    private Vector2 GetSpawnDirection()
    {
        // Поле оставляем: старый полный круг можно снова включить флагом.
        if (useFullCircleSpawn)
            return Random.insideUnitCircle.normalized;

        return GetSideDirection();
    }

    private Vector2 GetSideDirection()
    {
        var weights = Balance.Data.spawnWeights;
        float left = Mathf.Max(0f, weights.left);
        float right = Mathf.Max(0f, weights.right);
        float top = Mathf.Max(0f, weights.top);
        float sum = left + right + top;
        if (sum <= 0f)
        {
            left = right = top = 1f;
            sum = 3f;
        }

        float roll = Random.value * sum;
        if (roll < left)
            return new Vector2(-1f, Random.Range(-0.3f, 0.8f)).normalized;
        roll -= left;
        if (roll < right)
            return new Vector2(1f, Random.Range(-0.3f, 0.8f)).normalized;
        return new Vector2(Random.Range(-0.8f, 0.8f), 1f).normalized;
    }
}
