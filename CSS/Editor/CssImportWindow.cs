using System.Collections.Generic;
using System.IO;
using System.Text;
using UnityEditor;
using UnityEngine;


namespace WebUI
{
    /// <summary>
    /// CSS 一键导入窗口：粘贴文本 / 读取工程外的 .css 文件 / 选择已导入的 StyleSheet，
    /// 解析后按选择器批量应用到 Hierarchy 中选中的 WebUI。
    /// </summary>
    public class CssImportWindow : EditorWindow
    {
        private enum SourceMode
        {
            Paste = 0,
            File = 1,
            Asset = 2
        }

        private static readonly string[] ModeLabels = { "粘贴 CSS", "外部文件", "StyleSheet 资源" };

        private const string SampleCss =
            @"/* 支持 background / border / border-radius / box-shadow / opacity / width / height */
.card {
    width: 240px;
    height: 120px;
    border-radius: 12px;
    background: linear-gradient(135deg, #4F8CFF 0%, #9B5CFF 100%);
    border: 2px solid rgba(255, 255, 255, 0.35);
    box-shadow: 0 6px 16px 0 rgba(0, 0, 0, 0.35);
    opacity: 1;
}

.badge {
    border-radius: 50%;
    background-color: #FF5C5C;
    outline: 3px solid #FFFFFF;
}
";

        private SourceMode m_Mode = SourceMode.Paste;
        private string m_CssText = SampleCss;
        private string m_FilePath = string.Empty;
        private CssStyleSheet m_Asset;
        private CssStyleSheet m_Temp;

        private readonly HashSet<int> m_SelectedRules = new HashSet<int>();
        private bool m_ApplySize;
        private bool m_AddApplier = true;

        private Vector2 m_CssScroll;
        private Vector2 m_RuleScroll;
        private Vector2 m_LogScroll;
        private readonly List<string> m_Log = new List<string>();

        [MenuItem("Window/WebUI/CSS 解释器")]
        public static void Open()
        {
            var window = GetWindow<CssImportWindow>("CSS 解释器");
            window.minSize = new Vector2(420f, 520f);
            window.Show();
        }

        private CssStyleSheet ActiveSheet => m_Mode == SourceMode.Asset ? m_Asset : m_Temp;

        private void OnDestroy()
        {
            if (m_Temp != null) DestroyImmediate(m_Temp);
            m_Temp = null;
        }

        private void OnGUI()
        {
            DrawSource();
            EditorGUILayout.Space(4);
            DrawActions();
            EditorGUILayout.Space(4);
            DrawRules();
            EditorGUILayout.Space(4);
            DrawTargets();
            EditorGUILayout.Space(4);
            DrawLog();
        }

        // ------------------------------------------------------------------
        private void DrawSource()
        {
            SourceMode mode = (SourceMode)GUILayout.Toolbar((int)m_Mode, ModeLabels);
            if (mode != m_Mode)
            {
                m_Mode = mode;
                GUI.FocusControl(null);
            }

            EditorGUILayout.Space(2);

            switch (m_Mode)
            {
                case SourceMode.Paste:
                    m_CssScroll = EditorGUILayout.BeginScrollView(m_CssScroll, GUILayout.MinHeight(140f),
                        GUILayout.MaxHeight(260f));
                    m_CssText = EditorGUILayout.TextArea(m_CssText, GUILayout.ExpandHeight(true));
                    EditorGUILayout.EndScrollView();
                    using (new EditorGUILayout.HorizontalScope())
                    {
                        if (GUILayout.Button("从剪贴板读取")) m_CssText = EditorGUIUtility.systemCopyBuffer ?? string.Empty;
                        if (GUILayout.Button("载入示例")) m_CssText = SampleCss;
                        if (GUILayout.Button("清空")) m_CssText = string.Empty;
                    }

                    break;

                case SourceMode.File:
                    using (new EditorGUILayout.HorizontalScope())
                    {
                        m_FilePath = EditorGUILayout.TextField("CSS 文件", m_FilePath);
                        if (GUILayout.Button("浏览...", GUILayout.Width(64f)))
                        {
                            string start = string.IsNullOrEmpty(m_FilePath) || !File.Exists(m_FilePath)
                                ? Application.dataPath
                                : Path.GetDirectoryName(m_FilePath);
                            string picked = EditorUtility.OpenFilePanelWithFilters("选择外部 CSS 文件", start,
                                new[] { "CSS 或文本文件", "css,txt" });
                            if (!string.IsNullOrEmpty(picked))
                            {
                                m_FilePath = picked;
                                LoadFile();
                            }
                        }
                    }

                    EditorGUILayout.HelpBox("可以是工程目录之外的任意 .css 文件，读取后自动解析。", MessageType.Info);
                    using (new EditorGUILayout.HorizontalScope())
                    {
                        if (GUILayout.Button("读取并解析")) LoadFile();
                    }

                    m_CssScroll = EditorGUILayout.BeginScrollView(m_CssScroll, GUILayout.MinHeight(80f),
                        GUILayout.MaxHeight(180f));
                    m_CssText = EditorGUILayout.TextArea(m_CssText, GUILayout.ExpandHeight(true));
                    EditorGUILayout.EndScrollView();
                    break;

                case SourceMode.Asset:
                    m_Asset = (CssStyleSheet)EditorGUILayout.ObjectField("StyleSheet", m_Asset, typeof(CssStyleSheet),
                        false);
                    EditorGUILayout.HelpBox("把 .css 文件拖进工程会自动生成 StyleSheet 资源（见 CssStyleSheetImporter）。",
                        MessageType.Info);
                    break;
            }
        }

        private void DrawActions()
        {
            using (new EditorGUILayout.HorizontalScope())
            {
                if (GUILayout.Button("解析 CSS", GUILayout.Height(26f))) DoParse();

                bool canSave = m_Mode != SourceMode.Asset && !string.IsNullOrEmpty(m_CssText);
                GUI.enabled = canSave;
                if (GUILayout.Button("另存为 StyleSheet 资源...", GUILayout.Height(26f))) SaveAsAsset();
                GUI.enabled = true;
            }

            m_ApplySize = EditorGUILayout.ToggleLeft("同时把 width / height 写入 RectTransform", m_ApplySize);
            m_AddApplier = EditorGUILayout.ToggleLeft("在目标上创建 / 更新 CssStyleApplier 组件", m_AddApplier);
        }

        private void DrawRules()
        {
            CssStyleSheet sheet = ActiveSheet;
            EditorGUILayout.LabelField("解析结果", EditorStyles.boldLabel);

            if (sheet == null || sheet.rules.Count == 0)
            {
                EditorGUILayout.HelpBox("尚未解析出任何规则。", MessageType.None);
                return;
            }

            EditorGUILayout.LabelField("共 " + sheet.rules.Count + " 条规则（可多选，点击切换；按源码顺序层叠）");
            m_RuleScroll =
                EditorGUILayout.BeginScrollView(m_RuleScroll, GUILayout.MinHeight(70f), GUILayout.MaxHeight(150f));
            for (int i = 0; i < sheet.rules.Count; i++)
            {
                CssRule rule = sheet.rules[i];
                bool selected = m_SelectedRules.Contains(i);

                Color prev = GUI.backgroundColor;
                if (selected) GUI.backgroundColor = new Color(0.4f, 0.7f, 1f);
                string label = (selected ? "[x] " : "[  ] ") + rule.SelectorText + "   (" + rule.declarations.Count +
                               " 条声明)";
                if (GUILayout.Button(label, EditorStyles.miniButton))
                {
                    if (selected) m_SelectedRules.Remove(i);
                    else m_SelectedRules.Add(i);
                }

                GUI.backgroundColor = prev;
            }

            EditorGUILayout.EndScrollView();

            List<int> selectedIndices = SortedSelectedRules(sheet);
            if (selectedIndices.Count == 0)
            {
                EditorGUILayout.HelpBox("未选中任何规则。", MessageType.None);
            }
            else
            {
                EditorGUILayout.LabelField("已选中 " + selectedIndices.Count + " 条规则，将合并为一次层叠计算：", EditorStyles.boldLabel);
                EditorGUI.indentLevel++;
                foreach (int index in selectedIndices)
                {
                    CssRule selected = sheet.rules[index];
                    EditorGUILayout.LabelField(selected.SelectorText, EditorStyles.miniBoldLabel);
                    EditorGUI.indentLevel++;
                    foreach (CssDeclaration d in selected.declarations)
                        EditorGUILayout.LabelField(d.property, d.value + (d.important ? "  !important" : string.Empty));
                    EditorGUI.indentLevel--;
                }

                EditorGUI.indentLevel--;
            }
        }

        /// <summary>选中的规则索引，按源码顺序返回——与层叠覆盖顺序一致。</summary>
        private List<int> SortedSelectedRules(CssStyleSheet sheet)
        {
            var list = new List<int>();
            if (sheet == null) return list;
            for (int i = 0; i < sheet.rules.Count; i++)
                if (m_SelectedRules.Contains(i))
                    list.Add(i);
            return list;
        }

        /// <summary>重新解析后规则索引会变化，必须清掉旧选择。</summary>
        private void ResetRuleSelection(CssStyleSheet sheet)
        {
            m_SelectedRules.Clear();
            if (sheet != null && sheet.rules.Count > 0) m_SelectedRules.Add(0);
        }

        private void DrawTargets()
        {
            List<WebUI> targets = CollectTargets();
            EditorGUILayout.LabelField("应用目标", EditorStyles.boldLabel);

            if (targets.Count == 0)
                EditorGUILayout.HelpBox("请在 Hierarchy 中选中一个或多个包含 WebUI 的对象。", MessageType.Warning);
            else
            {
                EditorGUILayout.LabelField("已选中 " + targets.Count + " 个 WebUI：");
                EditorGUI.indentLevel++;
                foreach (WebUI t in targets) EditorGUILayout.LabelField("· " + GetPath(t.transform));
                EditorGUI.indentLevel--;
            }

            using (new EditorGUILayout.HorizontalScope())
            {
                GUI.enabled = targets.Count > 0 && ActiveSheet != null;
                if (GUILayout.Button("一键应用（按选择器自动匹配）", GUILayout.Height(28f))) ApplyAuto(targets);

                GUI.enabled = targets.Count > 0 && ActiveSheet != null && m_SelectedRules.Count > 0;
                if (GUILayout.Button("合并应用选中规则到全部目标（" + m_SelectedRules.Count + " 条）", GUILayout.Height(28f)))
                    ApplySelectedRule(targets);
                GUI.enabled = true;
            }
        }

        private void DrawLog()
        {
            EditorGUILayout.LabelField("日志", EditorStyles.boldLabel);
            m_LogScroll = EditorGUILayout.BeginScrollView(m_LogScroll, GUILayout.MinHeight(70f));
            EditorGUILayout.TextArea(string.Join("\n", m_Log.ToArray()), GUILayout.ExpandHeight(true));
            EditorGUILayout.EndScrollView();
            using (new EditorGUILayout.HorizontalScope())
            {
                if (GUILayout.Button("清空日志")) m_Log.Clear();
            }
        }

        // ------------------------------------------------------------------
        private string CurrentCss()
        {
            return m_Mode == SourceMode.Asset ? (m_Asset != null ? m_Asset.sourceCss : string.Empty) : m_CssText;
        }

        private void LoadFile()
        {
            if (string.IsNullOrEmpty(m_FilePath) || !File.Exists(m_FilePath))
            {
                Log("文件不存在：" + m_FilePath);
                return;
            }

            try
            {
                m_CssText = File.ReadAllText(m_FilePath);
            }
            catch (System.Exception e)
            {
                Log("读取失败：" + e.Message);
                return;
            }

            Log("已读取 " + m_FilePath + "（" + m_CssText.Length + " 字符）");
            DoParse();
        }

        private void DoParse()
        {
            m_Log.Clear();

            if (m_Mode == SourceMode.Asset)
            {
                if (m_Asset == null)
                {
                    Log("请先选择一个 StyleSheet 资源。");
                    return;
                }

                ResetRuleSelection(m_Asset);
                LogSheetSummary(m_Asset);
                return;
            }

            string css = CurrentCss();
            if (string.IsNullOrEmpty(css.Trim()))
            {
                Log("CSS 内容为空。");
                return;
            }

            if (m_Temp == null)
            {
                m_Temp = ScriptableObject.CreateInstance<CssStyleSheet>();
                m_Temp.hideFlags = HideFlags.HideAndDontSave;
            }

            m_Temp.SetSource(css);
            ResetRuleSelection(m_Temp);
            LogSheetSummary(m_Temp);
        }

        private void LogSheetSummary(CssStyleSheet sheet)
        {
            Log("解析出 " + sheet.rules.Count + " 条规则。");
            foreach (string w in sheet.parseWarnings) Log("  ! " + w);
        }

        private void SaveAsAsset()
        {
            string path = EditorUtility.SaveFilePanelInProject("保存 StyleSheet", "StyleSheet", "asset", "选择保存位置");
            if (string.IsNullOrEmpty(path)) return;

            var sheet = ScriptableObject.CreateInstance<CssStyleSheet>();
            sheet.SetSource(CurrentCss());
            AssetDatabase.CreateAsset(sheet, path);
            AssetDatabase.SaveAssets();

            m_Mode = SourceMode.Asset;
            m_Asset = sheet;
            ResetRuleSelection(sheet);
            Log("已创建资源：" + path);
        }

        // ------------------------------------------------------------------
        private static List<WebUI> CollectTargets()
        {
            var targets = new List<WebUI>();
            foreach (GameObject go in Selection.gameObjects)
            {
                if (go == null) continue;
                foreach (WebUI ui in go.GetComponentsInChildren<WebUI>(true))
                    if (!targets.Contains(ui))
                        targets.Add(ui);
            }

            return targets;
        }

        private void ApplyAuto(List<WebUI> targets)
        {
            CssStyleSheet sheet = ActiveSheet;
            if (sheet == null)
            {
                Log("请先解析 CSS。");
                return;
            }

            if (targets.Count == 0)
            {
                Log("没有选中任何 WebUI 对象。");
                return;
            }

            m_Log.Clear();
            int applied = 0;

            foreach (WebUI target in targets)
            {
                CssStyleApplier applier = EnsureApplier(target, sheet);
                string id = applier != null && !string.IsNullOrEmpty(applier.id) ? applier.id : target.gameObject.name;
                List<string> classes = CssStyleApplier.SplitClassList(applier != null ? applier.classes : string.Empty);

                List<CssDeclaration> declarations = sheet.Collect(id, classes, target.gameObject.name);
                if (declarations.Count == 0)
                {
                    Log("· " + target.gameObject.name + "：没有规则匹配（#" + id +
                        (classes.Count == 0 ? string.Empty : " ." + string.Join(".", classes.ToArray())) + "）");
                    continue;
                }

                ApplyDeclarations(target, declarations);
                applied++;
            }

            Log(string.Format("完成：{0}/{1} 个对象已应用样式。", applied, targets.Count));
        }

        private void ApplySelectedRule(List<WebUI> targets)
        {
            CssStyleSheet sheet = ActiveSheet;
            if (sheet == null)
            {
                Log("请先解析 CSS。");
                return;
            }

            if (targets.Count == 0)
            {
                Log("没有选中任何 WebUI 对象。");
                return;
            }

            List<int> selectedIndices = SortedSelectedRules(sheet);
            if (selectedIndices.Count == 0)
            {
                Log("请先在规则列表中选中至少一条规则。");
                return;
            }

            // 按源码顺序合并声明，交给同一次层叠计算，后选中的规则覆盖先选中的
            var merged = new List<CssDeclaration>();
            var names = new List<string>();
            foreach (int index in selectedIndices)
            {
                CssRule rule = sheet.rules[index];
                merged.AddRange(rule.declarations);
                names.Add(rule.SelectorText);
            }

            m_Log.Clear();
            Log("合并应用 " + names.Count + " 条规则 [" + string.Join(" + ", names.ToArray()) +
                "]（共 " + merged.Count + " 条声明）到 " + targets.Count + " 个对象：");
            foreach (WebUI target in targets)
            {
                EnsureApplier(target, sheet);
                ApplyDeclarations(target, merged);
            }
        }

        private void ApplyDeclarations(WebUI target, List<CssDeclaration> declarations)
        {
            Undo.RecordObject(target, "Apply CSS");
            Undo.RecordObject(target.rectTransform, "Apply CSS");

            CssComputedStyle style = CssInterpreter.Compute(declarations, target.rectTransform.rect.size);
            CssInterpreter.Apply(target, style, m_ApplySize);
            EditorUtility.SetDirty(target);

            var sb = new StringBuilder();
            sb.Append("· ").Append(target.gameObject.name).Append("：填充 ").Append(style.fills.Count)
                .Append("，描边 ").Append(style.strokes.Count).Append("，特效 ").Append(style.effects.Count)
                .Append("，圆角 ").Append(style.cornerRadius.ToString("0.##")).Append("px");
            Log(sb.ToString());

            foreach (string w in style.warnings) Log("    ! " + w);
            if (style.unsupportedProperties.Count > 0)
                Log("    - 未支持的属性：" + string.Join(", ", style.unsupportedProperties.ToArray()));
        }

        private CssStyleApplier EnsureApplier(WebUI target, CssStyleSheet sheet)
        {
            CssStyleApplier applier = target.GetComponent<CssStyleApplier>();
            if (!m_AddApplier) return applier;

            if (applier == null)
            {
                applier = Undo.AddComponent<CssStyleApplier>(target.gameObject);
                applier.hideFlags = HideFlags.None;
            }
            else Undo.RecordObject(applier, "Apply CSS");

            applier.applySize = m_ApplySize;
            // 临时解析结果不是持久化资源，写进去会变成空引用
            if (EditorUtility.IsPersistent(sheet)) applier.styleSheet = sheet;
            EditorUtility.SetDirty(applier);
            return applier;
        }

        private static string GetPath(Transform t)
        {
            string path = t.name;
            while (t.parent != null)
            {
                t = t.parent;
                path = t.name + "/" + path;
            }

            return path;
        }

        private void Log(string message)
        {
            m_Log.Add(message);
            if (m_Log.Count > 500) m_Log.RemoveRange(0, m_Log.Count - 500);
            Repaint();
        }
    }

    [CustomEditor(typeof(CssStyleSheet))]
    public class CssStyleSheetEditor : Editor
    {
        public override void OnInspectorGUI()
        {
            var sheet = (CssStyleSheet)target;

            EditorGUILayout.LabelField("规则：" + sheet.rules.Count + " 条");
            EditorGUI.indentLevel++;
            foreach (CssRule rule in sheet.rules)
                EditorGUILayout.LabelField(rule.SelectorText, rule.declarations.Count + " 条声明");
            EditorGUI.indentLevel--;

            if (sheet.parseWarnings.Count > 0)
            {
                EditorGUILayout.Space(4);
                EditorGUILayout.LabelField("解析警告", EditorStyles.boldLabel);
                EditorGUI.indentLevel++;
                foreach (string w in sheet.parseWarnings) EditorGUILayout.HelpBox(w, MessageType.Warning);
                EditorGUI.indentLevel--;
            }

            EditorGUILayout.Space(6);
            using (new EditorGUILayout.HorizontalScope())
            {
                if (GUILayout.Button("重新解析"))
                {
                    Undo.RecordObject(sheet, "Reparse CSS");
                    sheet.SetSource(sheet.sourceCss);
                    EditorUtility.SetDirty(sheet);
                }

                if (GUILayout.Button("在 CSS 解释器窗口中打开")) CssImportWindow.Open();
            }

            DrawDefaultInspector();
        }
    }
}
