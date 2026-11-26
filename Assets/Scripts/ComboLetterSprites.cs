using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// Кэш пиксельных спрайтов Z/X/C для HitCombo: цветные и обесцвеченные варианты.
/// </summary>
public static class ComboLetterSprites
{
    const string ResourceFolder = "Sprites/Combo/";
    /// <summary>
    /// PPU как у ghost-sad (~100). Monster размещает ряд в world-space (не child Circle×58.4)
    /// и задаёт localScale от высоты тела — meta spritePixelsToUnits=16 здесь игнорируем.
    /// </summary>
    const float PixelsPerUnit = 100f;
    /// <summary>Порог альфы при обрезке полей PNG (глиф ~450×520 в кадре 1280×698).</summary>
    const byte OpaqueAlphaThreshold = 16;
    /// <summary>Небольшой паддинг вокруг глифа после crop (px).</summary>
    const int CropPaddingPx = 4;

    static readonly Dictionary<char, Sprite> ColorSprites = new Dictionary<char, Sprite>();
    static readonly Dictionary<char, Sprite> GraySprites = new Dictionary<char, Sprite>();
    static bool attemptedLoad;

    /// <summary>Цветной или grayscale спрайт для клавиши; null если ассет не найден.</summary>
    public static Sprite Get(char key, bool saturated)
    {
        EnsureLoaded();
        char upper = char.ToUpperInvariant(key);
        if (saturated)
            return ColorSprites.TryGetValue(upper, out var color) ? color : null;

        // Неактивные всегда должны что-то вернуть: grayscale, иначе цветной (Monster гасит тинтом).
        if (GraySprites.TryGetValue(upper, out var gray) && gray != null)
            return gray;
        return ColorSprites.TryGetValue(upper, out var fallback) ? fallback : null;
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
        // Sprite после импорта; Texture2D — запасной путь (и для GetPixels32 grayscale/crop).
        Sprite imported = Resources.Load<Sprite>(path);
        Texture2D tex = imported != null ? imported.texture : Resources.Load<Texture2D>(path);
        if (tex == null)
        {
            Debug.LogWarning("ComboLetterSprites: нет ассета Resources/" + path);
            return;
        }

        tex.filterMode = FilterMode.Point;
        tex.wrapMode = TextureWrapMode.Clamp;

        // Полный кадр текстуры (Single sprite). imported.rect обычно 0..size, но не доверяем crop.
        Rect fullRect = new Rect(0f, 0f, tex.width, tex.height);
        if (imported != null && imported.rect.width > 0f && imported.rect.height > 0f
            && imported.texture == tex)
        {
            fullRect = imported.rect;
        }

        Color32[] pixels;
        try
        {
            pixels = tex.GetPixels32();
        }
        catch (UnityException)
        {
            Debug.LogWarning("ComboLetterSprites: текстура не readable — Resources/" + path);
            pixels = null;
        }

        // Обрезаем прозрачные поля: иначе Tight-mesh / огромный AABB ломают видимость и шаг ряда.
        Rect glyphRect = fullRect;
        if (pixels != null)
            glyphRect = OpaqueGlyphRect(pixels, tex.width, fullRect);

        Vector2 pivot = new Vector2(0.5f, 0.5f);

        // Своя копия пикселей + FullRect: не делим atlas/importer mesh с Resources Sprite.
        Texture2D colorTex = CopyRectTexture(pixels, tex, glyphRect, "ComboColor_" + key);
        if (colorTex == null)
        {
            // Fallback: Create прямо с исходной текстуры, но всегда FullRect (не Tight).
            ColorSprites[key] = Sprite.Create(
                tex, glyphRect, pivot, PixelsPerUnit, 0, SpriteMeshType.FullRect);
        }
        else
        {
            ColorSprites[key] = Sprite.Create(
                colorTex,
                new Rect(0f, 0f, colorTex.width, colorTex.height),
                pivot,
                PixelsPerUnit,
                0,
                SpriteMeshType.FullRect);
        }

        Sprite gray = TryMakeGraySprite(pixels, tex, glyphRect, pivot, key);
        GraySprites[key] = gray != null ? gray : ColorSprites[key];

        if (ColorSprites[key] == null)
            Debug.LogWarning("ComboLetterSprites: Sprite.Create вернул null для " + path);
    }

    /// <summary>AABB непрозрачных пикселей внутри fullRect + паддинг, в координатах текстуры.</summary>
    static Rect OpaqueGlyphRect(Color32[] pixels, int srcW, Rect fullRect)
    {
        int x0 = Mathf.RoundToInt(fullRect.x);
        int y0 = Mathf.RoundToInt(fullRect.y);
        int w = Mathf.RoundToInt(fullRect.width);
        int h = Mathf.RoundToInt(fullRect.height);
        if (w <= 0 || h <= 0) return fullRect;

        int minX = x0 + w;
        int minY = y0 + h;
        int maxX = x0 - 1;
        int maxY = y0 - 1;

        for (int row = 0; row < h; row++)
        {
            int srcRow = (y0 + row) * srcW;
            for (int col = 0; col < w; col++)
            {
                if (pixels[srcRow + (x0 + col)].a < OpaqueAlphaThreshold) continue;
                int x = x0 + col;
                int y = y0 + row;
                if (x < minX) minX = x;
                if (y < minY) minY = y;
                if (x > maxX) maxX = x;
                if (y > maxY) maxY = y;
            }
        }

        if (maxX < minX || maxY < minY)
            return fullRect;

        minX = Mathf.Max(x0, minX - CropPaddingPx);
        minY = Mathf.Max(y0, minY - CropPaddingPx);
        maxX = Mathf.Min(x0 + w - 1, maxX + CropPaddingPx);
        maxY = Mathf.Min(y0 + h - 1, maxY + CropPaddingPx);

        return new Rect(minX, minY, maxX - minX + 1, maxY - minY + 1);
    }

    static Texture2D CopyRectTexture(Color32[] pixels, Texture2D source, Rect rect, string name)
    {
        int x = Mathf.RoundToInt(rect.x);
        int y = Mathf.RoundToInt(rect.y);
        int w = Mathf.RoundToInt(rect.width);
        int h = Mathf.RoundToInt(rect.height);
        if (w <= 0 || h <= 0) return null;

        var copy = new Texture2D(w, h, TextureFormat.RGBA32, false);
        copy.filterMode = FilterMode.Point;
        copy.wrapMode = TextureWrapMode.Clamp;
        copy.name = name;

        if (pixels != null)
        {
            int srcW = source.width;
            var outPixels = new Color32[w * h];
            for (int row = 0; row < h; row++)
            {
                int srcRow = (y + row) * srcW + x;
                for (int col = 0; col < w; col++)
                    outPixels[row * w + col] = pixels[srcRow + col];
            }
            copy.SetPixels32(outPixels);
        }
        else
        {
            try
            {
                copy.SetPixels(source.GetPixels(x, y, w, h));
            }
            catch (UnityException)
            {
                Object.Destroy(copy);
                return null;
            }
        }

        copy.Apply(false, true);
        return copy;
    }

    /// <summary>Собрать обесцвеченную копию; null если текстура не readable.</summary>
    static Sprite TryMakeGraySprite(Color32[] pixels, Texture2D source, Rect rect, Vector2 pivot, char key)
    {
        if (pixels == null) return null;

        int x = Mathf.RoundToInt(rect.x);
        int y = Mathf.RoundToInt(rect.y);
        int w = Mathf.RoundToInt(rect.width);
        int h = Mathf.RoundToInt(rect.height);
        if (w <= 0 || h <= 0) return null;

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
                // Обесцвечивание ITU-R BT.601, затем подъём яркости:
                // исходные Z/X/C тёмные (avg ~35–130) → на тёмной комнате «исчезают».
                float lum01 = (c.r * 0.299f + c.g * 0.587f + c.b * 0.114f) / 255f;
                float lifted = 0.48f + lum01 * 0.52f; // 0..1 → 0.48..1.0, inactive читаемы
                byte lum = (byte)Mathf.Clamp(Mathf.RoundToInt(lifted * 255f), 0, 255);
                outPixels[row * w + col] = new Color32(lum, lum, lum, c.a);
            }
        }

        gray.SetPixels32(outPixels);
        gray.Apply(false, true);
        return Sprite.Create(
            gray,
            new Rect(0f, 0f, w, h),
            pivot,
            PixelsPerUnit,
            0,
            SpriteMeshType.FullRect);
    }
}
