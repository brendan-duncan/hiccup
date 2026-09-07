using System;
using System.Collections.Generic;
using System.Globalization;
using System.Reflection;
using System.Text;
using Hiccup.Mirror;
using UnityEngine;
using UnityEngine.UIElements;

namespace Hiccup.Uitk
{
    /// <summary>
    /// Mirrors a UI Toolkit runtime panel into an <see cref="HtmlDocument"/> so the browser draws and hit-tests it
    /// as real DOM: text is selectable and screen-readable, controls are native, and the picture is composited by
    /// HTML-in-Canvas like any other document. UI Toolkit keeps running underneath — USS, layout, transitions,
    /// bindings, manipulators and your own callbacks all work unchanged — it is just no longer rendered or picked.
    /// </summary>
    /// <remarks>
    /// <para>Every element in the panel's visual tree becomes an absolutely positioned element with the rectangle
    /// Yoga computed for it, so there is exactly one layout engine. Resolved USS maps almost one to one onto CSS:
    /// background color and image, borders and radii, opacity, visibility, overflow, translate/rotate/scale, font,
    /// size, color, alignment, spacing, text shadow. Buttons become <c>&lt;button&gt;</c>; Toggle, RadioButton,
    /// Slider, the text fields and DropdownField get a native control over their input part whose changes are fed
    /// back into the element; a ScrollView's viewport scrolls in the browser with the offset written back.
    /// Pointer events over the DOM are re-sent into the panel, so <c>:hover</c> and <c>:active</c> styles,
    /// Clickable, dropdown menus, list selection and custom manipulators still run.</para>
    /// <para>Not mirrored: content drawn with <c>generateVisualContent</c> or Painter2D, vector images, materials and
    /// filters, Scroller and MinMaxSlider dragging, world-space and render-texture panels, and the exact text
    /// metrics of the Unity font — the browser wraps text with its own.</para>
    /// </remarks>
    [AddComponentMenu("Hiccup/UI Toolkit Mirror")]
    [RequireComponent(typeof(UIDocument))]
    [DisallowMultipleComponent]
    public class HtmlUitkMirror : HtmlMirror
    {
        [Tooltip("UI Toolkit Menu: clicking opens the field's own dropdown menu in the panel, mirrored like everything else, so it looks exactly as styled. " +
                 "Native Select: an invisible <select> over the field opens the browser's picker, which screen readers and keyboards understand best.")]
        [SerializeField] private DropdownMode dropdownMode = DropdownMode.UitkMenu;
        [Tooltip("Re-send pointer hover, press and release from the DOM into the panel, so :hover and :active styles, Clickable, dropdown menus, list selection and custom manipulators run. Turn off to drive the panel through the native controls only.")]
        [SerializeField] private bool forwardPointer = true;

        public enum DropdownMode { UitkMenu, NativeSelect }

        public override int NodeCount => _nodes.Count;
        /// <summary>The panel being mirrored, once the UIDocument has created it.</summary>
        public IPanel Panel => _panel;

        private enum Control { None, Button, Toggle, Radio, Slider, TextField, Dropdown }

        private sealed class Node : MirrorNode
        {
            public VisualElement Element;
            public TextElement Text;           // the element itself when it draws text
            public Control Control;
            public VisualElement Part;         // what a native control is placed over: a field's input part, or null for the whole element
            public VisualElement SkipChild;    // a text field's inner text element: the native input draws the text
            public ScrollView Viewport;        // set when this element is a ScrollView's content viewport
            public bool IsRoot;
            public bool Hidden;                // visibility:hidden this frame, so a visible child has to say so

            // Whether the text fit on one line at the last measurement, redone when text, size or width change.
            public string MeasuredText;
            public float MeasuredSize, MeasuredWidth;
            public bool SingleLine;
        }

        private static readonly Func<UnityEngine.Object, string> s_fontAssetName = f =>
        {
            var fa = (UnityEngine.TextCore.Text.FontAsset)f;
            var family = fa.faceInfo.familyName;
            return string.IsNullOrEmpty(family) ? StripSdf(fa.name) : family;
        };

        private static readonly Func<UnityEngine.Object, string> s_fontName = f =>
        {
            var font = (Font)f;
            var names = font.fontNames;
            return names != null && names.Length > 0 && !string.IsNullOrEmpty(names[0]) ? names[0] : font.name;
        };

        private static readonly Func<Vector2, Vector3> s_blockedScreenToPanel = _ => new Vector3(float.NaN, float.NaN, float.NaN);

        private UIDocument _uiDocument;
        private IPanel _panel;
        private VisualElement _root;
        private PanelSettings _panelSettings;
        private bool _hidden;
        private bool _unsupportedPanelWarned;
        private float _rootLeft, _rootTop, _rootSx = 1f, _rootSy = 1f;   // CSS placement of #ugroot, for DOM to panel coordinates
        private bool _downInjected;
        private Event _event;

        private readonly Dictionary<VisualElement, Node> _nodes = new Dictionary<VisualElement, Node>();
        private readonly Dictionary<VisualElement, ScrollView> _viewports = new Dictionary<VisualElement, ScrollView>();
        private readonly List<VisualElement> _stale = new List<VisualElement>();

        protected override string DumpFolder => "HiccupUitkExports";
        protected override int OverlaySortingOrder => short.MaxValue;

        // ------------------------------------------------------------------ lifecycle

        protected override void OnEnable()
        {
            if (!Application.isPlaying)
                return;
            _uiDocument = GetComponent<UIDocument>();
            base.OnEnable();
        }

        protected override void OnDisable()
        {
            base.OnDisable();
            SetPanel(null);
        }

        protected override void ClearNodes()
        {
            _nodes.Clear();
            _viewports.Clear();
            _downInjected = false;
            base.ClearNodes();
        }

        /// <summary>Binds to the panel the UIDocument currently lives in; the panel appears after the document enables and can be replaced when its settings change.</summary>
        private void SetPanel(IPanel panel)
        {
            if (panel == _panel)
                return;
            if (_panel != null)
            {
                SetSourceHidden(false);
                foreach (var kv in _nodes)
                    RemoveNode(kv.Value);
                ClearNodes();
            }
            _panel = panel;
            _root = panel?.visualTree;
            _panelSettings = panel != null ? _uiDocument.panelSettings : null;
            if (_panel != null)
                SetSourceHidden(hideSource);
        }

        /// <summary>
        /// Hides the panel by giving its root opacity 0, which leaves styles, layout and events untouched, and
        /// stops it picking the real pointer by mapping every screen position to NaN, "outside the panel", so a click on
        /// the DOM copy is not also a click on the invisible original.
        /// </summary>
        private void SetSourceHidden(bool hide)
        {
            if (hide == _hidden || _root == null)
                return;
            _hidden = hide;
            _root.style.opacity = hide ? new StyleFloat(0f) : new StyleFloat(StyleKeyword.Null);
            if (_panelSettings != null)
                _panelSettings.SetScreenToPanelSpaceFunction3D(hide ? s_blockedScreenToPanel : null);   // the 2D overload cannot take null
        }

        // ------------------------------------------------------------------ per-frame sync

        // Runtime panels update (styles, layout) in PreLateUpdate.UIElementsUpdatePanels, before scripts' LateUpdate,
        // and render at the end of the frame, so this sees the frame's final geometry.
        private void LateUpdate()
        {
            if (_uiDocument == null)
                return;
            var root = _uiDocument.rootVisualElement;
            SetPanel(root?.panel);
            if (_panel == null)
                return;
            Sync();
        }

        /// <summary>Places the panel's root rectangle in the document: the root's layout is in panel units, the screen in pixels, so the root is scaled to fit.</summary>
        protected override bool DescribeRoot(StringBuilder sb)
        {
            if (_panelSettings != null && (_panelSettings.renderMode != PanelRenderMode.ScreenSpaceOverlay || _panelSettings.targetTexture != null))
            {
                if (!_unsupportedPanelWarned)
                {
                    _unsupportedPanelWarned = true;
                    Debug.LogWarning("[Hiccup] The UI Toolkit mirror only mirrors screen-space overlay panels without a target texture; " + _panelSettings.name + " is not mirrored.", this);
                }
                return false;
            }
            var r = _root.layout;
            if (float.IsNaN(r.width) || r.width <= 0f || r.height <= 0f)
                return false;
            float css = HtmlRuntime.HasInstance ? HtmlRuntime.Instance.CssPerScreenPixel : 1f;
            float sx = Screen.width * css / r.width;
            float sy = Screen.height * css / r.height;
            // Device pixels per panel unit, coarsened so a window resize does not re-export every sliced image per frame.
            float dpr = css > 0f ? 1f / css : 1f;
            _rootScale = Mathf.Max(0.25f, Mathf.Round(Mathf.Max(sx, sy) * dpr * 4f) / 4f);
            _rootLeft = 0f;
            _rootTop = 0f;
            _rootSx = sx;
            _rootSy = sy;

            AppendF(sb.Append("left:0px;top:0px;width:"), r.width);
            AppendF(sb.Append("px;height:"), r.height);
            AppendF(sb.Append("px;transform:scale("), sx).Append(',');
            AppendF(sb, sy).Append(')');
            return true;
        }

        protected override void SyncTree()
        {
            int order = 0;
            string prev = null;
            SyncElement(_root, null, "ugroot", ref order, ref prev, null);
        }

        private void SyncElement(VisualElement ve, Node parent, string parentId, ref int order, ref string prevSibling, StringBuilder emit)
        {
            if (!_nodes.TryGetValue(ve, out var node))
            {
                node = CreateNode(ve);
                _nodes[ve] = node;
            }
            SyncNode(node, parent, parentId, ref order, ref prevSibling, emit);
        }

        protected override void SyncChildren(MirrorNode mirrorNode, StringBuilder emit)
        {
            var node = (Node)mirrorNode;
            var h = node.Element.hierarchy;
            int order = 0;
            string prev = null;
            int count = h.childCount;
            for (int i = 0; i < count; i++)
            {
                var child = h[i];
                // display:none is not laid out and not drawn, like an inactive GameObject; an element added since the
                // panel's last update has no layout yet and appears next frame.
                if (child == node.SkipChild || child.resolvedStyle.display == DisplayStyle.None || float.IsNaN(child.layout.width))
                    continue;
                SyncElement(child, node, node.Id, ref order, ref prev, emit);
            }
        }

        protected override void RemoveStale(int frame)
        {
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

        // ------------------------------------------------------------------ node creation

        private Node CreateNode(VisualElement ve)
        {
            var n = new Node { Element = ve };
            Register(n);
            n.IsRoot = ve == _root;
            n.Text = ve as TextElement;
            n.HasText = n.Text != null;
            n.HasBg = !n.IsRoot;   // any element can gain a background or border through USS; the description says display:none until it does
            string cid = n.Id + "c";

            switch (ve)
            {
                case Button _:
                case RepeatButton _:
                    n.Control = Control.Button;
                    break;
                case RadioButton _:
                {
                    n.Control = Control.Radio;
                    // Radios in the same RadioButtonGroup share a name so the browser, like the group, keeps one checked.
                    string group = null;
                    for (var p = ve.parent; p != null; p = p.parent)
                    {
                        if (p is RadioButtonGroup && _nodes.TryGetValue(p, out var gn))
                        {
                            group = gn.Id;
                            break;
                        }
                    }
                    SetControl(n, "input", "<input type=\"radio\" id=\"" + cid + "\" class=\"ug-ctl\"" + (group != null ? " name=\"" + group + "\"" : string.Empty), null);
                    break;
                }
                case Toggle _:
                    n.Control = Control.Toggle;
                    SetControl(n, "input", "<input type=\"checkbox\" id=\"" + cid + "\" class=\"ug-ctl\"", null);
                    break;
                case Slider s:
                    n.Control = Control.Slider;
                    n.Part = ve.Q(className: "unity-base-slider__drag-container") ?? ve.Q(className: "unity-base-field__input");
                    SetControl(n, "input", "<input type=\"range\" id=\"" + cid + "\" class=\"ug-ctl\" min=\"" + F(s.lowValue) + "\" max=\"" + F(s.highValue) + "\" step=\"any\"", null);
                    break;
                case SliderInt s:
                    n.Control = Control.Slider;
                    n.Part = ve.Q(className: "unity-base-slider__drag-container") ?? ve.Q(className: "unity-base-field__input");
                    SetControl(n, "input", "<input type=\"range\" id=\"" + cid + "\" class=\"ug-ctl\" min=\"" + s.lowValue.ToString(Inv) + "\" max=\"" + s.highValue.ToString(Inv) + "\" step=\"1\"", null);
                    break;
                case TextField f:
                    TextInput(n, f.multiline, f.isPasswordField ? "password" : "text", null, f.maxLength, f.isReadOnly, f.textEdition.placeholder);
                    break;
                case IntegerField f:
                    TextInput(n, false, "text", "numeric", f.maxLength, f.isReadOnly, f.textEdition.placeholder);
                    break;
                case LongField f:
                    TextInput(n, false, "text", "numeric", f.maxLength, f.isReadOnly, f.textEdition.placeholder);
                    break;
                case UnsignedIntegerField f:
                    TextInput(n, false, "text", "numeric", f.maxLength, f.isReadOnly, f.textEdition.placeholder);
                    break;
                case UnsignedLongField f:
                    TextInput(n, false, "text", "numeric", f.maxLength, f.isReadOnly, f.textEdition.placeholder);
                    break;
                case FloatField f:
                    TextInput(n, false, "text", "decimal", f.maxLength, f.isReadOnly, f.textEdition.placeholder);
                    break;
                case DoubleField f:
                    TextInput(n, false, "text", "decimal", f.maxLength, f.isReadOnly, f.textEdition.placeholder);
                    break;
                case DropdownField _:
                    n.Control = Control.Dropdown;
                    n.Part = ve.Q(className: "unity-base-popup-field__input");
                    if (dropdownMode == DropdownMode.NativeSelect)
                        SetControl(n, "select", "<select id=\"" + cid + "\" class=\"ug-ctl\"", "</select>");
                    else
                        SetControl(n, "button", "<button type=\"button\" id=\"" + cid + "\" class=\"ug-ctl\"", "</button>");
                    break;
                case EnumField _:
                    // Its choices are not public, so the field's own menu is the only list.
                    n.Control = Control.Dropdown;
                    n.Part = ve.Q(className: "unity-base-popup-field__input");
                    SetControl(n, "button", "<button type=\"button\" id=\"" + cid + "\" class=\"ug-ctl\"", "</button>");
                    break;
            }

            if (ve is ScrollView sv && sv.contentViewport != null)
                _viewports[sv.contentViewport] = sv;
            if (_viewports.TryGetValue(ve, out var owner))
                n.Viewport = owner;
            return n;
        }

        /// <summary>A text field: the native input is placed on the inner text element, which is then not mirrored.</summary>
        private static void TextInput(Node n, bool multiline, string type, string inputMode, int limit, bool readOnly, string placeholder)
        {
            n.Control = Control.TextField;
            var input = n.Element.Q(className: "unity-base-text-field__input");
            var text = input != null ? input.Q<TextElement>() : n.Element.Q<TextElement>();
            n.Part = text ?? input;
            n.SkipChild = text;
            SetInputTag(n, multiline, type, inputMode, limit, readOnly, placeholder);
        }

        // ------------------------------------------------------------------ describing a node

        protected override void Describe(MirrorNode mirrorNode, MirrorNode mirrorParent, ref Desc d)
        {
            var node = (Node)mirrorNode;
            var parent = (Node)mirrorParent;
            var ve = node.Element;
            var rs = ve.resolvedStyle;
            var lay = ve.layout;
            float left = node.IsRoot ? 0f : lay.x, top = node.IsRoot ? 0f : lay.y;
            d.Left = left;
            d.Top = top;

            // The browser scrolls the viewport; the content sits at its rest position and the offset goes to scrollTop/Left.
            bool scrollContent = parent != null && parent.Viewport != null && parent.Viewport.contentContainer == ve;
            if (scrollContent)
                PushScroll(parent, parent.Viewport.scrollOffset);

            var sb = _style;
            sb.Clear();
            AppendF(sb.Append("left:"), left);
            AppendF(sb.Append("px;top:"), top);
            AppendF(sb.Append("px;width:"), lay.width);
            AppendF(sb.Append("px;height:"), lay.height).Append("px;");

            // translate, rotate and scale about the transform origin, in that order, exactly as UI Toolkit composes them.
            var tr = scrollContent ? Vector3.zero : rs.translate;   // a ScrollView moves its content by translate; the browser does that here
            float rot = rs.rotate.angle.ToDegrees();
            var sc = rs.scale.value;
            bool translated = Mathf.Abs(tr.x) > 0.001f || Mathf.Abs(tr.y) > 0.001f;
            bool rotated = Mathf.Abs(rot) > 0.001f;
            bool scaled = Mathf.Abs(sc.x - 1f) > 0.0001f || Mathf.Abs(sc.y - 1f) > 0.0001f;
            if (translated || rotated || scaled)
            {
                var origin = rs.transformOrigin;
                AppendF(sb.Append("transform-origin:"), origin.x).Append("px ");
                AppendF(sb, origin.y).Append("px;transform:");
                if (translated)
                {
                    AppendF(sb.Append("translate("), tr.x).Append("px,");
                    AppendF(sb, tr.y).Append("px) ");
                }
                if (rotated)
                    AppendF(sb.Append("rotate("), rot).Append("deg) ");
                if (scaled)
                {
                    AppendF(sb.Append("scale("), sc.x).Append(',');
                    AppendF(sb, sc.y).Append(") ");
                }
                sb.Append(';');
            }

            var cs = _cls;
            cs.Clear();
            cs.Append("ug");

            // visibility is inherited in both USS and CSS; a visible child of a hidden parent has to say so.
            bool hidden = rs.visibility == Visibility.Hidden;
            node.Hidden = hidden;
            if (hidden)
                sb.Append("visibility:hidden;");
            else if (parent != null && parent.Hidden)
                sb.Append("visibility:visible;");

            float opacity = node.IsRoot && _hidden ? 1f : rs.opacity;
            if (opacity < 1f)
                AppendF(sb.Append("opacity:"), Mathf.Max(0f, opacity)).Append(';');
            if (!ve.enabledSelf)
                cs.Append(" ug-noinput");

            float rtl = rs.borderTopLeftRadius, rtr = rs.borderTopRightRadius, rbr = rs.borderBottomRightRadius, rbl = rs.borderBottomLeftRadius;
            bool rounded = rtl > 0f || rtr > 0f || rbr > 0f || rbl > 0f;
            if (node.Viewport != null)
            {
                cs.Append(" ug-scroll");
                var mode = node.Viewport.mode;
                sb.Append("overflow-x:").Append(mode != ScrollViewMode.Vertical ? "auto" : "hidden")
                  .Append(";overflow-y:").Append(mode != ScrollViewMode.Horizontal ? "auto" : "hidden").Append(';');
                if (rounded)
                    AppendRadius(sb, rtl, rtr, rbr, rbl);
            }
            else if (Clips(ve))
            {
                sb.Append("overflow:hidden;");
                if (rounded)
                    AppendRadius(sb, rtl, rtr, rbr, rbl);   // clips the children to the rounded box, as the panel does
            }

            // ---- background and border
            if (node.HasBg)
                d.BgStyle = BackgroundStyle(node, rs, lay, cs, rtl, rtr, rbr, rbl, rounded);

            // ---- text
            if (node.HasText)
                TextDesc(node, rs, lay, ref d, sb);

            // ---- control
            switch (node.Control)
            {
                case Control.Button:
                    d.Tag = "button";
                    d.Disabled = !ve.enabledInHierarchy;
                    break;
                case Control.Toggle:
                    d.ControlChecked = ((Toggle)ve).value;
                    d.Disabled = !ve.enabledInHierarchy;
                    break;
                case Control.Radio:
                    d.ControlChecked = ((RadioButton)ve).value;
                    d.Disabled = !ve.enabledInHierarchy;
                    break;
                case Control.Slider:
                {
                    var ctl = _ctl;
                    ctl.Clear();
                    SliderDirection direction;
                    bool inverted;
                    if (ve is Slider s)
                    {
                        AppendF(ctl, s.value);
                        direction = s.direction;
                        inverted = s.inverted;
                    }
                    else
                    {
                        var si = (SliderInt)ve;
                        ctl.Append(si.value);
                        direction = si.direction;
                        inverted = si.inverted;
                    }
                    d.ControlValue = Take(ctl, node.Last.ControlValue);
                    d.Disabled = !ve.enabledInHierarchy;
                    ctl.Clear();
                    AppendPart(ctl, node);
                    // A vertical slider's high value sits at the top unless inverted; a horizontal one's at the right.
                    if (direction == SliderDirection.Vertical)
                        ctl.Append(inverted ? "writing-mode:vertical-lr;" : "writing-mode:vertical-lr;direction:rtl;");
                    else if (inverted)
                        ctl.Append("direction:rtl;");
                    d.ControlStyle = Take(ctl, node.Last.ControlStyle);
                    break;
                }
                case Control.TextField:
                    d.ControlValue = FieldText(ve);
                    d.ControlStyle = InputStyle(node);
                    d.Disabled = !ve.enabledInHierarchy;
                    break;
                case Control.Dropdown:
                    d.Disabled = !ve.enabledInHierarchy;
                    if (node.ControlTag == "select")
                    {
                        d.ControlHtml = OptionsHtml(node, out int selected);
                        d.ControlValue = Take(_ctl.Clear().Append(selected), node.Last.ControlValue);
                        d.ControlStyle = SelectStyle(node);
                    }
                    else
                    {
                        _ctl.Clear();
                        AppendPart(_ctl, node);
                        d.ControlStyle = Take(_ctl, node.Last.ControlStyle);
                    }
                    break;
            }

            d.Class = Take(cs, node.Last.Class);
            d.Style = Take(sb, node.Last.Style);
        }

        /// <summary>The USS class that forces an element to clip its children, for when <see cref="s_shouldClip"/> could not be bound.</summary>
        public const string ClipClass = "hiccup-clip";

        // UI Toolkit's own test for whether an element clips its children reads the computed overflow, which the
        // public resolved style does not expose; it is bound once here. Where that is not possible, an inline
        // style.overflow and the marker class are all that is known.
        private static readonly Func<VisualElement, bool> s_shouldClip = BindShouldClip();

        private static Func<VisualElement, bool> BindShouldClip()
        {
            try
            {
                var m = typeof(VisualElement).GetMethod("ShouldClip", BindingFlags.Instance | BindingFlags.NonPublic, null, Type.EmptyTypes, null);
                if (m == null || m.ReturnType != typeof(bool))
                    return null;
                return (Func<VisualElement, bool>)Delegate.CreateDelegate(typeof(Func<VisualElement, bool>), m);
            }
            catch (Exception)
            {
                return null;
            }
        }

        /// <summary>Whether an element clips its children.</summary>
        private static bool Clips(VisualElement ve)
        {
            if (ve.ClassListContains(ClipClass))
                return true;
            if (s_shouldClip != null)
                return s_shouldClip(ve);
            var o = ve.style.overflow;
            return o.keyword == StyleKeyword.Undefined && o.value == Overflow.Hidden;
        }

        private static void AppendRadius(StringBuilder sb, float tl, float tr, float br, float bl)
        {
            AppendF(sb.Append("border-radius:"), tl).Append("px ");
            AppendF(sb, tr).Append("px ");
            AppendF(sb, br).Append("px ");
            AppendF(sb, bl).Append("px;");
        }

        /// <summary>The bounds of a control's part relative to its node, as left/top/width/height. Nothing when the control covers the whole node.</summary>
        private static void AppendPart(StringBuilder sb, Node n)
        {
            var part = n.Part;
            if (part == null)
                return;
            var pw = part.worldBound;
            var nw = n.Element.worldBound;
            if (float.IsNaN(pw.width) || float.IsNaN(nw.width))
                return;
            AppendF(sb.Append("left:"), pw.x - nw.x);
            AppendF(sb.Append("px;top:"), pw.y - nw.y);
            AppendF(sb.Append("px;width:"), pw.width);
            AppendF(sb.Append("px;height:"), pw.height).Append("px;");
        }

        private string InputStyle(Node n)
        {
            var sb = _ctl;
            sb.Clear();
            AppendPart(sb, n);
            var text = n.SkipChild ?? n.Element;
            var rs = text.resolvedStyle;
            AppendFont(sb, rs);
            return Take(sb, n.Last.ControlStyle);
        }

        /// <summary>Font and colors for a native select so its picker (Chrome's customizable select) matches the field.</summary>
        private string SelectStyle(Node n)
        {
            var sb = _ctl;
            sb.Clear();
            AppendPart(sb, n);
            var text = n.Element.Q<TextElement>(className: "unity-base-popup-field__text");
            var part = n.Part ?? n.Element;
            AppendFont(sb, (text ?? part).resolvedStyle);
            AppendRgb(sb.Append("--ug-bg:"), part.resolvedStyle.backgroundColor).Append(';');
            AppendRgb(sb.Append("--ug-fg:"), (text ?? part).resolvedStyle.color).Append(';');
            return Take(sb, n.Last.ControlStyle);
        }

        private string OptionsHtml(Node n, out int selected)
        {
            var sb = _ctl;
            sb.Clear();
            selected = 0;
            if (n.Element is DropdownField dd)
            {
                selected = dd.index;
                var choices = dd.choices;
                if (choices != null)
                {
                    for (int i = 0; i < choices.Count; i++)
                        AppendOption(sb, i, i == dd.index, choices[i]);
                }
            }
            return Take(sb, n.Last.ControlHtml);
        }

        private static string FieldText(VisualElement ve)
        {
            switch (ve)
            {
                case TextField f: return f.text;
                case IntegerField f: return f.text;
                case LongField f: return f.text;
                case UnsignedIntegerField f: return f.text;
                case UnsignedLongField f: return f.text;
                case FloatField f: return f.text;
                case DoubleField f: return f.text;
                default: return string.Empty;
            }
        }

        // ---- fonts and text

        /// <summary>Family, size, weight, style, color and alignment from a resolved style.</summary>
        private void AppendFont(StringBuilder sb, IResolvedStyle rs)
        {
            string family;
            bool bold = false, italic = false;
            var def = rs.unityFontDefinition;
            var asset = def.fontAsset;
            if (asset != null)
            {
                family = Family(asset, s_fontAssetName);
                var style = asset.faceInfo.styleName;
                if (!string.IsNullOrEmpty(style))
                {
                    bold = style.IndexOf("Bold", StringComparison.OrdinalIgnoreCase) >= 0;
                    italic = style.IndexOf("Italic", StringComparison.OrdinalIgnoreCase) >= 0;
                }
            }
            else
            {
                var font = def.font != null ? def.font : rs.unityFont;
                family = Family(font, s_fontName);
            }
            var fs = rs.unityFontStyleAndWeight;
            bold |= fs == FontStyle.Bold || fs == FontStyle.BoldAndItalic;
            italic |= fs == FontStyle.Italic || fs == FontStyle.BoldAndItalic;
            AppendFont(sb, family, rs.fontSize, bold, italic, rs.color, HAlign(rs.unityTextAlign));
        }

        private void TextDesc(Node node, IResolvedStyle rs, Rect lay, ref Desc d, StringBuilder nodeStyle)
        {
            var te = node.Text;
            var ts = _text;
            ts.Clear();
            AppendFont(ts, rs);

            // The text is drawn in the content box: inside the border and padding.
            float pt = rs.paddingTop + rs.borderTopWidth, pr = rs.paddingRight + rs.borderRightWidth;
            float pb = rs.paddingBottom + rs.borderBottomWidth, pl = rs.paddingLeft + rs.borderLeftWidth;
            float contentWidth = lay.width - pl - pr;

            // Follow Unity's wrapping decision rather than the browser's: its font is a little wider or narrower, and a
            // label that fitted on one line in the panel would otherwise wrap its last word into a second line.
            string text = te.text;
            var ws = rs.whiteSpace;
            bool wrap = ws != WhiteSpace.NoWrap && ws != WhiteSpace.Pre && !SingleLine(node, te, text, rs.fontSize, contentWidth);
            ts.Append("white-space:").Append(wrap ? "pre-wrap" : "pre").Append(';');
            if (Mathf.Abs(rs.letterSpacing) > 0.001f)
                AppendF(ts.Append("letter-spacing:"), rs.letterSpacing).Append("px;");
            if (Mathf.Abs(rs.wordSpacing) > 0.001f)
                AppendF(ts.Append("word-spacing:"), rs.wordSpacing).Append("px;");
            var shadow = rs.textShadow;
            if (shadow.color.a > 0.001f && (shadow.offset != Vector2.zero || shadow.blurRadius > 0f))
            {
                AppendF(ts.Append("text-shadow:"), shadow.offset.x).Append("px ");
                AppendF(ts, shadow.offset.y).Append("px ");
                AppendF(ts, shadow.blurRadius).Append("px ");
                AppendRgba(ts, shadow.color).Append(';');
            }
            float outline = rs.unityTextOutlineWidth;
            if (outline > 0f && rs.unityTextOutlineColor.a > 0.001f)
            {
                // The stroke is centered on the glyph edge, so twice the width reaches as far out as Unity's outline;
                // painting the fill last keeps the inside of the glyph its own color.
                AppendF(ts.Append("-webkit-text-stroke:"), outline * 2f).Append("px ");
                AppendRgba(ts, rs.unityTextOutlineColor).Append(";paint-order:stroke fill;");
            }
            if (rs.textOverflow == TextOverflow.Ellipsis && Clips(node.Element))
                ts.Append("overflow:hidden;text-overflow:ellipsis;");
            d.TextStyle = Take(ts, node.Last.TextStyle);
            d.Text = TextHtml(node, text, te.enableRichText);

            nodeStyle.Append("display:flex;align-items:").Append(VAlign(rs.unityTextAlign)).Append(";padding:");
            AppendF(nodeStyle, pt).Append("px ");
            AppendF(nodeStyle, pr).Append("px ");
            AppendF(nodeStyle, pb).Append("px ");
            AppendF(nodeStyle, pl).Append("px;");
        }

        /// <summary>Whether the text fits on one line of the content box in Unity's font, measured again only when the text, size or width changed.</summary>
        private static bool SingleLine(Node n, TextElement te, string text, float fontSize, float contentWidth)
        {
            if (!ReferenceEquals(text, n.MeasuredText) || fontSize != n.MeasuredSize || Mathf.Abs(contentWidth - n.MeasuredWidth) > 0.5f)
            {
                n.MeasuredText = text;
                n.MeasuredSize = fontSize;
                n.MeasuredWidth = contentWidth;
                if (string.IsNullOrEmpty(text))
                    n.SingleLine = true;
                else
                {
                    var size = te.MeasureTextSize(text, 0f, VisualElement.MeasureMode.Undefined, 0f, VisualElement.MeasureMode.Undefined);
                    n.SingleLine = !float.IsNaN(size.x) && size.x <= contentWidth + 0.5f;
                }
            }
            return n.SingleLine;
        }

        // ---- background

        private string BackgroundStyle(Node n, IResolvedStyle rs, Rect lay, StringBuilder cs, float rtl, float rtr, float rbr, float rbl, bool rounded)
        {
            var bs = _bg;
            bs.Clear();
            bool any = false;

            var color = rs.backgroundColor;
            if (color.a > 0.001f)
            {
                AppendRgba(bs.Append("background-color:"), color).Append(';');
                any = true;
            }

            float bt = rs.borderTopWidth, br = rs.borderRightWidth, bb = rs.borderBottomWidth, bl = rs.borderLeftWidth;
            if (bt > 0f || br > 0f || bb > 0f || bl > 0f)
            {
                AppendF(bs.Append("border-style:solid;border-width:"), bt).Append("px ");
                AppendF(bs, br).Append("px ");
                AppendF(bs, bb).Append("px ");
                AppendF(bs, bl).Append("px;border-color:");
                AppendRgba(bs, rs.borderTopColor).Append(' ');
                AppendRgba(bs, rs.borderRightColor).Append(' ');
                AppendRgba(bs, rs.borderBottomColor).Append(' ');
                AppendRgba(bs, rs.borderLeftColor).Append(';');
                any = true;
            }
            if (rounded)
                AppendRadius(bs, rtl, rtr, rbr, rbl);

            var bg = rs.backgroundImage;
            var sprite = bg.sprite;
            Texture texture = sprite != null ? sprite.texture : bg.texture != null ? (Texture)bg.texture : bg.renderTexture;
            if (bg.vectorImage != null && texture == null)
            {
                if (outlineUnsupported)
                    cs.Append(" ug-unsupported");
            }
            else if (texture != null)
            {
                any = true;
                var tint = rs.unityBackgroundImageTintColor;
                if (tint.a < 0.999f)
                    AppendF(bs.Append("opacity:"), tint.a).Append(';');
                if (texture is RenderTexture)
                    RefreshRenderTexture(n, texture);
                var rect = sprite != null ? ToRectInt(SpriteRect(sprite)) : new RectInt(0, 0, texture.width, texture.height);
                // Sprites are drawn at the panel's reference pixels per unit; the export is one texture pixel per pixel.
                float unitsPerPixel = 1f;
                if (sprite != null && _panelSettings != null && sprite.pixelsPerUnit > 0f)
                    unitsPerPixel = _panelSettings.referenceSpritePixelsPerUnit / sprite.pixelsPerUnit;

                int sl = rs.unitySliceLeft, st = rs.unitySliceTop, sr = rs.unitySliceRight, sbm = rs.unitySliceBottom;
                bool sliced = sl > 0 || st > 0 || sr > 0 || sbm > 0;
                if (sliced)
                {
                    // Composed in Unity at the element's device-pixel size, so the browser draws one bitmap: CSS
                    // border-image leaves hairline seams between slices at fractional pixel positions.
                    float k = unitsPerPixel * Mathf.Max(0.001f, rs.unitySliceScale);
                    sliced = AppendSliced(bs, texture, rect, new Vector4(sl, sbm, sr, st), lay.width, lay.height,
                        sl * k, sbm * k, sr * k, st * k, true, tint);
                }
                if (!sliced)
                {
                    string url = Textures.DataUrl(texture, rect, tint);
                    if (url != null)
                    {
                        bs.Append("background-image:url(").Append(url).Append(");");
                        AppendBackgroundSize(bs, rs.backgroundSize, rect, unitsPerPixel);
                        AppendBackgroundPosition(bs, rs.backgroundPositionX, rs.backgroundPositionY);
                        var rep = rs.backgroundRepeat;
                        bs.Append("background-repeat:").Append(Repeat(rep.x)).Append(' ').Append(Repeat(rep.y)).Append(';');
                    }
                }
            }
            if (!any)
                return "display:none";
            return Take(bs, n.Last.BgStyle);
        }

        private static void AppendBackgroundSize(StringBuilder bs, BackgroundSize size, RectInt rect, float unitsPerPixel)
        {
            bs.Append("background-size:");
            switch (size.sizeType)
            {
                case BackgroundSizeType.Cover: bs.Append("cover;"); return;
                case BackgroundSizeType.Contain: bs.Append("contain;"); return;
            }
            AppendLength(bs, size.x, rect.width * unitsPerPixel).Append(' ');
            AppendLength(bs, size.y, rect.height * unitsPerPixel).Append(';');
        }

        /// <summary>A length as CSS; auto becomes the image's natural size in panel units, which is not always its pixel size.</summary>
        private static StringBuilder AppendLength(StringBuilder bs, Length l, float natural)
        {
            if (l.IsAuto() || l.IsNone())
                return AppendF(bs, natural).Append("px");
            AppendF(bs, l.value);
            return bs.Append(l.unit == LengthUnit.Percent ? "%" : "px");
        }

        private static void AppendBackgroundPosition(StringBuilder bs, BackgroundPosition x, BackgroundPosition y)
        {
            bs.Append("background-position:");
            AppendPosition(bs, x, "left", "right").Append(' ');
            AppendPosition(bs, y, "top", "bottom").Append(';');
        }

        private static StringBuilder AppendPosition(StringBuilder bs, BackgroundPosition p, string low, string high)
        {
            string edge;
            switch (p.keyword)
            {
                case BackgroundPositionKeyword.Center: return bs.Append("center");
                case BackgroundPositionKeyword.Right: case BackgroundPositionKeyword.Bottom: edge = high; break;
                default: edge = low; break;
            }
            bs.Append(edge);
            if (Mathf.Abs(p.offset.value) > 0.001f)
            {
                AppendF(bs.Append(' '), p.offset.value);
                bs.Append(p.offset.unit == LengthUnit.Percent ? "%" : "px");
            }
            return bs;
        }

        private static string Repeat(UnityEngine.UIElements.Repeat r)
        {
            switch (r)
            {
                case UnityEngine.UIElements.Repeat.Repeat: return "repeat";
                case UnityEngine.UIElements.Repeat.Space: return "space";
                case UnityEngine.UIElements.Repeat.Round: return "round";
                default: return "no-repeat";
            }
        }

        // ------------------------------------------------------------------ DOM -> UI Toolkit

        protected override void OnClick(HtmlEvent e)
        {
            var n = NodeOnPath<Node>(e, x => x.Control == Control.Button || (x.Control == Control.Dropdown && x.ControlTag == "button"));
            if (n == null || !n.Element.enabledInHierarchy)
                return;
            // A pointer click on a Button already reached it as the injected press and release, which its Clickable
            // turned into a click; a keyboard click carries no click count and is submitted here instead.
            if (n.Control == Control.Button && forwardPointer && e.detail != "0")
                return;
            Submit(n.Element);
            e.Handled = true;
        }

        /// <summary>Sends a submit to an element: a Button clicks, a popup field opens its menu.</summary>
        private static void Submit(VisualElement ve)
        {
            using (var evt = NavigationSubmitEvent.GetPooled())
            {
                evt.target = ve;
                ve.SendEvent(evt);
            }
        }

        protected override void OnInput(HtmlEvent e)
        {
            var n = NodeFor<Node>(e);
            if (n == null)
                return;
            switch (n.Control)
            {
                case Control.Slider:
                    if (n.Element is Slider s)
                    {
                        s.value = e.ValueAsFloat;
                        n.Last.ControlValue = F(s.value);
                    }
                    else
                    {
                        var si = (SliderInt)n.Element;
                        si.value = e.ValueAsInt;
                        n.Last.ControlValue = si.value.ToString(Inv);
                    }
                    e.Handled = true;
                    break;
                case Control.TextField:
                    if (WriteText(n.Element, e.value, false))
                        n.Last.ControlValue = FieldText(n.Element);
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
                    ((Toggle)n.Element).value = e.isChecked;
                    n.Last.ControlChecked = e.isChecked;
                    e.Handled = true;
                    break;
                case Control.Radio:
                    ((RadioButton)n.Element).value = e.isChecked;
                    n.Last.ControlChecked = e.isChecked;
                    e.Handled = true;
                    break;
                case Control.Dropdown:
                    if (n.ControlTag == "select" && n.Element is DropdownField dd)
                    {
                        dd.index = e.ValueAsInt;
                        e.Handled = true;
                    }
                    break;
                case Control.TextField:
                    if (WriteText(n.Element, e.value, true))
                        n.Last.ControlValue = FieldText(n.Element);
                    e.Handled = true;
                    break;
            }
        }

        /// <summary>
        /// Writes typed text into a field. A delayed field takes the value on change (Enter or blur), the others on
        /// every input, as they would from their own editing. Numeric fields wait for text that parses.
        /// </summary>
        private static bool WriteText(VisualElement ve, string s, bool onChange)
        {
            switch (ve)
            {
                case TextField f:
                    if (f.isDelayed != onChange)
                        return false;
                    f.value = s;
                    return true;
                case IntegerField f:
                    if (f.isDelayed != onChange || !int.TryParse(s, NumberStyles.Integer, Inv, out var i))
                        return false;
                    f.value = i;
                    return true;
                case LongField f:
                    if (f.isDelayed != onChange || !long.TryParse(s, NumberStyles.Integer, Inv, out var l))
                        return false;
                    f.value = l;
                    return true;
                case UnsignedIntegerField f:
                    if (f.isDelayed != onChange || !uint.TryParse(s, NumberStyles.Integer, Inv, out var ui))
                        return false;
                    f.value = ui;
                    return true;
                case UnsignedLongField f:
                    if (f.isDelayed != onChange || !ulong.TryParse(s, NumberStyles.Integer, Inv, out var ul))
                        return false;
                    f.value = ul;
                    return true;
                case FloatField f:
                    if (f.isDelayed != onChange || !float.TryParse(s, NumberStyles.Float, Inv, out var fl))
                        return false;
                    f.value = fl;
                    return true;
                case DoubleField f:
                    if (f.isDelayed != onChange || !double.TryParse(s, NumberStyles.Float, Inv, out var db))
                        return false;
                    f.value = db;
                    return true;
                default:
                    return false;
            }
        }

        protected override void OnScroll(MirrorNode viewport, float left, float top)
        {
            var n = (Node)viewport;
            if (n.Viewport == null)
                return;
            n.Viewport.scrollOffset = new Vector2(left, top);
        }

        // Pointer events are re-sent into the panel the way its own input path would: as an IMGUI Event in panel
        // coordinates, which the pooled pointer events read. A move updates the element under the pointer, so
        // PointerEnter/Leave and :hover follow; press and release reach Clickable, dropdown menus, list rows and
        // manipulators. Nodes with a native control keep their pointer: the control's own value events drive them.

        protected override void OnPointerOver(HtmlEvent e)
        {
            if (forwardPointer)
                Inject(EventType.MouseMove, e.x, e.y);
        }

        protected override void OnPointerDown(HtmlEvent e)
        {
            if (!forwardPointer || e.button != 0)
                return;
            var n = NodeFor<Node>(e);
            if (n == null || n.ControlTag != null)
                return;
            _downInjected = true;
            Inject(EventType.MouseDown, e.x, e.y);
        }

        protected override void OnPointerUp(HtmlEvent e)
        {
            if (!_downInjected)
                return;
            _downInjected = false;
            Inject(EventType.MouseUp, e.x, e.y);
            // The press focused the element in the panel. The DOM copy holds the real focus, and keys Unity still
            // sees in the Editor preview would otherwise submit the panel's element as well.
            _panel?.focusController?.focusedElement?.Blur();
        }

        protected override void OnPointerLeave(HtmlEvent e)
        {
            if (!forwardPointer || _panel == null)
                return;
            if (_downInjected)
            {
                _downInjected = false;
                Inject(EventType.MouseUp, e.x, e.y);
            }
            Inject(EventType.MouseMove, -100000f, -100000f);   // nothing under the pointer: leaves the hovered chain
        }

        private void Inject(EventType type, float cssX, float cssY)
        {
            if (_panel == null || _root == null)
                return;
            var p = new Vector2((cssX - _rootLeft) / _rootSx, (cssY - _rootTop) / _rootSy);
            var ev = _event ??= new Event();
            ev.type = type;
            ev.mousePosition = p;
            ev.button = 0;
            ev.clickCount = type == EventType.MouseDown ? 1 : 0;
            ev.modifiers = EventModifiers.None;
            ev.delta = Vector2.zero;
            EventBase evt;
            switch (type)
            {
                case EventType.MouseDown: evt = PointerDownEvent.GetPooled(ev); break;
                case EventType.MouseUp: evt = PointerUpEvent.GetPooled(ev); break;
                default: evt = PointerMoveEvent.GetPooled(ev); break;
            }
            using (evt)
            {
                // Picking respects pickingMode, as the panel's own input does; the root takes what nothing else does.
                var target = type == EventType.MouseMove ? null : _panel.Pick(p);
                if (target != null)
                    evt.target = target;
                (target ?? _root).SendEvent(evt);
            }
        }
    }
}
