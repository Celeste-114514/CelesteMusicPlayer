using System; using System.Collections.Generic; using System.Globalization; using Windows.UI;
namespace CelesteMusicPlayer { internal static class ColorPickerPalette {
private static readonly string[] PresetHexes =
        {
            "#40B4FF", "#5B9BD5", "#00B7C3", "#2ECC71", "#27AE60",
            "#F1C40F", "#E67E22", "#E74C3C", "#C0392B", "#9B59B6",
            "#8E44AD", "#3498DB", "#1ABC9C", "#16A085", "#F39C12",
            "#FFFFFF", "#F5F5F5", "#E0E0E0", "#BDBDBD", "#9E9E9E",
            "#757575", "#616161", "#424242", "#212121", "#000000",
            "#FF8A80", "#FF80AB", "#EA80FC", "#B388FF", "#8C9EFF",
            "#82B1FF", "#80D8FF", "#84FFFF", "#A7FFEB", "#B9F6CA"
        };
internal static IReadOnlyList<string> GetPresetHexes() => PresetHexes;
        internal static string? NormalizeHex(string? hex)
        {
            if (!TryParseHex(hex, out Color c))
            {
                return null;
            }

            return $"#{c.R:X2}{c.G:X2}{c.B:X2}";
        }

        internal static bool TryParseHex(string? hex, out Color color)
        {
            color = default;
            if (string.IsNullOrWhiteSpace(hex))
            {
                return false;
            }

            string h = hex.Trim();
            if (h.StartsWith('#'))
            {
                h = h[1..];
            }

            if (h.Length == 3)
            {
                h = $"{h[0]}{h[0]}{h[1]}{h[1]}{h[2]}{h[2]}";
            }

            if (h.Length != 6
                || !byte.TryParse(h.AsSpan(0, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out byte r)
                || !byte.TryParse(h.AsSpan(2, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out byte g)
                || !byte.TryParse(h.AsSpan(4, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out byte b))
            {
                return false;
            }

            color = Color.FromArgb(255, r, g, b);
            return true;
        }
}}
