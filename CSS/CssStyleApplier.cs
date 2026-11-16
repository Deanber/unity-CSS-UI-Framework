using System;
using System.Collections.Generic;
using UnityEngine;

namespace WebUI
{
    /// <summary>
    /// 把一份 CssStyleSheet 绑定到 WebUI 上，按 id / class 选择器自动匹配并应用样式。
    /// </summary>
    [ExecuteAlways]
    [DisallowMultipleComponent]
    [RequireComponent(typeof(WebUI))]
    [AddComponentMenu("WebUI/CSS Style Applier")]
    public class CssStyleApplier : MonoBehaviour
    {
        [Tooltip("样式表资源（可由 .css 文件自动导入生成）")] public CssStyleSheet styleSheet;

        [Tooltip("用于匹配 #id 选择器；留空则使用 GameObject 名字")]
        public string id;

        [Tooltip("空格分隔的 class 列表，用于匹配 .class 选择器")]
        public string classes;

        [Tooltip("把 CSS 的 width / height 写入 RectTransform.sizeDelta")]
        public bool applySize;

        [Tooltip("组件启用或脚本域重载时自动重新应用")] public bool applyOnEnable = true;

        [NonSerialized] public List<string> lastWarnings = new List<string>();
        [NonSerialized] public List<string> lastApplied = new List<string>();
        [NonSerialized] public int lastMatchedDeclarations;

        private WebUI m_Target;

        public WebUI Target
        {
            get
            {
                if (m_Target == null) m_Target = GetComponent<WebUI>();
                return m_Target;
            }
        }

        public string EffectiveId => string.IsNullOrEmpty(id) ? gameObject.name : id;

        public static List<string> SplitClassList(string raw)
        {
            var list = new List<string>();
            if (string.IsNullOrEmpty(raw)) return list;
            foreach (string token in raw.Split(new[] { ' ', '\t', ',', '\r', '\n' },
                         StringSplitOptions.RemoveEmptyEntries))
            {
                string c = token.Trim().TrimStart('.');
                if (c.Length > 0 && !list.Contains(c)) list.Add(c);
            }

            return list;
        }

        private void OnEnable()
        {
            if (applyOnEnable && styleSheet != null) Apply();
        }

        [ContextMenu("应用 CSS")]
        public void Apply()
        {
            lastWarnings = new List<string>();
            lastApplied = new List<string>();
            lastMatchedDeclarations = 0;

            WebUI target = Target;
            if (target == null || styleSheet == null) return;

            var declarations = styleSheet.Collect(EffectiveId, SplitClassList(classes), gameObject.name);
            lastMatchedDeclarations = declarations.Count;
            if (declarations.Count == 0)
            {
                lastWarnings.Add("没有任何规则匹配 #" + EffectiveId +
                                 (string.IsNullOrEmpty(classes) ? "" : " ." + classes.Replace(' ', '.')));
                return;
            }
#if UNITY_EDITOR
            UnityEditor.Undo.RecordObject(target, "Apply CSS");
            UnityEditor.Undo.RecordObject(target.rectTransform, "Apply CSS");
#endif

            var style = CssInterpreter.Compute(declarations, target.rectTransform.rect.size);
            CssInterpreter.Apply(target, style, applySize);

#if UNITY_EDITOR
            UnityEditor.EditorUtility.SetDirty(target);
            UnityEditor.EditorUtility.SetDirty(target.rectTransform);
#endif

            // 强制立即重建 Canvas，确保编辑器下视觉立即更新
            if (target.canvas != null)
                Canvas.ForceUpdateCanvases();

            lastWarnings = style.warnings;
            lastApplied = style.appliedProperties;
        }
    }
}