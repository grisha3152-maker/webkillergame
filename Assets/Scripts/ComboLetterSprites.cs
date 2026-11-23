using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// Кэш пиксельных спрайтов Z/X/C для HitCombo: цветные и обесцвеченные варианты.
/// </summary>
public static class ComboLetterSprites
{
    const string ResourceFolder = "Sprites/Combo/";
    /// <summary>PPU: буква ~450px → ~28 world-units при 16 (масштаб призрака ~58).</summary>
    const float PixelsPerUnit = 16f;

    static readonly Dictionary<char, Sprite> ColorSprites = new Dictionary<char, Sprite>();
    static readonly Dictionary<char, Sprite> GraySprites = new Dictionary<char, Sprite>();
    static bool attemptedLoad;

    /// <summary>Цветной или grayscale спрайт для клавиши; null если ассет не найден.</summary>
    public static Sprite Get(char key, bool saturated)
    {
        EnsureLoaded();
        char upper = char.ToUpperInvariant(key);
        var map = saturated ? ColorSprites : GraySprites;
        return map.TryGetValue(upper, out var sprite) ? sprite : null;
    }

    static void EnsureLoaded()
    {
        if (attemptedLoad) return;
        attemptedLoad = true;

        LoadLetter('Z');
        LoadLetter('X');
        LoadLetter('C');
    }

    static void LoadLetter(char key)
    {
        string path = ResourceFolder + key;
        // Сначала Sprite (после импорта Unity), иначе Texture2D → Sprite.Create.
        Sprite imported = Resources.Load<Sprite>(path);
        Texture2D tex = imported != null ? imported.texture : Resources.Load<Texture2D>(path);
        if (tex == null)
        {
            Debug.LogWarning("ComboLetterSprites: нет ассета Resources/" + path);
            return;
        }

        tex.filterMode = FilterMode.Point;
        tex.wrapMode = TextureWrapMode.Clamp;

        Rect rect = imported != null
            ? imported.rect
            : new Rect(0f, 0f, tex.width, tex.height);
        // Sprite.Create ждёт нормализованный pivot (0..1).
        Vector2 pivot = new Vector2(0.5f, 0.5f);
        float ppu = imported != null ? imported.pixelsPerUnit : PixelsPerUnit;

        if (imported != null)
            ColorSprites[key] = imported;
        else
            ColorSprites[key] = Sprite.Create(tex, rect, pivot, ppu);

        Sprite gray = TryMakeGraySprite(tex, rect, pivot, ppu, key);
        GraySprites[key] = gray != null ? gray : ColorSprites[key];
    }

    /// <summary>Собрать обесцвеченную копию; null если текстура не readable.</summary>
    static Sprite TryMakeGraySprite(Texture2D source, Rect rect, Vector2 pivot, float ppu, char key)
    {
        int x = Mathf.RoundToInt(rect.x);
        int y = Mathf.RoundToInt(rect.y);
        int w = Mathf.RoundToInt(rect.width);
        int h = Mathf.RoundToInt(rect.height);
        if (w <= 0 || h <= 0) return null;

        Color32[] pixels;
        try
        {
            pixels = source.GetPixels32();
        }
        catch (UnityException)
        {
            return null;
        }

        var gray = new Texture2D(w, h, TextureFormat.RGBA32, false);
        gray.filterMode = FilterMode.Point;
        gray.wrapMode = TextureWrapMode.Clamp;
        gray.name = "ComboGray_" + key;

        int srcW = source.width;
        var outPixels = new Color32[w * h];
        for (int row = 0; row < h; row++)
        {
            for (int col = 0; col < w; col++)
            {
                Color32 c = pixels[(y + row) * srcW + (x + col)];
                // Обесцвечивание: яркость по ITU-R BT.601, альфа без изменений.
                byte lum = (byte)Mathf.Clamp(
                    Mathf.RoundToInt(c.r * 0.299f + c.g * 0.587f + c.b * 0.114f),
                    0, 255);
                outPixels[row * w + col] = new Color32(lum, lum, lum, c.a);
            }
        }

        gray.SetPixels32(outPixels);
        gray.Apply(false, true);
        return Sprite.Create(gray, new Rect(0f, 0f, w, h), pivot, ppu);
    }
}
