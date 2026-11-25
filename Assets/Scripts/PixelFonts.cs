using UnityEngine;
using UnityEngine.UI;

/// <summary>
/// Общий загрузчик пиксельного шрифта m5x7 (Resources/Fonts/m5x7).
/// Все runtime-текст (UGUI Text / TextMesh) берёт шрифт отсюда.
/// </summary>
public static class PixelFonts
{
    const string ResourcePath = "Fonts/m5x7";
    /// <summary>Кириллица + EN mid/final ending lines (глифы вшиты в m5x7.ttf).</summary>
    const string CyrillicWarmup =
        "тытамвообщеживойхорошийвопросwas I even alive.. really? and " +
        "maybe I should've just.. talked? Was it worth it?. " +
        "абвгдеёжзийклмнопрстуфхцчшщъыьэюяАБВГДЕЁЖЗИЙКЛМНОПРСТУФХЦЧШЩЪЫЬЭЮЯ";

    static Font cached;

    /// <summary>Пиксельный m5x7; point-filter на текстуре глифов.</summary>
    public static Font M5x7
    {
        get
        {
            if (cached != null) return cached;

            cached = Resources.Load<Font>(ResourcePath);
            if (cached == null)
            {
                Debug.LogWarning("PixelFonts: не найден Resources/Fonts/m5x7 — fallback LegacyRuntime.");
                cached = Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf");
                return cached;
            }

            ApplyPointFilter(cached);
            // Прогрев кириллицы под типичные кегли ending UI.
            WarmCyrillic(cached, 40);
            WarmCyrillic(cached, 72);
            return cached;
        }
    }

    /// <summary>Назначить m5x7 на UGUI Text.</summary>
    public static void Apply(Text text)
    {
        if (text == null) return;
        var font = M5x7;
        if (font == null) return;
        text.font = font;
        WarmCyrillic(font, text.fontSize > 0 ? text.fontSize : 32);
    }

    /// <summary>Назначить m5x7 на TextMesh и материал рендерера.</summary>
    public static void Apply(TextMesh mesh)
    {
        if (mesh == null) return;
        var font = M5x7;
        if (font == null) return;
        mesh.font = font;
        var renderer = mesh.GetComponent<MeshRenderer>();
        if (renderer != null && font.material != null)
            renderer.sharedMaterial = font.material;
        ApplyPointFilter(font);
        WarmCyrillic(font, mesh.fontSize > 0 ? mesh.fontSize : 32);
    }

    static void WarmCyrillic(Font font, int size)
    {
        if (font == null || size <= 0) return;
        font.RequestCharactersInTexture(CyrillicWarmup, size, FontStyle.Normal);
        ApplyPointFilter(font);
    }

    static void ApplyPointFilter(Font font)
    {
        if (font == null || font.material == null) return;
        var tex = font.material.mainTexture;
        if (tex != null)
            tex.filterMode = FilterMode.Point;
    }
}
