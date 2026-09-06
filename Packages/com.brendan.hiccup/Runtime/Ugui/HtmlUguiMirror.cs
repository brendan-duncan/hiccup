using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
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
    public class HtmlUguiMirror : MonoBehaviour
    {
        [Serializable]
        public struct FontFace
        {
            [Tooltip("The font-family to register. Use the Unity Font name, or the TMP font asset name without ' SDF'.")]
            public string family;
            [Tooltip("A TTF, OTF or WOFF2 file imported as a TextAsset (rename the file to .bytes).")]
            public TextAsset file;
        }

        [Tooltip("Document to mirror into. Leave empty to create a full-screen overlay document automatically.")]
        [SerializeField] private HtmlDocument document;
        [Tooltip("Hide the uGUI canvas (CanvasGroup alpha 0, raycasts off) so only the HTML copy is visible and interactive.")]
        [SerializeField] private bool hideSource = true;
        [Tooltip("CSS font-family list appended after each Unity font name.")]
        [SerializeField] private string fallbackFonts = "system-ui, 'Segoe UI', Roboto, Helvetica, Arial, sans-serif";
        [Tooltip("Web fonts to embed so text uses the same faces as the Unity fonts.")]
        [SerializeField] private FontFace[] fonts;
        [Tooltip("How often a RawImage showing a RenderTexture is re-exported, in seconds. 0 exports it once.")]
        [SerializeField] private float renderTextureRefresh = 0.5f;
        [Tooltip("Draw a dashed outline where a Graphic has no HTML equivalent (custom meshes, unknown Graphic subclasses).")]
        [SerializeField] private bool outlineUnsupported = true;
        [Tooltip("uGUI List: clicking opens the Dropdown's own template list, mirrored like everything else, so it looks exactly as authored. " +
                 "Native Select: an invisible <select> over the caption opens the browser's picker (styled to the dropdown's colours where Chrome allows), which screen readers and keyboards understand best.")]
        [SerializeField] private DropdownMode dropdownMode = DropdownMode.UguiList;
        [Tooltip("Also write every exported sprite/texture PNG to <persistentDataPath>/HiccupUguiExports, to check what the page receives.")]
        [SerializeField] private bool dumpExports;

        public enum DropdownMode { UguiList, NativeSelect }

        /// <summary>The document the canvas is mirrored into.</summary>
        public HtmlDocument Document => _doc;
        /// <summary>Mirrored RectTransforms.</summary>
        public int NodeCount => _nodes.Count;
        /// <summary>PNGs exported for sprites and textures so far.</summary>
        public int TextureCount => _textures?.Count ?? 0;

        private enum Control { None, Button, Toggle, Slider, Dropdown, InputField }

        private sealed class Node
        {
            public RectTransform Rect;
            public string Id;                  // element id; b/t/c/k suffixes name the background, text, control and children elements
            public string ParentId;
            public int Order;
            public int Visit;
            public bool Created;

            public Graphic Graphic;
            public bool HasBg, HasText;
            public Selectable Selectable;
            public Control Control;
            public string ControlTag, ControlOpen, ControlClose;
            public Graphic InputText;          // an InputField's text component: the native input draws the text
            public RectTransform SkipChild;
            public ScrollRect Viewport;        // set when this RectTransform is a ScrollRect's viewport
            public Vector2 ScrollPushed = new Vector2(float.NaN, float.NaN);
            public float Left, Top;            // raw geometry from the last sync, used for scroll write-back
            public float TextureTime;

            // last emitted state
            public string Tag, Class, Style, BgStyle, Text, TextStyle, ControlHtml, ControlStyle, ControlValue;
            public bool ControlChecked, Disabled;
        }

        private sealed class Desc
        {
            public string Tag, Class, Style, BgStyle, Text, TextStyle, ControlHtml, ControlStyle, ControlValue;
            public bool ControlChecked, Disabled;
            public float Left, Top;

            public void Reset()
            {
                Tag = "div"; Class = "ug"; Style = BgStyle = Text = TextStyle = ControlHtml = ControlStyle = ControlValue = null;
                ControlChecked = Disabled = false; Left = Top = 0f;
            }
        }

        private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

        private Canvas _canvas;
        private RectTransform _canvasRect;
        private CanvasGroup _group;
        private bool _groupAdded, _groupBlocks;
        private float _groupAlpha;
        private GameObject _ownedDocument;
        private HtmlDocument _doc;
        private bool _wired, _handlers;
        private int _frame;
        private string _rootStyle;
        private float _rootScale = 1f;   // canvas units to device pixels, so composed images come out crisp

        private readonly Dictionary<EntityId, Node> _nodes = new Dictionary<EntityId, Node>();
        private readonly Dictionary<string, Node> _byElement = new Dictionary<string, Node>();
        private readonly Dictionary<EntityId, ScrollRect> _viewports = new Dictionary<EntityId, ScrollRect>();
        private readonly List<EntityId> _stale = new List<EntityId>();
        private int _nextId = 1;   // element ids are sequential; EntityId is not a number
        private readonly List<Node> _scrollWrites = new List<Node>();
        private readonly StringBuilder _html = new StringBuilder(4096);
        private readonly StringBuilder _style = new StringBuilder(256);
        private readonly StringBuilder _bg = new StringBuilder(256);
        private readonly StringBuilder _text = new StringBuilder(256);
        private readonly StringBuilder _ctl = new StringBuilder(256);
        private readonly Vector3[] _corners = new Vector3[4];
        private readonly Desc _desc = new Desc();
        private UguiTextureCache _textures;
        private Selectable _hovered, _pressed;

        // ------------------------------------------------------------------ lifecycle

        private void OnEnable()
        {
            if (!Application.isPlaying)
                return;
            _canvas = GetComponent<Canvas>();
            _canvasRect = (RectTransform)transform;
            _textures = new UguiTextureCache();
            if (dumpExports)
            {
                _textures.DumpDirectory = System.IO.Path.Combine(Application.persistentDataPath, "HiccupUguiExports");
                Debug.Log("[Hiccup] uGUI mirror texture exports are written to " + _textures.DumpDirectory);
            }
            EnsureDocument();
            SetSourceHidden(hideSource);
            var _ = CanvasUpdateRegistry.instance;   // subscribes uGUI's layout rebuild ahead of us
            Canvas.willRenderCanvases += OnWillRenderCanvases;
            _doc.Created += Wire;
            if (_doc.IsCreated)
                Wire(_doc);
        }

        private void OnDisable()
        {
            Canvas.willRenderCanvases -= OnWillRenderCanvases;
            if (_doc != null)
            {
                _doc.Created -= Wire;
                RemoveHandlers();
            }
            SetSourceHidden(false);
            ClearNodes();
            _textures?.Dispose();
            _textures = null;
            if (_ownedDocument != null)
            {
                Destroy(_ownedDocument);
                _ownedDocument = null;
            }
            _doc = null;
            _wired = false;
        }

        private void EnsureDocument()
        {
            _doc = document;
            if (_doc != null)
            {
                _doc.ExtraCss = string.IsNullOrEmpty(_doc.ExtraCss) ? BuildCss() : _doc.ExtraCss + "\n" + BuildCss();
                return;
            }
            var go = new GameObject("uGUI Mirror (HTML)", typeof(RectTransform), typeof(Canvas), typeof(RawImage));
            go.SetActive(false);   // configure before OnEnable creates the browser-side panel
            var canvas = go.GetComponent<Canvas>();
            canvas.renderMode = RenderMode.ScreenSpaceOverlay;
            canvas.sortingOrder = _canvas.renderMode == RenderMode.ScreenSpaceOverlay ? _canvas.sortingOrder + 1 : short.MaxValue;
            go.GetComponent<RawImage>().raycastTarget = false;
            _doc = go.AddComponent<HtmlDocument>();
            _doc.PointerMode = HtmlPointerMode.Panel;
            _doc.BlockUnityInput = true;
            _doc.ExtraCss = BuildCss();
            go.AddComponent<HtmlScreenSurface>();
            _ownedDocument = go;
            go.SetActive(true);
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

        private void Wire(HtmlDocument doc)
        {
            doc.SetHtml("<div id=\"ugroot\" class=\"ug-root\"></div>");
            doc.Eval(ScrollScript);
            if (!_handlers)
            {
                _handlers = true;
                doc.On("click", OnClick);
                doc.On("input", OnInput);
                doc.On("change", OnChange);
                doc.On("ugscroll", OnScroll);
                doc.On("pointerover", OnPointerOver);
                doc.On("pointerdown", OnPointerDown);
                doc.On("pointerup", OnPointerUp);
                doc.On("pointerleave", OnPointerLeave);
            }
            ClearNodes();
            _rootStyle = null;
            _wired = true;
        }

        private void RemoveHandlers()
        {
            if (!_handlers)
                return;
            _handlers = false;
            _doc.Off("click", OnClick);
            _doc.Off("input", OnInput);
            _doc.Off("change", OnChange);
            _doc.Off("ugscroll", OnScroll);
            _doc.Off("pointerover", OnPointerOver);
            _doc.Off("pointerdown", OnPointerDown);
            _doc.Off("pointerup", OnPointerUp);
            _doc.Off("pointerleave", OnPointerLeave);
        }

        private void ClearNodes()
        {
            _nodes.Clear();
            _byElement.Clear();
            _viewports.Clear();
            _hovered = _pressed = null;
        }

        // The panel root only sees bubbling events and scroll does not bubble, so relay it as one that does,
        // carrying the offsets in a data attribute the event payload already forwards.
        private const string ScrollScript = @"
            root.addEventListener('scroll', function (e) {
                var t = e.target;
                if (!t || !t.dataset) return;
                t.dataset.scroll = Math.round(t.scrollTop) + ',' + Math.round(t.scrollLeft);
                t.dispatchEvent(new CustomEvent('ugscroll', { bubbles: true }));
            }, true);";

        // ------------------------------------------------------------------ per-frame sync

        private void OnWillRenderCanvases()
        {
            if (!_wired || _doc == null || !_doc.IsCreated || !isActiveAndEnabled || _textures == null)
                return;
            _frame++;
            _scrollWrites.Clear();

            SyncRoot();
            int order = 0;
            string prev = null;
            SyncNode(_canvasRect, null, "ugroot", ref order, ref prev, null);

            // Stale nodes are found by key: their RectTransform may already be destroyed, so it cannot be asked for its id.
            _stale.Clear();
            foreach (var kv in _nodes)
            {
                if (kv.Value.Visit != _frame)
                    _stale.Add(kv.Key);
            }
            foreach (var key in _stale)
            {
                var n = _nodes[key];
                _nodes.Remove(key);
                RemoveNode(n);
            }

            foreach (var n in _scrollWrites)
            {
                using (var el = _doc.Q("#" + n.Id))
                {
                    el.SetProperty("scrollLeft", F(n.ScrollPushed.x));
                    el.SetProperty("scrollTop", F(n.ScrollPushed.y));
                }
            }
        }

        /// <summary>Places the canvas rectangle in the document: screen pixels to CSS pixels, canvas units scaled to fit.</summary>
        private void SyncRoot()
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

            var sb = _style;
            sb.Clear();
            sb.Append("left:").Append(F(tl.x * css)).Append("px;top:").Append(F((Screen.height - tl.y) * css))
              .Append("px;width:").Append(F(r.width)).Append("px;height:").Append(F(r.height))
              .Append("px;transform:scale(").Append(F(sx)).Append(',').Append(F(sy)).Append(')');
            var style = sb.ToString();
            if (style == _rootStyle)
                return;
            _rootStyle = style;
            using (var el = _doc.Q("#ugroot"))
                el.SetAttribute("style", style);
        }

        private void SyncNode(RectTransform rt, Node parent, string parentId, ref int order, ref string prevSibling, StringBuilder emit)
        {
            var key = rt.GetEntityId();
            if (!_nodes.TryGetValue(key, out var node))
            {
                node = CreateNode(rt);
                _nodes[key] = node;
            }
            node.Visit = _frame;

            var d = _desc;
            d.Reset();
            Describe(node, parent, rt, d);

            bool recreate = !node.Created || node.ParentId != parentId || node.Order != order;
            if (emit != null)
            {
                EmitOpen(node, d, emit);
                Commit(node, d, parentId, order);
                SyncChildren(rt, node, emit);
                EmitClose(node, emit);
            }
            else if (recreate)
            {
                if (node.Created)
                {
                    using (var old = _doc.Q("#" + node.Id))
                    old.Remove();
                }
                var sb = _html;
                sb.Clear();
                EmitOpen(node, d, sb);
                Commit(node, d, parentId, order);
                SyncChildren(rt, node, sb);
                EmitClose(node, sb);
                var html = sb.ToString();
                if (prevSibling == null)
                {
                    using (var kids = _doc.Q("#" + (parentId == "ugroot" ? "ugroot" : parentId + "k")))
                        kids.Prepend(html);
                }
                else
                {
                    using (var before = _doc.Q("#" + prevSibling))
                        before.InsertHtml("afterend", html);
                }
            }
            else
            {
                Diff(node, d);
                Commit(node, d, parentId, order);
                SyncChildren(rt, node, null);
            }

            order++;
            prevSibling = node.Id;
        }

        private void SyncChildren(RectTransform rt, Node node, StringBuilder emit)
        {
            int order = 0;
            string prev = null;
            for (int i = 0; i < rt.childCount; i++)
            {
                var child = rt.GetChild(i) as RectTransform;
                if (child == null || !child.gameObject.activeSelf || child == node.SkipChild)
                    continue;
                if (child.GetComponent<HtmlScreenSurface>() != null)
                    continue;   // a document inside the canvas is not a picture to copy
                SyncNode(child, node, node.Id, ref order, ref prev, emit);
            }
        }

        private static void Commit(Node n, Desc d, string parentId, int order)
        {
            n.Tag = d.Tag; n.Class = d.Class; n.Style = d.Style; n.BgStyle = d.BgStyle; n.Text = d.Text; n.TextStyle = d.TextStyle;
            n.ControlHtml = d.ControlHtml; n.ControlStyle = d.ControlStyle; n.ControlValue = d.ControlValue;
            n.ControlChecked = d.ControlChecked; n.Disabled = d.Disabled;
            n.Left = d.Left; n.Top = d.Top;
            n.ParentId = parentId; n.Order = order; n.Created = true;
        }

        private void Diff(Node n, Desc d)
        {
            bool isButton = d.Tag == "button";   // a Button, or a Dropdown in uGUI-list mode
            if (d.Style != n.Style || d.Class != n.Class || d.Disabled != n.Disabled && isButton)
            {
                using (var el = _doc.Q("#" + n.Id))
                {
                    if (d.Style != n.Style)
                        el.SetAttribute("style", d.Style);
                    if (d.Class != n.Class)
                        el.SetAttribute("class", d.Class);
                    if (isButton && d.Disabled != n.Disabled)
                        el.Disabled = d.Disabled;
                }
            }
            if (n.HasBg && d.BgStyle != n.BgStyle)
            {
                using (var el = _doc.Q("#" + n.Id + "b"))
                    el.SetAttribute("style", d.BgStyle ?? "display:none");
            }
            if (n.HasText && (d.Text != n.Text || d.TextStyle != n.TextStyle))
            {
                using (var el = _doc.Q("#" + n.Id + "t"))
                {
                    if (d.TextStyle != n.TextStyle)
                        el.SetAttribute("style", d.TextStyle);
                    if (d.Text != n.Text)
                        el.InnerHtml = d.Text ?? string.Empty;
                }
            }
            if (n.ControlTag != null)
            {
                bool disabled = !isButton && d.Disabled != n.Disabled;
                if (d.ControlHtml != n.ControlHtml || d.ControlStyle != n.ControlStyle || d.ControlValue != n.ControlValue || d.ControlChecked != n.ControlChecked || disabled)
                {
                    using (var el = _doc.Q("#" + n.Id + "c"))
                    {
                        if (d.ControlHtml != n.ControlHtml)
                            el.InnerHtml = d.ControlHtml ?? string.Empty;
                        if (d.ControlStyle != n.ControlStyle)
                            el.SetAttribute("style", d.ControlStyle ?? string.Empty);
                        if (d.ControlValue != n.ControlValue)
                            el.SetProperty("value", d.ControlValue ?? string.Empty);
                        if (d.ControlChecked != n.ControlChecked)
                            el.Checked = d.ControlChecked;
                        if (disabled)
                            el.Disabled = d.Disabled;
                    }
                }
            }
        }

        /// <summary>Forgets a node already taken out of <see cref="_nodes"/> and removes its element.</summary>
        private void RemoveNode(Node n)
        {
            _byElement.Remove(n.Id);
            _byElement.Remove(n.Id + "b");
            _byElement.Remove(n.Id + "t");
            _byElement.Remove(n.Id + "c");
            _byElement.Remove(n.Id + "k");
            if (_hovered == n.Selectable)
                _hovered = null;
            if (_pressed == n.Selectable)
                _pressed = null;
            using (var el = _doc.Q("#" + n.Id))
                el.Remove();   // a no-op when it went with its parent
        }

        // ------------------------------------------------------------------ node creation

        private Node CreateNode(RectTransform rt)
        {
            var iid = rt.GetEntityId();
            var n = new Node { Rect = rt, Id = "ug" + (_nextId++).ToString(Inv) };
            n.Graphic = rt.GetComponent<Graphic>();
            n.HasText = n.Graphic is Text || n.Graphic is TMP_Text;
            n.HasBg = n.Graphic != null && !n.HasText;

            var sel = rt.GetComponent<Selectable>();
            n.Selectable = sel;
            string cid = n.Id + "c";
            switch (sel)
            {
                case InputField f:
                    n.Control = Control.InputField;
                    n.InputText = f.textComponent;
                    n.SkipChild = f.textComponent != null ? f.textComponent.rectTransform : null;
                    SetInputTag(n, cid, f.lineType != InputField.LineType.SingleLine, InputType(f.contentType), f.characterLimit, f.readOnly);
                    break;
                case TMP_InputField f:
                    n.Control = Control.InputField;
                    n.InputText = f.textComponent;
                    n.SkipChild = f.textComponent != null ? f.textComponent.rectTransform : null;
                    SetInputTag(n, cid, f.lineType != TMP_InputField.LineType.SingleLine, InputType(f.contentType), f.characterLimit, f.readOnly);
                    break;
                case Dropdown _:
                case TMP_Dropdown _:
                    n.Control = Control.Dropdown;
                    if (dropdownMode == DropdownMode.NativeSelect)
                    {
                        n.ControlTag = "select";
                        n.ControlOpen = "<select id=\"" + cid + "\" class=\"ug-ctl\"";
                        n.ControlClose = "</select>";
                    }
                    // Otherwise the node is a <button> that calls Show(); uGUI's own list is mirrored when it appears.
                    break;
                case Slider s:
                    n.Control = Control.Slider;
                    n.ControlTag = "input";
                    n.ControlOpen = "<input type=\"range\" id=\"" + cid + "\" class=\"ug-ctl\" min=\"" + F(s.minValue) + "\" max=\"" + F(s.maxValue) +
                                    "\" step=\"" + (s.wholeNumbers ? "1" : "any") + "\"";
                    break;
                case Toggle _:
                    n.Control = Control.Toggle;
                    n.ControlTag = "input";
                    n.ControlOpen = "<input type=\"checkbox\" id=\"" + cid + "\" class=\"ug-ctl\"";
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

            _byElement[n.Id] = n;
            _byElement[n.Id + "b"] = n;
            _byElement[n.Id + "t"] = n;
            _byElement[n.Id + "c"] = n;
            _byElement[n.Id + "k"] = n;
            return n;
        }

        private static void SetInputTag(Node n, string cid, bool multiline, string type, int limit, bool readOnly)
        {
            var sb = new StringBuilder(96);
            if (multiline)
            {
                n.ControlTag = "textarea";
                sb.Append("<textarea id=\"").Append(cid).Append("\" class=\"ug-input\"");
                n.ControlClose = "</textarea>";
            }
            else
            {
                n.ControlTag = "input";
                sb.Append("<input type=\"").Append(type).Append("\" id=\"").Append(cid).Append("\" class=\"ug-input\"");
                if (type == "text" && n.Selectable is InputField f && (f.contentType == InputField.ContentType.IntegerNumber || f.contentType == InputField.ContentType.DecimalNumber))
                    sb.Append(" inputmode=\"").Append(f.contentType == InputField.ContentType.IntegerNumber ? "numeric" : "decimal").Append('"');
            }
            if (limit > 0)
                sb.Append(" maxlength=\"").Append(limit).Append('"');
            if (readOnly)
                sb.Append(" readonly");
            sb.Append(" autocomplete=\"off\" spellcheck=\"false\"");
            n.ControlOpen = sb.ToString();
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

        private static string InputType(TMP_InputField.ContentType t)
        {
            switch (t)
            {
                case TMP_InputField.ContentType.Password: case TMP_InputField.ContentType.Pin: return "password";
                case TMP_InputField.ContentType.EmailAddress: return "email";
                default: return "text";
            }
        }

        // ------------------------------------------------------------------ describing a node

        private void Describe(Node node, Node parent, RectTransform rt, Desc d)
        {
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
                var desired = new Vector2(Mathf.Max(-left, 0f), Mathf.Max(-top, 0f));
                if (float.IsNaN(parent.ScrollPushed.x) || (desired - parent.ScrollPushed).sqrMagnitude > 0.6f)
                {
                    parent.ScrollPushed = desired;
                    _scrollWrites.Add(parent);
                }
            }

            var sb = _style;
            sb.Clear();
            sb.Append("left:").Append(F(cssLeft)).Append("px;top:").Append(F(cssTop)).Append("px;width:").Append(F(r.width)).Append("px;height:").Append(F(r.height)).Append("px;");

            // The canvas root's own scale (a CanvasScaler writes scaleFactor into its localScale) and rotation are
            // already accounted for by #ugroot, which maps the canvas rectangle onto the screen; only descendants
            // carry their transforms here.
            var s = parent != null ? rt.localScale : Vector3.one;
            float rot = parent != null ? rt.localEulerAngles.z : 0f;
            if (rot > 180f)
                rot -= 360f;
            if (Mathf.Abs(rot) > 0.001f || Mathf.Abs(s.x - 1f) > 0.0001f || Mathf.Abs(s.y - 1f) > 0.0001f)
            {
                sb.Append("transform-origin:").Append(F(rt.pivot.x * 100f)).Append("% ").Append(F((1f - rt.pivot.y) * 100f)).Append("%;transform:");
                if (Mathf.Abs(rot) > 0.001f)
                    sb.Append("rotate(").Append(F(-rot)).Append("deg) ");   // CSS turns clockwise, Unity anticlockwise
                if (Mathf.Abs(s.x - 1f) > 0.0001f || Mathf.Abs(s.y - 1f) > 0.0001f)
                    sb.Append("scale(").Append(F(s.x)).Append(',').Append(F(s.y)).Append(')');
                sb.Append(';');
            }

            // A nested Canvas that overrides sorting (a Dropdown's list and blocker, a popup) paints above later siblings.
            // (GetComponent returns a placeholder object for a missing component; a type pattern would match it.)
            var nested = parent != null ? rt.GetComponent<Canvas>() : null;
            if (nested != null && nested.overrideSorting)
                sb.Append("z-index:").Append(nested.sortingOrder).Append(';');

            string cls = "ug";
            var group = rt.GetComponent<CanvasGroup>();
            if (group != null)
            {
                float alpha = group == _group ? _groupAlpha : group.alpha;
                bool blocks = group == _group ? _groupBlocks : group.blocksRaycasts;
                if (alpha < 1f)
                    sb.Append("opacity:").Append(F(alpha)).Append(';');
                if (!group.interactable || !blocks)
                    cls += " ug-noinput";
            }

            bool clip = rt.GetComponent<RectMask2D>() != null;
            bool hideMaskGraphic = false;
            var mask = rt.GetComponent<Mask>();
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
                cls += " ug-scroll";
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
                        case Image img: d.BgStyle = ImageStyle(img, color); break;
                        case RawImage raw: d.BgStyle = RawImageStyle(node, raw, color); break;
                        default:
                            d.BgStyle = "display:none";
                            if (outlineUnsupported)
                                cls += " ug-unsupported";
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
                        TextDesc(t, color, rt, d, sb);
                    else if (g is TMP_Text tmp)
                        TmpDesc(tmp, color, rt, d, sb);
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
                    d.ControlValue = F(slider.value);
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
                    d.ControlHtml = OptionsHtml(sel, out int selected);
                    d.ControlValue = selected.ToString(Inv);
                    d.ControlStyle = SelectStyle(node);
                    break;
                case Control.InputField:
                    d.ControlValue = sel is InputField inf ? inf.text : ((TMP_InputField)sel).text;
                    d.ControlStyle = InputStyle(node, rt);
                    d.Disabled = !sel.IsInteractable();
                    break;
            }

            d.Class = cls;
            d.Style = sb.ToString();
        }

        private string OptionsHtml(Selectable sel, out int selected)
        {
            var sb = _ctl;
            sb.Clear();
            selected = 0;
            if (sel is Dropdown dd)
            {
                selected = dd.value;
                for (int i = 0; i < dd.options.Count; i++)
                    sb.Append("<option value=\"").Append(i).Append(i == dd.value ? "\" selected>" : "\">").Append(UguiRichText.Escape(dd.options[i].text)).Append("</option>");
            }
            else if (sel is TMP_Dropdown td)
            {
                selected = td.value;
                for (int i = 0; i < td.options.Count; i++)
                    sb.Append("<option value=\"").Append(i).Append(i == td.value ? "\" selected>" : "\">").Append(UguiRichText.Escape(td.options[i].text)).Append("</option>");
            }
            return sb.ToString();
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
            sb.Append("left:").Append(F(tl.x - r.xMin)).Append("px;top:").Append(F(r.yMax - tl.y)).Append("px;width:").Append(F(br.x - tl.x)).Append("px;height:").Append(F(tl.y - br.y)).Append("px;");
            switch (g)
            {
                case Text t:
                    AppendFont(sb, t.font != null ? t.font.name : null, t.fontSize, t.fontStyle == FontStyle.Bold || t.fontStyle == FontStyle.BoldAndItalic,
                        t.fontStyle == FontStyle.Italic || t.fontStyle == FontStyle.BoldAndItalic, t.color, HAlign(t.alignment));
                    break;
                case TMP_Text t:
                    AppendFont(sb, t.font != null ? StripSdf(t.font.name) : null, t.fontSize, (t.fontStyle & FontStyles.Bold) != 0,
                        (t.fontStyle & FontStyles.Italic) != 0, t.color, HAlign(t.horizontalAlignment));
                    break;
            }
            if (n.Selectable is InputField f && f.customCaretColor)
                sb.Append("caret-color:").Append(Rgba(f.caretColor)).Append(';');
            else if (n.Selectable is TMP_InputField tf && tf.customCaretColor)
                sb.Append("caret-color:").Append(Rgba(tf.caretColor)).Append(';');
            return sb.ToString();
        }

        /// <summary>Font and colours for a native select so its picker (Chrome's customisable select) matches the dropdown.</summary>
        private string SelectStyle(Node n)
        {
            var sb = _ctl;
            sb.Clear();
            Graphic caption = n.Selectable is Dropdown dd ? dd.captionText : (n.Selectable as TMP_Dropdown)?.captionText;
            if (caption == null)
                caption = null;   // a destroyed caption is Unity-null but would still match a type pattern
            switch (caption)
            {
                case Text t:
                    AppendFont(sb, t.font != null ? t.font.name : null, t.fontSize, t.fontStyle == FontStyle.Bold || t.fontStyle == FontStyle.BoldAndItalic,
                        t.fontStyle == FontStyle.Italic || t.fontStyle == FontStyle.BoldAndItalic, t.color, HAlign(t.alignment));
                    break;
                case TMP_Text t:
                    AppendFont(sb, t.font != null ? StripSdf(t.font.name) : null, t.fontSize, (t.fontStyle & FontStyles.Bold) != 0,
                        (t.fontStyle & FontStyles.Italic) != 0, t.color, HAlign(t.horizontalAlignment));
                    break;
            }
            if (n.Graphic != null)
                sb.Append("--ug-bg:").Append(Rgb(n.Graphic.color)).Append(';');
            if (caption != null)
                sb.Append("--ug-fg:").Append(Rgb(caption.color)).Append(';');
            return sb.ToString();
        }

        private void AppendFont(StringBuilder sb, string fontName, float size, bool bold, bool italic, Color color, string align)
        {
            sb.Append("font-family:").Append(FontFamily(fontName)).Append(";font-size:").Append(F(size)).Append("px;");
            if (bold)
                sb.Append("font-weight:bold;");
            if (italic)
                sb.Append("font-style:italic;");
            sb.Append("color:").Append(Rgba(color)).Append(";text-align:").Append(align).Append(';');
        }

        // ---- text

        private void TextDesc(Text t, Color color, RectTransform rt, Desc d, StringBuilder nodeStyle)
        {
            float size = t.fontSize;
            if (t.resizeTextForBestFit && t.cachedTextGenerator != null && t.cachedTextGenerator.fontSizeUsedForBestFit > 0)
                size = t.cachedTextGenerator.fontSizeUsedForBestFit / Mathf.Max(0.0001f, t.pixelsPerUnit);

            var ts = _text;
            ts.Clear();
            AppendFont(ts, t.font != null ? t.font.name : null, size, t.fontStyle == FontStyle.Bold || t.fontStyle == FontStyle.BoldAndItalic,
                t.fontStyle == FontStyle.Italic || t.fontStyle == FontStyle.BoldAndItalic, color, HAlign(t.alignment));
            // Follow Unity's wrapping decision rather than the browser's: its font is a little wider or narrower, and a
            // label uGUI fitted on one line would otherwise wrap its last word into a second line that is then clipped.
            int lines = t.cachedTextGenerator != null ? t.cachedTextGenerator.lineCount : 1;
            bool wrap = t.horizontalOverflow == HorizontalWrapMode.Wrap && lines > 1;
            ts.Append("white-space:").Append(wrap ? "pre-wrap" : "pre").Append(';');
            if (Mathf.Abs(t.lineSpacing - 1f) > 0.001f)
                ts.Append("line-height:").Append(F(1.2f * t.lineSpacing)).Append(';');
            AppendEffects(rt, ts);
            d.TextStyle = ts.ToString();
            d.Text = t.supportRichText ? UguiRichText.Convert(t.text) : UguiRichText.Escape(t.text);

            nodeStyle.Append("display:flex;align-items:").Append(VAlign(t.alignment)).Append(';');
            // Truncate clips vertically only; a slightly wider line may run past the rectangle instead of losing its end.
            if (t.verticalOverflow == VerticalWrapMode.Truncate)
                nodeStyle.Append("overflow-x:visible;overflow-y:clip;");
        }

        private void TmpDesc(TMP_Text t, Color color, RectTransform rt, Desc d, StringBuilder nodeStyle)
        {
            var ts = _text;
            ts.Clear();
            var fs = t.fontStyle;
            ts.Append("font-family:").Append(FontFamily(t.font != null ? StripSdf(t.font.name) : null)).Append(";font-size:").Append(F(t.fontSize)).Append("px;");
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
            ts.Append("color:").Append(Rgba(color)).Append(";text-align:").Append(HAlign(t.horizontalAlignment)).Append(';');
            int lines = t.textInfo != null ? t.textInfo.lineCount : 1;   // see TextDesc: wrap only where TMP wrapped
            bool wrap = t.textWrappingMode != TextWrappingModes.NoWrap && lines > 1;
            ts.Append("white-space:").Append(wrap ? "pre-wrap" : "pre").Append(';');
            if (Mathf.Abs(t.characterSpacing) > 0.001f)
                ts.Append("letter-spacing:").Append(F(t.characterSpacing * 0.01f)).Append("em;");
            if (Mathf.Abs(t.lineSpacing) > 0.001f)
                ts.Append("line-height:").Append(F(1.2f + t.lineSpacing * 0.01f)).Append(';');
            var m = t.margin;
            if (m != Vector4.zero)
                ts.Append("padding:").Append(F(m.y)).Append("px ").Append(F(m.z)).Append("px ").Append(F(m.w)).Append("px ").Append(F(m.x)).Append("px;");
            AppendEffects(rt, ts);
            d.TextStyle = ts.ToString();
            d.Text = t.richText ? UguiRichText.Convert(t.text) : UguiRichText.Escape(t.text);

            nodeStyle.Append("display:flex;align-items:").Append(VAlign(t.verticalAlignment)).Append(';');
            if (t.overflowMode != TextOverflowModes.Overflow)
                nodeStyle.Append("overflow-x:visible;overflow-y:clip;");
        }

        private static void AppendEffects(RectTransform rt, StringBuilder ts)
        {
            var outline = rt.GetComponent<Outline>();
            if (outline != null && outline.enabled)
            {
                string c = Rgba(outline.effectColor);
                string x = F(outline.effectDistance.x), y = F(-outline.effectDistance.y), nx = F(-outline.effectDistance.x), ny = F(outline.effectDistance.y);
                ts.Append("text-shadow:").Append(x).Append("px ").Append(y).Append("px 0 ").Append(c).Append(',')
                  .Append(nx).Append("px ").Append(y).Append("px 0 ").Append(c).Append(',')
                  .Append(x).Append("px ").Append(ny).Append("px 0 ").Append(c).Append(',')
                  .Append(nx).Append("px ").Append(ny).Append("px 0 ").Append(c).Append(';');
                return;
            }
            var shadow = rt.GetComponent<Shadow>();
            if (shadow != null && shadow.enabled)
                ts.Append("text-shadow:").Append(F(shadow.effectDistance.x)).Append("px ").Append(F(-shadow.effectDistance.y)).Append("px 0 ").Append(Rgba(shadow.effectColor)).Append(';');
        }

        // ---- images

        private string ImageStyle(Image img, Color color)
        {
            var bs = _bg;
            bs.Clear();
            if (color.a < 0.999f)
                bs.Append("opacity:").Append(F(color.a)).Append(';');
            var sprite = img.overrideSprite;
            string url = sprite != null ? SpriteUrl(sprite, color) : null;
            if (url == null)
            {
                bs.Append("background-color:").Append(Rgb(color)).Append(';');
                return bs.ToString();
            }
            float ppu = Mathf.Max(0.001f, img.pixelsPerUnit);
            switch (img.type)
            {
                case Image.Type.Sliced:
                {
                    // Composed in Unity at the element's device-pixel size, so the browser draws one bitmap: CSS
                    // border-image leaves hairline seams between slices at fractional pixel positions.
                    var rr = img.rectTransform.rect;
                    float scale = _rootScale;
                    int outW = Mathf.Max(1, Mathf.CeilToInt(rr.width * scale)), outH = Mathf.Max(1, Mathf.CeilToInt(rr.height * scale));
                    var b = sprite.border;   // x left, y bottom, z right, w top, in sprite pixels
                    float unitsPerPixel = 1f / Mathf.Max(0.001f, ppu * img.pixelsPerUnitMultiplier);
                    float l = b.x * unitsPerPixel * scale, bo = b.y * unitsPerPixel * scale, rt = b.z * unitsPerPixel * scale, t = b.w * unitsPerPixel * scale;
                    // Image.GetAdjustedBorders: borders that do not fit are scaled down together.
                    if (l + rt > outW && l + rt > 0f)
                    {
                        float f = outW / (l + rt);
                        l *= f;
                        rt *= f;
                    }
                    if (bo + t > outH && bo + t > 0f)
                    {
                        float f = outH / (bo + t);
                        bo *= f;
                        t *= f;
                    }
                    string sliced = _textures.SlicedDataUrl(sprite.texture, ToRectInt(SpriteRect(sprite)), b, outW, outH,
                        Mathf.RoundToInt(l), Mathf.RoundToInt(bo), Mathf.RoundToInt(rt), Mathf.RoundToInt(t), img.fillCenter, color);
                    if (sliced == null)
                        goto default;
                    bs.Append("background-image:url(").Append(sliced).Append(");background-size:100% 100%;");
                    break;
                }
                case Image.Type.Tiled:
                {
                    var tr = SpriteRect(sprite);
                    bs.Append("background-image:url(").Append(url).Append(");background-repeat:repeat;background-position:left bottom;background-size:")
                      .Append(F(tr.width / ppu)).Append("px ").Append(F(tr.height / ppu)).Append("px;");
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
            return bs.ToString();
        }

        private static void AppendFill(Image img, StringBuilder bs)
        {
            float a = Mathf.Clamp01(img.fillAmount);
            string rest = F((1f - a) * 100f);
            switch (img.fillMethod)
            {
                case Image.FillMethod.Horizontal:
                    bs.Append(img.fillOrigin == (int)Image.OriginHorizontal.Left ? "clip-path:inset(0 " + rest + "% 0 0);" : "clip-path:inset(0 0 0 " + rest + "%);");
                    break;
                case Image.FillMethod.Vertical:
                    bs.Append(img.fillOrigin == (int)Image.OriginVertical.Bottom ? "clip-path:inset(" + rest + "% 0 0 0);" : "clip-path:inset(0 0 " + rest + "% 0);");
                    break;
                case Image.FillMethod.Radial360:
                {
                    int from = img.fillOrigin == (int)Image.Origin360.Bottom ? 180 : img.fillOrigin == (int)Image.Origin360.Right ? 90 : img.fillOrigin == (int)Image.Origin360.Left ? 270 : 0;
                    string grad = img.fillClockwise
                        ? "conic-gradient(from " + from + "deg,#000 " + F(a * 360f) + "deg,transparent 0)"
                        : "conic-gradient(from " + from + "deg,transparent " + F((1f - a) * 360f) + "deg,#000 0)";
                    bs.Append("-webkit-mask-image:").Append(grad).Append(";mask-image:").Append(grad).Append(';');
                    break;
                }
                // Radial90 / Radial180: no equivalent here; the image shows unfilled.
            }
        }

        private string RawImageStyle(Node n, RawImage raw, Color color)
        {
            var bs = _bg;
            bs.Clear();
            if (color.a < 0.999f)
                bs.Append("opacity:").Append(F(color.a)).Append(';');
            var tex = raw.texture;
            if (tex == null)
            {
                bs.Append("background-color:").Append(Rgb(color)).Append(';');
                return bs.ToString();
            }
            if (tex is RenderTexture)
            {
                bool first = n.TextureTime == 0f;
                if (first || (renderTextureRefresh > 0f && Time.unscaledTime - n.TextureTime >= renderTextureRefresh))
                {
                    if (!first)
                        _textures.Invalidate(tex);
                    n.TextureTime = Mathf.Max(Time.unscaledTime, 0.0001f);
                }
            }
            string url = _textures.DataUrl(tex, new RectInt(0, 0, tex.width, tex.height), color);
            var uv = raw.uvRect;
            bs.Append("background-image:url(").Append(url).Append(");");
            if (uv.x == 0f && uv.y == 0f && uv.width == 1f && uv.height == 1f)
                bs.Append("background-size:100% 100%;");
            else
            {
                var rr = raw.rectTransform.rect;
                float bw = rr.width / Mathf.Max(0.0001f, uv.width), bh = rr.height / Mathf.Max(0.0001f, uv.height);
                bs.Append("background-size:").Append(F(bw)).Append("px ").Append(F(bh)).Append("px;background-position:")
                  .Append(F(-uv.x * bw)).Append("px ").Append(F(-(1f - uv.y - uv.height) * bh)).Append("px;");
            }
            return bs.ToString();
        }

        private static Rect SpriteRect(Sprite sprite)
        {
            try { return sprite.textureRect; }   // throws for tightly packed atlas sprites
            catch (Exception) { return sprite.rect; }
        }

        private static RectInt ToRectInt(Rect r) =>
            new RectInt(Mathf.RoundToInt(r.x), Mathf.RoundToInt(r.y), Mathf.Max(1, Mathf.RoundToInt(r.width)), Mathf.Max(1, Mathf.RoundToInt(r.height)));

        private string SpriteUrl(Sprite sprite, Color tint)
        {
            var tex = sprite.texture;
            if (tex == null)
                return null;
            return _textures.DataUrl(tex, ToRectInt(SpriteRect(sprite)), tint);
        }

        // ------------------------------------------------------------------ html emission

        private static void EmitOpen(Node n, Desc d, StringBuilder sb)
        {
            sb.Append('<').Append(d.Tag).Append(" id=\"").Append(n.Id).Append("\" class=\"").Append(d.Class).Append("\" style=\"").Append(d.Style).Append('"');
            if (d.Tag == "button")
            {
                sb.Append(" type=\"button\"");
                if (d.Disabled)
                    sb.Append(" disabled");
            }
            sb.Append('>');

            if (n.HasBg)
                sb.Append("<div id=\"").Append(n.Id).Append("b\" class=\"ug-bg\" style=\"").Append(d.BgStyle ?? "display:none").Append("\"></div>");

            if (n.ControlOpen != null)
            {
                sb.Append(n.ControlOpen);
                if (d.ControlStyle != null)
                    sb.Append(" style=\"").Append(d.ControlStyle).Append('"');
                if (d.Disabled)
                    sb.Append(" disabled");
                if (d.ControlChecked)
                    sb.Append(" checked");
                if (n.ControlTag == "input" && d.ControlValue != null)
                    sb.Append(" value=\"").Append(UguiRichText.Escape(d.ControlValue)).Append('"');
                sb.Append('>');
                if (n.ControlTag == "textarea")
                    sb.Append(UguiRichText.Escape(d.ControlValue));
                else if (d.ControlHtml != null)
                    sb.Append(d.ControlHtml);
                if (n.ControlClose != null)
                    sb.Append(n.ControlClose);
            }

            if (n.HasText)
                sb.Append("<span id=\"").Append(n.Id).Append("t\" class=\"ug-txt\" style=\"").Append(d.TextStyle).Append("\">").Append(d.Text).Append("</span>");
            sb.Append("<div id=\"").Append(n.Id).Append("k\" class=\"ug-kids\">");
        }

        private static void EmitClose(Node n, StringBuilder sb) => sb.Append("</div></").Append(n.Tag).Append('>');

        // ------------------------------------------------------------------ DOM -> uGUI

        private Node NodeFor(HtmlEvent e) => e.id != null && _byElement.TryGetValue(e.id, out var n) ? n : null;

        /// <summary>The event target's node, or the nearest ancestor node that satisfies <paramref name="pred"/>.</summary>
        private Node NodeOnPath(HtmlEvent e, Func<Node, bool> pred)
        {
            var n = NodeFor(e);
            if (n != null && pred(n))
                return n;
            if (string.IsNullOrEmpty(e.path))
                return null;
            foreach (var id in e.path.Split(' '))
            {
                if (_byElement.TryGetValue(id, out var p) && pred(p))
                    return p;
            }
            return null;
        }

        private void OnClick(HtmlEvent e)
        {
            var n = NodeOnPath(e, x => x.Control == Control.Button || (x.Control == Control.Dropdown && x.ControlTag == null));
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

        private void OnInput(HtmlEvent e)
        {
            var n = NodeFor(e);
            if (n == null)
                return;
            switch (n.Control)
            {
                case Control.Slider:
                    ((Slider)n.Selectable).value = e.ValueAsFloat;
                    n.ControlValue = F(((Slider)n.Selectable).value);
                    e.Handled = true;
                    break;
                case Control.InputField:
                    if (n.Selectable is InputField f)
                    {
                        f.text = e.value;
                        n.ControlValue = f.text;
                    }
                    else if (n.Selectable is TMP_InputField tf)
                    {
                        tf.text = e.value;
                        n.ControlValue = tf.text;
                    }
                    e.Handled = true;
                    break;
            }
        }

        private void OnChange(HtmlEvent e)
        {
            var n = NodeFor(e);
            if (n == null)
                return;
            switch (n.Control)
            {
                case Control.Toggle:
                    ((Toggle)n.Selectable).isOn = e.isChecked;
                    n.ControlChecked = e.isChecked;
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

        private void OnScroll(HtmlEvent e)
        {
            var n = NodeFor(e);
            if (n == null || n.Viewport == null || n.Viewport.content == null)
                return;
            var parts = (e.GetData("scroll") ?? string.Empty).Split(',');
            if (parts.Length < 2 || !float.TryParse(parts[0], NumberStyles.Float, Inv, out float top) || !float.TryParse(parts[1], NumberStyles.Float, Inv, out float left))
                return;
            n.ScrollPushed = new Vector2(left, top);
            if (!_nodes.TryGetValue(n.Viewport.content.GetEntityId(), out var content))
                return;
            var ap = n.Viewport.content.anchoredPosition;
            // Content top must end up at -scrollTop: CSS top grows downward, anchoredPosition.y upward.
            n.Viewport.content.anchoredPosition = new Vector2(ap.x - left - content.Left, ap.y + content.Top + top);
            n.Viewport.velocity = Vector2.zero;
            e.Handled = true;
        }

        private void OnPointerOver(HtmlEvent e)
        {
            var n = NodeOnPath(e, x => x.Selectable != null);
            var sel = n?.Selectable;
            if (sel == _hovered)
                return;
            if (_hovered != null)
                Pointer(_hovered, ExecuteEvents.pointerExitHandler);
            _hovered = sel;
            if (sel != null)
                Pointer(sel, ExecuteEvents.pointerEnterHandler);
        }

        private void OnPointerDown(HtmlEvent e)
        {
            var n = NodeOnPath(e, x => x.Selectable != null);
            if (n == null)
                return;
            _pressed = n.Selectable;
            Pointer(_pressed, ExecuteEvents.pointerDownHandler);
        }

        private void OnPointerUp(HtmlEvent e)
        {
            if (_pressed == null)
                return;
            Pointer(_pressed, ExecuteEvents.pointerUpHandler);
            _pressed = null;
        }

        private void OnPointerLeave(HtmlEvent e)
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

        // ------------------------------------------------------------------ css and formatting

        private string BuildCss()
        {
            var sb = new StringBuilder();
            if (fonts != null)
            {
                foreach (var f in fonts)
                {
                    if (f.file == null || string.IsNullOrEmpty(f.family))
                        continue;
                    var bytes = f.file.bytes;
                    if (bytes == null || bytes.Length < 4)
                        continue;
                    string mime = bytes[0] == 'w' && bytes[1] == 'O' && bytes[2] == 'F' && bytes[3] == '2' ? "font/woff2"
                        : bytes[0] == 'O' && bytes[1] == 'T' && bytes[2] == 'T' && bytes[3] == 'O' ? "font/otf" : "font/ttf";
                    sb.Append("@font-face{font-family:'").Append(f.family.Replace("'", string.Empty)).Append("';src:url(data:").Append(mime)
                      .Append(";base64,").Append(Convert.ToBase64String(bytes)).Append(")}\n");
                }
            }
            sb.Append(BaseCss);
            return sb.ToString();
        }

        private const string BaseCss = @"
.ug-root{position:absolute;left:0;top:0;transform-origin:0 0;overflow:hidden}
.ug,.ug-bg,.ug-kids{position:absolute;box-sizing:border-box;margin:0;padding:0}
.ug{left:0;top:0;pointer-events:none;overflow:visible}
.ug-bg{inset:0;background-repeat:no-repeat;pointer-events:none}
.ug-kids{inset:0;overflow:visible;pointer-events:none}
.ug-txt{display:block;width:100%;overflow-wrap:break-word;pointer-events:none}
button.ug{background:none;border:0;color:inherit;font:inherit;text-align:inherit;pointer-events:auto;cursor:pointer;appearance:none;-webkit-appearance:none}
button.ug:disabled{cursor:default}
.ug-ctl{position:absolute;inset:0;width:100%;height:100%;margin:0;opacity:0;pointer-events:auto;cursor:pointer}
.ug-ctl:disabled{cursor:default}
select.ug-ctl{appearance:base-select;-webkit-appearance:base-select}
select.ug-ctl::picker(select){appearance:base-select;background:var(--ug-bg,#1a1f2e);color:var(--ug-fg,#fff);border:1px solid rgba(255,255,255,.14);border-radius:8px;padding:4px;margin-top:4px;font:inherit;box-shadow:0 8px 24px rgba(0,0,0,.45)}
select.ug-ctl::picker-icon{display:none}
select.ug-ctl option{padding:6px 10px;border-radius:5px;font:inherit}
select.ug-ctl option:hover{background:rgba(255,255,255,.1)}
select.ug-ctl option:checked{background:rgba(255,255,255,.18)}
select.ug-ctl option::checkmark{display:none}
.ug-input{position:absolute;background:transparent;border:0;outline:0;margin:0;padding:0;resize:none;pointer-events:auto;overflow:hidden}
.ug-scroll{pointer-events:auto;scrollbar-width:none}
.ug-scroll::-webkit-scrollbar{display:none}
.ug-noinput *{pointer-events:none!important}
.ug:has(>.ug-ctl:focus-visible),button.ug:focus-visible{outline:2px solid Highlight;outline-offset:2px}
.ug-unsupported{outline:1px dashed rgba(255,0,255,.6);outline-offset:-1px}
";

        private string FontFamily(string unityFont)
        {
            if (string.IsNullOrEmpty(unityFont))
                return fallbackFonts;
            // Unity's built-in LegacyRuntime is Liberation Sans, which shares Arial's metrics; prefer those so line
            // widths match what uGUI measured before falling back to the UI font stack.
            if (unityFont == "LegacyRuntime" || unityFont == "Arial")
                return "'Liberation Sans', Arial, Helvetica, " + fallbackFonts;
            return "'" + unityFont.Replace("'", string.Empty) + "', " + fallbackFonts;
        }

        private static string StripSdf(string fontAsset)
        {
            if (string.IsNullOrEmpty(fontAsset))
                return fontAsset;
            int i = fontAsset.IndexOf(" SDF", StringComparison.OrdinalIgnoreCase);
            return i > 0 ? fontAsset.Substring(0, i) : fontAsset;
        }

        private static string HAlign(TextAnchor a)
        {
            switch (a)
            {
                case TextAnchor.UpperCenter: case TextAnchor.MiddleCenter: case TextAnchor.LowerCenter: return "center";
                case TextAnchor.UpperRight: case TextAnchor.MiddleRight: case TextAnchor.LowerRight: return "right";
                default: return "left";
            }
        }

        private static string VAlign(TextAnchor a)
        {
            switch (a)
            {
                case TextAnchor.MiddleLeft: case TextAnchor.MiddleCenter: case TextAnchor.MiddleRight: return "center";
                case TextAnchor.LowerLeft: case TextAnchor.LowerCenter: case TextAnchor.LowerRight: return "flex-end";
                default: return "flex-start";
            }
        }

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

        private static string F(float v) => v.ToString("0.##", Inv);

        private static string Rgba(Color c) =>
            "rgba(" + Mathf.RoundToInt(Mathf.Clamp01(c.r) * 255f) + "," + Mathf.RoundToInt(Mathf.Clamp01(c.g) * 255f) + "," +
            Mathf.RoundToInt(Mathf.Clamp01(c.b) * 255f) + "," + Mathf.Clamp01(c.a).ToString("0.###", Inv) + ")";

        private static string Rgb(Color c) =>
            "rgb(" + Mathf.RoundToInt(Mathf.Clamp01(c.r) * 255f) + "," + Mathf.RoundToInt(Mathf.Clamp01(c.g) * 255f) + "," + Mathf.RoundToInt(Mathf.Clamp01(c.b) * 255f) + ")";
    }
}
