using System;
using System.Collections.Generic;
using System.Text;
using Hiccup.Mirror;
using TMPro;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UI;

namespace Hiccup.Ugui
{
    /// <summary>
    /// Mirrors a uGUI <see cref="Canvas"/> into an <see cref="HtmlDocument"/> so the browser lays out and draws it
    /// as real DOM: text is selectable and screen-readable, controls are native, and the picture is composited by
    /// HTML-in-Canvas like any other document. uGUI keeps running underneath — layout groups, animations, Selectable
    /// transitions and your own code all work unchanged — it is just no longer rendered or clicked.
    /// </summary>
    /// <remarks>
    /// <para>Every active <see cref="RectTransform"/> becomes an absolutely positioned element with the rectangle
    /// uGUI computed for it, so there is exactly one layout engine. Images become backgrounds (tinted PNG exports,
    /// sliced via <c>border-image</c>), Text and TextMesh Pro become styled text, Buttons become <c>&lt;button&gt;</c>,
    /// Toggles, Sliders, Dropdowns and InputFields get a native control over their rectangle whose changes are fed
    /// back into the uGUI component, and a ScrollRect's viewport scrolls in the browser with the offset written back
    /// to the content. The tree is diffed once per frame after uGUI's layout pass.</para>
    /// <para>Not mirrored: custom <see cref="Graphic"/> subclasses and mesh effects other than Shadow/Outline on
    /// text, Radial90/180 fills, materials and shaders, Scrollbar dragging (scroll the viewport instead), and the
    /// pixel-exact text metrics of the Unity fonts — the browser wraps text with its own font.</para>
    /// </remarks>
    [AddComponentMenu("Hiccup/uGUI Mirror")]
    [RequireComponent(typeof(Canvas))]
    [DisallowMultipleComponent]
    public class HtmlUguiMirror : HtmlMirror
    {
        [Tooltip("uGUI List: clicking opens the Dropdown's own template list, mirrored like everything else, so it looks exactly as authored. " +
                 "Native Select: an invisible <select> over the caption opens the browser's picker (styled to the dropdown's colors where Chrome allows), which screen readers and keyboards understand best.")]
        [SerializeField] private DropdownMode dropdownMode = DropdownMode.UguiList;

        public enum DropdownMode { UguiList, NativeSelect }

        public override int NodeCount => _nodes.Count;

        private enum Control { None, Button, Toggle, Slider, Dropdown, InputField }

        private sealed class Node : MirrorNode
        {
            public RectTransform Rect;

            public Graphic Graphic;
            public Selectable Selectable;
            public Control Control;
            public Graphic InputText;          // an InputField's text component: the native input draws the text
            public RectTransform SkipChild;
            public ScrollRect Viewport;        // set when this RectTransform is a ScrollRect's viewport

            // Components read every frame, resolved once like Graphic and Selectable: one added later is not seen.
            public Canvas NestedCanvas;
            public CanvasGroup Group;
            public Mask Mask;
            public bool HasRectMask;
            public Outline Outline;
            public Shadow Shadow;
        }

        private static readonly Func<UnityEngine.Object, string> s_fontName = f => f.name;
        private static readonly Func<UnityEngine.Object, string> s_tmpName = f => StripSdf(f.name);

        private Canvas _canvas;
        private RectTransform _canvasRect;
        private CanvasGroup _group;
        private bool _groupAdded, _groupBlocks;
        private float _groupAlpha;

        private readonly Dictionary<EntityId, Node> _nodes = new Dictionary<EntityId, Node>();
        private readonly Dictionary<EntityId, ScrollRect> _viewports = new Dictionary<EntityId, ScrollRect>();
        private readonly List<EntityId> _stale = new List<EntityId>();
        private readonly Vector3[] _corners = new Vector3[4];
        private Selectable _hovered, _pressed;

        protected override string DumpFolder => "HiccupUguiExports";
        protected override int OverlaySortingOrder => _canvas.renderMode == RenderMode.ScreenSpaceOverlay ? _canvas.sortingOrder + 1 : short.MaxValue;

        // ------------------------------------------------------------------ lifecycle

        protected override void OnEnable()
        {
            if (!Application.isPlaying)
                return;
            _canvas = GetComponent<Canvas>();
            _canvasRect = (RectTransform)transform;
            base.OnEnable();
            SetSourceHidden(hideSource);
            var _ = CanvasUpdateRegistry.instance;   // subscribes uGUI's layout rebuild ahead of us
            Canvas.willRenderCanvases += OnWillRenderCanvases;
        }

        protected override void OnDisable()
        {
            Canvas.willRenderCanvases -= OnWillRenderCanvases;
            base.OnDisable();
            SetSourceHidden(false);
        }

        private void SetSourceHidden(bool hide)
        {
            if (hide)
            {
                if (_group != null)
                    return;
                _group = GetComponent<CanvasGroup>();
                _groupAdded = _group == null;
                if (_groupAdded)
                    _group = gameObject.AddComponent<CanvasGroup>();
                _groupAlpha = _group.alpha;
                _groupBlocks = _group.blocksRaycasts;
                _group.alpha = 0f;
                _group.blocksRaycasts = false;
            }
            else if (_group != null)
            {
                if (_groupAdded)
                    Destroy(_group);
                else
                {
                    _group.alpha = _groupAlpha;
                    _group.blocksRaycasts = _groupBlocks;
                }
                _group = null;
            }
        }

        protected override void ClearNodes()
        {
            _nodes.Clear();
            _viewports.Clear();
            _hovered = _pressed = null;
            base.ClearNodes();
        }

        // ------------------------------------------------------------------ per-frame sync

        private void OnWillRenderCanvases() => Sync();

        /// <summary>Places the canvas rectangle in the document: screen pixels to CSS pixels, canvas units scaled to fit.</summary>
        protected override bool DescribeRoot(StringBuilder sb)
        {
            var cam = _canvas.renderMode == RenderMode.ScreenSpaceOverlay ? null : _canvas.worldCamera;
            _canvasRect.GetWorldCorners(_corners);
            Vector2 bl = RectTransformUtility.WorldToScreenPoint(cam, _corners[0]);
            Vector2 tl = RectTransformUtility.WorldToScreenPoint(cam, _corners[1]);
            Vector2 tr = RectTransformUtility.WorldToScreenPoint(cam, _corners[2]);
            float css = HtmlRuntime.HasInstance ? HtmlRuntime.Instance.CssPerScreenPixel : 1f;
            var r = _canvasRect.rect;
            float sx = r.width > 0f ? Vector2.Distance(tl, tr) * css / r.width : 1f;
            float sy = r.height > 0f ? Vector2.Distance(tl, bl) * css / r.height : 1f;
            // Device pixels per canvas unit, coarsened so a window resize does not re-export every sliced image per frame.
            float dpr = css > 0f ? 1f / css : 1f;
            _rootScale = Mathf.Max(0.25f, Mathf.Round(Mathf.Max(sx, sy) * dpr * 4f) / 4f);

            AppendF(sb.Append("left:"), tl.x * css);
            AppendF(sb.Append("px;top:"), (Screen.height - tl.y) * css);
            AppendF(sb.Append("px;width:"), r.width);
            AppendF(sb.Append("px;height:"), r.height);
            AppendF(sb.Append("px;transform:scale("), sx).Append(',');
            AppendF(sb, sy).Append(')');
            return true;
        }

        protected override void SyncTree()
        {
            int order = 0;
            string prev = null;
            SyncRect(_canvasRect, null, "ugroot", ref order, ref prev, null);
        }

        private void SyncRect(RectTransform rt, Node parent, string parentId, ref int order, ref string prevSibling, StringBuilder emit)
        {
            var key = rt.GetEntityId();
            if (!_nodes.TryGetValue(key, out var node))
            {
                node = CreateNode(rt);
                _nodes[key] = node;
            }
            SyncNode(node, parent, parentId, ref order, ref prevSibling, emit);
        }

        protected override void SyncChildren(MirrorNode mirrorNode, StringBuilder emit)
        {
            var node = (Node)mirrorNode;
            var rt = node.Rect;
            int order = 0;
            string prev = null;
            for (int i = 0; i < rt.childCount; i++)
            {
                var child = rt.GetChild(i) as RectTransform;
                if (child == null || !child.gameObject.activeSelf || child == node.SkipChild)
                    continue;
                SyncRect(child, node, node.Id, ref order, ref prev, emit);
            }
        }

        protected override void RemoveStale(int frame)
        {
            // Stale nodes are found by key: their RectTransform may already be destroyed, so it cannot be asked for its id.
            _stale.Clear();
            foreach (var kv in _nodes)
            {
                if (kv.Value.Visit != frame)
                    _stale.Add(kv.Key);
            }
            foreach (var key in _stale)
            {
                var n = _nodes[key];
                _nodes.Remove(key);
                RemoveNode(n);
            }
        }

        protected override void RemoveNode(MirrorNode mirrorNode)
        {
            var n = (Node)mirrorNode;
            if (_hovered == n.Selectable)
                _hovered = null;
            if (_pressed == n.Selectable)
                _pressed = null;
            base.RemoveNode(n);
        }

        // ------------------------------------------------------------------ node creation

        private Node CreateNode(RectTransform rt)
        {
            var iid = rt.GetEntityId();
            var n = new Node { Rect = rt };
            Register(n);
            n.IsSurface = rt.GetComponent<HtmlScreenSurface>() != null;
            n.Graphic = rt.GetComponent<Graphic>();
            n.HasText = n.Graphic is Text || n.Graphic is TMP_Text;
            n.HasBg = n.Graphic != null && !n.HasText;
            // The canvas root's own Canvas maps the rectangle onto the screen through #ugroot; only nested ones sort.
            n.NestedCanvas = rt == _canvasRect ? null : rt.GetComponent<Canvas>();
            n.Group = rt.GetComponent<CanvasGroup>();
            n.Mask = rt.GetComponent<Mask>();
            n.HasRectMask = rt.GetComponent<RectMask2D>() != null;
            n.Outline = rt.GetComponent<Outline>();
            n.Shadow = rt.GetComponent<Shadow>();

            var sel = rt.GetComponent<Selectable>();
            n.Selectable = sel;
            string cid = n.Id + "c";
            switch (sel)
            {
                case InputField f:
                    n.Control = Control.InputField;
                    n.InputText = f.textComponent;
                    n.SkipChild = f.textComponent != null ? f.textComponent.rectTransform : null;
                    SetInputTag(n, f.lineType != InputField.LineType.SingleLine, InputType(f.contentType), InputMode(f.contentType), f.characterLimit, f.readOnly, null);
                    break;
                case TMP_InputField f:
                    n.Control = Control.InputField;
                    n.InputText = f.textComponent;
                    n.SkipChild = f.textComponent != null ? f.textComponent.rectTransform : null;
                    SetInputTag(n, f.lineType != TMP_InputField.LineType.SingleLine, InputType(f.contentType), InputMode(f.contentType), f.characterLimit, f.readOnly, null);
                    break;
                case Dropdown _:
                case TMP_Dropdown _:
                    n.Control = Control.Dropdown;
                    if (dropdownMode == DropdownMode.NativeSelect)
                        SetControl(n, "select", "<select id=\"" + cid + "\" class=\"ug-ctl\"", "</select>");
                    // Otherwise the node is a <button> that calls Show(); uGUI's own list is mirrored when it appears.
                    break;
                case Slider s:
                    n.Control = Control.Slider;
                    SetControl(n, "input", "<input type=\"range\" id=\"" + cid + "\" class=\"ug-ctl\" min=\"" + F(s.minValue) + "\" max=\"" + F(s.maxValue) +
                                           "\" step=\"" + (s.wholeNumbers ? "1" : "any") + "\"", null);
                    break;
                case Toggle _:
                    n.Control = Control.Toggle;
                    SetControl(n, "input", "<input type=\"checkbox\" id=\"" + cid + "\" class=\"ug-ctl\"", null);
                    break;
                case Button _:
                    n.Control = Control.Button;
                    break;
            }

            var scroll = rt.GetComponent<ScrollRect>();
            if (scroll != null)
            {
                var vp = scroll.viewport != null ? scroll.viewport : rt;
                _viewports[vp.GetEntityId()] = scroll;
            }
            if (_viewports.TryGetValue(iid, out var owner))
                n.Viewport = owner;
            return n;
        }

        private static string InputType(InputField.ContentType t)
        {
            switch (t)
            {
                case InputField.ContentType.Password: case InputField.ContentType.Pin: return "password";
                case InputField.ContentType.EmailAddress: return "email";
                default: return "text";
            }
        }

        private static string InputMode(InputField.ContentType t)
        {
            switch (t)
            {
                case InputField.ContentType.IntegerNumber: return "numeric";
                case InputField.ContentType.DecimalNumber: return "decimal";
                default: return null;
            }
        }

        private static string InputType(TMP_InputField.ContentType t)
        {
            switch (t)
            {
                case TMP_InputField.ContentType.Password: case TMP_InputField.ContentType.Pin: return "password";
                case TMP_InputField.ContentType.EmailAddress: return "email";
                default: return "text";
            }
        }

        private static string InputMode(TMP_InputField.ContentType t)
        {
            switch (t)
            {
                case TMP_InputField.ContentType.IntegerNumber: return "numeric";
                case TMP_InputField.ContentType.DecimalNumber: return "decimal";
                default: return null;
            }
        }

        // ------------------------------------------------------------------ describing a node

        protected override void Describe(MirrorNode mirrorNode, MirrorNode mirrorParent, ref Desc d)
        {
            var node = (Node)mirrorNode;
            var parent = (Node)mirrorParent;
            var rt = node.Rect;
            var r = rt.rect;
            float left = 0f, top = 0f;
            if (parent != null && rt.parent is RectTransform parentRt)
            {
                // The rect is pivot-relative and localPosition is the pivot in the parent's space, whose origin is
                // the parent's pivot; the parent's CSS box starts at its own rect's top-left corner.
                var pr = parentRt.rect;
                var lp = rt.localPosition;
                left = lp.x + r.xMin - pr.xMin;
                top = pr.yMax - (lp.y + r.yMax);
            }
            d.Left = left;
            d.Top = top;

            bool scrollContent = parent != null && parent.Viewport != null && parent.Viewport.content == rt;
            float cssLeft = left, cssTop = top;
            if (scrollContent)
            {
                // The browser scrolls the viewport; the content sits at its rest position and the offset goes to scrollTop/Left.
                cssLeft = Mathf.Max(left, 0f);
                cssTop = Mathf.Max(top, 0f);
                PushScroll(parent, new Vector2(Mathf.Max(-left, 0f), Mathf.Max(-top, 0f)));
            }

            var sb = _style;
            sb.Clear();
            AppendF(sb.Append("left:"), cssLeft);
            AppendF(sb.Append("px;top:"), cssTop);
            AppendF(sb.Append("px;width:"), r.width);
            AppendF(sb.Append("px;height:"), r.height).Append("px;");

            // The canvas root's own scale (a CanvasScaler writes scaleFactor into its localScale) and rotation are
            // already accounted for by #ugroot, which maps the canvas rectangle onto the screen; only descendants
            // carry their transforms here.
            var s = parent != null ? rt.localScale : Vector3.one;
            float rot = 0f;
            if (parent != null && rt.localRotation != Quaternion.identity)   // the Euler conversion only when there is a rotation
                rot = rt.localEulerAngles.z;
            if (rot > 180f)
                rot -= 360f;
            if (Mathf.Abs(rot) > 0.001f || Mathf.Abs(s.x - 1f) > 0.0001f || Mathf.Abs(s.y - 1f) > 0.0001f)
            {
                AppendF(sb.Append("transform-origin:"), rt.pivot.x * 100f).Append("% ");
                AppendF(sb, (1f - rt.pivot.y) * 100f).Append("%;transform:");
                if (Mathf.Abs(rot) > 0.001f)
                    AppendF(sb.Append("rotate("), -rot).Append("deg) ");   // CSS turns clockwise, Unity counterclockwise
                if (Mathf.Abs(s.x - 1f) > 0.0001f || Mathf.Abs(s.y - 1f) > 0.0001f)
                {
                    AppendF(sb.Append("scale("), s.x).Append(',');
                    AppendF(sb, s.y).Append(')');
                }
                sb.Append(';');
            }

            // A nested Canvas that overrides sorting (a Dropdown's list and blocker, a popup) paints above later siblings.
            var nested = node.NestedCanvas;
            if (nested != null && nested.overrideSorting)
                sb.Append("z-index:").Append(nested.sortingOrder).Append(';');

            var cs = _cls;
            cs.Clear();
            cs.Append("ug");
            var group = node.Group;
            if (group != null)
            {
                float alpha = group == _group ? _groupAlpha : group.alpha;
                bool blocks = group == _group ? _groupBlocks : group.blocksRaycasts;
                if (alpha < 1f)
                    AppendF(sb.Append("opacity:"), alpha).Append(';');
                if (!group.interactable || !blocks)
                    cs.Append(" ug-noinput");
            }

            bool clip = node.HasRectMask;
            bool hideMaskGraphic = false;
            var mask = node.Mask;
            if (mask != null && mask.enabled)
            {
                clip = true;
                hideMaskGraphic = !mask.showMaskGraphic;
                var maskSprite = node.Graphic != null && node.Graphic is Image mi ? mi.overrideSprite : null;
                string url = maskSprite != null ? SpriteUrl(maskSprite, Color.white) : null;
                if (url != null)
                    sb.Append("-webkit-mask-image:url(").Append(url).Append(");mask-image:url(").Append(url).Append(");-webkit-mask-size:100% 100%;mask-size:100% 100%;");
            }
            if (node.Viewport != null)
            {
                cs.Append(" ug-scroll");
                sb.Append("overflow-x:").Append(node.Viewport.horizontal ? "auto" : "hidden").Append(";overflow-y:").Append(node.Viewport.vertical ? "auto" : "hidden").Append(';');
            }
            else if (clip)
                sb.Append("overflow:hidden;");

            // ---- graphic
            var g = node.Graphic;
            if (node.HasBg)
            {
                if (g == null || !g.enabled || hideMaskGraphic)
                    d.BgStyle = "display:none";
                else
                {
                    var color = g.color * g.canvasRenderer.GetColor();
                    switch (g)
                    {
                        case Image img: d.BgStyle = ImageStyle(node, img, color); break;
                        case RawImage raw: d.BgStyle = RawImageStyle(node, raw, color); break;
                        default:
                            d.BgStyle = "display:none";
                            if (outlineUnsupported)
                                cs.Append(" ug-unsupported");
                            break;
                    }
                }
            }
            else if (node.HasText)
            {
                if (g == null || !g.enabled)
                {
                    d.TextStyle = "display:none";
                    d.Text = string.Empty;
                }
                else
                {
                    var color = g.color * g.canvasRenderer.GetColor();
                    if (g is Text t)
                        TextDesc(t, color, node, ref d, sb);
                    else if (g is TMP_Text tmp)
                        TmpDesc(tmp, color, node, ref d, sb);
                }
            }

            // ---- control
            var sel = node.Selectable;
            switch (node.Control)
            {
                case Control.Button:
                    d.Tag = "button";
                    d.Disabled = !sel.IsInteractable();
                    break;
                case Control.Toggle:
                    d.ControlChecked = ((Toggle)sel).isOn;
                    d.Disabled = !sel.IsInteractable();
                    break;
                case Control.Slider:
                    var slider = (Slider)sel;
                    d.ControlValue = Take(AppendF(_ctl.Clear(), slider.value), node.Last.ControlValue);
                    d.Disabled = !sel.IsInteractable();
                    switch (slider.direction)
                    {
                        case Slider.Direction.RightToLeft: d.ControlStyle = "direction:rtl"; break;
                        case Slider.Direction.BottomToTop: d.ControlStyle = "writing-mode:vertical-lr;direction:rtl"; break;
                        case Slider.Direction.TopToBottom: d.ControlStyle = "writing-mode:vertical-lr"; break;
                    }
                    break;
                case Control.Dropdown:
                    d.Disabled = !sel.IsInteractable();
                    if (node.ControlTag == null)
                    {
                        d.Tag = "button";
                        break;
                    }   // uGUI list mode: click opens the template list
                    d.ControlHtml = OptionsHtml(node, out int selected);
                    d.ControlValue = Take(_ctl.Clear().Append(selected), node.Last.ControlValue);
                    d.ControlStyle = SelectStyle(node);
                    break;
                case Control.InputField:
                    d.ControlValue = sel is InputField inf ? inf.text : ((TMP_InputField)sel).text;
                    d.ControlStyle = InputStyle(node, rt);
                    d.Disabled = !sel.IsInteractable();
                    break;
            }

            d.Class = Take(cs, node.Last.Class);
            d.Style = Take(sb, node.Last.Style);
        }

        private string OptionsHtml(Node n, out int selected)
        {
            var sb = _ctl;
            sb.Clear();
            selected = 0;
            if (n.Selectable is Dropdown dd)
            {
                selected = dd.value;
                for (int i = 0; i < dd.options.Count; i++)
                    AppendOption(sb, i, i == dd.value, dd.options[i].text);
            }
            else if (n.Selectable is TMP_Dropdown td)
            {
                selected = td.value;
                for (int i = 0; i < td.options.Count; i++)
                    AppendOption(sb, i, i == td.value, td.options[i].text);
            }
            return Take(sb, n.Last.ControlHtml);
        }

        private string InputStyle(Node n, RectTransform rt)
        {
            var g = n.InputText;
            if (g == null)
                return null;
            g.rectTransform.GetWorldCorners(_corners);
            var tl = rt.InverseTransformPoint(_corners[1]);
            var br = rt.InverseTransformPoint(_corners[3]);
            var r = rt.rect;
            var sb = _ctl;
            sb.Clear();
            AppendF(sb.Append("left:"), tl.x - r.xMin);
            AppendF(sb.Append("px;top:"), r.yMax - tl.y);
            AppendF(sb.Append("px;width:"), br.x - tl.x);
            AppendF(sb.Append("px;height:"), tl.y - br.y).Append("px;");
            AppendFont(sb, g);
            if (n.Selectable is InputField f && f.customCaretColor)
                AppendRgba(sb.Append("caret-color:"), f.caretColor).Append(';');
            else if (n.Selectable is TMP_InputField tf && tf.customCaretColor)
                AppendRgba(sb.Append("caret-color:"), tf.caretColor).Append(';');
            return Take(sb, n.Last.ControlStyle);
        }

        /// <summary>Font and colors for a native select so its picker (Chrome's customizable select) matches the dropdown.</summary>
        private string SelectStyle(Node n)
        {
            var sb = _ctl;
            sb.Clear();
            Graphic caption = n.Selectable is Dropdown dd ? dd.captionText : (n.Selectable as TMP_Dropdown)?.captionText;
            AppendFont(sb, caption);
            if (n.Graphic != null)
                AppendRgb(sb.Append("--ug-bg:"), n.Graphic.color).Append(';');
            if (caption != null)
                AppendRgb(sb.Append("--ug-fg:"), caption.color).Append(';');
            return Take(sb, n.Last.ControlStyle);
        }

        /// <summary>
        /// Family, size, weight, style, color and alignment of a Text or TMP_Text. <paramref name="size"/> and
        /// <paramref name="color"/> replace the component's own when given. Nothing for null, including a destroyed
        /// component, which is Unity-null but would still match a type pattern.
        /// </summary>
        private void AppendFont(StringBuilder sb, Graphic text, float size = -1f, Color? color = null)
        {
            if (text == null)
                return;
            switch (text)
            {
                case Text t:
                    AppendFont(sb, Family(t.font, s_fontName), size >= 0f ? size : t.fontSize, t.fontStyle == FontStyle.Bold || t.fontStyle == FontStyle.BoldAndItalic,
                        t.fontStyle == FontStyle.Italic || t.fontStyle == FontStyle.BoldAndItalic, color ?? t.color, HAlign(t.alignment));
                    break;
                case TMP_Text t:
                    AppendFont(sb, Family(t.font, s_tmpName), size >= 0f ? size : t.fontSize, (t.fontStyle & FontStyles.Bold) != 0,
                        (t.fontStyle & FontStyles.Italic) != 0, color ?? t.color, HAlign(t.horizontalAlignment));
                    break;
            }
        }

        // ---- text

        private void TextDesc(Text t, Color color, Node node, ref Desc d, StringBuilder nodeStyle)
        {
            float size = t.fontSize;
            if (t.resizeTextForBestFit && t.cachedTextGenerator != null && t.cachedTextGenerator.fontSizeUsedForBestFit > 0)
                size = t.cachedTextGenerator.fontSizeUsedForBestFit / Mathf.Max(0.0001f, t.pixelsPerUnit);

            var ts = _text;
            ts.Clear();
            AppendFont(ts, t, size, color);
            // Follow Unity's wrapping decision rather than the browser's: its font is a little wider or narrower, and a
            // label uGUI fitted on one line would otherwise wrap its last word into a second line that is then clipped.
            int lines = t.cachedTextGenerator != null ? t.cachedTextGenerator.lineCount : 1;
            bool wrap = t.horizontalOverflow == HorizontalWrapMode.Wrap && lines > 1;
            ts.Append("white-space:").Append(wrap ? "pre-wrap" : "pre").Append(';');
            if (Mathf.Abs(t.lineSpacing - 1f) > 0.001f)
                AppendF(ts.Append("line-height:"), 1.2f * t.lineSpacing).Append(';');
            AppendEffects(node, ts);
            d.TextStyle = Take(ts, node.Last.TextStyle);
            d.Text = TextHtml(node, t.text, t.supportRichText);

            nodeStyle.Append("display:flex;align-items:").Append(VAlign(t.alignment)).Append(';');
            // Truncate clips vertically only; a slightly wider line may run past the rectangle instead of losing its end.
            if (t.verticalOverflow == VerticalWrapMode.Truncate)
                nodeStyle.Append("overflow-x:visible;overflow-y:clip;");
        }

        private void TmpDesc(TMP_Text t, Color color, Node node, ref Desc d, StringBuilder nodeStyle)
        {
            var ts = _text;
            ts.Clear();
            var fs = t.fontStyle;
            ts.Append("font-family:").Append(Family(t.font, s_tmpName)).Append(";font-size:");
            AppendF(ts, t.fontSize).Append("px;");
            if ((fs & FontStyles.Bold) != 0)
                ts.Append("font-weight:bold;");
            else if (t.fontWeight != FontWeight.Regular)
                ts.Append("font-weight:").Append((int)t.fontWeight).Append(';');
            if ((fs & FontStyles.Italic) != 0)
                ts.Append("font-style:italic;");
            if ((fs & FontStyles.Underline) != 0 || (fs & FontStyles.Strikethrough) != 0)
                ts.Append("text-decoration:").Append((fs & FontStyles.Underline) != 0 ? "underline " : "").Append((fs & FontStyles.Strikethrough) != 0 ? "line-through" : "").Append(';');
            if ((fs & FontStyles.UpperCase) != 0)
                ts.Append("text-transform:uppercase;");
            else if ((fs & FontStyles.LowerCase) != 0)
                ts.Append("text-transform:lowercase;");
            else if ((fs & FontStyles.SmallCaps) != 0)
                ts.Append("font-variant:small-caps;");
            AppendRgba(ts.Append("color:"), color).Append(";text-align:").Append(HAlign(t.horizontalAlignment)).Append(';');
            int lines = t.textInfo != null ? t.textInfo.lineCount : 1;   // see TextDesc: wrap only where TMP wrapped
            bool wrap = t.textWrappingMode != TextWrappingModes.NoWrap && lines > 1;
            ts.Append("white-space:").Append(wrap ? "pre-wrap" : "pre").Append(';');
            if (Mathf.Abs(t.characterSpacing) > 0.001f)
                AppendF(ts.Append("letter-spacing:"), t.characterSpacing * 0.01f).Append("em;");
            if (Mathf.Abs(t.lineSpacing) > 0.001f)
                AppendF(ts.Append("line-height:"), 1.2f + t.lineSpacing * 0.01f).Append(';');
            var m = t.margin;
            if (m != Vector4.zero)
            {
                AppendF(ts.Append("padding:"), m.y).Append("px ");
                AppendF(ts, m.z).Append("px ");
                AppendF(ts, m.w).Append("px ");
                AppendF(ts, m.x).Append("px;");
            }
            AppendEffects(node, ts);
            d.TextStyle = Take(ts, node.Last.TextStyle);
            d.Text = TextHtml(node, t.text, t.richText);

            nodeStyle.Append("display:flex;align-items:").Append(VAlign(t.verticalAlignment)).Append(';');
            if (t.overflowMode != TextOverflowModes.Overflow)
                nodeStyle.Append("overflow-x:visible;overflow-y:clip;");
        }

        private static void AppendEffects(Node node, StringBuilder ts)
        {
            var outline = node.Outline;
            if (outline != null && outline.enabled)
            {
                var c = outline.effectColor;
                float x = outline.effectDistance.x, y = outline.effectDistance.y;
                ts.Append("text-shadow:");
                AppendShadow(ts, x, -y, c).Append(',');
                AppendShadow(ts, -x, -y, c).Append(',');
                AppendShadow(ts, x, y, c).Append(',');
                AppendShadow(ts, -x, y, c).Append(';');
                return;
            }
            var shadow = node.Shadow;
            if (shadow != null && shadow.enabled)
            {
                ts.Append("text-shadow:");
                AppendShadow(ts, shadow.effectDistance.x, -shadow.effectDistance.y, shadow.effectColor).Append(';');
            }
        }

        // ---- images

        private string ImageStyle(Node n, Image img, Color color)
        {
            var bs = _bg;
            bs.Clear();
            if (color.a < 0.999f)
                AppendF(bs.Append("opacity:"), color.a).Append(';');
            var sprite = img.overrideSprite;
            string url = sprite != null ? SpriteUrl(sprite, color) : null;
            if (url == null)
            {
                AppendRgb(bs.Append("background-color:"), color).Append(';');
                return Take(bs, n.Last.BgStyle);
            }
            float ppu = Mathf.Max(0.001f, img.pixelsPerUnit);
            switch (img.type)
            {
                case Image.Type.Sliced:
                {
                    // Composed in Unity at the element's device-pixel size, so the browser draws one bitmap: CSS
                    // border-image leaves hairline seams between slices at fractional pixel positions.
                    var rr = img.rectTransform.rect;
                    var b = sprite.border;   // x left, y bottom, z right, w top, in sprite pixels
                    float unitsPerPixel = 1f / Mathf.Max(0.001f, ppu * img.pixelsPerUnitMultiplier);
                    if (AppendSliced(bs, sprite.texture, ToRectInt(SpriteRect(sprite)), b, rr.width, rr.height,
                            b.x * unitsPerPixel, b.y * unitsPerPixel, b.z * unitsPerPixel, b.w * unitsPerPixel, img.fillCenter, color))
                        break;
                    goto default;
                }
                case Image.Type.Tiled:
                {
                    var tr = SpriteRect(sprite);
                    bs.Append("background-image:url(").Append(url).Append(");background-repeat:repeat;background-position:left bottom;background-size:");
                    AppendF(bs, tr.width / ppu).Append("px ");
                    AppendF(bs, tr.height / ppu).Append("px;");
                    break;
                }
                case Image.Type.Filled:
                    bs.Append("background-image:url(").Append(url).Append(");background-size:100% 100%;");
                    AppendFill(img, bs);
                    break;
                default:
                    bs.Append("background-image:url(").Append(url).Append(");background-size:").Append(img.preserveAspect ? "contain;background-position:center;" : "100% 100%;");
                    break;
            }
            return Take(bs, n.Last.BgStyle);
        }

        private static void AppendFill(Image img, StringBuilder bs)
        {
            float a = Mathf.Clamp01(img.fillAmount);
            float rest = (1f - a) * 100f;
            switch (img.fillMethod)
            {
                case Image.FillMethod.Horizontal:
                    if (img.fillOrigin == (int)Image.OriginHorizontal.Left)
                        AppendF(bs.Append("clip-path:inset(0 "), rest).Append("% 0 0);");
                    else
                        AppendF(bs.Append("clip-path:inset(0 0 0 "), rest).Append("%);");
                    break;
                case Image.FillMethod.Vertical:
                    if (img.fillOrigin == (int)Image.OriginVertical.Bottom)
                        AppendF(bs.Append("clip-path:inset("), rest).Append("% 0 0 0);");
                    else
                        AppendF(bs.Append("clip-path:inset(0 0 "), rest).Append("% 0);");
                    break;
                case Image.FillMethod.Radial360:
                {
                    int from = img.fillOrigin == (int)Image.Origin360.Bottom ? 180 : img.fillOrigin == (int)Image.Origin360.Right ? 90 : img.fillOrigin == (int)Image.Origin360.Left ? 270 : 0;
                    AppendConic(bs.Append("-webkit-mask-image:"), img.fillClockwise, a, from);
                    AppendConic(bs.Append(";mask-image:"), img.fillClockwise, a, from).Append(';');
                    break;
                }
                // Radial90 / Radial180: no equivalent here; the image shows unfilled.
            }
        }

        private static StringBuilder AppendConic(StringBuilder bs, bool clockwise, float amount, int from)
        {
            bs.Append("conic-gradient(from ").Append(from).Append("deg,");
            if (clockwise)
                return AppendF(bs.Append("#000 "), amount * 360f).Append("deg,transparent 0)");
            return AppendF(bs.Append("transparent "), (1f - amount) * 360f).Append("deg,#000 0)");
        }

        private string RawImageStyle(Node n, RawImage raw, Color color)
        {
            var bs = _bg;
            bs.Clear();
            if (color.a < 0.999f)
                AppendF(bs.Append("opacity:"), color.a).Append(';');
            var tex = raw.texture;
            if (tex == null)
            {
                AppendRgb(bs.Append("background-color:"), color).Append(';');
                return Take(bs, n.Last.BgStyle);
            }
            if (tex is RenderTexture)
                RefreshRenderTexture(n, tex);
            string url = Textures.DataUrl(tex, new RectInt(0, 0, tex.width, tex.height), color);
            var uv = raw.uvRect;
            bs.Append("background-image:url(").Append(url).Append(");");
            if (uv.x == 0f && uv.y == 0f && uv.width == 1f && uv.height == 1f)
                bs.Append("background-size:100% 100%;");
            else
            {
                var rr = raw.rectTransform.rect;
                float bw = rr.width / Mathf.Max(0.0001f, uv.width), bh = rr.height / Mathf.Max(0.0001f, uv.height);
                AppendF(bs.Append("background-size:"), bw).Append("px ");
                AppendF(bs, bh).Append("px;background-position:");
                AppendF(bs, -uv.x * bw).Append("px ");
                AppendF(bs, -(1f - uv.y - uv.height) * bh).Append("px;");
            }
            return Take(bs, n.Last.BgStyle);
        }

        // ------------------------------------------------------------------ DOM -> uGUI

        protected override void OnClick(HtmlEvent e)
        {
            var n = NodeOnPath<Node>(e, x => x.Control == Control.Button || (x.Control == Control.Dropdown && x.ControlTag == null));
            if (n == null || n.Selectable == null || !n.Selectable.IsInteractable())
                return;
            switch (n.Selectable)
            {
                case Button b:
                    ExecuteEvents.Execute(b.gameObject, new BaseEventData(EventSystem.current), ExecuteEvents.submitHandler);
                    break;
                case Dropdown dd:
                    dd.Show();   // instantiates the template list under the canvas, which the next sync mirrors
                    break;
                case TMP_Dropdown td:
                    td.Show();
                    break;
                default:
                    return;
            }
            e.Handled = true;
        }

        protected override void OnInput(HtmlEvent e)
        {
            var n = NodeFor<Node>(e);
            if (n == null)
                return;
            switch (n.Control)
            {
                case Control.Slider:
                    ((Slider)n.Selectable).value = e.ValueAsFloat;
                    n.Last.ControlValue = F(((Slider)n.Selectable).value);
                    e.Handled = true;
                    break;
                case Control.InputField:
                    if (n.Selectable is InputField f)
                    {
                        f.text = e.value;
                        n.Last.ControlValue = f.text;
                    }
                    else if (n.Selectable is TMP_InputField tf)
                    {
                        tf.text = e.value;
                        n.Last.ControlValue = tf.text;
                    }
                    e.Handled = true;
                    break;
            }
        }

        protected override void OnChange(HtmlEvent e)
        {
            var n = NodeFor<Node>(e);
            if (n == null)
                return;
            switch (n.Control)
            {
                case Control.Toggle:
                    ((Toggle)n.Selectable).isOn = e.isChecked;
                    n.Last.ControlChecked = e.isChecked;
                    e.Handled = true;
                    break;
                case Control.Dropdown:
                    if (n.Selectable is Dropdown dd)
                        dd.value = e.ValueAsInt;
                    else if (n.Selectable is TMP_Dropdown td)
                        td.value = e.ValueAsInt;
                    e.Handled = true;
                    break;
                case Control.InputField:
                    if (n.Selectable is InputField f)
                        f.onEndEdit.Invoke(f.text);
                    else if (n.Selectable is TMP_InputField tf)
                        tf.onEndEdit.Invoke(tf.text);
                    e.Handled = true;
                    break;
            }
        }

        protected override void OnScroll(MirrorNode viewport, float left, float top)
        {
            var n = (Node)viewport;
            if (n.Viewport == null || n.Viewport.content == null)
                return;
            if (!_nodes.TryGetValue(n.Viewport.content.GetEntityId(), out var content))
                return;
            var ap = n.Viewport.content.anchoredPosition;
            // Content top must end up at -scrollTop: CSS top grows downward, anchoredPosition.y upward.
            n.Viewport.content.anchoredPosition = new Vector2(ap.x - left - content.Last.Left, ap.y + content.Last.Top + top);
            n.Viewport.velocity = Vector2.zero;
        }

        protected override void OnPointerOver(HtmlEvent e)
        {
            var n = NodeOnPath<Node>(e, x => x.Selectable != null);
            var sel = n?.Selectable;
            if (sel == _hovered)
                return;
            if (_hovered != null)
                Pointer(_hovered, ExecuteEvents.pointerExitHandler);
            _hovered = sel;
            if (sel != null)
                Pointer(sel, ExecuteEvents.pointerEnterHandler);
        }

        protected override void OnPointerDown(HtmlEvent e)
        {
            var n = NodeOnPath<Node>(e, x => x.Selectable != null);
            if (n == null)
                return;
            _pressed = n.Selectable;
            Pointer(_pressed, ExecuteEvents.pointerDownHandler);
        }

        protected override void OnPointerUp(HtmlEvent e)
        {
            if (_pressed == null)
                return;
            Pointer(_pressed, ExecuteEvents.pointerUpHandler);
            _pressed = null;
        }

        protected override void OnPointerLeave(HtmlEvent e)
        {
            if (_hovered != null)
                Pointer(_hovered, ExecuteEvents.pointerExitHandler);
            _hovered = null;
        }

        private static void Pointer<T>(Selectable sel, ExecuteEvents.EventFunction<T> handler) where T : IEventSystemHandler
        {
            if (sel == null)
                return;
            var data = new PointerEventData(EventSystem.current) { button = PointerEventData.InputButton.Left };
            ExecuteEvents.Execute(sel.gameObject, data, handler);
        }

        // ------------------------------------------------------------------ formatting

        private static string HAlign(HorizontalAlignmentOptions a)
        {
            switch (a)
            {
                case HorizontalAlignmentOptions.Center: case HorizontalAlignmentOptions.Geometry: return "center";
                case HorizontalAlignmentOptions.Right: return "right";
                case HorizontalAlignmentOptions.Justified: case HorizontalAlignmentOptions.Flush: return "justify";
                default: return "left";
            }
        }

        private static string VAlign(VerticalAlignmentOptions a)
        {
            switch (a)
            {
                case VerticalAlignmentOptions.Middle: case VerticalAlignmentOptions.Geometry: return "center";
                case VerticalAlignmentOptions.Bottom: case VerticalAlignmentOptions.Baseline: return "flex-end";
                default: return "flex-start";
            }
        }
    }
}
