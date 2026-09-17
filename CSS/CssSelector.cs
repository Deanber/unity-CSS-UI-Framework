using System;
using System.Collections.Generic;

/// <summary>
/// 简单选择器匹配。Unity 层级没有真正的 DOM，因此组合选择器（后代 / 子代 / 兄弟）只取最右侧的
/// 主体部分参与匹配，祖先条件被忽略。
/// </summary>
public static class CssSelector
{
    public static string Normalize(string selector)
    {
        if (string.IsNullOrEmpty(selector)) return string.Empty;
        var parts = selector.Split(new[] { ' ', '\t', '\r', '\n', '\f' }, StringSplitOptions.RemoveEmptyEntries);
        return string.Join(" ", parts);
    }

    /// <summary>id / class / 元素 权重打包为可比较的整数：a*10000 + b*100 + c。</summary>
    public static int Specificity(CssRule rule)
    {
        int best = 0;
        foreach (string sel in rule.selectors)
        {
            int a = 0, b = 0, c = 0;
            foreach (string atom in SplitAtoms(Subject(sel)))
            {
                if (atom.Length == 0) continue;
                if (atom[0] == '#') a++;
                else if (atom[0] == '.') b++;
                else if (atom[0] == '[' || atom[0] == ':') b++;
                else if (atom != "*") c++;
            }
            int score = a * 10000 + b * 100 + c;
            if (score > best) best = score;
        }
        return best;
    }

    public static bool Matches(string selector, string id, List<string> classes, string tagName)
    {
        if (string.IsNullOrEmpty(selector)) return false;
        string subject = Subject(selector);
        if (subject.Length == 0) return false;

        var atoms = SplitAtoms(subject);
        if (atoms.Count == 0) return false;

        foreach (string raw in atoms)
        {
            string atom = raw;
            if (atom.Length == 0) continue;

            // 伪类 / 伪元素 / 属性选择器无法在 Unity 侧求值，直接忽略
            if (atom[0] == ':') continue;
            if (atom[0] == '[')
            {
                if (!MatchesAttribute(atom, id, classes, tagName)) return false;
                continue;
            }
            if (atom[0] == '#')
            {
                if (!string.Equals(id, atom.Substring(1), StringComparison.Ordinal)) return false;
                continue;
            }
            if (atom[0] == '.')
            {
                if (classes == null || !ContainsClass(classes, atom.Substring(1))) return false;
                continue;
            }
            if (atom == "*") continue;

            // 元素选择器：对应 GameObject 名字
            if (!string.Equals(tagName, atom, StringComparison.OrdinalIgnoreCase)) return false;
        }
        return true;
    }

    private static bool MatchesAttribute(string atom, string id, List<string> classes, string tagName)
    {
        if (atom.Length < 3 || atom[atom.Length - 1] != ']') return false;
        string inner = atom.Substring(1, atom.Length - 2).Trim();
        int eq = inner.IndexOf('=');
        string name = (eq >= 0 ? inner.Substring(0, eq) : inner).Trim().ToLowerInvariant();
        string value = eq >= 0 ? CssValue.StripQuotes(inner.Substring(eq + 1).Trim()) : null;

        string actual = null;
        if (name == "id") actual = id;
        else if (name == "name") actual = tagName;
        else if (name == "class") actual = classes == null ? null : string.Join(" ", classes.ToArray());
        if (actual == null) return false;
        if (value == null) return true;
        return string.Equals(actual, value, StringComparison.Ordinal);
    }

    private static bool ContainsClass(List<string> classes, string name)
    {
        for (int i = 0; i < classes.Count; i++)
            if (string.Equals(classes[i], name, StringComparison.Ordinal)) return true;
        return false;
    }

    /// <summary>取组合选择器最右侧的主体复合选择器。</summary>
    public static string Subject(string selector)
    {
        string s = Normalize(selector);
        int cut = -1;
        char quote = '\0';
        for (int i = 0; i < s.Length; i++)
        {
            char c = s[i];
            if (quote != '\0')
            {
                if (c == quote) quote = '\0';
                continue;
            }
            if (c == '"' || c == '\'') { quote = c; continue; }
            if (c == ' ' || c == '>' || c == '+' || c == '~') cut = i;
        }
        if (cut < 0) return s;

        string tail = s.Substring(cut + 1).Trim();
        return tail.Length == 0 ? s : tail;
    }

    /// <summary>把复合选择器拆成 #id / .class / tag / [attr] / :pseudo 原子。</summary>
    public static List<string> SplitAtoms(string compound)
    {
        var atoms = new List<string>();
        if (string.IsNullOrEmpty(compound)) return atoms;

        int start = 0;
        char quote = '\0';
        for (int i = 0; i < compound.Length; i++)
        {
            char c = compound[i];
            if (quote != '\0')
            {
                if (c == '\\' && i + 1 < compound.Length) { i++; continue; }
                if (c == quote) quote = '\0';
                continue;
            }
            if (c == '"' || c == '\'') { quote = c; continue; }
            if (i > start && (c == '#' || c == '.' || c == '[' || c == ':'))
            {
                atoms.Add(compound.Substring(start, i - start));
                start = i;
            }
        }
        atoms.Add(compound.Substring(start));
        return atoms;
    }
}
