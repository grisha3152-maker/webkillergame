using UnityEngine;

public class FloatingIncome : MonoBehaviour
{
    TextMesh text;
    float life = 0.9f;
    float age;
    Color color = new Color(0.78f, 0.9f, 0.72f, 1f);

    public static void Spawn(Vector3 worldPos, int amount)
    {
        var go = new GameObject("Income");
        go.transform.position = worldPos;
        var view = go.AddComponent<FloatingIncome>();
        var mesh = go.AddComponent<TextMesh>();
        mesh.text = "+$" + amount;
        mesh.anchor = TextAnchor.MiddleCenter;
        mesh.alignment = TextAlignment.Center;
        mesh.characterSize = 18f;
        mesh.fontSize = 48;
        mesh.color = view.color;
        PixelFonts.Apply(mesh);
        var renderer = go.GetComponent<MeshRenderer>();
        renderer.sortingOrder = 30;
        view.text = mesh;
    }

    void Update()
    {
        age += Time.deltaTime;
        transform.position += Vector3.up * (36f * Time.deltaTime);
        float a = Mathf.Clamp01(1f - age / life);
        text.color = new Color(color.r, color.g, color.b, a);
        if (age >= life) Destroy(gameObject);
    }
}

public class PixelBurst : MonoBehaviour
{
    static Sprite pixel;
    Vector2 velocity;
    float life;
    SpriteRenderer spriteRenderer;

    public static void Spawn(Vector3 worldPos)
    {
        for (int i = 0; i < 10; i++)
        {
            var go = new GameObject("Pixel");
            go.transform.position = worldPos;
            go.transform.localScale = Vector3.one * Random.Range(10f, 22f);
            var renderer = go.AddComponent<SpriteRenderer>();
            renderer.sprite = Pixel();
            renderer.sortingOrder = 25;
            renderer.color = Color.Lerp(new Color(0.65f, 0.86f, 1f), Color.white, Random.value);
            var bit = go.AddComponent<PixelBurst>();
            bit.spriteRenderer = renderer;
            float angle = Random.Range(0f, Mathf.PI * 2f);
            float speed = Random.Range(80f, 220f);
            bit.velocity = new Vector2(Mathf.Cos(angle), Mathf.Sin(angle)) * speed;
            bit.life = Random.Range(0.25f, 0.5f);
        }
    }

    static Sprite Pixel()
    {
        if (pixel != null) return pixel;
        var tex = new Texture2D(2, 2);
        tex.filterMode = FilterMode.Point;
        tex.SetPixels(new[] { Color.white, Color.white, Color.white, Color.white });
        tex.Apply();
        pixel = Sprite.Create(tex, new Rect(0, 0, 2, 2), new Vector2(0.5f, 0.5f), 2f);
        return pixel;
    }

    void Update()
    {
        life -= Time.deltaTime;
        transform.position += (Vector3)(velocity * Time.deltaTime);
        velocity *= 0.92f;
        if (spriteRenderer != null)
        {
            var c = spriteRenderer.color;
            c.a = Mathf.Clamp01(life * 3f);
            spriteRenderer.color = c;
        }
        if (life <= 0f) Destroy(gameObject);
    }
}
