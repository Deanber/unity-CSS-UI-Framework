using System;
using System.Collections.Generic;
using UnityEngine;

namespace WebUI
{
    /// <summary>
    /// 一份声明集合计算后的结果，字段与 WebUI 的渲染堆栈一一对应。
    /// </summary>
    public class CssComputedStyle
    {
        public List<FillData> fills = new List<FillData>();
        public List<StrokeData> strokes = new List<StrokeData>();
        public List<EffectData> effects = new List<EffectData>();

        public float cornerRadius;
        public float opacity = 1f;
        public bool hasOpacity;
        public CssLength? width;
        public CssLength? height;
        public Texture texture;

        public List<string> warnings = new List<string>();
        public List<string> appliedProperties = new List<string>();
        public List<string> unsupportedProperties = new List<string>();

        public bool IsEmpty => fills.Count == 0 && strokes.Count == 0 && effects.Count == 0 &&
                               cornerRadius == 0f && !hasOpacity && !width.HasValue && !height.HasValue;
    }

    /// <summary>
    /// 把 CSS 声明翻译成 WebUI 的 SDF 渲染堆栈。
    ///
    /// 坐标与角度约定：
    ///   - Shader 的 UV 以左下角为原点、Y 轴朝上；CSS 屏幕坐标 Y 轴朝下。
    ///   - Shader 的渐变角用数学约定 dir = (cos θ, sin θ)；CSS 用 0deg 朝上、顺时针。
    ///     两者关系为 θ = 90° - cssAngle。
    ///   - Shader 的填充/描边/特效数组下标 0 在最底层，而 CSS 中先声明的层在最上层，
    ///     因此多重背景与多重阴影都需要反转顺序。
    /// </summary>
    public static class CssInterpreter
    {
        public const int MaxFills = 4;
        public const int MaxStrokes = 4;
        public const int MaxEffects = 4;

        /// <summary>无 CSS 字体上下文时 em / rem 的换算基准。</summary>
        public const float DefaultFontSize = 16f;

        /// <summary>url() 纹理解析钩子。编辑器下由 AssetDatabase 实现，运行时回落到 Resources。</summary>
        public static Func<string, Texture> textureResolver;

        private static readonly HashSet<string> KnownProperties = new HashSet<string>
        {
            "background", "background-color", "background-image",
            "border", "border-width", "border-color", "border-style", "border-radius",
            "border-image", "border-image-source", "border-image-slice",
            "border-image-width", "border-image-outset", "border-image-repeat",
            "outline", "outline-width", "outline-color", "outline-style",
            "box-shadow", "opacity", "width", "height",
        };

        // ------------------------------------------------------------------
        // 主入口
        // ------------------------------------------------------------------
        public static CssComputedStyle Compute(IEnumerable<CssDeclaration> declarations, Vector2 rectSize)
        {
            var style = new CssComputedStyle();
            var map = Flatten(declarations);

            ComputeBackground(map, rectSize, style);
            ComputeBorder(map, rectSize, style);
            ComputeBoxShadow(map, rectSize, style);
            ComputeBox(map, rectSize, style);

            ReportUnknown(map, style);
            ClampStacks(style);
            return style;
        }

        /// <summary>
        /// 后声明覆盖先声明，!important 覆盖普通声明。
        /// </summary>
        private static Dictionary<string, CssDeclaration> Flatten(IEnumerable<CssDeclaration> declarations)
        {
            var map = new Dictionary<string, CssDeclaration>(StringComparer.Ordinal);
            if (declarations == null) return map;

            foreach (var d in declarations)
            {
                if (d == null || string.IsNullOrEmpty(d.property)) continue;
                if (map.TryGetValue(d.property, out CssDeclaration existing) && existing.important && !d.important)
                    continue;
                map[d.property] = d;
            }

            return map;
        }

        // ------------------------------------------------------------------
        // background / background-color / background-image
        // ------------------------------------------------------------------
        private static void ComputeBackground(Dictionary<string, CssDeclaration> map, Vector2 rectSize,
            CssComputedStyle style)
        {
            List<string> imageLayers = null;
            Color? backgroundColor = null;

            if (map.TryGetValue("background", out CssDeclaration bg))
            {
                ParseBackgroundShorthand(bg.value, out imageLayers, out backgroundColor, style);
                style.appliedProperties.Add("background");
            }

            if (map.TryGetValue("background-image", out CssDeclaration bi))
            {
                imageLayers = CssValue.SplitTopLevel(bi.value, ',');
                style.appliedProperties.Add("background-image");
            }

            if (map.TryGetValue("background-color", out CssDeclaration bc))
            {
                if (CssValue.TryParseColor(bc.value, out Color c)) backgroundColor = c;
                else style.warnings.Add("无法解析 background-color：" + bc.value);
                style.appliedProperties.Add("background-color");
            }

            if (backgroundColor.HasValue)
                style.fills.Add(new FillData
                    { type = FillType.Solid, colorA = backgroundColor.Value, colorB = backgroundColor.Value });

            if (imageLayers == null) return;

            // CSS 中先声明的背景层绘制在最上面，WebUI 的 fills[0] 在最下面
            for (int i = imageLayers.Count - 1; i >= 0; i--)
            {
                string layer = imageLayers[i].Trim();
                if (layer.Length == 0 || layer == "none") continue;

                if (TryBuildPaint(layer, rectSize, style, out FillData paint)) style.fills.Add(paint);
                else style.warnings.Add("无法识别的背景层，已跳过：" + Truncate(layer));
            }
        }

        private static void ParseBackgroundShorthand(string value, out List<string> imageLayers, out Color? color,
            CssComputedStyle style)
        {
            imageLayers = new List<string>();
            color = null;

            var layers = CssValue.SplitTopLevel(value, ',');
            for (int i = 0; i < layers.Count; i++)
            {
                bool isFinalLayer = i == layers.Count - 1;

                foreach (string component in CssValue.SplitTopLevel(layers[i], ' '))
                {
                    if (component.Length == 0) continue;

                    if (CssValue.TryParseGradient(component, out _) || ExtractUrl(component) != null)
                    {
                        imageLayers.Add(component);
                        continue;
                    }

                    // 规范中只有最后一层可以携带背景色
                    if (isFinalLayer && !color.HasValue && CssValue.TryParseColor(component, out Color c))
                    {
                        color = c;
                        continue;
                    }

                    WarnIgnoredBackgroundComponent(component, style);
                }
            }
        }

        private static void WarnIgnoredBackgroundComponent(string component, CssComputedStyle style)
        {
            string t = component.TrimEnd(',', ';').ToLowerInvariant();
            switch (t)
            {
                case "none":
                case "repeat":
                case "no-repeat":
                case "repeat-x":
                case "repeat-y":
                case "cover":
                case "contain":
                case "scroll":
                case "fixed":
                case "local":
                case "border-box":
                case "padding-box":
                case "content-box":
                case "center":
                case "top":
                case "bottom":
                case "left":
                case "right":
                    style.warnings.Add("background 简写中的 '" + t + "' 不被渲染管线支持，已忽略。");
                    break;
            }
        }

        private static FillData BuildGradientFill(CssGradient g, Vector2 rectSize, CssComputedStyle style)
        {
            if (g.stopCount > 2)
                style.warnings.Add("渐变包含 " + g.stopCount + " 个色标，渲染管线只支持首尾两个，已取首尾色。");

            var fill = new FillData { colorA = g.colorA, colorB = g.colorB };

            switch (g.kind)
            {
                case CssGradientKind.Linear:
                    fill.type = FillType.LinearGradient;
                    fill.paramsX = new Vector4(90f - g.angleDeg, 0f, 0f, 0f);
                    break;

                case CssGradientKind.Conic:
                    fill.type = FillType.Rotate;
                    fill.paramsX = new Vector4(90f - g.angleDeg, 0f, 0f, 0f);
                    style.warnings.Add("conic-gradient 的旋转方向与 CSS 相反（Shader 以逆时针采样），仅起始角对齐。");
                    break;

                case CssGradientKind.Radial:
                    fill.type = FillType.RadialGradient;
                    float outer = ResolveRadialUvRadius(g, rectSize, style);
                    fill.paramsX = new Vector4(0f, Mathf.Max(outer, 0.0001f), 0f, 0f);
                    style.warnings.Add("radial-gradient 的圆心固定为矩形中心，'at <position>' 不被支持。");
                    break;
            }

            return fill;
        }

        /// <summary>
        /// Shader 的径向渐变是 uv 空间的正圆，这里把 CSS 的椭圆半径换算成一个等效 uv 半径。
        /// 取两轴 uv 比例的几何均值，可以在近似形状的同时保住面积。
        /// </summary>
        private static float ResolveRadialUvRadius(CssGradient g, Vector2 rectSize, CssComputedStyle style)
        {
            if (!g.hasRadiusX)
                return g.closestSide ? 0.5f : 0.70710678f; // closest-side / farthest-corner

            float w = Mathf.Max(rectSize.x, 0.0001f);
            float h = Mathf.Max(rectSize.y, 0.0001f);

            // 只给一个半径时按 CSS 语义是圆，两轴像素等长
            float rx = Mathf.Max(0f, g.radiusX.Resolve(w, DefaultFontSize));
            float ry = g.hasRadiusY ? Mathf.Max(0f, g.radiusY.Resolve(h, DefaultFontSize)) : rx;

            float ux = rx / w;
            float uy = ry / h;

            if (Mathf.Abs(ux - uy) > 0.001f)
                style.warnings.Add("radial-gradient 在 uv 空间是椭圆（" + ux.ToString("0.###") + " x " + uy.ToString("0.###") +
                                   "），Shader 只支持正圆，已取几何均值。");

            return Mathf.Sqrt(ux * uy);
        }

        /// <summary>
        /// 把一个 CSS 图层值解析成可直接使用的着色载荷（渐变或贴图）。
        /// background、border-image、outline 共用这一套，描边因此获得与填充相同的渐变能力。
        /// </summary>
        private static bool TryBuildPaint(string value, Vector2 rectSize, CssComputedStyle style, out FillData paint)
        {
            paint = default;
            if (string.IsNullOrEmpty(value)) return false;

            string v = value.Trim();
            if (v.Length == 0 || v == "none") return false;

            if (CssValue.TryParseGradient(v, out CssGradient gradient))
            {
                paint = BuildGradientFill(gradient, rectSize, style);
                return true;
            }

            string url = ExtractUrl(v);
            if (url == null) return false;

            Texture tex = ResolveTexture(url, style);
            if (tex == null) return false;

            if (style.texture != null && style.texture != tex)
                style.warnings.Add("渲染管线只有一张 _MainTex，多处 url() 贴图会互相覆盖，当前使用：" + tex.name);

            style.texture = tex;
            paint = new FillData { type = FillType.Texture, colorA = Color.white, colorB = Color.white };
            return true;
        }

        // ------------------------------------------------------------------
        // border / outline
        // ------------------------------------------------------------------
        private static void ComputeBorder(Dictionary<string, CssDeclaration> map, Vector2 rectSize,
            CssComputedStyle style)
        {
            float minSide = Mathf.Min(rectSize.x, rectSize.y);

            CssLength width = new CssLength(0f, CssUnit.Px);
            bool hasWidth = false;
            Color color = Color.black;
            bool hasColor = false;
            bool disabled = false;
            FillData paint = default;
            bool hasPaint = false;

            if (map.TryGetValue("border", out CssDeclaration border))
            {
                ParseBorderShorthand(border.value, rectSize, ref width, ref hasWidth, ref color, ref hasColor,
                    ref disabled, ref paint, ref hasPaint, style);
                style.appliedProperties.Add("border");
            }

            if (map.TryGetValue("border-width", out CssDeclaration bw))
            {
                if (TryParseFirstLength(bw.value, rectSize, out width)) hasWidth = true;
                style.appliedProperties.Add("border-width");
            }

            if (map.TryGetValue("border-color", out CssDeclaration bc))
            {
                // 便捷扩展：border-color 允许直接写渐变
                if (TryBuildPaint(bc.value, rectSize, style, out paint)) hasPaint = true;
                else if (CssValue.TryParseColor(FirstColorToken(bc.value), out color)) hasColor = true;
                else style.warnings.Add("无法解析 border-color：" + Truncate(bc.value));
                style.appliedProperties.Add("border-color");
            }

            if (map.TryGetValue("border-style", out CssDeclaration bs))
            {
                CheckBorderStyle(bs.value, style);
                style.appliedProperties.Add("border-style");
            }

            // ---------- border-image：描边着色的标准来源 ----------
            BorderImageData image = ReadBorderImage(map, style);

            if (image != null && image.hasSource)
            {
                if (TryBuildPaint(image.source, rectSize, style, out FillData imagePaint))
                {
                    paint = imagePaint;
                    hasPaint = true;
                }

                style.warnings.Add("border-image 的九宫格切片不被支持，渐变已直接铺满整个描边区域。");
            }

            // border-image-width 会覆盖 border-width；<number> 形式是 border-width 的倍数
            if (image != null && !hasWidth && image.hasSource)
            {
                width = new CssLength(3f, CssUnit.Px); // CSS 初始值 medium
                hasWidth = true;
                style.warnings.Add("未指定 border-width，border-image 已按 CSS 初始值 medium（3px）绘制。");
            }

            if (image != null && image.hasWidthMultiplier)
            {
                width = new CssLength(image.widthMultiplier * width.Resolve(minSide, DefaultFontSize), CssUnit.Px);
                hasWidth = true;
            }
            else if (image != null && image.hasWidth)
            {
                width = image.width;
                hasWidth = true;
            }

            if (!disabled && hasWidth)
            {
                float w = width.Resolve(minSide, DefaultFontSize);
                if (w > 0f)
                {
                    // CSS 边框绘制在 border-box 内侧
                    style.strokes.Add(MakeStroke(StrokePosition.Inner, w, hasPaint, paint,
                        hasColor ? color : Color.black));
                }
            }

            // outline -> 外描边
            CssLength outlineWidth = new CssLength(0f, CssUnit.Px);
            bool hasOutlineWidth = false;
            Color outlineColor = Color.black;
            bool hasOutlineColor = false;
            bool outlineDisabled = false;
            FillData outlinePaint = default;
            bool hasOutlinePaint = false;

            if (map.TryGetValue("outline", out CssDeclaration outline))
            {
                ParseBorderShorthand(outline.value, rectSize, ref outlineWidth, ref hasOutlineWidth, ref outlineColor,
                    ref hasOutlineColor, ref outlineDisabled, ref outlinePaint, ref hasOutlinePaint, style);
                style.appliedProperties.Add("outline");
            }

            if (map.TryGetValue("outline-width", out CssDeclaration ow))
            {
                if (TryParseFirstLength(ow.value, rectSize, out outlineWidth)) hasOutlineWidth = true;
                style.appliedProperties.Add("outline-width");
            }

            if (map.TryGetValue("outline-color", out CssDeclaration oc))
            {
                if (TryBuildPaint(oc.value, rectSize, style, out outlinePaint)) hasOutlinePaint = true;
                else if (CssValue.TryParseColor(FirstColorToken(oc.value), out outlineColor)) hasOutlineColor = true;
                else style.warnings.Add("无法解析 outline-color：" + Truncate(oc.value));
                style.appliedProperties.Add("outline-color");
            }

            if (map.TryGetValue("outline-style", out CssDeclaration os))
            {
                CheckBorderStyle(os.value, style);
                style.appliedProperties.Add("outline-style");
            }

            if (!outlineDisabled && hasOutlineWidth)
            {
                float w = outlineWidth.Resolve(0f, DefaultFontSize);
                if (w > 0f)
                    style.strokes.Add(MakeStroke(StrokePosition.Outer, w, hasOutlinePaint, outlinePaint, outlineColor));
            }

            WarnOnPerSideBorder(map, style);
        }

        /// <summary>把着色载荷装进 StrokeData，type / colorB / paramsX 的语义与 FillData 完全一致。</summary>
        private static StrokeData MakeStroke(StrokePosition position, float width, bool hasPaint, FillData paint,
            Color solid)
        {
            var s = new StrokeData { position = position, width = width };
            if (hasPaint)
            {
                s.type = paint.type;
                s.color = paint.colorA;
                s.colorB = paint.colorB;
                s.paramsX = paint.paramsX;
            }
            else
            {
                s.type = FillType.Solid;
                s.color = solid;
                s.colorB = solid;
            }

            return s;
        }

        /// <summary>border-image 中真正能被渲染管线使用的部分。</summary>
        private class BorderImageData
        {
            public string source;
            public bool hasSource;
            public CssLength width;
            public bool hasWidth;

            /// <summary>border-image-width 写成裸数字时是 border-width 的倍数。</summary>
            public float widthMultiplier;

            public bool hasWidthMultiplier;
        }

        private static BorderImageData ReadBorderImage(Dictionary<string, CssDeclaration> map, CssComputedStyle style)
        {
            BorderImageData image = null;

            if (map.TryGetValue("border-image", out CssDeclaration shorthand))
            {
                image = ParseBorderImage(shorthand.value, style);
                style.appliedProperties.Add("border-image");
            }

            if (map.TryGetValue("border-image-source", out CssDeclaration src))
            {
                image = image ?? new BorderImageData();
                string v = src.value.Trim();
                if (v.Length > 0 && v != "none")
                {
                    image.source = v;
                    image.hasSource = true;
                }
                else
                {
                    image.source = null;
                    image.hasSource = false;
                }

                style.appliedProperties.Add("border-image-source");
            }

            if (map.TryGetValue("border-image-width", out CssDeclaration w))
            {
                image = image ?? new BorderImageData();
                ApplyBorderImageWidth(w.value, image, style);
                style.appliedProperties.Add("border-image-width");
            }

            foreach (string prop in new[] { "border-image-slice", "border-image-outset", "border-image-repeat" })
            {
                if (!map.ContainsKey(prop)) continue;
                style.warnings.Add("'" + prop + "' 不被支持：SDF 描边不做九宫格切片、外扩与平铺。");
                style.appliedProperties.Add(prop);
            }

            return image;
        }

        /// <summary>
        /// 解析 border-image 简写：&lt;source&gt; &lt;slice&gt; [ / &lt;width&gt; [ / &lt;outset&gt; ] ] &lt;repeat&gt;。
        /// 注意 '18' 这种紧跟在 source 后的数字是 slice，不是宽度。
        /// </summary>
        private static BorderImageData ParseBorderImage(string value, CssComputedStyle style)
        {
            var image = new BorderImageData();
            if (string.IsNullOrEmpty(value)) return image;

            // '/' 是 slice / width / outset 三段的分隔符，括号内的 '/'（如 rgb(0 0 0 / 50%)）不参与切分
            List<string> sections = CssValue.SplitTopLevel(value, '/');
            var ignored = new List<string>();

            for (int si = 0; si < sections.Count && si < 3; si++)
            {
                foreach (string raw in CssValue.SplitTopLevel(sections[si], ' '))
                {
                    string token = raw.Trim();
                    if (token.Length == 0) continue;
                    string lower = token.ToLowerInvariant();

                    if (!image.hasSource && (CssValue.TryParseGradient(token, out _) || ExtractUrl(token) != null))
                    {
                        image.source = token;
                        image.hasSource = true;
                        continue;
                    }

                    if (si == 1)
                    {
                        if (lower == "auto") continue;
                        if (CssValue.TryParseLength(lower, out CssLength len) && !CssValue.IsAngleUnit(len.unit))
                        {
                            // 结尾是数字说明是 <number>（倍数），带单位才是绝对宽度
                            if (char.IsDigit(lower[lower.Length - 1]) || lower[lower.Length - 1] == '.')
                            {
                                image.widthMultiplier = len.value;
                                image.hasWidthMultiplier = true;
                            }
                            else
                            {
                                image.width = len;
                                image.hasWidth = true;
                            }

                            continue;
                        }
                    }

                    ignored.Add(lower);
                }
            }

            if (ignored.Count > 0)
                style.warnings.Add("border-image 的 slice / outset / repeat（" + string.Join(", ", ignored.ToArray()) +
                                   "）不被支持，已忽略。");

            return image;
        }

        private static void ApplyBorderImageWidth(string value, BorderImageData image, CssComputedStyle style)
        {
            foreach (string raw in CssValue.SplitTopLevel(value, ' '))
            {
                string lower = raw.Trim().ToLowerInvariant();
                if (lower.Length == 0 || lower == "auto") continue;
                if (!CssValue.TryParseLength(lower, out CssLength len) || CssValue.IsAngleUnit(len.unit)) continue;

                if (char.IsDigit(lower[lower.Length - 1]) || lower[lower.Length - 1] == '.')
                {
                    image.widthMultiplier = len.value;
                    image.hasWidthMultiplier = true;
                }
                else
                {
                    image.width = len;
                    image.hasWidth = true;
                }

                return;
            }

            style.warnings.Add("无法解析 border-image-width：" + Truncate(value));
        }

        private static void ParseBorderShorthand(string value, Vector2 rectSize,
            ref CssLength width, ref bool hasWidth,
            ref Color color, ref bool hasColor, ref bool disabled,
            ref FillData paint, ref bool hasPaint,
            CssComputedStyle style)
        {
            foreach (string token in CssValue.SplitTopLevel(value, ' '))
            {
                string t = token.Trim();
                if (t.Length == 0) continue;
                string lower = t.ToLowerInvariant();

                if (lower == "none" || lower == "hidden")
                {
                    disabled = true;
                    continue;
                }

                if (lower == "solid") continue;
                if (lower == "dashed" || lower == "dotted" || lower == "double" || lower == "groove" ||
                    lower == "ridge" || lower == "inset" || lower == "outset")
                {
                    style.warnings.Add("边框样式 '" + lower + "' 无法由 SDF 管线表达，已按 solid 渲染。");
                    continue;
                }

                if (CssValue.TryParseLength(lower, out CssLength len) && !CssValue.IsAngleUnit(len.unit))
                {
                    width = len;
                    hasWidth = true;
                    continue;
                }

                // 便捷扩展：简写里直接写渐变
                if (TryBuildPaint(t, rectSize, style, out paint))
                {
                    hasPaint = true;
                    continue;
                }

                if (CssValue.TryParseColor(t, out Color c))
                {
                    color = c;
                    hasColor = true;
                }
            }
        }

        private static void CheckBorderStyle(string value, CssComputedStyle style)
        {
            string t = value.Trim().ToLowerInvariant();
            if (t == "none" || t == "hidden") return;
            if (t != "solid")
                style.warnings.Add("边框样式 '" + t + "' 无法由 SDF 管线表达，已按 solid 渲染。");
        }

        private static void WarnOnPerSideBorder(Dictionary<string, CssDeclaration> map, CssComputedStyle style)
        {
            string[] sides = { "top", "right", "bottom", "left" };
            foreach (string side in sides)
            {
                foreach (string prop in new[]
                             { "border-" + side, "border-" + side + "-width", "border-" + side + "-color" })
                {
                    if (map.ContainsKey(prop))
                    {
                        style.warnings.Add("'" + prop + "' 不被支持：SDF 描边只能四边等宽。");
                        style.unsupportedProperties.Add(prop);
                    }
                }
            }
        }

        private static bool TryParseFirstLength(string value, Vector2 rectSize, out CssLength length)
        {
            length = CssLength.Zero;
            foreach (string token in CssValue.SplitTopLevel(value, ' '))
            {
                if (CssValue.TryParseLength(token, out length) && !CssValue.IsAngleUnit(length.unit))
                    return true;
            }

            return false;
        }

        private static string FirstColorToken(string value)
        {
            var tokens = CssValue.SplitTopLevel(value, ' ');
            return tokens.Count > 0 ? tokens[0] : value;
        }

        // ------------------------------------------------------------------
        // box-shadow
        // ------------------------------------------------------------------
        private static void ComputeBoxShadow(Dictionary<string, CssDeclaration> map, Vector2 rectSize,
            CssComputedStyle style)
        {
            if (!map.TryGetValue("box-shadow", out CssDeclaration decl)) return;
            style.appliedProperties.Add("box-shadow");

            List<CssShadow> shadows = CssValue.ParseShadows(decl.value);
            if (shadows.Count == 0) return;

            bool warnedInset = false;
            // CSS 中先声明的阴影在最上层，WebUI 的 effects[0] 在最下面
            for (int i = shadows.Count - 1; i >= 0; i--)
            {
                CssShadow s = shadows[i];
                float ox = s.offsetX.Resolve(rectSize.x, DefaultFontSize);
                float oy = s.offsetY.Resolve(rectSize.y, DefaultFontSize);
                float blur = Mathf.Max(0f, s.blur.Resolve(0f, DefaultFontSize));
                float spread = s.spread.Resolve(Mathf.Min(rectSize.x, rectSize.y), DefaultFontSize);

                style.effects.Add(new EffectData
                {
                    type = s.inset ? EffectType.InnerShadow : EffectType.DropShadow,
                    color = s.color,
                    // CSS 的 Y 轴朝下，Shader 的 localPos Y 轴朝上
                    offset = new Vector2(ox, -oy),
                    blurRadius = blur,
                    spread = spread,
                });

                if (s.inset && !warnedInset)
                {
                    style.warnings.Add("inset 阴影已映射为 InnerShadow，但当前 Shader 尚未实现内阴影渲染。");
                    warnedInset = true;
                }
            }
        }

        // ------------------------------------------------------------------
        // border-radius / opacity / width / height
        // ------------------------------------------------------------------
        private static void ComputeBox(Dictionary<string, CssDeclaration> map, Vector2 rectSize, CssComputedStyle style)
        {
            if (map.TryGetValue("border-radius", out CssDeclaration br))
            {
                style.appliedProperties.Add("border-radius");
                string value = br.value;
                int slash = value.IndexOf('/');
                if (slash >= 0)
                {
                    style.warnings.Add("border-radius 的椭圆半径（'/' 之后的部分）不被支持，已只取水平半径。");
                    value = value.Substring(0, slash);
                }

                var tokens = CssValue.SplitTopLevel(value, ' ');
                float minSide = Mathf.Min(rectSize.x, rectSize.y);
                float first = -1f;
                for (int i = 0; i < tokens.Count && i < 4; i++)
                {
                    if (!CssValue.TryParseLength(tokens[i], out CssLength len)) continue;
                    float px = len.Resolve(minSide, DefaultFontSize);
                    if (first < 0f) first = px;
                    else if (!Mathf.Approximately(px, first))
                    {
                        style.warnings.Add("border-radius 四角不一致，SDF 只支持统一圆角，已取第一个值 " + first + "px。");
                        break;
                    }
                }

                if (first >= 0f) style.cornerRadius = Mathf.Max(0f, first);
            }

            if (map.TryGetValue("opacity", out CssDeclaration op))
            {
                if (float.TryParse(op.value.Trim(), System.Globalization.NumberStyles.Float,
                        System.Globalization.CultureInfo.InvariantCulture, out float v))
                {
                    style.opacity = Mathf.Clamp01(v);
                    style.hasOpacity = true;
                    style.appliedProperties.Add("opacity");
                }
                else style.warnings.Add("无法解析 opacity：" + op.value);
            }

            if (map.TryGetValue("width", out CssDeclaration wd) &&
                CssValue.TryParseLength(wd.value.Trim(), out CssLength wlen))
            {
                style.width = wlen;
                style.appliedProperties.Add("width");
            }

            if (map.TryGetValue("height", out CssDeclaration ht) &&
                CssValue.TryParseLength(ht.value.Trim(), out CssLength hlen))
            {
                style.height = hlen;
                style.appliedProperties.Add("height");
            }
        }

        private static void ReportUnknown(Dictionary<string, CssDeclaration> map, CssComputedStyle style)
        {
            foreach (var kv in map)
            {
                if (KnownProperties.Contains(kv.Key)) continue;
                if (kv.Key.StartsWith("border-top") || kv.Key.StartsWith("border-right") ||
                    kv.Key.StartsWith("border-bottom") ||
                    kv.Key.StartsWith("border-left")) continue; // 已在 WarnOnPerSideBorder 报告
                style.unsupportedProperties.Add(kv.Key);
            }
        }

        private static void ClampStacks(CssComputedStyle style)
        {
            if (style.fills.Count > MaxFills)
            {
                style.warnings.Add("填充层超过 " + MaxFills + " 个上限，多余的已被丢弃。");
                style.fills.RemoveRange(MaxFills, style.fills.Count - MaxFills);
            }

            if (style.strokes.Count > MaxStrokes)
            {
                style.warnings.Add("描边层超过 " + MaxStrokes + " 个上限，多余的已被丢弃。");
                style.strokes.RemoveRange(MaxStrokes, style.strokes.Count - MaxStrokes);
            }

            if (style.effects.Count > MaxEffects)
            {
                style.warnings.Add("特效层超过 " + MaxEffects + " 个上限，多余的已被丢弃。");
                style.effects.RemoveRange(MaxEffects, style.effects.Count - MaxEffects);
            }
        }

        // ------------------------------------------------------------------
        // url()
        // ------------------------------------------------------------------
        private static string ExtractUrl(string value)
        {
            int idx = value.IndexOf("url(", StringComparison.OrdinalIgnoreCase);
            if (idx < 0) return null;
            int open = idx + 4;
            int close = value.IndexOf(')', open);
            if (close < 0) return null;
            return CssValue.StripQuotes(value.Substring(open, close - open).Trim());
        }

        private static Texture ResolveTexture(string url, CssComputedStyle style)
        {
            if (string.IsNullOrEmpty(url)) return null;
            if (url.StartsWith("http://", StringComparison.OrdinalIgnoreCase) ||
                url.StartsWith("https://", StringComparison.OrdinalIgnoreCase) ||
                url.StartsWith("data:", StringComparison.OrdinalIgnoreCase))
            {
                style.warnings.Add("不支持远程或 data URI 纹理：" + Truncate(url));
                return null;
            }

            Texture tex = textureResolver != null ? textureResolver(url) : null;
            if (tex == null) style.warnings.Add("未能加载纹理：" + url);
            return tex;
        }

        // ------------------------------------------------------------------
        // 写回 WebUI
        // ------------------------------------------------------------------
        public static void Apply(WebUI target, CssComputedStyle style, bool applySize)
        {
            if (target == null || style == null) return;

            target.fills.Clear();
            target.fills.AddRange(style.fills);
            target.strokes.Clear();
            target.strokes.AddRange(style.strokes);
            target.effects.Clear();
            target.effects.AddRange(style.effects);

            target.cornerRadius = style.cornerRadius;

            if (style.texture != null) target.texture = style.texture;

            if (style.hasOpacity)
            {
                Color c = target.color;
                c.a = style.opacity;
                target.color = c;
            }

            if (applySize) ApplySize(target, style);

            target.UpdateMaterialProperties();
        }

        private static void ApplySize(WebUI target, CssComputedStyle style)
        {
            if (!style.width.HasValue && !style.height.HasValue) return;

            RectTransform rt = target.rectTransform;
            if (rt.anchorMin != rt.anchorMax)
            {
                style.warnings.Add("目标 RectTransform 使用了拉伸锚点，已跳过 width / height 应用。");
                return;
            }

            RectTransform parent = rt.parent as RectTransform;
            Vector2 parentSize = parent != null ? parent.rect.size : new Vector2(Screen.width, Screen.height);
            Vector2 size = rt.sizeDelta;

            if (style.width.HasValue)
                size.x = style.width.Value.Resolve(parentSize.x, DefaultFontSize);
            if (style.height.HasValue)
                size.y = style.height.Value.Resolve(parentSize.y, DefaultFontSize);

            rt.sizeDelta = size;
        }

        private static string Truncate(string s)
        {
            s = s.Trim();
            return s.Length <= 48 ? s : s.Substring(0, 48) + "...";
        }
    }
}
