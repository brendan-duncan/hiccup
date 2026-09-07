using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using UnityEngine;
using UnityEngine.UI;

namespace Hiccup.Mirror
{
    /// <summary>
    /// The DOM side of a mirror: a component that copies a Unity UI tree into an <see cref="HtmlDocument"/> every
    /// frame so the browser draws and hit-tests it as real DOM. Subclasses walk their own source tree (a uGUI
    /// Canvas, a UI Toolkit panel) and describe each node; this class owns the document, turns descriptions into
    /// HTML, diffs them against what the DOM already shows, exports textures, embeds fonts and routes DOM events
    /// back to the subclass.
    /// </summary>
    /// <remarks>
    /// Every source element becomes one absolutely positioned element with the rectangle Unity computed for it, so
    /// there is exactly one layout engine. The element holds, in paint order, a background (<c>.ug-bg</c>), an
    /// optional native control (<c>.ug-ctl</c>), a text span (<c>.ug-txt</c>) and a container for the children
    /// (<c>.ug-kids</c>). The tree is diffed once per frame: a node that is new or reordered is emitted as an HTML
    /// subtree, an existing node gets only the attribute writes whose strings changed, and nodes not visited are
    /// removed.
    /// </remarks>
    public abstract class HtmlMirror : MonoBehaviour
    {
        [Serializable]
        public struct FontFace
        {
            [Tooltip("The font-family to register: the Unity Font name, the font asset's face name, or the TMP font asset name without ' SDF'.")]
            public string family;
            [Tooltip("A TTF, OTF or WOFF2 file imported as a TextAsset (rename the file to .bytes).")]
            public TextAsset file;
        }

        [Tooltip("Document to mirror into. Leave empty to create a full-screen overlay document automatically.")]
        [SerializeField] protected HtmlDocument document;
        [Tooltip("Hide the source UI so only the HTML copy is visible and interactive. It keeps laying out and running underneath.")]
        [SerializeField] protected bool hideSource = true;
        [Tooltip("CSS font-family list appended after each Unity font name.")]
        [SerializeField] protected string fallbackFonts = "system-ui, 'Segoe UI', Roboto, Helvetica, Arial, sans-serif";
        [Tooltip("Web fonts to embed so text uses the same faces as the Unity fonts.")]
        [SerializeField] protected FontFace[] fonts;
        [Tooltip("How often an element showing a RenderTexture is re-exported, in seconds. 0 exports it once.")]
        [SerializeField] protected float renderTextureRefresh = 0.5f;
        [Tooltip("Draw a dashed outline where an element has no HTML equivalent (custom meshes, vector images, unknown types).")]
        [SerializeField] protected bool outlineUnsupported = true;
        [Tooltip("Also write every exported sprite/texture PNG to a folder under persistentDataPath, to check what the page receives.")]
        [SerializeField] protected bool dumpExports;

        /// <summary>The document the source is mirrored into.</summary>
        public HtmlDocument Document => _doc;
        /// <summary>Mirrored source elements.</summary>
        public abstract int NodeCount { get; }
        /// <summary>PNGs exported for sprites and textures so far.</summary>
        public int TextureCount => _textures?.Count ?? 0;

        /// <summary>What a node's elements should show this frame; a node keeps the copy the DOM currently shows.</summary>
        protected struct Desc
        {
            public string Tag, Class, Style, BgStyle, Text, TextStyle, ControlHtml, ControlStyle, ControlValue;
            public bool ControlChecked, Disabled;
            public float Left, Top;            // raw geometry, used for scroll write-back

            public void Reset()
            {
                Tag = "div"; Class = null; Style = BgStyle = Text = TextStyle = ControlHtml = ControlStyle = ControlValue = null;
                ControlChecked = Disabled = false; Left = Top = 0f;
            }
        }

        /// <summary>The DOM-side state of one mirrored element. Subclasses add the source object and whatever they read from it.</summary>
        protected class MirrorNode
        {
            public string Id;                  // element id; b/t/c/k suffixes name the background, text, control and children elements
            public string ParentId;
            public int Order;
            public int Visit;
            public bool Created;
            public bool IsSurface;             // an HtmlScreenSurface inside the source: a document, not a picture to copy

            public bool HasBg, HasText;
            public string ControlTag, ControlOpen, ControlClose;
            public Vector2 ScrollPushed = new Vector2(float.NaN, float.NaN);
            public float TextureTime;

            // The Unity string the last text conversion came from, so it is redone only when the text changes.
            public string TextSource;
            public bool TextRich;
            public string TextHtml;

            public Desc Last;                  // what the DOM shows
        }

        protected static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

        private GameObject _ownedDocument;
        private HtmlDocument _doc;
        private bool _wired, _handlers;
        private int _frame;
        private string _rootStyle;
        private string _externalCss;     // an external document's ExtraCss before the mirror appended its own, put back on disable
        private MirrorTextureCache _textures;
        private readonly Dictionary<string, MirrorNode> _byElement = new Dictionary<string, MirrorNode>();   // by element id without suffix
        private readonly Dictionary<UnityEngine.Object, string> _families = new Dictionary<UnityEngine.Object, string>();   // CSS font-family per Unity font
        private int _nextId = 1;   // element ids are sequential
        private readonly List<MirrorNode> _scrollWrites = new List<MirrorNode>();
        private readonly StringBuilder _html = new StringBuilder(4096);
        private char[] _cmp = new char[512];   // scratch for comparing a builder against last frame's string

        /// <summary>Source units to device pixels, so composed images come out crisp. Set by <see cref="DescribeRoot"/>.</summary>
        protected float _rootScale = 1f;
        protected readonly StringBuilder _style = new StringBuilder(256);
        protected readonly StringBuilder _bg = new StringBuilder(256);
        protected readonly StringBuilder _text = new StringBuilder(256);
        protected readonly StringBuilder _ctl = new StringBuilder(256);
        protected readonly StringBuilder _cls = new StringBuilder(32);
        protected Desc _desc;

        protected HtmlDocument Doc => _doc;
        private protected MirrorTextureCache Textures => _textures;
        protected int Frame => _frame;
        protected bool IsWired => _wired && _doc != null && _doc.IsCreated && _textures != null;

        // ------------------------------------------------------------------ subclass hooks

        /// <summary>Folder name under persistentDataPath for <c>Dump Exports</c>.</summary>
        protected abstract string DumpFolder { get; }
        /// <summary>Sorting order for the overlay canvas the mirror creates when no document is assigned.</summary>
        protected abstract int OverlaySortingOrder { get; }
        /// <summary>Appends the CSS placing the source's root rectangle in the document (left/top/width/height/transform) to <paramref name="sb"/>, and sets <see cref="_rootScale"/>. Returns false when there is nothing to mirror yet.</summary>
        protected abstract bool DescribeRoot(StringBuilder sb);
        /// <summary>Walks the source tree, calling <see cref="SyncNode"/> for each element.</summary>
        protected abstract void SyncTree();
        /// <summary>Removes every node whose <see cref="MirrorNode.Visit"/> is not <paramref name="frame"/> from the subclass's table, passing each to <see cref="RemoveNode"/>.</summary>
        protected abstract void RemoveStale(int frame);
        /// <summary>Fills <paramref name="d"/> with what the node's elements should show this frame.</summary>
        protected abstract void Describe(MirrorNode node, MirrorNode parent, ref Desc d);
        /// <summary>Calls <see cref="SyncNode"/> for each child of the node, in order.</summary>
        protected abstract void SyncChildren(MirrorNode node, StringBuilder emit);

        protected virtual void OnClick(HtmlEvent e) { }
        protected virtual void OnInput(HtmlEvent e) { }
        protected virtual void OnChange(HtmlEvent e) { }
        protected virtual void OnPointerOver(HtmlEvent e) { }
        protected virtual void OnPointerDown(HtmlEvent e) { }
        protected virtual void OnPointerUp(HtmlEvent e) { }
        protected virtual void OnPointerLeave(HtmlEvent e) { }
        /// <summary>The browser scrolled a viewport element to the given offsets.</summary>
        protected virtual void OnScroll(MirrorNode viewport, float left, float top) { }

        // ------------------------------------------------------------------ lifecycle

        protected virtual void OnEnable()
        {
            if (!Application.isPlaying)
                return;
            _textures = new MirrorTextureCache();
            if (dumpExports)
            {
                _textures.DumpDirectory = System.IO.Path.Combine(Application.persistentDataPath, DumpFolder);
                Debug.Log("[Hiccup] Mirror texture exports are written to " + _textures.DumpDirectory);
            }
            EnsureDocument();
            _doc.Created += Wire;
            if (_doc.IsCreated)
                Wire(_doc);
        }

        protected virtual void OnDisable()
        {
            if (_doc != null)
            {
                _doc.Created -= Wire;
                RemoveHandlers();
                if (_ownedDocument == null)
                    _doc.ExtraCss = _externalCss;   // otherwise every enable would append another copy of the fonts and base CSS
            }
            ClearNodes();
            _families.Clear();
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
                _externalCss = _doc.ExtraCss;
                _doc.ExtraCss = string.IsNullOrEmpty(_externalCss) ? BuildCss() : _externalCss + "\n" + BuildCss();
                return;
            }
            var go = new GameObject(GetType().Name + " (HTML)", typeof(RectTransform), typeof(Canvas), typeof(RawImage));
            go.SetActive(false);   // configure before OnEnable creates the browser-side panel
            var canvas = go.GetComponent<Canvas>();
            canvas.renderMode = RenderMode.ScreenSpaceOverlay;
            canvas.sortingOrder = OverlaySortingOrder;
            go.GetComponent<RawImage>().raycastTarget = false;
            _doc = go.AddComponent<HtmlDocument>();
            _doc.PointerMode = HtmlPointerMode.Panel;
            _doc.BlockUnityInput = true;
            _doc.ExtraCss = BuildCss();
            go.AddComponent<HtmlScreenSurface>();
            _ownedDocument = go;
            go.SetActive(true);
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
                doc.OnMessage("ugscroll", OnScrollMessage);
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
            _doc.OffMessage("ugscroll", OnScrollMessage);
            _doc.Off("pointerover", OnPointerOver);
            _doc.Off("pointerdown", OnPointerDown);
            _doc.Off("pointerup", OnPointerUp);
            _doc.Off("pointerleave", OnPointerLeave);
        }

        /// <summary>Forgets every node. Subclasses clear their own table and call this.</summary>
        protected virtual void ClearNodes()
        {
            _byElement.Clear();
        }

        // The panel root only sees bubbling events and scroll does not bubble, so a capture-phase listener
        // sends the viewport's id and offsets to C# as a message instead.
        private const string ScrollScript = @"
            root.addEventListener('scroll', function (e) {
                var t = e.target;
                if (!t || !t.id) return;
                HUI.send('ugscroll', t.id + ',' + Math.round(t.scrollTop) + ',' + Math.round(t.scrollLeft));
            }, true);";

        // ------------------------------------------------------------------ per-frame sync

        /// <summary>Runs one sync pass: root placement, the tree walk, stale removal and queued scroll writes.</summary>
        protected void Sync()
        {
            if (!IsWired || !isActiveAndEnabled)
                return;
            _frame++;
            _scrollWrites.Clear();

            var sb = _style;
            sb.Clear();
            if (!DescribeRoot(sb))
                return;
            var style = Take(sb, _rootStyle);
            if (!ReferenceEquals(style, _rootStyle))
            {
                _rootStyle = style;
                using (var el = _doc.Q("#ugroot"))
                    el.SetAttribute("style", style);
            }

            SyncTree();
            RemoveStale(_frame);

            foreach (var n in _scrollWrites)
            {
                using (var el = _doc.Q("#" + n.Id))
                {
                    el.SetProperty("scrollLeft", F(n.ScrollPushed.x));
                    el.SetProperty("scrollTop", F(n.ScrollPushed.y));
                }
            }
            _textures.EndSync();
        }

        /// <summary>Gives a new node its element id and registers it for event lookup.</summary>
        protected void Register(MirrorNode n)
        {
            n.Id = "ug" + (_nextId++).ToString(Inv);
            _byElement[n.Id] = n;
        }

        /// <summary>
        /// Syncs one node: describes it, then either emits it into <paramref name="emit"/> (when a parent is being
        /// emitted), recreates it in place (new, reparented or reordered) or diffs it against the DOM. Then syncs
        /// its children the same way.
        /// </summary>
        protected void SyncNode(MirrorNode node, MirrorNode parent, string parentId, ref int order, ref string prevSibling, StringBuilder emit)
        {
            node.Visit = _frame;
            if (node.IsSurface)
                return;   // a document inside the source is not a picture to copy; the node only marks it as seen

            // One description is shared by the whole walk: nothing reads it after the children are synced.
            ref Desc d = ref _desc;
            d.Reset();
            Describe(node, parent, ref d);

            bool recreate = !node.Created || node.ParentId != parentId || node.Order != order;
            if (emit != null)
            {
                EmitOpen(node, in d, emit);
                Commit(node, in d, parentId, order);
                SyncChildren(node, emit);
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
                EmitOpen(node, in d, sb);
                Commit(node, in d, parentId, order);
                SyncChildren(node, sb);
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
                Diff(node, in d);
                Commit(node, in d, parentId, order);
                SyncChildren(node, null);
            }

            order++;
            prevSibling = node.Id;
        }

        private static void Commit(MirrorNode n, in Desc d, string parentId, int order)
        {
            n.Last = d;
            n.ParentId = parentId; n.Order = order; n.Created = true;
        }

        /// <summary>Writes whatever differs between the description and what the node's elements show. Strings that did not change are the same instance, so most comparisons are a reference check.</summary>
        private void Diff(MirrorNode n, in Desc d)
        {
            ref Desc last = ref n.Last;
            bool isButton = d.Tag == "button";
            if (d.Style != last.Style || d.Class != last.Class || (isButton && d.Disabled != last.Disabled))
            {
                using (var el = _doc.Q("#" + n.Id))
                {
                    if (d.Style != last.Style)
                        el.SetAttribute("style", d.Style);
                    if (d.Class != last.Class)
                        el.SetAttribute("class", d.Class);
                    if (isButton && d.Disabled != last.Disabled)
                        el.Disabled = d.Disabled;
                }
            }
            if (n.HasBg && d.BgStyle != last.BgStyle)
            {
                using (var el = _doc.Q("#" + n.Id + "b"))
                    el.SetAttribute("style", d.BgStyle ?? "display:none");
            }
            if (n.HasText && (d.Text != last.Text || d.TextStyle != last.TextStyle))
            {
                using (var el = _doc.Q("#" + n.Id + "t"))
                {
                    if (d.TextStyle != last.TextStyle)
                        el.SetAttribute("style", d.TextStyle);
                    if (d.Text != last.Text)
                        el.InnerHtml = d.Text ?? string.Empty;
                }
            }
            if (n.ControlTag != null)
            {
                bool disabled = !isButton && d.Disabled != last.Disabled;
                if (d.ControlHtml != last.ControlHtml || d.ControlStyle != last.ControlStyle || d.ControlValue != last.ControlValue || d.ControlChecked != last.ControlChecked || disabled)
                {
                    using (var el = _doc.Q("#" + n.Id + "c"))
                    {
                        if (d.ControlHtml != last.ControlHtml)
                            el.InnerHtml = d.ControlHtml ?? string.Empty;
                        if (d.ControlStyle != last.ControlStyle)
                            el.SetAttribute("style", d.ControlStyle ?? string.Empty);
                        if (d.ControlValue != last.ControlValue)
                            el.SetProperty("value", d.ControlValue ?? string.Empty);
                        if (d.ControlChecked != last.ControlChecked)
                            el.Checked = d.ControlChecked;
                        if (disabled)
                            el.Disabled = d.Disabled;
                    }
                }
            }
        }

        /// <summary>Forgets a node already taken out of the subclass's table and removes its element.</summary>
        protected virtual void RemoveNode(MirrorNode n)
        {
            _byElement.Remove(n.Id);
            if (!n.Created)
                return;
            using (var el = _doc.Q("#" + n.Id))
                el.Remove();   // a no-op when it went with its parent
        }

        /// <summary>Queues a scrollTop/scrollLeft write for a viewport node when the wanted offset moved by more than a fraction of a pixel.</summary>
        protected void PushScroll(MirrorNode viewport, Vector2 offset)
        {
            if (float.IsNaN(viewport.ScrollPushed.x) || (offset - viewport.ScrollPushed).sqrMagnitude > 0.6f)
            {
                viewport.ScrollPushed = offset;
                _scrollWrites.Add(viewport);
            }
        }

        /// <summary>
        /// Notes that a RenderTexture-backed node is due for a fresh export: the first time, and then every
        /// <c>Render Texture Refresh</c> seconds, when the cached export is dropped.
        /// </summary>
        protected void RefreshRenderTexture(MirrorNode n, Texture tex)
        {
            bool first = n.TextureTime == 0f;
            if (first || (renderTextureRefresh > 0f && Time.unscaledTime - n.TextureTime >= renderTextureRefresh))
            {
                if (!first)
                    _textures.Invalidate(tex);
                n.TextureTime = Mathf.Max(Time.unscaledTime, 0.0001f);
            }
        }

        // ------------------------------------------------------------------ html emission

        private static void EmitOpen(MirrorNode n, in Desc d, StringBuilder sb)
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
                    sb.Append(" value=\"").Append(MirrorRichText.Escape(d.ControlValue)).Append('"');
                sb.Append('>');
                if (n.ControlTag == "textarea")
                    sb.Append(MirrorRichText.Escape(d.ControlValue));
                else if (d.ControlHtml != null)
                    sb.Append(d.ControlHtml);
                if (n.ControlClose != null)
                    sb.Append(n.ControlClose);
            }

            if (n.HasText)
                sb.Append("<span id=\"").Append(n.Id).Append("t\" class=\"ug-txt\" style=\"").Append(d.TextStyle).Append("\">").Append(d.Text).Append("</span>");
            sb.Append("<div id=\"").Append(n.Id).Append("k\" class=\"ug-kids\">");
        }

        private static void EmitClose(MirrorNode n, StringBuilder sb) => sb.Append("</div></").Append(n.Last.Tag).Append('>');

        /// <summary>Sets a node's native control markup: the open tag without its closing '&gt;', and the close tag for container controls.</summary>
        protected static void SetControl(MirrorNode n, string tag, string open, string close)
        {
            n.ControlTag = tag;
            n.ControlOpen = open;
            n.ControlClose = close;
        }

        /// <summary>The open tag of a text input or textarea for a node, with the attributes that are read once.</summary>
        protected static void SetInputTag(MirrorNode n, bool multiline, string type, string inputMode, int limit, bool readOnly, string placeholder)
        {
            string cid = n.Id + "c";
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
                if (inputMode != null)
                    sb.Append(" inputmode=\"").Append(inputMode).Append('"');
            }
            if (limit > 0)
                sb.Append(" maxlength=\"").Append(limit).Append('"');
            if (readOnly)
                sb.Append(" readonly");
            if (!string.IsNullOrEmpty(placeholder))
                sb.Append(" placeholder=\"").Append(MirrorRichText.Escape(placeholder)).Append('"');
            sb.Append(" autocomplete=\"off\" spellcheck=\"false\"");
            n.ControlOpen = sb.ToString();
        }

        protected static void AppendOption(StringBuilder sb, int index, bool selected, string text)
        {
            sb.Append("<option value=\"").Append(index).Append(selected ? "\" selected>" : "\">");
            MirrorRichText.Escape(text, sb);
            sb.Append("</option>");
        }

        // ------------------------------------------------------------------ DOM -> source

        /// <summary>The node an element id belongs to: the node's own id, or that id plus the b/t/c/k suffix of one of its parts.</summary>
        protected MirrorNode NodeFor(string id)
        {
            if (string.IsNullOrEmpty(id))
                return null;
            if (_byElement.TryGetValue(id, out var n))
                return n;
            char last = id[id.Length - 1];
            if (last >= '0' && last <= '9')
                return null;
            return _byElement.TryGetValue(id.Substring(0, id.Length - 1), out n) ? n : null;
        }

        protected T NodeFor<T>(HtmlEvent e) where T : MirrorNode => NodeFor(e.id) as T;

        /// <summary>The event target's node, or the nearest ancestor node that satisfies <paramref name="pred"/>.</summary>
        protected T NodeOnPath<T>(HtmlEvent e, Func<T, bool> pred) where T : MirrorNode
        {
            var n = NodeFor(e.id) as T;
            if (n != null && pred(n))
                return n;
            if (string.IsNullOrEmpty(e.path))
                return null;
            foreach (var id in e.path.Split(' '))
            {
                if (NodeFor(id) is T p && pred(p))
                    return p;
            }
            return null;
        }

        private void OnScrollMessage(HtmlMessage m)
        {
            // "<viewport id>,<scrollTop>,<scrollLeft>"
            var parts = m.Data.Split(',');
            if (parts.Length < 3)
                return;
            var n = NodeFor(parts[0]);
            if (n == null)
                return;
            if (!float.TryParse(parts[1], NumberStyles.Float, Inv, out float top) || !float.TryParse(parts[2], NumberStyles.Float, Inv, out float left))
                return;
            n.ScrollPushed = new Vector2(left, top);
            OnScroll(n, left, top);
            m.Handled = true;
        }

        // ------------------------------------------------------------------ css

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
            sb.Append(ExtraCss);
            return sb.ToString();
        }

        /// <summary>CSS a subclass appends after the shared rules.</summary>
        protected virtual string ExtraCss => string.Empty;

        private const string BaseCss = @"
.ug-root{position:absolute;left:0;top:0;transform-origin:0 0;overflow:hidden}
.ug,.ug-bg,.ug-kids{position:absolute;box-sizing:border-box;margin:0;padding:0}
.ug{left:0;top:0;pointer-events:none;overflow:visible}
.ug-bg{inset:0;background-repeat:no-repeat;pointer-events:none}
.ug-kids{inset:0;overflow:visible;pointer-events:none}
.ug-txt{display:block;position:relative;width:100%;overflow-wrap:break-word;pointer-events:none}
button.ug{background:none;border:0;color:inherit;font:inherit;text-align:inherit;pointer-events:auto;cursor:pointer;appearance:none;-webkit-appearance:none}
button.ug:disabled{cursor:default}
.ug-ctl{position:absolute;inset:0;width:100%;height:100%;margin:0;opacity:0;pointer-events:auto;cursor:pointer}
.ug-ctl:disabled{cursor:default}
button.ug-ctl{appearance:none;-webkit-appearance:none;border:0;background:none;padding:0}
select.ug-ctl{appearance:base-select;-webkit-appearance:base-select}
select.ug-ctl::picker(select){appearance:base-select;background:var(--ug-bg,#1a1f2e);color:var(--ug-fg,#fff);border:1px solid rgba(255,255,255,.14);border-radius:8px;padding:4px;margin-top:4px;font:inherit;box-shadow:0 8px 24px rgba(0,0,0,.45)}
select.ug-ctl::picker-icon{display:none}
select.ug-ctl option{padding:6px 10px;border-radius:5px;font:inherit}
select.ug-ctl option:hover{background:rgba(255,255,255,.1)}
select.ug-ctl option:checked{background:rgba(255,255,255,.18)}
select.ug-ctl option::checkmark{display:none}
.ug-input{position:absolute;background:transparent;border:0;outline:0;margin:0;padding:0;resize:none;pointer-events:auto;overflow:hidden}
.ug-input::placeholder{color:inherit;opacity:.5}
.ug-scroll{pointer-events:auto;scrollbar-width:none}
.ug-scroll::-webkit-scrollbar{display:none}
.ug-noinput *{pointer-events:none!important}
.ug:has(>.ug-ctl:focus-visible),button.ug:focus-visible{outline:2px solid Highlight;outline-offset:2px}
.ug-unsupported{outline:1px dashed rgba(255,0,255,.6);outline-offset:-1px}
";

        // ------------------------------------------------------------------ fonts

        /// <summary>
        /// The CSS font-family list for a Unity font object, built once per object: <paramref name="nameOf"/> is
        /// asked for the family name only on the first sight, since reading a Unity object's name allocates a
        /// string every time.
        /// </summary>
        protected string Family(UnityEngine.Object font, Func<UnityEngine.Object, string> nameOf)
        {
            if (font == null)
                return fallbackFonts;
            if (!_families.TryGetValue(font, out var family))
            {
                family = FontFamily(nameOf(font));
                _families[font] = family;
            }
            return family;
        }

        protected string FontFamily(string unityFont)
        {
            if (string.IsNullOrEmpty(unityFont))
                return fallbackFonts;
            // Unity's built-in LegacyRuntime is Liberation Sans, which shares Arial's metrics; prefer those so line
            // widths match what Unity measured before falling back to the UI font stack.
            if (unityFont == "LegacyRuntime" || unityFont == "Arial" || unityFont == "Liberation Sans")
                return "'Liberation Sans', Arial, Helvetica, " + fallbackFonts;
            return "'" + unityFont.Replace("'", string.Empty) + "', " + fallbackFonts;
        }

        /// <summary>The family a TMP-style font asset name stands for: the name without its " SDF" suffix.</summary>
        protected static string StripSdf(string fontAsset)
        {
            if (string.IsNullOrEmpty(fontAsset))
                return fontAsset;
            int i = fontAsset.IndexOf(" SDF", StringComparison.OrdinalIgnoreCase);
            return i > 0 ? fontAsset.Substring(0, i) : fontAsset;
        }

        protected static void AppendFont(StringBuilder sb, string family, float size, bool bold, bool italic, Color color, string align)
        {
            sb.Append("font-family:").Append(family).Append(";font-size:");
            AppendF(sb, size).Append("px;");
            if (bold)
                sb.Append("font-weight:bold;");
            if (italic)
                sb.Append("font-style:italic;");
            AppendRgba(sb.Append("color:"), color).Append(";text-align:").Append(align).Append(';');
        }

        protected static StringBuilder AppendShadow(StringBuilder ts, float x, float y, Color c)
        {
            AppendF(ts, x).Append("px ");
            AppendF(ts, y).Append("px 0 ");
            return AppendRgba(ts, c);
        }

        /// <summary>The text as HTML, converted again only when the source hands out a different string or changes its rich-text setting.</summary>
        protected static string TextHtml(MirrorNode node, string source, bool rich)
        {
            if (!ReferenceEquals(source, node.TextSource) || rich != node.TextRich || node.TextHtml == null)
            {
                node.TextSource = source;
                node.TextRich = rich;
                node.TextHtml = rich ? MirrorRichText.Convert(source) : MirrorRichText.Escape(source);
            }
            return node.TextHtml;
        }

        protected static string HAlign(TextAnchor a)
        {
            switch (a)
            {
                case TextAnchor.UpperCenter: case TextAnchor.MiddleCenter: case TextAnchor.LowerCenter: return "center";
                case TextAnchor.UpperRight: case TextAnchor.MiddleRight: case TextAnchor.LowerRight: return "right";
                default: return "left";
            }
        }

        protected static string VAlign(TextAnchor a)
        {
            switch (a)
            {
                case TextAnchor.MiddleLeft: case TextAnchor.MiddleCenter: case TextAnchor.MiddleRight: return "center";
                case TextAnchor.LowerLeft: case TextAnchor.LowerCenter: case TextAnchor.LowerRight: return "flex-end";
                default: return "flex-start";
            }
        }

        // ------------------------------------------------------------------ images

        /// <summary>The sprite's pixels on its texture. textureRect throws for a tightly packed atlas sprite, so that case asks for the untrimmed rect instead.</summary>
        protected static Rect SpriteRect(Sprite sprite) =>
            sprite.packed && sprite.packingMode == SpritePackingMode.Tight ? sprite.rect : sprite.textureRect;

        protected static RectInt ToRectInt(Rect r) =>
            new RectInt(Mathf.RoundToInt(r.x), Mathf.RoundToInt(r.y), Mathf.Max(1, Mathf.RoundToInt(r.width)), Mathf.Max(1, Mathf.RoundToInt(r.height)));

        protected string SpriteUrl(Sprite sprite, Color tint)
        {
            var tex = sprite.texture;
            if (tex == null)
                return null;
            return _textures.DataUrl(tex, ToRectInt(SpriteRect(sprite)), tint);
        }

        /// <summary>Scales a pair of opposite borders down together when they do not fit the output size, as Image.GetAdjustedBorders does.</summary>
        protected static void FitBorders(ref float low, ref float high, float size)
        {
            if (low + high > size && low + high > 0f)
            {
                float f = size / (low + high);
                low *= f;
                high *= f;
            }
        }

        /// <summary>
        /// Appends a background-image composed from a 9-sliced texture rectangle at the element's device-pixel
        /// size (see <see cref="MirrorTextureCache.SlicedDataUrl"/>). Borders are in source units, already scaled
        /// to the element's coordinate space; the element size is in the same units. Returns false when the
        /// texture could not be read, so the caller can fall back to a plain stretched image.
        /// </summary>
        protected bool AppendSliced(StringBuilder bs, Texture texture, RectInt rect, Vector4 sourceBorder, float width, float height,
                                    float left, float bottom, float right, float top, bool fillCenter, Color tint)
        {
            float scale = _rootScale;
            int outW = Mathf.Max(1, Mathf.CeilToInt(width * scale)), outH = Mathf.Max(1, Mathf.CeilToInt(height * scale));
            left *= scale; right *= scale; bottom *= scale; top *= scale;
            FitBorders(ref left, ref right, outW);
            FitBorders(ref bottom, ref top, outH);
            string url = _textures.SlicedDataUrl(texture, rect, sourceBorder, outW, outH,
                Mathf.RoundToInt(left), Mathf.RoundToInt(bottom), Mathf.RoundToInt(right), Mathf.RoundToInt(top), fillCenter, tint);
            if (url == null)
                return false;
            bs.Append("background-image:url(").Append(url).Append(");background-size:100% 100%;");
            return true;
        }

        // ------------------------------------------------------------------ formatting

        protected static string F(float v) => v.ToString("0.##", Inv);

        /// <summary>Appends <paramref name="v"/> as "0.##" would print it, without allocating: up to two decimals, none when they are zero.</summary>
        protected static StringBuilder AppendF(StringBuilder sb, float v) => AppendFixed(sb, v, 100);

        /// <summary><paramref name="scale"/> is 10 to the number of decimals kept; trailing zeros are dropped and a value that rounds to zero prints as 0.</summary>
        protected static StringBuilder AppendFixed(StringBuilder sb, float v, int scale)
        {
            if (float.IsNaN(v) || float.IsInfinity(v) || Mathf.Abs(v) > 1e9f)
                return sb.Append(v.ToString(Inv));
            long units = (long)Math.Round((double)v * scale, MidpointRounding.AwayFromZero);
            if (units < 0)
            {
                sb.Append('-');
                units = -units;
            }
            sb.Append(units / scale);
            int frac = (int)(units % scale);
            if (frac == 0)
                return sb;
            sb.Append('.');
            for (int div = scale / 10; div > 0 && frac > 0; div /= 10)
            {
                int digit = frac / div;
                sb.Append((char)('0' + digit));
                frac -= digit * div;
            }
            return sb;
        }

        protected static int Channel(float channel) => Mathf.RoundToInt(Mathf.Clamp01(channel) * 255f);

        protected static StringBuilder AppendRgba(StringBuilder sb, Color c)
        {
            sb.Append("rgba(").Append(Channel(c.r)).Append(',').Append(Channel(c.g)).Append(',').Append(Channel(c.b)).Append(',');
            return AppendFixed(sb, Mathf.Clamp01(c.a), 1000).Append(')');
        }

        protected static StringBuilder AppendRgb(StringBuilder sb, Color c) =>
            sb.Append("rgb(").Append(Channel(c.r)).Append(',').Append(Channel(c.g)).Append(',').Append(Channel(c.b)).Append(')');

        /// <summary>
        /// The builder's text as a string, but <paramref name="last"/> itself when it already reads the same, so a
        /// frame in which nothing changed allocates nothing and the diff compares by reference.
        /// </summary>
        protected string Take(StringBuilder sb, string last)
        {
            int n = sb.Length;
            if (last == null || last.Length != n)
                return sb.ToString();
            if (_cmp.Length < n)
                _cmp = new char[Mathf.Max(n, _cmp.Length * 2)];
            sb.CopyTo(0, _cmp, 0, n);
            for (int i = 0; i < n; i++)
            {
                if (_cmp[i] != last[i])
                    return sb.ToString();
            }
            return last;
        }
    }
}
