using Town.Core.Dragonsleep;
using UnityEngine;
using UnityEngine.UIElements;

namespace Town.Unity.Dragonsleep
{
    public sealed partial class LoomLensHud
    {
        static string Trim(string s, int n) => s.Length <= n ? s : s.Substring(0, n) + " …";
        static VisualElement Row() => new VisualElement { style = { flexDirection = FlexDirection.Row } };
        static Label Wrap(Label l) { l.style.whiteSpace = WhiteSpace.Normal; return l; }
        static Label Tiny(string text, Color c) { var l = L(text, 12, c); l.style.marginTop = 6; return l; }

        static VisualElement Diamond(float size, Color border)
        {
            var d = new VisualElement { style = { width = size, height = size, marginRight = 6, rotate = new Rotate(45) } };
            d.style.borderTopWidth = 1; d.style.borderBottomWidth = 1; d.style.borderLeftWidth = 1; d.style.borderRightWidth = 1;
            d.style.borderTopColor = border; d.style.borderBottomColor = border; d.style.borderLeftColor = border; d.style.borderRightColor = border;
            return d;
        }

        static Label L(string text, int size, Color color, bool bold = false)
        {
            var l = new Label(text)
            {
                style = { fontSize = size, color = color, marginLeft = 0, marginRight = 0, paddingLeft = 0, paddingRight = 0 }
            };
            if (bold) l.style.unityFontStyleAndWeight = FontStyle.Bold;
            return l;
        }

        /// <summary>按贴图原比例显示（标题牌、头像框这类不能拉伸的装饰）。</summary>
        static VisualElement Img(Texture2D tex, float w, float h)
        {
            var v = new VisualElement { style = { width = w, height = h } };
            if (tex != null) { v.style.backgroundImage = tex; v.style.backgroundSize = new BackgroundSize(BackgroundSizeType.Contain); }
            return v;
        }

        /// <summary>九宫格：边框按贴图比例切，再缩放到目标像素宽，内容区可任意伸缩。</summary>
        static VisualElement Sliced(Texture2D tex, float fx, float fy, float borderPx)
        {
            var v = new VisualElement();
            if (tex == null) { v.style.backgroundColor = Shade; return v; }
            v.style.backgroundImage = tex;
            int sx = Mathf.Max(1, Mathf.RoundToInt(tex.width * fx)), sy = Mathf.Max(1, Mathf.RoundToInt(tex.height * fy));
            v.style.unitySliceLeft = sx; v.style.unitySliceRight = sx; v.style.unitySliceTop = sy; v.style.unitySliceBottom = sy;
            v.style.unitySliceScale = borderPx / Mathf.Max(sx, sy);
            return v;
        }

        static Button SkinButton(string text, System.Action onClick, Texture2D tex, float w, float h)
        {
            var b = new Button(onClick) { text = text };
            b.style.width = w; b.style.height = h;
            b.style.color = Paper; b.style.fontSize = 16;
            b.style.backgroundColor = tex == null ? Shade : Color.clear;
            if (tex != null) { b.style.backgroundImage = tex; b.style.backgroundSize = new BackgroundSize(BackgroundSizeType.Contain); }
            b.style.borderTopWidth = 0; b.style.borderBottomWidth = 0; b.style.borderLeftWidth = 0; b.style.borderRightWidth = 0;
            b.style.marginLeft = 0; b.style.marginRight = 0;
            DressButton(b, tex != null && tex.name.ToLowerInvariant().Contains("red"));
            return b;
        }

        static void StyleInput(TextField f)
        {
            f.style.marginLeft = 0; f.style.marginRight = 0;
            f.style.fontSize = 17;
            var inner = f.Q(TextField.textInputUssName);
            if (inner != null)
            {
                inner.style.backgroundColor = Color.clear;
                inner.style.borderTopWidth = 0; inner.style.borderBottomWidth = 0; inner.style.borderLeftWidth = 0; inner.style.borderRightWidth = 0;
                inner.style.color = Paper;
            }
        }

        static Color MechColor(AiTaskKind k)
        {
            switch (k)
            {
                case AiTaskKind.Dialogue: return new Color(0.44f, 0.70f, 1f);
                case AiTaskKind.Adjudicate: return new Color(0.95f, 0.65f, 0.35f);
                case AiTaskKind.AgentPlan: return new Color(0.48f, 0.83f, 0.54f);
                case AiTaskKind.Direct: return new Color(0.78f, 0.57f, 0.92f);
                default: return new Color(0.37f, 0.84f, 0.82f);
            }
        }

        static Color StatusColor(StageStatus s)
        {
            switch (s)
            {
                case StageStatus.Ok: return new Color(0.55f, 0.85f, 0.6f);
                case StageStatus.Warn: return new Color(0.98f, 0.78f, 0.38f);
                case StageStatus.Fail: return new Color(0.95f, 0.45f, 0.42f);
                case StageStatus.Skipped: return new Color(0.5f, 0.5f, 0.52f);
                default: return new Color(0.35f, 0.35f, 0.38f);
            }
        }

        static string StatusText(StageStatus s) =>
            s == StageStatus.Ok ? "通过" : s == StageStatus.Warn ? "修正" : s == StageStatus.Fail ? "失败" : s == StageStatus.Skipped ? "跳过" : "等待";

        static string VerdictText(AiTrace t) => t.FailureCode != null ? "失败 " + t.FailureCode
            : t.Verdict.Kind == VerdictKind.Accepted ? "接受" : t.Verdict.Kind == VerdictKind.Partial ? "部分接受" : "驳回";
    }
}
