using System;
using System.Collections.Generic;
using System.Text;
using UnityEngine;

namespace Hiccup
{
    /// <summary>Which parts of a document receive pointer input.</summary>
    public enum HtmlPointerMode
    {
        /// <summary>The whole panel rectangle captures pointer input (clicks on empty space do not reach Unity).</summary>
        Panel = 0,
        /// <summary>Only the top-level children of the document capture input; clicks on empty space fall through to Unity.</summary>
        ChildrenOnly = 1,
        /// <summary>The document is display-only.</summary>
        None = 2
    }

    /// <summary>
    /// A live HTML document hosted inside the Unity canvas. The document is laid out by the browser, exposed to
    /// assistive technology like normal DOM, and snapshotted into <see cref="Texture"/> (HTML-in-Canvas) so it can be
    /// drawn anywhere in the scene through <see cref="HtmlScreenSurface"/> or <see cref="HtmlWorldSurface"/>.
    /// </summary>
    [AddComponentMenu("Hiccup/HTML Document")]
    [DisallowMultipleComponent]
    public class HtmlDocument : MonoBehaviour
    {
        [Header("Content")]
        [Tooltip("HTML fragment (body content). <script> tags are not executed; use Eval() for custom JS.")]
        [SerializeField] private TextAsset html;
        [Tooltip("Style sheets injected into the document, in order.")]
        [SerializeField] private TextAsset[] styleSheets;
        [TextArea(2, 8)]
        [SerializeField] private string extraCss;

        [Header("Layout")]
        [Tooltip("Document size in CSS pixels. Surfaces may override this (e.g. to match a RectTransform).")]
        [SerializeField] private Vector2Int size = new Vector2Int(800, 600);
        [Tooltip("Texture density relative to the device pixel ratio. Use 2 for world-space panels that are viewed at an angle or minified.")]
        [Range(0.25f, 4f)]
        [SerializeField] private float resolutionScale = 1f;
        [Tooltip("Generate mipmaps after every update and sample trilinear + anisotropic. Recommended for world-space panels.")]
        [SerializeField] private bool mipmaps = true;

        [Header("Behavior")]
        [SerializeField] private bool visible = true;
        [SerializeField] private HtmlPointerMode pointerMode = HtmlPointerMode.ChildrenOnly;
        [Tooltip("Stop pointer/keyboard events that target the document from also reaching Unity's input.")]
        [SerializeField] private bool blockUnityInput = true;
        [Tooltip("Call preventDefault() on form submit so the page never navigates. Submit events are still forwarded.")]
        [SerializeField] private bool preventFormSubmit = true;
        [Tooltip("Snapshot with premultiplied alpha (matches the Hiccup shaders).")]
        [SerializeField] private bool premultipliedAlpha = true;
        [SerializeField] private bool createOnEnable = true;

        private int _panel;
        private bool _created;   // the bridge-side panel exists
        private bool _ready;     // ...and its page accepts reads and writes (see Created)
        private Texture _texture;
        // False when the texture belongs to an IHtmlBackend (Editor preview) and must not be destroyed here.
        private bool _ownsTexture = true;
        private Vector2Int _textureSize;
        private readonly float[] _matrix = new float[16];
        private readonly int[] _wh = new int[2];

        private readonly Dictionary<string, List<Action<HtmlEvent>>> _typeHandlers = new Dictionary<string, List<Action<HtmlEvent>>>();
        private readonly Dictionary<string, List<Action<HtmlEvent>>> _elementHandlers = new Dictionary<string, List<Action<HtmlEvent>>>();
        private readonly Dictionary<string, List<Action<HtmlEvent>>> _actionHandlers = new Dictionary<string, List<Action<HtmlEvent>>>();
        private readonly HashSet<string> _listened = new HashSet<string>();

        /// <summary>Raised for every DOM event forwarded from the browser, before element/action handlers.</summary>
        public event Action<HtmlEvent> EventReceived;
        /// <summary>Raised when <see cref="Texture"/> is (re)created, e.g. after a resize.</summary>
        public event Action<HtmlDocument> TextureChanged;
        /// <summary>
        /// Raised once the browser-side panel exists and element reads and writes reach it. In a web build that is
        /// inside <see cref="Create"/>; the Editor preview raises it a few frames later, once Chrome has loaded the
        /// page, so anything written before then would otherwise be lost.
        /// </summary>
        public event Action<HtmlDocument> Created;

        // ------------------------------------------------------------------ state

        /// <summary>True once <see cref="Created"/> has been raised: the panel exists and can be read and written.</summary>
        public bool IsCreated => _created && _ready;
        internal int PanelId => _panel;
        public HtmlRuntime Runtime => HtmlRuntime.Instance;
        public HtmlRenderMode RenderMode => HtmlRuntime.HasInstance ? HtmlRuntime.Instance.Mode : HtmlRenderMode.Unavailable;

        /// <summary>Texture containing the rendered document (null in overlay mode). Row 0 is the top of the document.</summary>
        public Texture Texture => _texture;
        public Vector2Int TextureSize => _textureSize;
        /// <summary>True: the texture's first row is the top of the page, so sample with a flipped V (surfaces do this for you).</summary>
        public bool TextureIsTopDown => true;

        public TextAsset Html
        {
            get => html;
            set
            {
                html = value;
                if (_created)
                    SetHtml(value != null ? value.text : string.Empty);
            }
        }
        public TextAsset[] StyleSheets
        {
            get => styleSheets;
            set
            {
                styleSheets = value;
                if (_created)
                    SetCss(BuildCss());
            }
        }
        public string ExtraCss
        {
            get => extraCss;
            set
            {
                extraCss = value;
                if (_created)
                    SetCss(BuildCss());
            }
        }

        /// <summary>Document size in CSS pixels.</summary>
        public Vector2Int Size
        {
            get => size;
            set => SetSize(value.x, value.y);
        }

        public bool Visible
        {
            get => visible;
            set
            {
                visible = value;
                if (_created)
                    HtmlNative.Hiccup_PanelSetVisible(_panel, value ? 1 : 0);
            }
        }

        public HtmlPointerMode PointerMode
        {
            get => pointerMode;
            set
            {
                pointerMode = value;
                if (_created)
                    HtmlNative.Hiccup_PanelSetPointerMode(_panel, (int)value);
            }
        }

        public bool BlockUnityInput
        {
            get => blockUnityInput;
            set
            {
                blockUnityInput = value;
                if (_created)
                    HtmlNative.Hiccup_PanelSetBlockInput(_panel, value ? 1 : 0);
            }
        }

        public bool PremultipliedAlpha
        {
            get => premultipliedAlpha;
            set
            {
                premultipliedAlpha = value;
                if (_created)
                    HtmlNative.Hiccup_PanelSetPremultiplied(_panel, value ? 1 : 0);
            }
        }

        /// <summary>Texture pixels per CSS pixel, on top of the device pixel ratio. 2 supersamples the document.</summary>
        public float ResolutionScale
        {
            get => resolutionScale;
            set
            {
                value = Mathf.Clamp(value, 0.25f, 4f);
                if (Mathf.Approximately(resolutionScale, value))
                    return;
                resolutionScale = value;
                if (!_created)
                    return;
                HtmlNative.Hiccup_PanelSetResolutionScale(_panel, value);
                HtmlNative.Hiccup_PanelSetSize(_panel, size.x, size.y);
                CreateTexture();
            }
        }

        /// <summary>Generate mipmaps after each update and sample trilinear + anisotropic.</summary>
        public bool Mipmaps
        {
            get => mipmaps;
            set
            {
                if (mipmaps == value)
                    return;
                mipmaps = value;
                if (!_created)
                    return;
                HtmlNative.Hiccup_PanelSetMipmaps(_panel, value ? 1 : 0);
                CreateTexture();
            }
        }

        // ------------------------------------------------------------------ lifecycle

        private void OnEnable()
        {
            if (createOnEnable)
                Create();
            else if (_created)
                HtmlNative.Hiccup_PanelSetVisible(_panel, visible ? 1 : 0);
        }

        private void OnDisable()
        {
            if (_created)
                HtmlNative.Hiccup_PanelSetVisible(_panel, 0);
        }

        private void OnDestroy() => DestroyPanel();

        /// <summary>Creates the browser-side panel, loads the content and allocates the texture.</summary>
        public void Create()
        {
            if (_created)
                return;
            var runtime = HtmlRuntime.Instance;
            if (runtime == null)
                return;

            size = new Vector2Int(Mathf.Max(1, size.x), Mathf.Max(1, size.y));
            _panel = HtmlNative.Hiccup_PanelCreate(size.x, size.y);
            runtime.Register(this, _panel);
            _created = true;

            HtmlNative.Hiccup_PanelSetPointerMode(_panel, (int)pointerMode);
            HtmlNative.Hiccup_PanelSetBlockInput(_panel, blockUnityInput ? 1 : 0);
            HtmlNative.Hiccup_PanelSetPreventSubmit(_panel, preventFormSubmit ? 1 : 0);
            HtmlNative.Hiccup_PanelSetPremultiplied(_panel, premultipliedAlpha ? 1 : 0);
            HtmlNative.Hiccup_PanelSetMipmaps(_panel, mipmaps ? 1 : 0);
            HtmlNative.Hiccup_PanelSetResolutionScale(_panel, resolutionScale);
            HtmlNative.Hiccup_PanelSetSize(_panel, size.x, size.y);
            HtmlNative.Hiccup_PanelSetVisible(_panel, visible && isActiveAndEnabled ? 1 : 0);
            foreach (var type in _listened)
                HtmlNative.Hiccup_PanelListen(_panel, type, 1);

            SetCss(BuildCss());
            SetHtml(html != null ? html.text : string.Empty);
            CreateTexture();

            // The jslib's DOM exists at once. A backend that starts a browser reports ready later, and
            // AfterBridgeUpdate raises Created when it does; until then IsCreated stays false.
            if (HtmlNative.Hiccup_PanelIsReady(_panel) != 0)
                RaiseCreated();
        }

        private void RaiseCreated()
        {
            _ready = true;
            try { Created?.Invoke(this); }
            catch (Exception ex) { Debug.LogException(ex, this); }
        }

        /// <summary>Removes the panel from the page and releases the texture.</summary>
        public void DestroyPanel()
        {
            if (!_created)
                return;
            ReleaseTexture();
            HtmlNative.Hiccup_PanelDestroy(_panel);
            if (HtmlRuntime.HasInstance)
                HtmlRuntime.Instance.Unregister(_panel);
            _created = false;
            _ready = false;
            _panel = 0;
        }

        /// <summary>Re-applies the serialized HTML and style sheets.</summary>
        public void Reload()
        {
            if (!_created)
            {
                Create();
                return;
            }
            SetCss(BuildCss());
            SetHtml(html != null ? html.text : string.Empty);
        }

        private string BuildCss()
        {
            var sb = new StringBuilder();
            if (styleSheets != null)
            {
                foreach (var s in styleSheets)
                {
                    if (s != null)
                        sb.Append(s.text).Append('\n');
                }
            }
            if (!string.IsNullOrEmpty(extraCss))
                sb.Append(extraCss);
            return sb.ToString();
        }

        // ------------------------------------------------------------------ content API

        /// <summary>Replaces the document body with an HTML fragment.</summary>
        public void SetHtml(string fragment)
        {
            if (!_created)
            {
                Create();
            }
            if (_created)
                HtmlNative.Hiccup_PanelSetHtml(_panel, fragment ?? string.Empty);
        }

        /// <summary>Replaces the document's style sheet.</summary>
        public void SetCss(string css)
        {
            if (!_created)
            {
                Create();
            }
            if (_created)
                HtmlNative.Hiccup_PanelSetCss(_panel, css ?? string.Empty);
        }

        public void SetSize(int width, int height)
        {
            width = Mathf.Max(1, width);
            height = Mathf.Max(1, height);
            if (size.x == width && size.y == height && (_texture != null || !_created))
                return;
            size = new Vector2Int(width, height);
            if (!_created)
                return;
            HtmlNative.Hiccup_PanelSetSize(_panel, width, height);
            CreateTexture();
        }

        /// <summary>
        /// Positions the document for hit testing and accessibility. <paramref name="pixelToClip"/> maps document CSS pixel
        /// coordinates (origin top-left, y down, z = 0) to Unity clip space (x, y in -1..1, w). Surfaces call this every frame.
        /// </summary>
        public void SetGeometry(Matrix4x4 pixelToClip)
        {
            if (!_created)
                return;
            for (int c = 0; c < 4; c++)
            {
                for (int r = 0; r < 4; r++)
                    _matrix[c * 4 + r] = pixelToClip[r, c];
            }
            HtmlNative.Hiccup_PanelSetGeometry(_panel, _matrix);
        }

        /// <summary>Marks the texture dirty (the bridge normally tracks this through paint events).</summary>
        public void Invalidate()
        {
            if (_created)
                HtmlNative.Hiccup_PanelInvalidate(_panel);
        }

        /// <summary>Finds the first element matching a CSS selector.</summary>
        public HtmlElement Q(string selector)
        {
            if (!_created)
                return HtmlElement.None;
            return new HtmlElement(this, HtmlNative.Hiccup_Query(_panel, selector));
        }

        /// <summary>Finds all elements matching a CSS selector.</summary>
        public List<HtmlElement> QAll(string selector)
        {
            var list = new List<HtmlElement>();
            if (!_created)
                return list;
            var csv = HtmlNative.TakeString(HtmlNative.Hiccup_QueryAll(_panel, selector));
            if (string.IsNullOrEmpty(csv))
                return list;
            foreach (var part in csv.Split(','))
            {
                if (int.TryParse(part, out var h) && h != 0)
                    list.Add(new HtmlElement(this, h));
            }
            return list;
        }

        /// <summary>
        /// Runs JavaScript inside the page. The code receives <c>panel</c> (the drawable root), <c>root</c> (the content element)
        /// and <c>HUI</c> (the bridge). A returned value is stringified (objects as JSON).
        /// </summary>
        public string Eval(string javascript)
        {
            if (!_created)
                return string.Empty;
            return HtmlNative.TakeString(HtmlNative.Hiccup_PanelEval(_panel, javascript ?? string.Empty));
        }

        /// <summary>Announces text to screen readers through an aria-live region.</summary>
        public void Announce(string text, bool assertive = false)
        {
            if (_created)
                HtmlNative.Hiccup_PanelAnnounce(_panel, text ?? string.Empty, assertive ? 1 : 0);
        }

        // ------------------------------------------------------------------ events

        /// <summary>Handles every event of a type anywhere in the document.</summary>
        public void On(string eventType, Action<HtmlEvent> handler)
        {
            AddHandler(_typeHandlers, eventType, handler);
            Listen(eventType);
        }

        public void Off(string eventType, Action<HtmlEvent> handler) => RemoveHandler(_typeHandlers, eventType, handler);

        /// <summary>Handles an event dispatched on (or bubbling through) the element with the given id.</summary>
        public void On(string elementId, string eventType, Action<HtmlEvent> handler)
        {
            AddHandler(_elementHandlers, elementId + "|" + eventType, handler);
            Listen(eventType);
        }

        public void Off(string elementId, string eventType, Action<HtmlEvent> handler) => RemoveHandler(_elementHandlers, elementId + "|" + eventType, handler);

        /// <summary>Handles clicks on any element carrying <c>data-action="name"</c> (or a descendant of one).</summary>
        public void OnAction(string action, Action<HtmlEvent> handler)
        {
            AddHandler(_actionHandlers, action, handler);
            Listen("click");
        }

        public void OffAction(string action, Action<HtmlEvent> handler) => RemoveHandler(_actionHandlers, action, handler);

        /// <summary>Forward an additional DOM event type (e.g. "pointerover", "keyup", "wheel").</summary>
        public void Listen(string eventType)
        {
            if (string.IsNullOrEmpty(eventType) || !_listened.Add(eventType))
                return;
            if (_created)
                HtmlNative.Hiccup_PanelListen(_panel, eventType, 1);
        }

        private static void AddHandler(Dictionary<string, List<Action<HtmlEvent>>> map, string key, Action<HtmlEvent> handler)
        {
            if (handler == null)
                return;
            if (!map.TryGetValue(key, out var list))
                map[key] = list = new List<Action<HtmlEvent>>();
            list.Add(handler);
        }

        private static void RemoveHandler(Dictionary<string, List<Action<HtmlEvent>>> map, string key, Action<HtmlEvent> handler)
        {
            if (map.TryGetValue(key, out var list))
                list.Remove(handler);
        }

        internal void DispatchNative(string json)
        {
            var e = HtmlEvent.Parse(json, this);
            if (e != null)
                Dispatch(e);
        }

        /// <summary>Dispatches an event through the C# handlers (target, ancestors, data-action, then type handlers).</summary>
        public void Dispatch(HtmlEvent e)
        {
            e.Document = this;
            try { EventReceived?.Invoke(e); }
            catch (Exception ex) { Debug.LogException(ex, this); }

            if (!e.Handled && !string.IsNullOrEmpty(e.id))
                Invoke(_elementHandlers, e.id + "|" + e.type, e);
            if (!e.Handled && !string.IsNullOrEmpty(e.path))
            {
                foreach (var id in e.path.Split(' '))
                {
                    if (e.Handled)
                        break;
                    Invoke(_elementHandlers, id + "|" + e.type, e);
                }
            }
            if (!e.Handled && e.type == "click" && !string.IsNullOrEmpty(e.action))
                Invoke(_actionHandlers, e.action, e);
            if (!e.Handled)
                Invoke(_typeHandlers, e.type, e);
        }

        private void Invoke(Dictionary<string, List<Action<HtmlEvent>>> map, string key, HtmlEvent e)
        {
            if (!map.TryGetValue(key, out var list) || list.Count == 0)
                return;
            var snapshot = list.ToArray();
            foreach (var h in snapshot)
            {
                if (e.Handled)
                    return;
                try { h(e); }
                catch (Exception ex) { Debug.LogException(ex, this); }
            }
        }

        // ------------------------------------------------------------------ texture

        private void CreateTexture()
        {
            ReleaseTexture();
            _ownsTexture = true;
            var runtime = HtmlRuntime.Instance;

            if (!HtmlNative.Available)
            {
                _textureSize = size;
                _texture = CreatePlaceholder(size.x, size.y);
            }
            else if (runtime.Mode == HtmlRenderMode.Texture)
            {
                HtmlNative.Hiccup_PanelGetTextureSize(_panel, _wh);
                _textureSize = new Vector2Int(Mathf.Max(1, _wh[0]), Mathf.Max(1, _wh[1]));

                if (HtmlBackend.Current != null)
                {
                    // The backend owns and refreshes the texture; it may not exist yet (see AfterBridgeUpdate).
                    _ownsTexture = false;
                    _texture = HtmlBackend.Current.PanelGetTexture(_panel);
                }
                else if (runtime.IsWebGPU)
                {
                    // Unity owns the texture; the browser draws the element into it.
                    var rt = new RenderTexture(_textureSize.x, _textureSize.y, 0, RenderTextureFormat.ARGB32, RenderTextureReadWrite.sRGB)
                    {
                        name = $"Hiccup {name}",
                        useMipMap = mipmaps,
                        autoGenerateMips = false,   // the browser writes level 0; mips are generated in AfterBridgeUpdate
                        filterMode = mipmaps ? FilterMode.Trilinear : FilterMode.Bilinear,
                        anisoLevel = mipmaps ? 16 : 1,
                        wrapMode = TextureWrapMode.Clamp
                    };
                    rt.Create();
                    HtmlNative.Hiccup_PanelBindGPUTexture(_panel, rt.GetNativeTexturePtr());
                    _texture = rt;
                }
                else
                {
                    // The browser owns a GL texture (so it can be re-specified freely); Unity wraps it.
                    int glId = HtmlNative.Hiccup_PanelCreateGLTexture(_panel);
                    if (glId != 0)
                    {
                        // The bridge generates mips with gl.generateMipmap; tell Unity so its sampler state agrees.
                        var tex = Texture2D.CreateExternalTexture(_textureSize.x, _textureSize.y, TextureFormat.RGBA32, mipmaps, false, (IntPtr)glId);
                        tex.name = $"Hiccup {name}";
                        tex.filterMode = mipmaps ? FilterMode.Trilinear : FilterMode.Bilinear;
                        tex.anisoLevel = mipmaps ? 16 : 1;
                        tex.wrapMode = TextureWrapMode.Clamp;
                        _texture = tex;
                    }
                }
            }
            else
            {
                // Overlay mode: the DOM is drawn by the browser on top of the canvas; nothing to sample.
                _textureSize = Vector2Int.zero;
                _texture = null;
            }

            TextureChanged?.Invoke(this);
        }

        /// <summary>Called by <see cref="HtmlRuntime"/> right after the bridge uploaded textures for this frame.</summary>
        internal void AfterBridgeUpdate()
        {
            if (!_created)
                return;

            // A backend that brings its page up asynchronously (the Editor preview) becomes ready some frames
            // after Create(); this is where the deferred Created fires, so wiring code sees a working page.
            if (!_ready && HtmlNative.Hiccup_PanelIsReady(_panel) != 0)
                RaiseCreated();

            var backend = HtmlBackend.Current;
            if (backend != null)
            {
                // The backend creates its texture once the browser delivers the first frame, and replaces it on resize.
                var tex = backend.PanelGetTexture(_panel);
                if (!ReferenceEquals(tex, _texture))
                {
                    _texture = tex;
                    _ownsTexture = false;
                    backend.PanelGetTextureSize(_panel, _wh);
                    _textureSize = new Vector2Int(Mathf.Max(1, _wh[0]), Mathf.Max(1, _wh[1]));
                    TextureChanged?.Invoke(this);
                }
                return;
            }

            if (!_ownsTexture)
            {
                // The backend that owned the texture has been unregistered (the Editor preview stops as play
                // mode ends) and released it with everything else it owned. Drop the dead reference rather
                // than touching it; the next Create() allocates afresh.
                bool hadTexture = !ReferenceEquals(_texture, null);
                _texture = null;
                _ownsTexture = true;
                if (hadTexture)
                    TextureChanged?.Invoke(this);
                return;
            }

            if (!mipmaps)
                return;
            if (_texture is RenderTexture rt && rt != null && rt.useMipMap && HtmlNative.Hiccup_PanelTakeUpdated(_panel) != 0)
                rt.GenerateMips();
        }

        private void ReleaseTexture()
        {
            if (_texture == null)
                return;
            if (_ownsTexture)
            {
                if (_texture is RenderTexture rt)
                    rt.Release();
                Destroy(_texture);
            }
            _texture = null;
        }

        private static Texture2D CreatePlaceholder(int w, int h)
        {
            const int Res = 64;
            var tex = new Texture2D(Res, Res, TextureFormat.RGBA32, false) { name = "Hiccup placeholder", hideFlags = HideFlags.HideAndDontSave };
            var fill = new Color(0.10f, 0.12f, 0.18f, 0.55f);
            var edge = new Color(0.45f, 0.75f, 1.00f, 0.90f);
            var px = new Color[Res * Res];
            for (int y = 0; y < Res; y++)
            {
                for (int x = 0; x < Res; x++)
                    px[y * Res + x] = (x < 2 || y < 2 || x >= Res - 2 || y >= Res - 2) ? edge : fill;
            }
            tex.SetPixels(px);
            tex.Apply(false, true);
            return tex;
        }
    }
}
