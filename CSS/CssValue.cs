using System;
using System.Collections.Generic;
using System.Globalization;
using UnityEngine;

public enum CssUnit { Px, Percent, Em, Rem, Pt, Deg, Rad, Grad, Turn, Unitless }

[Serializable]
public struct CssLength
{
    public float value;
    public CssUnit unit;

    public CssLength(float value, CssUnit unit)
    {
        this.value = value;
        this.unit = unit;
    }

    public static CssLength Zero => new CssLength(0f, CssUnit.Px);

    /// <summary>
    /// 角度单位统一换算为度，长度单位统一换算为像素。
    /// </summary>
    public float Resolve(float percentReference, float fontSize)
    {
        switch (unit)
        {
            case CssUnit.Percent: return value * 0.01f * percentReference;
            case CssUnit.Pt: return value * 96f / 72f;
            case CssUnit.Em:
            case CssUnit.Rem: return value * fontSize;
            case CssUnit.Rad: return value * Mathf.Rad2Deg;
            case CssUnit.Grad: return value * 0.9f;
            case CssUnit.Turn: return value * 360f;
            default: return value;
        }
    }
}

public enum CssGradientKind { Linear, Radial, Conic }

public class CssGradient
{
    public CssGradientKind kind;
    /// <summary>Linear / Conic 的 CSS 角度（0deg 朝上，顺时针）。</summary>
    public float angleDeg;
    public bool hasRadiusX;
    public CssLength radiusX;
    public bool hasRadiusY;
    public CssLength radiusY;
    public bool closestSide;
    public Color colorA = Color.white;
    public Color colorB = Color.black;
    public int stopCount;
}

public class CssShadow
{
    public bool inset;
    public CssLength offsetX;
    public CssLength offsetY;
    public CssLength blur;
    public CssLength spread;
    public Color color = new Color(0f, 0f, 0f, 0.5f);
}

public static class CssValue
{
    private const NumberStyles FloatStyle = NumberStyles.Float | NumberStyles.AllowThousands;

    // ------------------------------------------------------------------
    // 通用切分：尊重括号与引号层级
    // ------------------------------------------------------------------
    public static List<string> SplitTopLevel(string input, params char[] separators)
    {
        var parts = new List<string>();
        if (string.IsNullOrEmpty(input)) return parts;

        int depth = 0;
        char quote = '\0';
        int start = 0;
        for (int i = 0; i < input.Length; i++)
        {
            char c = input[i];
            if (quote != '\0')
            {
                if (c == '\\' && i + 1 < input.Length) { i++; continue; }
                if (c == quote) quote = '\0';
                continue;
            }
            if (c == '"' || c == '\'') { quote = c; continue; }
            if (c == '(' || c == '[') depth++;
            else if (c == ')' || c == ']') depth--;
            else if (depth == 0 && Array.IndexOf(separators, c) >= 0)
            {
                parts.Add(input.Substring(start, i - start).Trim());
                start = i + 1;
            }
        }
        parts.Add(input.Substring(start).Trim());
        return parts;
    }

    public static string StripQuotes(string s)
    {
        if (string.IsNullOrEmpty(s)) return s;
        s = s.Trim();
        if (s.Length >= 2 && (s[0] == '"' || s[0] == '\'') && s[s.Length - 1] == s[0])
            return s.Substring(1, s.Length - 2);
        return s;
    }

    public static float ParseFloat(string token, float fallback = 0f)
    {
        if (string.IsNullOrEmpty(token)) return fallback;
        token = token.Trim();

        int end = 0;
        while (end < token.Length)
        {
            char c = token[end];
            bool numeric = char.IsDigit(c) || c == '.';
            if (!numeric && (c == '-' || c == '+') && end == 0) numeric = true;
            if (!numeric && (c == 'e' || c == 'E') && end > 0 && end + 1 < token.Length &&
                (char.IsDigit(token[end + 1]) || token[end + 1] == '-' || token[end + 1] == '+')) numeric = true;
            if (!numeric) break;
            end++;
        }

        if (end == 0) return fallback;
        string num = token.Substring(0, end);
        if (float.TryParse(num, FloatStyle, CultureInfo.InvariantCulture, out float v)) return v;
        return fallback;
    }

    // ------------------------------------------------------------------
    // 长度 / 角度
    // ------------------------------------------------------------------
    public static bool TryParseLength(string token, out CssLength length)
    {
        length = CssLength.Zero;
        if (string.IsNullOrEmpty(token)) return false;
        token = token.Trim().ToLowerInvariant();
        if (token.Length == 0) return false;

        CssUnit unit = CssUnit.Px;
        int cut = token.Length;
        if (token.EndsWith("%")) { unit = CssUnit.Percent; cut = token.Length - 1; }
        else if (token.EndsWith("px")) { unit = CssUnit.Px; cut = token.Length - 2; }
        else if (token.EndsWith("rem")) { unit = CssUnit.Rem; cut = token.Length - 3; }
        else if (token.EndsWith("em")) { unit = CssUnit.Em; cut = token.Length - 2; }
        else if (token.EndsWith("pt")) { unit = CssUnit.Pt; cut = token.Length - 2; }
        else if (token.EndsWith("deg")) { unit = CssUnit.Deg; cut = token.Length - 3; }
        else if (token.EndsWith("rad")) { unit = CssUnit.Rad; cut = token.Length - 3; }
        else if (token.EndsWith("grad")) { unit = CssUnit.Grad; cut = token.Length - 4; }
        else if (token.EndsWith("turn")) { unit = CssUnit.Turn; cut = token.Length - 4; }
        else if (!char.IsDigit(token[token.Length - 1]) && token[token.Length - 1] != '.') unit = CssUnit.Unitless;

        string num = token.Substring(0, cut);
        if (!float.TryParse(num, FloatStyle, CultureInfo.InvariantCulture, out float v)) return false;
        length = new CssLength(v, unit);
        return true;
    }

    public static float ParseAngle(string token, float fallback = 0f)
    {
        if (TryParseLength(token, out CssLength len) && IsAngleUnit(len.unit))
            return len.Resolve(0f, 0f);
        return fallback;
    }

    public static bool IsAngleUnit(CssUnit unit)
    {
        return unit == CssUnit.Deg || unit == CssUnit.Rad || unit == CssUnit.Grad || unit == CssUnit.Turn;
    }

    // ------------------------------------------------------------------
    // 颜色
    // ------------------------------------------------------------------
    public static bool TryParseColor(string token, out Color color)
    {
        color = Color.clear;
        if (string.IsNullOrEmpty(token)) return false;
        token = token.Trim();
        string lower = token.ToLowerInvariant();

        if (lower == "transparent") { color = new Color(0f, 0f, 0f, 0f); return true; }
        if (lower == "currentcolor") { color = Color.white; return true; }

        if (lower[0] == '#') return TryParseHex(lower, out color);

        int paren = lower.IndexOf('(');
        if (paren > 0 && lower.EndsWith(")"))
        {
            string fn = lower.Substring(0, paren).Trim();
            string args = token.Substring(paren + 1, token.Length - paren - 2);
            if (fn == "rgb" || fn == "rgba") return TryParseRgb(args, out color);
            if (fn == "hsl" || fn == "hsla") return TryParseHsl(args, out color);
        }

        if (NamedColors.TryGetValue(lower, out uint packed))
        {
            color = Unpack(packed);
            return true;
        }
        return false;
    }

    private static bool TryParseHex(string hex, out Color color)
    {
        color = Color.clear;
        hex = hex.Substring(1);
        if (hex.Length != 3 && hex.Length != 4 && hex.Length != 6 && hex.Length != 8) return false;
        if (hex.Length == 3 || hex.Length == 4)
        {
            var sb = new System.Text.StringBuilder();
            foreach (char c in hex) { sb.Append(c); sb.Append(c); }
            hex = sb.ToString();
        }
        if (!uint.TryParse(hex, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out uint v)) return false;

        if (hex.Length == 6) v = (v << 8) | 0xFF;
        color = Unpack(v);
        return true;
    }

    private static Color Unpack(uint rgba)
    {
        return new Color(((rgba >> 24) & 0xFF) / 255f, ((rgba >> 16) & 0xFF) / 255f,
                         ((rgba >> 8) & 0xFF) / 255f, (rgba & 0xFF) / 255f);
    }

    /// <summary>
    /// 颜色函数参数切分，同时兼容逗号语法与现代空格语法（含 '/' 分隔的 alpha）。
    /// </summary>
    private static List<string> SplitColorArgs(string args)
    {
        var clean = new List<string>();
        if (string.IsNullOrEmpty(args)) return clean;

        string normalized = args.Replace('/', ' ');
        var parts = SplitTopLevel(normalized, ',');
        if (parts.Count < 3) parts = SplitTopLevel(normalized, ' ', '\t');

        foreach (string p in parts)
        {
            string t = p.Trim();
            if (t.Length > 0) clean.Add(t);
        }
        return clean;
    }

    private static bool TryParseRgb(string args, out Color color)
    {
        color = Color.clear;
        var parts = SplitColorArgs(args);
        if (parts.Count < 3) return false;

        float r = Channel(parts[0]);
        float g = Channel(parts[1]);
        float b = Channel(parts[2]);
        float a = parts.Count >= 4 ? Alpha(parts[3]) : 1f;
        color = new Color(r, g, b, a);
        return true;
    }

    private static float Channel(string token)
    {
        token = token.Trim();
        if (token.EndsWith("%")) return Mathf.Clamp01(ParseFloat(token) / 100f);
        return Mathf.Clamp01(ParseFloat(token) / 255f);
    }

    private static float Alpha(string token)
    {
        token = token.Trim();
        if (token.EndsWith("%")) return Mathf.Clamp01(ParseFloat(token) / 100f);
        return Mathf.Clamp01(ParseFloat(token, 1f));
    }

    private static bool TryParseHsl(string args, out Color color)
    {
        color = Color.clear;
        var parts = SplitColorArgs(args);
        if (parts.Count < 3) return false;

        float h = Mathf.Repeat(ParseFloat(parts[0]) / 360f, 1f);
        float s = Mathf.Clamp01(ParseFloat(parts[1]) / 100f);
        float l = Mathf.Clamp01(ParseFloat(parts[2]) / 100f);
        float a = parts.Count >= 4 ? Alpha(parts[3]) : 1f;

        // HSL -> HSV
        float v = l + s * Mathf.Min(l, 1f - l);
        float sv = Mathf.Approximately(v, 0f) ? 0f : 2f * (1f - l / v);
        Color rgb = Color.HSVToRGB(h, sv, v);
        rgb.a = a;
        color = rgb;
        return true;
    }

    // ------------------------------------------------------------------
    // 渐变
    // ------------------------------------------------------------------
    public static bool TryParseGradient(string value, out CssGradient gradient)
    {
        gradient = null;
        if (string.IsNullOrEmpty(value)) return false;
        string lower = value.Trim().ToLowerInvariant();

        bool repeating = lower.StartsWith("repeating-");
        if (repeating) lower = lower.Substring("repeating-".Length);

        CssGradientKind kind;
        if (lower.StartsWith("linear-gradient(")) kind = CssGradientKind.Linear;
        else if (lower.StartsWith("radial-gradient(")) kind = CssGradientKind.Radial;
        else if (lower.StartsWith("conic-gradient(")) kind = CssGradientKind.Conic;
        else return false;

        int open = value.IndexOf('(');
        int close = value.LastIndexOf(')');
        if (open < 0 || close <= open) return false;
        string inner = value.Substring(open + 1, close - open - 1);

        var args = SplitTopLevel(inner, ',');
        var g = new CssGradient { kind = kind, angleDeg = kind == CssGradientKind.Linear ? 180f : 0f };

        var stops = new List<string>();
        int startIndex = 0;
        if (args.Count > 0 && IsGeometryArg(kind, args[0]))
        {
            ParseGeometry(g, args[0]);
            startIndex = 1;
        }
        for (int i = startIndex; i < args.Count; i++) stops.Add(args[i]);

        if (stops.Count == 0) return false;

        Color a, b;
        if (!TryParseStopColor(stops[0], out a)) return false;
        if (!TryParseStopColor(stops[stops.Count - 1], out b)) return false;

        g.colorA = a;
        g.colorB = stops.Count == 1 ? a : b;
        g.stopCount = stops.Count;
        gradient = g;
        return true;
    }

    private static bool IsGeometryArg(CssGradientKind kind, string arg)
    {
        string a = arg.Trim().ToLowerInvariant();
        if (a.StartsWith("to ")) return true;
        if (a.Contains(" at ")) return true;
        if (kind == CssGradientKind.Linear) return TryParseLength(a, out CssLength l) && IsAngleUnit(l.unit);
        if (kind == CssGradientKind.Conic) return a.StartsWith("from ");
        // radial: circle / ellipse / 尺寸关键字 / 显式半径
        return a.StartsWith("circle") || a.StartsWith("ellipse") || a.StartsWith("closest") ||
               a.StartsWith("farthest") || TryParseLength(a, out _);
    }

    private static void ParseGeometry(CssGradient g, string arg)
    {
        string a = arg.Trim().ToLowerInvariant();

        if (a.StartsWith("to "))
        {
            g.angleDeg = DirectionKeywordToAngle(a.Substring(3).Trim());
            return;
        }
        if (a.StartsWith("from "))
        {
            int at = a.IndexOf(" at ", StringComparison.Ordinal);
            string anglePart = at >= 0 ? a.Substring(5, at - 5) : a.Substring(5);
            g.angleDeg = TryParseLength(anglePart.Trim(), out CssLength al) ? al.Resolve(0f, 0f) : 0f;
            return;
        }

        if (g.kind == CssGradientKind.Linear)
        {
            if (TryParseLength(a, out CssLength len) && IsAngleUnit(len.unit)) g.angleDeg = len.Resolve(0f, 0f);
            return;
        }

        if (g.kind == CssGradientKind.Radial)
        {
            g.closestSide = a.Contains("closest-side");

            // 'at <position>' 是圆心位置，不是半径，必须先剥离
            int at = a.IndexOf(" at ", StringComparison.Ordinal);
            string sizePart = at >= 0 ? a.Substring(0, at) : a;

            foreach (string token in SplitTopLevel(sizePart, ' '))
            {
                if (!TryParseLength(token, out CssLength r) || IsAngleUnit(r.unit)) continue;
                if (r.unit != CssUnit.Px && r.unit != CssUnit.Percent) continue;

                if (!g.hasRadiusX) { g.hasRadiusX = true; g.radiusX = r; }
                else if (!g.hasRadiusY) { g.hasRadiusY = true; g.radiusY = r; }
            }
        }
    }

    public static float DirectionKeywordToAngle(string keyword)
    {
        bool top = keyword.Contains("top");
        bool bottom = keyword.Contains("bottom");
        bool left = keyword.Contains("left");
        bool right = keyword.Contains("right");

        if (top && left) return 315f;
        if (top && right) return 45f;
        if (bottom && left) return 225f;
        if (bottom && right) return 135f;
        if (right) return 90f;
        if (left) return 270f;
        if (top) return 0f;
        return 180f; // bottom / 默认
    }

    private static bool TryParseStopColor(string stop, out Color color)
    {
        color = Color.clear;
        string s = stop.Trim();

        // 函数式颜色需要整体匹配，先尝试直接解析
        if (TryParseColor(s, out color)) return true;

        var tokens = SplitTopLevel(s, ' ');
        var head = new List<string>();
        int depth = 0;
        for (int i = 0; i < tokens.Count; i++)
        {
            string t = tokens[i];
            head.Add(t);
            depth += CountChar(t, '(') - CountChar(t, ')');
            if (depth <= 0 && TryParseColor(string.Join(" ", head.ToArray()), out color)) return true;
        }
        return false;
    }

    private static int CountChar(string s, char c)
    {
        int n = 0;
        for (int i = 0; i < s.Length; i++) if (s[i] == c) n++;
        return n;
    }

    // ------------------------------------------------------------------
    // box-shadow
    // ------------------------------------------------------------------
    public static List<CssShadow> ParseShadows(string value)
    {
        var shadows = new List<CssShadow>();
        if (string.IsNullOrEmpty(value) || value.Trim() == "none") return shadows;

        foreach (string layer in SplitTopLevel(value, ','))
        {
            if (string.IsNullOrEmpty(layer)) continue;
            var tokens = SplitTopLevel(layer, ' ');

            var s = new CssShadow();
            int lengthCount = 0;
            for (int i = 0; i < tokens.Count; i++)
            {
                string t = tokens[i];
                if (t.ToLowerInvariant() == "inset") { s.inset = true; continue; }
                if (TryParseLength(t, out CssLength l) && !IsAngleUnit(l.unit))
                {
                    switch (lengthCount)
                    {
                        case 0: s.offsetX = l; break;
                        case 1: s.offsetY = l; break;
                        case 2: s.blur = l; break;
                        case 3: s.spread = l; break;
                    }
                    lengthCount++;
                    continue;
                }
                if (TryParseColor(t, out Color c)) s.color = c;
            }
            if (lengthCount >= 2) shadows.Add(s);
        }
        return shadows;
    }

    // ------------------------------------------------------------------
    // CSS 命名颜色
    // ------------------------------------------------------------------
    private static readonly Dictionary<string, uint> NamedColors = new Dictionary<string, uint>
    {
        {"transparent", 0x00000000}, {"black", 0x000000FF}, {"silver", 0xC0C0C0FF}, {"gray", 0x808080FF},
        {"grey", 0x808080FF}, {"white", 0xFFFFFFFF}, {"maroon", 0x800000FF}, {"red", 0xFF0000FF},
        {"purple", 0x800080FF}, {"fuchsia", 0xFF00FFFF}, {"magenta", 0xFF00FFFF}, {"green", 0x008000FF},
        {"lime", 0x00FF00FF}, {"olive", 0x808000FF}, {"yellow", 0xFFFF00FF}, {"navy", 0x000080FF},
        {"blue", 0x0000FFFF}, {"teal", 0x008080FF}, {"aqua", 0x00FFFFFF}, {"cyan", 0x00FFFFFF},
        {"orange", 0xFFA500FF}, {"aliceblue", 0xF0F8FFFF}, {"antiquewhite", 0xFAEBD7FF},
        {"aquamarine", 0x7FFFD4FF}, {"azure", 0xF0FFFFFF}, {"beige", 0xF5F5DCFF}, {"bisque", 0xFFE4C4FF},
        {"blanchedalmond", 0xFFEBCDFF}, {"blueviolet", 0x8A2BE2FF}, {"brown", 0xA52A2AFF},
        {"burlywood", 0xDEB887FF}, {"cadetblue", 0x5F9EA0FF}, {"chartreuse", 0x7FFF00FF},
        {"chocolate", 0xD2691EFF}, {"coral", 0xFF7F50FF}, {"cornflowerblue", 0x6495EDFF},
        {"cornsilk", 0xFFF8DCFF}, {"crimson", 0xDC143CFF}, {"darkblue", 0x00008BFF},
        {"darkcyan", 0x008B8BFF}, {"darkgoldenrod", 0xB8860BFF}, {"darkgray", 0xA9A9A9FF},
        {"darkgrey", 0xA9A9A9FF}, {"darkgreen", 0x006400FF}, {"darkkhaki", 0xBDB76BFF},
        {"darkmagenta", 0x8B008BFF}, {"darkolivegreen", 0x556B2FFF}, {"darkorange", 0xFF8C00FF},
        {"darkorchid", 0x9932CCFF}, {"darkred", 0x8B0000FF}, {"darksalmon", 0xE9967AFF},
        {"darkseagreen", 0x8FBC8FFF}, {"darkslateblue", 0x483D8BFF}, {"darkslategray", 0x2F4F4FFF},
        {"darkslategrey", 0x2F4F4FFF}, {"darkturquoise", 0x00CED1FF}, {"darkviolet", 0x9400D3FF},
        {"deeppink", 0xFF1493FF}, {"deepskyblue", 0x00BFFFFF}, {"dimgray", 0x696969FF},
        {"dimgrey", 0x696969FF}, {"dodgerblue", 0x1E90FFFF}, {"firebrick", 0xB22222FF},
        {"floralwhite", 0xFFFAF0FF}, {"forestgreen", 0x228B22FF}, {"gainsboro", 0xDCDCDCFF},
        {"ghostwhite", 0xF8F8FFFF}, {"gold", 0xFFD700FF}, {"goldenrod", 0xDAA520FF},
        {"greenyellow", 0xADFF2FFF}, {"honeydew", 0xF0FFF0FF}, {"hotpink", 0xFF69B4FF},
        {"indianred", 0xCD5C5CFF}, {"indigo", 0x4B0082FF}, {"ivory", 0xFFFFF0FF},
        {"khaki", 0xF0E68CFF}, {"lavender", 0xE6E6FAFF}, {"lavenderblush", 0xFFF0F5FF},
        {"lawngreen", 0x7CFC00FF}, {"lemonchiffon", 0xFFFACDFF}, {"lightblue", 0xADD8E6FF},
        {"lightcoral", 0xF08080FF}, {"lightcyan", 0xE0FFFFFF}, {"lightgoldenrodyellow", 0xFAFAD2FF},
        {"lightgray", 0xD3D3D3FF}, {"lightgrey", 0xD3D3D3FF}, {"lightgreen", 0x90EE90FF},
        {"lightpink", 0xFFB6C1FF}, {"lightsalmon", 0xFFA07AFF}, {"lightseagreen", 0x20B2AAFF},
        {"lightskyblue", 0x87CEFAFF}, {"lightslategray", 0x778899FF}, {"lightslategrey", 0x778899FF},
        {"lightsteelblue", 0xB0C4DEFF}, {"lightyellow", 0xFFFFE0FF}, {"limegreen", 0x32CD32FF},
        {"linen", 0xFAF0E6FF}, {"mediumaquamarine", 0x66CDAAFF}, {"mediumblue", 0x0000CDFF},
        {"mediumorchid", 0xBA55D3FF}, {"mediumpurple", 0x9370DBFF}, {"mediumseagreen", 0x3CB371FF},
        {"mediumslateblue", 0x7B68EEFF}, {"mediumspringgreen", 0x00FA9AFF},
        {"mediumturquoise", 0x48D1CCFF}, {"mediumvioletred", 0xC71585FF}, {"midnightblue", 0x191970FF},
        {"mintcream", 0xF5FFFAFF}, {"mistyrose", 0xFFE4E1FF}, {"moccasin", 0xFFE4B5FF},
        {"navajowhite", 0xFFDEADFF}, {"oldlace", 0xFDF5E6FF}, {"olivedrab", 0x6B8E23FF},
        {"orangered", 0xFF4500FF}, {"orchid", 0xDA70D6FF}, {"palegoldenrod", 0xEEE8AAFF},
        {"palegreen", 0x98FB98FF}, {"paleturquoise", 0xAFEEEEFF}, {"palevioletred", 0xDB7093FF},
        {"papayawhip", 0xFFEFD5FF}, {"peachpuff", 0xFFDAB9FF}, {"peru", 0xCD853FFF},
        {"pink", 0xFFC0CBFF}, {"plum", 0xDDA0DDFF}, {"powderblue", 0xB0E0E6FF},
        {"rosybrown", 0xBC8F8FFF}, {"royalblue", 0x4169E1FF}, {"saddlebrown", 0x8B4513FF},
        {"salmon", 0xFA8072FF}, {"sandybrown", 0xF4A460FF}, {"seagreen", 0x2E8B57FF},
        {"seashell", 0xFFF5EEFF}, {"sienna", 0xA0522DFF}, {"skyblue", 0x87CEEBFF},
        {"slateblue", 0x6A5ACDFF}, {"slategray", 0x708090FF}, {"slategrey", 0x708090FF},
        {"snow", 0xFFFAFAFF}, {"springgreen", 0x00FF7FFF}, {"steelblue", 0x4682B4FF},
        {"tan", 0xD2B48CFF}, {"thistle", 0xD8BFD8FF}, {"tomato", 0xFF6347FF},
        {"turquoise", 0x40E0D0FF}, {"violet", 0xEE82EEFF}, {"wheat", 0xF5DEB3FF},
        {"whitesmoke", 0xF5F5F5FF}, {"yellowgreen", 0x9ACD32FF}, {"rebeccapurple", 0x663399FF},
    };
}
