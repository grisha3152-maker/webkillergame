using UnityEngine;

public class MonsterSpawner : MonoBehaviour
{
    [Header("Ссылки")]
    [SerializeField] private Monster monsterPrefab;
    [SerializeField] private Transform playerTransform;

    [Header("Настройки спавна")]
    [SerializeField] private float spawnInterval = 1.5f;
    [SerializeField] private float spawnDistance = 250f; // Расстояние появления за пределами экрана

    private float timer;

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
        float age = GameManager.Instance != null ? GameManager.Instance.Age01 : 0f;
        float fastest = Mathf.Max(0.35f, spawnInterval * 0.32f);
        return Mathf.Lerp(spawnInterval, fastest, age);
    }

    private void SpawnMonster()
    {
        Vector2 spawnDirection = GetRandomDirection();
        Vector2 spawnPosition = (Vector2)playerTransform.position + spawnDirection * spawnDistance;

        Monster newMonster = Instantiate(monsterPrefab, spawnPosition, Quaternion.identity);
        newMonster.Initialize(playerTransform);
    }

    private Vector2 GetRandomDirection()
    {
        // 0 = сверху, 1 = слева, 2 = справа
        int side = Random.Range(0, 3);

        switch (side)
        {
            case 0: // Сверху
                return new Vector2(Random.Range(-0.8f, 0.8f), 1f).normalized;
            case 1: // Слева
                return new Vector2(-1f, Random.Range(-0.3f, 0.8f)).normalized;
            case 2: // Справа
                return new Vector2(1f, Random.Range(-0.3f, 0.8f)).normalized;
            default:
                return Vector2.up;
        }
    }
}