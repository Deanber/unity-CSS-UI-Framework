using System.Collections.Generic;
using System.IO;
using UnityEditor;
using UnityEditor.AssetImporters;
using UnityEngine;

/// <summary>
/// 把项目里的 .css 文件直接导入为 CssStyleSheet 资源，拖进工程即自动解析。
/// </summary>
[ScriptedImporter(1, "css")]
public class CssStyleSheetImporter : ScriptedImporter
{
    public override void OnImportAsset(AssetImportContext ctx)
    {
        string css = File.Exists(ctx.assetPath) ? File.ReadAllText(ctx.assetPath) : string.Empty;

        var sheet = ScriptableObject.CreateInstance<CssStyleSheet>();
        sheet.name = Path.GetFileNameWithoutExtension(ctx.assetPath);
        sheet.SetSource(css);

        ctx.AddObjectToAsset("stylesheet", sheet);
        ctx.SetMainObject(sheet);
    }
}

/// <summary>
/// 编辑器下为 CSS 解释器提供纹理解析（AssetDatabase / Resources / 磁盘绝对路径）。
/// </summary>
[InitializeOnLoad]
public static class CssEditorBootstrap
{
    static CssEditorBootstrap()
    {
        CssInterpreter.textureResolver = ResolveTexture;
    }

    public static Texture ResolveTexture(string url)
    {
        if (string.IsNullOrEmpty(url)) return null;
        string path = url.Replace('\\', '/').Trim();

        Texture tex = TryLoadAtPath(path);
        if (tex != null) return tex;

        // 磁盘绝对路径 -> 工程相对路径
        if (Path.IsPathRooted(path))
        {
            string full = Path.GetFullPath(path).Replace('\\', '/');
            string root = Path.GetFullPath(Application.dataPath).Replace('\\', '/');
            if (full.StartsWith(root) && full.Length > root.Length)
            {
                tex = TryLoadAtPath("Assets" + full.Substring(root.Length));
                if (tex != null) return tex;
            }
        }

        tex = TryLoadAtPath("Assets/" + path.TrimStart('/'));
        if (tex != null) return tex;

        // Resources 相对路径
        tex = Resources.Load<Texture>(path);
        if (tex != null) return tex;
        tex = Resources.Load<Texture>(Path.GetFileNameWithoutExtension(path));
        if (tex != null) return tex;

        // 按文件名在整个工程里搜一次
        string[] guids = AssetDatabase.FindAssets(Path.GetFileNameWithoutExtension(path) + " t:Texture2D");
        if (guids.Length > 0) return AssetDatabase.LoadAssetAtPath<Texture>(AssetDatabase.GUIDToAssetPath(guids[0]));

        return null;
    }

    private static Texture TryLoadAtPath(string path)
    {
        if (string.IsNullOrEmpty(path)) return null;
        return AssetDatabase.LoadAssetAtPath<Texture>(path);
    }
}

public static class CssAssetMenu
{
    [MenuItem("Assets/WebUI/从选中的 CSS 文本创建 StyleSheet", true)]
    private static bool ValidateCreateFromTextAsset()
    {
        return Selection.activeObject is TextAsset;
    }

    [MenuItem("Assets/WebUI/从选中的 CSS 文本创建 StyleSheet")]
    private static void CreateFromTextAsset()
    {
        var text = Selection.activeObject as TextAsset;
        if (text == null) return;

        string dir = Path.GetDirectoryName(AssetDatabase.GetAssetPath(text))?.Replace('\\', '/');
        if (string.IsNullOrEmpty(dir)) dir = "Assets";

        var sheet = ScriptableObject.CreateInstance<CssStyleSheet>();
        sheet.SetSource(text.text);

        string assetPath = AssetDatabase.GenerateUniqueAssetPath(dir + "/" + text.name + "_StyleSheet.asset");
        AssetDatabase.CreateAsset(sheet, assetPath);
        AssetDatabase.SaveAssets();
        EditorGUIUtility.PingObject(sheet);
    }
}
