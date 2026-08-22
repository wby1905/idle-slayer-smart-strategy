using UnityEngine;

namespace SmartStrategy;

internal static class SmartSettingsIcon
{
    private const int Width = 128;
    private const int Height = 64;
    private static Texture2D _texture;

    internal static Texture2D GetOrCreate()
    {
        if (_texture != null) return _texture;

        var pixels = Enumerable.Repeat(Color.clear, Width * Height).ToArray();
        var line = new Color(0.94f, 0.96f, 1f, 1f);
        var shadow = new Color(0.08f, 0.12f, 0.18f, 0.85f);
        var accent = new Color(0.20f, 0.92f, 0.46f, 1f);

        DrawRect(pixels, 17, 13, 94, 8, shadow);
        DrawRect(pixels, 17, 28, 94, 8, shadow);
        DrawRect(pixels, 17, 43, 94, 8, shadow);
        DrawRect(pixels, 15, 11, 94, 6, line);
        DrawRect(pixels, 15, 26, 94, 6, line);
        DrawRect(pixels, 15, 41, 94, 6, line);

        DrawKnob(pixels, 38, 8, accent, shadow);
        DrawKnob(pixels, 80, 23, accent, shadow);
        DrawKnob(pixels, 57, 38, accent, shadow);

        _texture = new Texture2D(Width, Height, TextureFormat.RGBA32, false)
        {
            name = "Smart Strategy Fallback Settings Icon",
            filterMode = FilterMode.Point,
            wrapMode = TextureWrapMode.Clamp,
            hideFlags = HideFlags.HideAndDontSave
        };
        _texture.SetPixels(pixels);
        _texture.Apply(false, true);
        return _texture;
    }

    private static void DrawKnob(Color[] pixels, int x, int y, Color color, Color shadow)
    {
        DrawRect(pixels, x + 2, y + 2, 17, 17, shadow);
        DrawRect(pixels, x, y, 17, 17, color);
        DrawRect(pixels, x + 4, y + 4, 9, 9, Color.white);
    }

    private static void DrawRect(Color[] pixels, int x, int y, int width, int height, Color color)
    {
        var minX = Math.Clamp(x, 0, Width);
        var maxX = Math.Clamp(x + width, 0, Width);
        var minY = Math.Clamp(y, 0, Height);
        var maxY = Math.Clamp(y + height, 0, Height);
        for (var row = minY; row < maxY; row++)
            for (var column = minX; column < maxX; column++)
                pixels[row * Width + column] = color;
    }
}
