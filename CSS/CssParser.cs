using System;
using System.Collections.Generic;

public static class CssParser
{
    /// <summary>
    /// 解析整份 CSS 文本为规则列表。解析过程尽力而为：无法识别的 at-rule 会被跳过并记录警告，
    /// 但不会中断整体解析。
    /// </summary>
    public static List<CssRule> Parse(string css, List<string> warnings)
    {
        var rules = new List<CssRule>();
        if (warnings == null) warnings = new List<string>();
        if (string.IsNullOrEmpty(css)) return rules;

        ParseBlock(StripComments(css), rules, warnings, 0);
        return rules;
    }

    public static string StripComments(string css)
    {
        if (string.IsNullOrEmpty(css)) return css;
        var sb = new System.Text.StringBuilder(css.Length);
        int i = 0;
        char quote = '\0';
        while (i < css.Length)
        {
            char c = css[i];
            if (quote != '\0')
            {
                sb.Append(c);
                if (c == '\\' && i + 1 < css.Length) { sb.Append(css[i + 1]); i += 2; continue; }
                if (c == quote) quote = '\0';
                i++;
                continue;
            }
            if (c == '"' || c == '\'') { quote = c; sb.Append(c); i++; continue; }
            if (c == '/' && i + 1 < css.Length && css[i + 1] == '*')
            {
                int end = css.IndexOf("*/", i + 2, StringComparison.Ordinal);
                if (end < 0) break;
                i = end + 2;
                continue;
            }
            sb.Append(c);
            i++;
        }
        return sb.ToString();
    }

    private static void ParseBlock(string css, List<CssRule> rules, List<string> warnings, int depth)
    {
        if (depth > 16)
        {
            warnings.Add("嵌套层级过深，已停止解析。");
            return;
        }

        int i = 0;
        while (i < css.Length)
        {
            int open = css.IndexOf('{', i);
            if (open < 0) break;

            string prelude = css.Substring(i, open - i).Trim();
            int close = FindMatchingBrace(css, open);
            if (close < 0)
            {
                warnings.Add("存在未闭合的 '{'，其后内容已被忽略。");
                break;
            }

            string body = css.Substring(open + 1, close - open - 1);

            if (prelude.StartsWith("@"))
            {
                HandleAtRule(prelude, body, rules, warnings, depth);
            }
            else if (prelude.Length > 0)
            {
                var rule = new CssRule { sourceIndex = rules.Count };
                foreach (string sel in CssValue.SplitTopLevel(prelude, ','))
                {
                    string s = CssSelector.Normalize(sel);
                    if (s.Length > 0) rule.selectors.Add(s);
                }
                rule.declarations = ParseDeclarations(body, warnings);
                if (rule.selectors.Count > 0 && rule.declarations.Count > 0) rules.Add(rule);
            }

            i = close + 1;
        }
    }

    private static void HandleAtRule(string prelude, string body, List<CssRule> rules, List<string> warnings, int depth)
    {
        string name = prelude.Substring(1);
        int sp = name.IndexOfAny(new[] { ' ', '\t', '\r', '\n' });
        if (sp >= 0) name = name.Substring(0, sp);
        name = name.ToLowerInvariant();

        switch (name)
        {
            case "media":
            case "supports":
            case "layer":
                // 条件规则：展开内部规则，忽略条件本身
                warnings.Add("@" + name + " 的条件未被求值，内部规则已直接展开。");
                ParseBlock(body, rules, warnings, depth + 1);
                break;
            case "import":
            case "charset":
            case "namespace":
            case "font-face":
            case "keyframes":
            case "-webkit-keyframes":
            case "property":
                break;
            default:
                warnings.Add("已忽略不支持的 at-rule：@" + name);
                break;
        }
    }

    public static List<CssDeclaration> ParseDeclarations(string body, List<string> warnings)
    {
        var list = new List<CssDeclaration>();
        if (string.IsNullOrEmpty(body)) return list;

        foreach (string chunk in CssValue.SplitTopLevel(body, ';'))
        {
            if (string.IsNullOrEmpty(chunk.Trim())) continue;

            int colon = IndexOfTopLevel(chunk, ':');
            if (colon < 0)
            {
                warnings.Add("无法解析的声明（缺少 ':'）：" + Truncate(chunk));
                continue;
            }

            string prop = chunk.Substring(0, colon).Trim().ToLowerInvariant();
            string value = chunk.Substring(colon + 1).Trim();
            if (prop.Length == 0 || value.Length == 0) continue;

            bool important = false;
            int bang = value.LastIndexOf('!');
            if (bang >= 0)
            {
                string tail = value.Substring(bang + 1).Trim().ToLowerInvariant();
                if (tail == "important")
                {
                    important = true;
                    value = value.Substring(0, bang).Trim();
                }
            }

            list.Add(new CssDeclaration(prop, value, important));
        }
        return list;
    }

    public static int IndexOfTopLevel(string s, char target)
    {
        int depth = 0;
        char quote = '\0';
        for (int i = 0; i < s.Length; i++)
        {
            char c = s[i];
            if (quote != '\0')
            {
                if (c == '\\' && i + 1 < s.Length) { i++; continue; }
                if (c == quote) quote = '\0';
                continue;
            }
            if (c == '"' || c == '\'') { quote = c; continue; }
            if (c == '(' || c == '[') depth++;
            else if (c == ')' || c == ']') depth--;
            else if (depth == 0 && c == target) return i;
        }
        return -1;
    }

    public static int FindMatchingBrace(string css, int openIndex)
    {
        int depth = 0;
        char quote = '\0';
        for (int i = openIndex; i < css.Length; i++)
        {
            char c = css[i];
            if (quote != '\0')
            {
                if (c == '\\' && i + 1 < css.Length) { i++; continue; }
                if (c == quote) quote = '\0';
                continue;
            }
            if (c == '"' || c == '\'') { quote = c; continue; }
            if (c == '{') depth++;
            else if (c == '}')
            {
                depth--;
                if (depth == 0) return i;
            }
        }
        return -1;
    }

    private static string Truncate(string s)
    {
        s = s.Trim();
        return s.Length <= 40 ? s : s.Substring(0, 40) + "...";
    }
}
