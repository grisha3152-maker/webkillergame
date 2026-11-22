using UnityEngine;

[RequireComponent(typeof(SpriteRenderer))]
public class BackgroundFitter : MonoBehaviour
{
    private SpriteRenderer spriteRenderer;

    void Start()
    {
        spriteRenderer = GetComponent<SpriteRenderer>();
        FitToScreen();
    }

    void FitToScreen()
    {
        if (spriteRenderer.sprite == null) return;

        // 1. Получаем размеры экрана в мировых координатах камеры
        float worldScreenHeight = Camera.main.orthographicSize * 2f;
        float worldScreenWidth = worldScreenHeight * Screen.width / Screen.height;

        // 2. Получаем размеры нашего спрайта
        float spriteWidth = spriteRenderer.sprite.bounds.size.x;
        float spriteHeight = spriteRenderer.sprite.bounds.size.y;

        // 3. Вычисляем нужный масштаб по ширине и по высоте
        float scaleX = worldScreenWidth / spriteWidth;
        float scaleY = worldScreenHeight / spriteHeight;

        // 4. Выбираем БОЛЬШИЙ масштаб, чтобы картинка точно заполнила экран (Cover)
        float finalScale = Mathf.Min(scaleX, scaleY);

        // Применяем масштаб
        transform.localScale = new Vector3(finalScale, finalScale, 1f);

        // 5. Центрируем картинку ровно по центру камеры
        transform.position = new Vector3(Camera.main.transform.position.x, Camera.main.transform.position.y, 0);
    }
}