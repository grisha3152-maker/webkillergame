using UnityEngine;

/// <summary>
/// Мировая подпись под ГГ: короткая реплика при открытии телефона с непрочитанными (не в чате).
/// Слушает GameEvents.OnHeroMutter, гаснет за пару секунд.
/// </summary>
public class HeroMutterView : MonoBehaviour
{
    const float LifeSeconds = 2.2f;
    const float FadeSeconds = 0.7f;

    TextMesh mesh;
    Transform player;
    float age;
    float life;
    Color baseColor = new Color(0.82f, 0.84f, 0.86f, 0.95f);

    void OnEnable()
    {
        GameEvents.OnHeroMutter += Show;
        GameEvents.OnFinaleStarted += Clear;
    }

    void OnDisable()
    {
        GameEvents.OnHeroMutter -= Show;
        GameEvents.OnFinaleStarted -= Clear;
    }

    void Start()
    {
        ResolvePlayer();
    }

    void Update()
    {
        if (mesh == null) return;

        if (player == null) ResolvePlayer();
        PlaceUnderHero();

        age += Time.deltaTime;
        float remain = life - age;
        float a = remain <= FadeSeconds
            ? Mathf.Clamp01(remain / FadeSeconds) * baseColor.a
            : baseColor.a;
        var c = mesh.color;
        c.a = a;
        mesh.color = c;

        if (age >= life)
            Clear();
    }

    void Show(string line)
    {
        if (string.IsNullOrEmpty(line)) return;
        ResolvePlayer();
        EnsureMesh();
        mesh.text = line;
        mesh.color = baseColor;
        age = 0f;
        life = LifeSeconds;
        PlaceUnderHero();
    }

    void Clear()
    {
        if (mesh != null)
            mesh.text = string.Empty;
        age = 0f;
        life = 0f;
    }

    void ResolvePlayer()
    {
        if (player != null) return;
        var go = GameObject.FindGameObjectWithTag("Player");
        if (go != null) player = go.transform;
    }

    void EnsureMesh()
    {
        if (mesh != null) return;
        var go = new GameObject("HeroMutter");
        go.transform.SetParent(transform, false);
        mesh = go.AddComponent<TextMesh>();
        mesh.anchor = TextAnchor.UpperCenter;
        mesh.alignment = TextAlignment.Center;
        mesh.characterSize = 14f;
        mesh.fontSize = 42;
        mesh.color = baseColor;
        var font = Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf");
        if (font != null) mesh.font = font;
        var renderer = go.GetComponent<MeshRenderer>();
        if (renderer != null) renderer.sortingOrder = 35;
        mesh.text = string.Empty;
    }

    void PlaceUnderHero()
    {
        if (mesh == null) return;
        Vector3 pos;
        if (player != null)
        {
            var col = player.GetComponent<Collider2D>();
            var sr = player.GetComponent<SpriteRenderer>();
            float bottom = player.position.y;
            if (col != null) bottom = col.bounds.min.y;
            else if (sr != null) bottom = sr.bounds.min.y;
            pos = new Vector3(player.position.x, bottom - 28f, 0f);
        }
        else
        {
            pos = mesh.transform.position;
        }
        mesh.transform.position = pos;
    }
}
