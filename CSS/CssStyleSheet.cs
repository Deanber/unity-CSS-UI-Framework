using System.Collections.Generic;
using UnityEngine;

public class CssStyleSheet : ScriptableObject
{
    [TextArea(6, 20)] public string sourceCss;
    public List<CssRule> rules = new List<CssRule>();
    public List<string> parseWarnings = new List<string>();

    public void SetSource(string css)
    {
        sourceCss = css ?? string.Empty;
        parseWarnings = new List<string>();
        rules = CssParser.Parse(sourceCss, parseWarnings);
    }

    public CssRule FindRule(string selector)
    {
        if (string.IsNullOrEmpty(selector)) return null;
        string needle = selector.Trim();
        for (int i = 0; i < rules.Count; i++)
        for (int s = 0; s < rules[i].selectors.Count; s++)
            if (rules[i].selectors[s] == needle)
                return rules[i];
        return null;
    }

    public List<CssDeclaration> Collect(string id, List<string> classes, string tagName)
    {
        return CssCascade.Resolve(rules, id, classes, tagName);
    }
}
