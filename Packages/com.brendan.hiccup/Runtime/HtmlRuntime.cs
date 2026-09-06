using System;
using System.Collections.Generic;
using AOT;
using UnityEngine;
using UnityEngine.Rendering;

namespace Hiccup
{
    /// <summary>How the browser presents HTML documents.</summary>
    public enum HtmlRenderMode
    {
        /// <summary>Not running in a web player (Editor / other platforms). Documents exist but render nothing.</summary>
        Unavailable = -1,
        /// <summary>HTML-in-Canvas is not available; the DOM is placed in a layer on top of the Unity canvas.</summary>
        Overlay = 0,
        /// <summary>HTML-in-Canvas is active; the DOM is composited into Unity textures and stays accessible.</summary>
        Texture = 1
    }

    /// <summary>When textures are refreshed from the DOM.</summary>
    public enum HtmlUpdateMode
    {
        /// <summary>Use the canvas paint event; fall back to every frame if the browser never fires it.</summary>
        Auto = 0,
        OnPaintEvent = 1,
        EveryFrame = 2
    }

    /// <summary>How a document's on-screen placement is communicated to the browser for hit testing and accessibility.</summary>
    public enum HtmlGeometryMode
    {
        /// <summary>
        /// Affine placement through canvas.updateElementGeometry(); perspective placement through the two-argument
        /// canvas.getElementTransform() (Chrome's WebGL/WebGPU approach), else an identity geometry plus a CSS matrix3d. Default.
        /// </summary>
        Auto = 0,
        /// <summary>Always canvas.updateElementGeometry() (affine hit testing only in current Chrome builds).</summary>
        UpdateElementGeometry = 1,
        /// <summary>Always canvas.getElementTransform(element, matrix) and apply the returned CSS transform.</summary>
        GetElementTransform = 2,
        /// <summary>Only a CSS matrix3d. Canvas children are not hit testable this way in current Chrome; useful for overlay mode tests.</summary>
        CssTransform = 3
    }

    /// <summary>Browser capabilities discovered at start-up (mirrors the JSON produced by Hiccup.jslib).</summary>
    [Serializable]
    public class HtmlFeatures
    {
        public string version;
        public bool htmlInCanvas;
        public string textureApi;
        public string geometryApi;
        public bool hasUpdateElementGeometry;
        public bool hasGetElementTransform;
        public int mode;
        public int backend;
        public bool overlayBehindCanvas;
        public float devicePixelRatio;
        public string userAgent;
    }

    /// <summary>
    /// Process-wide driver for the HTML UI bridge. Created on demand; ticks the browser bridge once per frame
    /// after all documents and surfaces have updated (see the execution order attribute).
    /// </summary>
    [DefaultExecutionOrder(10000)]
    [AddComponentMenu("")]
    public sealed class HtmlRuntime : MonoBehaviour
    {
        private static HtmlRuntime s_instance;
        private static bool s_quitting;
        // Keep the delegate alive for the lifetime of the app: the browser holds a raw pointer to it.
        private static readonly HtmlNative.EventCallback s_callback = OnNativeEvent;

        /// <summary>Set before the first document is created to force the DOM overlay even if HTML-in-Canvas is available.</summary>
        public static bool ForceOverlay { get; set; }
        /// <summary>Set before the first document is created to enable verbose console logging in the browser.</summary>
        public static bool DebugLogging { get; set; }

        private static HtmlUpdateMode s_updateMode = HtmlUpdateMode.Auto;
        public static HtmlUpdateMode UpdateMode
        {
            get => s_updateMode;
            set
            {
                s_updateMode = value;
                if (s_instance != null)
                    HtmlNative.Hiccup_SetUpdateMode((int)value);
            }
        }

        private static HtmlGeometryMode s_geometryMode = HtmlGeometryMode.Auto;
        /// <summary>Geometry strategy for hit testing / accessibility bounds. Can be changed at any time.</summary>
        public static HtmlGeometryMode GeometryMode
        {
            get => s_geometryMode;
            set
            {
                s_geometryMode = value;
                if (s_instance != null)
                    HtmlNative.Hiccup_SetGeometryMode((int)value);
            }
        }

        /// <summary>True in WebGL/WebGPU player builds, false in the Editor and on other platforms.</summary>
        public static bool IsWebPlayer => HtmlNative.Available;

        public static bool HasInstance => s_instance != null;

        public static HtmlRuntime Instance
        {
            get
            {
                if (s_instance == null && !s_quitting)
                {
                    var go = new GameObject("[Hiccup Runtime]");
                    DontDestroyOnLoad(go);
                    s_instance = go.AddComponent<HtmlRuntime>();
                    s_instance.Initialize();
                }
                return s_instance;
            }
        }

        public HtmlRenderMode Mode { get; private set; } = HtmlRenderMode.Unavailable;
        public HtmlFeatures Features { get; private set; } = new HtmlFeatures();
        public bool IsWebGPU { get; private set; }
        /// <summary>
        /// Overlay mode with the DOM placed behind a transparent canvas: surfaces cut holes through the frame where
        /// their panels are, so scene geometry in front of a panel covers it. Enabled automatically when the canvas has
        /// an alpha channel (the WebGL template opts in with <c>webglContextAttributes: { alpha: true }</c>).
        /// </summary>
        public bool OverlayCutout => Mode == HtmlRenderMode.Overlay && Features.overlayBehindCanvas;

        /// <summary>Size of the Unity canvas in CSS pixels.</summary>
        public Vector2 CanvasCssSize { get; private set; }
        /// <summary>Size of the Unity canvas backing store in device pixels (matches Screen.width/height in a web player).</summary>
        public Vector2Int CanvasPixelSize { get; private set; }
        public float DevicePixelRatio { get; private set; } = 1f;

        /// <summary>Multiply a Unity screen-pixel distance by this to get CSS pixels.</summary>
        public float CssPerScreenPixel => (Screen.width > 0 && CanvasCssSize.x > 0) ? CanvasCssSize.x / Screen.width : 1f;

        private readonly Dictionary<int, HtmlDocument> _documents = new Dictionary<int, HtmlDocument>();
        private readonly List<HtmlDocument> _updating = new List<HtmlDocument>();
        private readonly float[] _info = new float[5];

        private void Initialize()
        {
            IsWebGPU = SystemInfo.graphicsDeviceType == GraphicsDeviceType.WebGPU;
            int backend = IsWebGPU ? 2 : 1;
            int linear = QualitySettings.activeColorSpace == ColorSpace.Linear ? 1 : 0;

            HtmlNative.Hiccup_SetGeometryMode((int)s_geometryMode);
            int mode = HtmlNative.Hiccup_Init(backend, linear, ForceOverlay ? 1 : 0, DebugLogging ? 1 : 0, s_callback);
            Mode = HtmlNative.Available ? (HtmlRenderMode)mode : HtmlRenderMode.Unavailable;

            if (HtmlBackend.Current != null)
            {
                Debug.Log($"[Hiccup] mode={Mode} backend={HtmlBackend.Current.GetType().Name} (Editor preview: layout and input are real, accessibility and HTML-in-Canvas compositing are not — test those in a web build).");
            }
            else if (HtmlNative.Available)
            {
                var json = HtmlNative.TakeString(HtmlNative.Hiccup_GetFeatures());
                try { Features = JsonUtility.FromJson<HtmlFeatures>(json) ?? new HtmlFeatures(); }
                catch { Features = new HtmlFeatures(); }
                HtmlNative.Hiccup_SetUpdateMode((int)s_updateMode);
                Debug.Log($"[Hiccup] mode={Mode} backend={(IsWebGPU ? "WebGPU" : "WebGL2")} htmlInCanvas={Features.htmlInCanvas} textureApi={Features.textureApi} geometryApi={Features.geometryApi} dpr={Features.devicePixelRatio}");
            }
            else
            {
                Debug.Log("[Hiccup] Not a web player: HTML documents will render placeholders only. Build for WebGL/WebGPU to see the UI.");
            }
            RefreshCanvasInfo();
        }

        /// <summary>Routes a DOM event payload from a bridge to the document that owns the panel.</summary>
        internal static void DispatchToPanel(int panel, string json)
        {
            if (s_instance == null)
                return;
            if (!s_instance._documents.TryGetValue(panel, out var doc) || doc == null)
                return;
            try { doc.DispatchNative(json); }
            catch (Exception ex) { Debug.LogException(ex); }
        }

        private void RefreshCanvasInfo()
        {
            HtmlNative.Hiccup_GetCanvasInfo(_info);
            CanvasCssSize = new Vector2(_info[0], _info[1]);
            DevicePixelRatio = _info[2] > 0 ? _info[2] : 1f;
            CanvasPixelSize = new Vector2Int((int)_info[3], (int)_info[4]);
        }

        internal void Register(HtmlDocument doc, int panel) => _documents[panel] = doc;
        internal void Unregister(int panel) => _documents.Remove(panel);

        [MonoPInvokeCallback(typeof(HtmlNative.EventCallback))]
        private static void OnNativeEvent(int panel, IntPtr json)
        {
            if (s_instance == null)
                return;
            DispatchToPanel(panel, HtmlNative.ReadUtf8(json));
        }

        private void LateUpdate()
        {
            RefreshCanvasInfo();
            HtmlNative.Hiccup_Update();
            // Snapshot: AfterBridgeUpdate can raise Created, whose handlers may create or destroy documents.
            _updating.Clear();
            _updating.AddRange(_documents.Values);
            foreach (var doc in _updating)
            {
                if (doc != null)
                    doc.AfterBridgeUpdate();
            }
        }

        private void OnApplicationQuit() => s_quitting = true;

        private void OnDestroy()
        {
            if (s_instance == this)
                s_instance = null;
        }
    }
}
