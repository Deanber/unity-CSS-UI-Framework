using System;
using System.Collections.Generic;
using UnityEngine;

[Serializable]
public class CssDeclaration
{
    public string property;
    public string value;
    public bool important;

    public CssDeclaration() { }

    public CssDeclaration(string property, string value, bool important)
    {
        this.property = property;
        this.value = value;
        this.important = important;
    }

    public override string ToString()
    {
        return important ? property + ": " + value + " !important" : property + ": " + value;
    }
}

[Serializable]
public class CssRule
{
    public List<string> selectors = new List<string>();
    public List<CssDeclaration> declarations = new List<CssDeclaration>();

    [NonSerialized] public int sourceIndex;

    public string SelectorText => string.Join(", ", selectors);

    public CssDeclaration Find(string property)
    {
        CssDeclaration last = null;
        for (int i = 0; i < declarations.Count; i++)
            if (declarations[i].property == property) last = declarations[i];
        return last;
    }
}

/// <summary>
/// 层叠解析：按选择器权重与源码顺序排序，把命中的规则声明依次串接，
/// 后加入的声明在解释阶段覆盖先加入的。
/// </summary>
public static class CssCascade
{
    public static List<CssDeclaration> Resolve(List<CssRule> rules, string id, List<string> classes, string tagName)
    {
        var result = new List<CssDeclaration>();
        if (rules == null || rules.Count == 0) return result;

        var ordered = new List<CssRule>(rules);
        ordered.Sort((a, b) =>
        {
            int sa = CssSelector.Specificity(a), sb = CssSelector.Specificity(b);
            if (sa != sb) return sa.CompareTo(sb);
            return a.sourceIndex.CompareTo(b.sourceIndex);
        });

        foreach (var rule in ordered)
        {
            foreach (var sel in rule.selectors)
            {
                if (CssSelector.Matches(sel, id, classes, tagName))
                {
                    result.AddRange(rule.declarations);
                    break;
                }
            }
        }
        return result;
    }
}
