using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using UnityEngine;
using UnityEngine.Experimental.Rendering;

namespace Hiccup.Editor.Cdp
{
    /// <summary>
    /// An <see cref="IHtmlBackend"/> that renders documents in a real Chrome driven over the DevTools Protocol,
    /// so HTML UIs show up and respond in the Editor's Game view.
    /// </summary>
    /// <remarks>
    /// One browser target per document: the target's viewport is the document, DevTools screencasts it, and each
    /// frame is decoded into a <see cref="RenderTexture"/> that matches what <c>Hiccup.jslib</c> would have produced
    /// (premultiplied, first row at the top of the page). Pointer input is projected back through the document's
    /// pixel-to-clip matrix, so both screen and world surfaces are clickable.
    /// <para>
    /// This is a preview, not the real thing: accessibility, IME and HTML-in-Canvas compositing only exist in a
    /// web build. Layout, styling, script behavior and event payloads are genuine, because it is genuinely Chrome.
    /// </para>
    /// </remarks>
    internal sealed class CdpHtmlBackend : IHtmlBackend
    {
        private const string BlitShaderName = "Hidden/Hiccup/EditorPremultiply";
        private const double CaptureFallbackDelay = 2.5;   // seconds without a screencast frame before polling instead
        private const double CapturePollInterval = 0.05;

        private sealed class Panel
        {
            public int Id;
            public string TargetId;
            public string SessionId;

            public int Width = 1, Height = 1;      // CSS pixels
            public float Scale = 1f;               // extra device pixel ratio
            public bool Visible = true;
            public bool Mipmaps = true;            // the target gets a mip chain, regenerated per frame
            public readonly HashSet<string> Listened = new HashSet<string>();
            public string Html = string.Empty;
            public string Css = string.Empty;

            public bool Ready;                     // bridge injected; commands can be sent directly
            public bool SetupFailed;               // the page could not be created; it will never become Ready
            public double ReadyTime;
            public bool Screencasting;

            // Frames cross three threads: the receive thread submits encoded PNGs, a pool thread decodes them,
            // and the main thread uploads the newest result. Everything below FrameLock is guarded by it.
            public readonly object FrameLock = new object();
            public byte[] QueuedPng;               // newest encoded frame waiting for the decoder; a FrameBuffers buffer
            public int QueuedPngLength;            // bytes of QueuedPng that are the frame
            public bool Decoding;                  // a decode loop is running for this panel
            public PngDecoder Decoder;             // scratch buffers; only the decode loop touches it
            public DecodedFrame Pending;           // newest decoded frame, consumed on the main thread
            public readonly Stack<byte[]> FreePixels = new Stack<byte[]>();   // recycled RGBA buffers
            public byte[] PendingFrame;            // encoded frame the decoder rejected, for the LoadImage fallback

            public Texture2D Staging;
            public RenderTexture Target;
            public int TextureWidth, TextureHeight;

            public Matrix4x4 PixelToClip = Matrix4x4.identity;
            public bool HasGeometry;
            public bool PointerDown;
            public bool PointerInside;
            public Vector2 LastPointer;

            public bool UseCapturePolling;
            public bool CaptureInFlight;
            public double LastCaptureRequest;
            public double LastFrameTime;
        }

        private sealed class DecodedFrame
        {
            public byte[] Rgba;                    // bottom-up RGBA32, exactly Width * Height * 4 bytes
            public int Width, Height;
        }

        private const int MaxFreePixelBuffers = 2;   // one being uploaded, one being written, one spare is plenty

        private readonly Dictionary<int, Panel> _panels = new Dictionary<int, Panel>();
        // Read on the receive thread to route frames, written on the main thread.
        private readonly ConcurrentDictionary<string, Panel> _bySession = new ConcurrentDictionary<string, Panel>();
        private readonly ConcurrentQueue<Action> _mainThread = new ConcurrentQueue<Action>();
        private readonly List<Panel> _iteration = new List<Panel>();
        private readonly CancellationTokenSource _cancel = new CancellationTokenSource();

        private ChromeLauncher _chrome;
        private PreviewOrigin _origin;        // loopback page every document is created on; null if it could not start
        private CdpClient _client;
        private Material _blit;
        private bool _keyboardCapturing;
        private readonly List<HtmlKeyPress> _keys = new List<HtmlKeyPress>();
        private Panel _keyboardPanel;          // the panel the last press landed in; keys go there
        private bool _mouseWasDown;
        private int _nextPanelId = 1;
        private bool _starting;
        private bool _failed;
        private readonly bool _headless;
        private readonly bool _debug;

        /// <summary>True once Chrome is up and the protocol connection is live.</summary>
        public bool Connected => _client != null && _client.IsOpen;
        public bool Failed => _failed;
        public string Status { get; private set; } = "not started";

        public CdpHtmlBackend(bool headless, bool debugLogging)
        {
            _headless = headless;
            _debug = debugLogging;
        }

        // ------------------------------------------------------------------ lifecycle

        public int Init(int backend, int linear, int forceOverlay, int debug)
        {
            StartBrowser();
            // Documents behave as if HTML-in-Canvas were available: they get a texture to sample.
            return (int)HtmlRenderMode.Texture;
        }

        private void StartBrowser()
        {
            if (_starting || _failed)
                return;
            _starting = true;
            Status = "starting Chrome";
            if (_origin == null)
            {
                try { _origin = new PreviewOrigin(); }
                catch (Exception e) { Debug.LogWarning("[Hiccup] Editor preview could not open a loopback origin; documents will use about:blank, which embeds such as YouTube reject: " + e.Message); }
            }
            _ = StartBrowserAsync();
        }

        private async Task StartBrowserAsync()
        {
            try
            {
                var chrome = new ChromeLauncher();
                await chrome.LaunchAsync(_headless, _debug, _cancel.Token).ConfigureAwait(false);
                // Only the events HandleEvent acts on are worth parsing; everything else Chrome sends is dropped at the socket.
                var wantedEvents = _debug
                    ? new[] { "Runtime.bindingCalled", "Target.detachedFromTarget", "Runtime.consoleAPICalled" }
                    : new[] { "Runtime.bindingCalled", "Target.detachedFromTarget" };
                var client = await CdpClient.ConnectAsync(chrome.BrowserWebSocketUrl, OnScreencastFrame, wantedEvents, _cancel.Token).ConfigureAwait(false);

                Post(() =>
                {
                    if (_cancel.IsCancellationRequested)
                    {
                        client.Dispose();
                        chrome.Dispose();
                        return;
                    }
                    _chrome = chrome;
                    _client = client;
                    Status = "connected (" + System.IO.Path.GetFileName(chrome.ExecutablePath) + ")";
                    Debug.Log($"[Hiccup] Editor preview connected to {chrome.ExecutablePath}");
                    foreach (var panel in _panels.Values)
                        BeginPanelSetup(panel);
                });
            }
            catch (OperationCanceledException) { }
            catch (Exception e)
            {
                Post(() =>
                {
                    _failed = true;
                    Status = "failed: " + e.Message;
                    Debug.LogWarning($"[Hiccup] Editor preview unavailable: {e.Message}");
                });
            }
        }

        public void Shutdown()
        {
            try { _cancel.Cancel(); } catch { }

            foreach (var panel in _panels.Values)
                ReleaseTextures(panel);
            _panels.Clear();
            _bySession.Clear();
            _pendingOps.Clear();
            _handles.Clear();
            _handleAge.Clear();

            if (_blit != null)
            {
                UnityEngine.Object.DestroyImmediate(_blit);
                _blit = null;
            }
            if (_keyboardCapturing)
                HtmlBackend.SetKeyboardCapture(false);
            _keyboardCapturing = false;
            _keyboardPanel = null;
            _client?.Dispose();
            _client = null;
            _chrome?.Dispose();
            _chrome = null;
            _origin?.Dispose();
            _origin = null;
            Status = "stopped";
            // The token source is deliberately not disposed: setup tasks may still be holding its token.
        }

        private void Post(Action action) => _mainThread.Enqueue(action);

        // ------------------------------------------------------------------ panels

        public int PanelCreate(int width, int height)
        {
            var panel = new Panel
            {
                Id = _nextPanelId++,
                Width = Mathf.Max(1, width),
                Height = Mathf.Max(1, height),
            };
            _panels[panel.Id] = panel;
            if (Connected)
                BeginPanelSetup(panel);
            return panel.Id;
        }

        public void PanelDestroy(int id)
        {
            if (!_panels.TryGetValue(id, out var panel))
                return;
            _panels.Remove(id);
            _pendingOps.Remove(id);
            if (panel.SessionId != null)
                _bySession.TryRemove(panel.SessionId, out _);
            if (_keyboardPanel == panel)
                _keyboardPanel = null;
            ReleaseTextures(panel);

            if (Connected && panel.TargetId != null)
                _client.Send("Target.closeTarget", "{\"targetId\":" + Json.Quote(panel.TargetId) + "}");
        }

        /// <summary>
        /// A panel is ready once its page has loaded and the bridge is installed. If the browser or the page setup
        /// failed it reports ready as well, so <see cref="HtmlDocument"/> still raises <c>Created</c> and the
        /// scene behaves as it does with no preview at all.
        /// </summary>
        public bool PanelIsReady(int id)
        {
            if (!_panels.TryGetValue(id, out var panel))
                return true;   // unknown panel: nothing to wait for
            return panel.Ready || panel.SetupFailed || _failed;
        }

        /// <summary>Creates the browser target for a panel and injects the bridge. Runs off the main thread.</summary>
        private void BeginPanelSetup(Panel panel)
        {
            if (panel.TargetId != null || !Connected)
                return;
            _ = SetUpPanelAsync(panel);
        }

        private async Task SetUpPanelAsync(Panel panel)
        {
            var client = _client;
            try
            {
                // No width/height here: Chrome rejects those unless a new window is being opened, and the
                // viewport is set properly by Emulation.setDeviceMetricsOverride in FlushPanel anyway.
                var pageUrl = _origin != null ? _origin.Url : "about:blank";
                var created = await client.SendAsync("Target.createTarget", "{\"url\":" + Json.Quote(pageUrl) + "}").ConfigureAwait(false);
                var targetId = Json.Str(created, "targetId");
                if (string.IsNullOrEmpty(targetId))
                    throw new CdpException("Target.createTarget returned no targetId");

                var attached = await client.SendAsync("Target.attachToTarget",
                    "{\"targetId\":" + Json.Quote(targetId) + ",\"flatten\":true}").ConfigureAwait(false);
                var sessionId = Json.Str(attached, "sessionId");
                if (string.IsNullOrEmpty(sessionId))
                    throw new CdpException("Target.attachToTarget returned no sessionId");

                await client.SendAsync("Page.enable", null, sessionId).ConfigureAwait(false);
                await client.SendAsync("Runtime.enable", null, sessionId).ConfigureAwait(false);
                await client.SendAsync("Runtime.addBinding",
                    "{\"name\":" + Json.Quote(CdpBridgeJs.EventBinding) + "}", sessionId).ConfigureAwait(false);
                // A transparent page lets the document composite over the Unity scene like the real thing.
                await client.SendAsync("Emulation.setDefaultBackgroundColorOverride",
                    "{\"color\":{\"r\":0,\"g\":0,\"b\":0,\"a\":0}}", sessionId).ConfigureAwait(false);
                // Every document is its own target and none of them is the browser's focused window, so without
                // this a page never believes it has focus: no caret, no focus styles, and keys go nowhere.
                try { await client.SendAsync("Emulation.setFocusEmulationEnabled", "{\"enabled\":true}", sessionId).ConfigureAwait(false); }
                catch (CdpException) { /* an older Chrome; typing will need the non-headless window */ }
                // Re-inject after any navigation the document's own content might trigger.
                await client.SendAsync("Page.addScriptToEvaluateOnNewDocument",
                    "{\"source\":" + Json.Quote(CdpBridgeJs.Source) + "}", sessionId).ConfigureAwait(false);

                // Target.createTarget returns before the served page has loaded. Everything FlushPanel evaluates
                // must land in that document, not in the initial about:blank the navigation replaces.
                if (_origin != null)
                    await WaitForDocumentAsync(client, sessionId, pageUrl).ConfigureAwait(false);
                await EvaluateAsync(client, sessionId, CdpBridgeJs.Source).ConfigureAwait(false);

                Post(() =>
                {
                    if (!_panels.ContainsKey(panel.Id))
                        return;   // destroyed while we were setting it up
                    panel.TargetId = targetId;
                    panel.SessionId = sessionId;
                    panel.Ready = true;
                    panel.ReadyTime = NowSeconds();
                    _bySession[sessionId] = panel;
                    FlushPanel(panel);
                });
            }
            catch (OperationCanceledException) { }
            catch (Exception e)
            {
                Post(() =>
                {
                    // Let the document wire up anyway (its writes become no-ops) rather than hold Created forever.
                    panel.SetupFailed = true;
                    Debug.LogWarning($"[Hiccup] Editor preview could not create a page for document {panel.Id}: {e.Message}");
                });
            }
        }

        private static async Task WaitForDocumentAsync(CdpClient client, string sessionId, string url)
        {
            var probe = "location.href === " + Json.Quote(url) + " && document.readyState === 'complete'";
            for (int i = 0; i < 100; i++)   // 2 s; a loopback page normally loads within the first poll or two
            {
                var reply = await EvaluateAsync(client, sessionId, probe).ConfigureAwait(false);
                var result = Json.Dict(reply, "result");
                if (result != null && result.TryGetValue("value", out var v) &&
                    (v is bool b ? b : string.Equals(Convert.ToString(v), "true", StringComparison.OrdinalIgnoreCase)))
                    return;
                await Task.Delay(20).ConfigureAwait(false);
            }
        }

        /// <summary>Pushes the panel's whole authoritative state to the browser. Main thread.</summary>
        private void FlushPanel(Panel panel)
        {
            if (!panel.Ready || !Connected)
                return;

            ApplyMetrics(panel);
            Eval(panel, "init", null, panel.Id.ToString(CultureInfo.InvariantCulture));
            Eval(panel, "setCss", panel.Css);
            Eval(panel, "setHtml", panel.Html);
            Eval(panel, "setVisible", null, JsBool(panel.Visible));
            foreach (var type in panel.Listened)
                Eval(panel, "listen", type, "true");

            StartScreencast(panel);
        }

        private void ApplyMetrics(Panel panel)
        {
            if (!panel.Ready || !Connected)
                return;
            _client.Send("Emulation.setDeviceMetricsOverride",
                "{\"width\":" + panel.Width +
                ",\"height\":" + panel.Height +
                ",\"deviceScaleFactor\":" + Json.Number(panel.Scale) +
                ",\"mobile\":false}", panel.SessionId);
        }

        private void StartScreencast(Panel panel)
        {
            if (!panel.Ready || !Connected)
                return;
            panel.Screencasting = true;
            _client.Send("Page.startScreencast",
                "{\"format\":\"png\",\"maxWidth\":" + PixelWidth(panel) +
                ",\"maxHeight\":" + PixelHeight(panel) + ",\"everyNthFrame\":1}", panel.SessionId);
        }

        private int PixelWidth(Panel p) => Mathf.Max(1, Mathf.RoundToInt(p.Width * p.Scale));
        private int PixelHeight(Panel p) => Mathf.Max(1, Mathf.RoundToInt(p.Height * p.Scale));

        private const string EvaluateHead = "{\"expression\":";
        private const string EvaluateTail = ",\"returnByValue\":true,\"awaitPromise\":false}";
        private readonly StringBuilder _evaluate = new StringBuilder(4096);   // main thread only

        /// <summary>
        /// Sends <c>window.__HUI.&lt;function&gt;(...)</c> without waiting for the reply. The params are built in
        /// one pass: <paramref name="stringArg"/> is written straight into the expression as a JS string literal,
        /// escaped for the JSON around it at the same time, and <paramref name="rawArg"/> (a number, a bool, a JSON
        /// array) as JS source. String first, then raw, which is the order every bridge function takes them in.
        /// </summary>
        private void Eval(Panel panel, string function, string stringArg = null, string rawArg = null)
        {
            if (!panel.Ready || !Connected)
                return;
            var sb = _evaluate;
            sb.Clear();
            sb.Append(EvaluateHead).Append("\"window.__HUI.").Append(function).Append('(');
            if (stringArg != null)
                Json.Quote(stringArg, sb, nested: true);
            if (rawArg != null)
            {
                if (stringArg != null)
                    sb.Append(',');
                Json.Escape(rawArg, sb);
            }
            sb.Append(")\"").Append(EvaluateTail);
            _client.Send("Runtime.evaluate", sb.ToString(), panel.SessionId);
        }

        /// <summary>The <c>Runtime.evaluate</c> params for an arbitrary expression.</summary>
        private static string EvaluateParams(string expression)
        {
            var sb = new StringBuilder(expression.Length + EvaluateHead.Length + EvaluateTail.Length + 2);
            sb.Append(EvaluateHead);
            Json.Quote(expression, sb);
            sb.Append(EvaluateTail);
            return sb.ToString();
        }

        private static Task<Dictionary<string, object>> EvaluateAsync(CdpClient client, string sessionId, string expression)
            => client.SendAsync("Runtime.evaluate", EvaluateParams(expression), sessionId);

        /// <summary>
        /// Blocks on a command reply for at most <paramref name="milliseconds"/>. False on a timeout or when the
        /// command failed (a CDP error, a detached session, a dropped socket), which <see cref="Task.Wait(int)"/>
        /// reports by throwing rather than returning.
        /// </summary>
        private static bool TryWait(Task<Dictionary<string, object>> task, int milliseconds, out Dictionary<string, object> reply)
        {
            reply = null;
            try
            {
                if (!task.Wait(milliseconds))
                    return false;
            }
            catch (AggregateException)
            {
                return false;
            }
            reply = task.Result;
            return true;
        }

        private static string ValueToString(object value)
            => value as string ?? Convert.ToString(value, CultureInfo.InvariantCulture);

        private static string JsBool(bool value) => value ? "true" : "false";

        // ------------------------------------------------------------------ IHtmlBackend content API

        public void PanelSetHtml(int id, string html)
        {
            if (!_panels.TryGetValue(id, out var p))
                return;
            p.Html = html ?? string.Empty;
            if (p.Ready)
                Eval(p, "setHtml", p.Html);
        }

        public void PanelSetCss(int id, string css)
        {
            if (!_panels.TryGetValue(id, out var p))
                return;
            p.Css = css ?? string.Empty;
            if (p.Ready)
                Eval(p, "setCss", p.Css);
        }

        public void PanelSetSize(int id, int width, int height)
        {
            if (!_panels.TryGetValue(id, out var p))
                return;
            width = Mathf.Max(1, width);
            height = Mathf.Max(1, height);
            if (p.Width == width && p.Height == height)
                return;
            p.Width = width;
            p.Height = height;
            ApplyMetrics(p);
            if (p.Screencasting)
                StartScreencast(p);   // refresh the cap so the frame is not downscaled
        }

        public void PanelSetVisible(int id, bool visible)
        {
            if (!_panels.TryGetValue(id, out var p))
                return;
            p.Visible = visible;
            if (p.Ready)
                Eval(p, "setVisible", null, JsBool(visible));
        }

        public void PanelSetMipmaps(int id, bool enabled)
        {
            if (!_panels.TryGetValue(id, out var p) || p.Mipmaps == enabled)
                return;
            p.Mipmaps = enabled;
            if (p.Target == null)
                return;
            // Rebuild the target with the new sampler state and refill it from the last frame, so nothing goes blank.
            EnsureTarget(p, p.TextureWidth, p.TextureHeight);
            if (p.Staging != null)
                BlitToTarget(p);
        }

        public void PanelSetResolutionScale(int id, float scale)
        {
            if (!_panels.TryGetValue(id, out var p))
                return;
            scale = Mathf.Clamp(scale, 0.25f, 4f);
            if (Mathf.Approximately(p.Scale, scale))
                return;
            p.Scale = scale;
            ApplyMetrics(p);
            if (p.Screencasting)
                StartScreencast(p);
        }

        public void PanelListen(int id, string eventType, bool enabled)
        {
            if (!_panels.TryGetValue(id, out var p) || string.IsNullOrEmpty(eventType))
                return;
            if (enabled ? !p.Listened.Add(eventType) : !p.Listened.Remove(eventType))
                return;
            if (p.Ready)
                Eval(p, "listen", eventType, JsBool(enabled));
        }

        public void PanelInvalidate(int id) { /* the browser repaints on its own; nothing to force */ }

        public void PanelAnnounce(int id, string text, bool assertive)
        {
            if (!_panels.TryGetValue(id, out var p) || !p.Ready)
                return;
            Eval(p, "announce", text ?? string.Empty, JsBool(assertive));
        }

        public string PanelEval(int id, string javascript)
        {
            if (!_panels.TryGetValue(id, out var p) || !p.Ready || !Connected)
                return string.Empty;

            // Give the caller the same synchronous contract the jslib has, at the cost of a short block. The code is a
            // function body, exactly as Hiccup_PanelEval's `new Function('panel','root','HUI', code)` treats it: statements
            // are allowed and a value comes back only through `return`.
            var wrapped = "(function(panel,root,HUI){" + javascript + "\n})(window.__HUI.panel,window.__HUI.content,window.__HUI)";
            if (!TryWait(EvaluateAsync(_client, p.SessionId, wrapped), 250, out var reply))
                return string.Empty;

            var result = Json.Dict(reply, "result");
            if (result == null)
                return string.Empty;
            if (result.TryGetValue("value", out var value) && value != null)
                return ValueToString(value);
            return Json.Str(result, "description", string.Empty);
        }

        public void PanelSetGeometry(int id, float[] columnMajor)
        {
            if (!_panels.TryGetValue(id, out var p) || columnMajor == null || columnMajor.Length < 16)
                return;
            var m = new Matrix4x4();
            for (int c = 0; c < 4; c++)
            {
                for (int r = 0; r < 4; r++)
                    m[r, c] = columnMajor[c * 4 + r];
            }
            p.PixelToClip = m;
            p.HasGeometry = true;
        }

        public Texture PanelGetTexture(int id) => _panels.TryGetValue(id, out var p) ? p.Target : null;

        public void PanelGetTextureSize(int id, int[] outWidthHeight)
        {
            if (outWidthHeight == null || outWidthHeight.Length < 2)
                return;
            if (!_panels.TryGetValue(id, out var p))
            {
                outWidthHeight[0] = outWidthHeight[1] = 0;
                return;
            }
            // Before the first frame lands, report the size the browser was asked for.
            outWidthHeight[0] = p.TextureWidth > 0 ? p.TextureWidth : PixelWidth(p);
            outWidthHeight[1] = p.TextureHeight > 0 ? p.TextureHeight : PixelHeight(p);
        }

        // ------------------------------------------------------------------ elements

        /// <summary>
        /// What an element handle actually is: a recipe for finding the element again, not a reference to it.
        /// The browser resolves it per operation, which avoids a round trip on every <c>Q()</c> and keeps the
        /// page from holding references to elements Unity has forgotten about.
        /// </summary>
        private sealed class ElementSpec
        {
            public int PanelId;
            public string Selector;
            public int Index = -1;
            public bool Parent;
            public ElementSpec Of;
        }

        // Handles are never reused by the runtime API, and HtmlElement.Dispose is optional, so the table is
        // bounded and the oldest entries are evicted. Handles are used within a frame of being created.
        private const int MaxHandles = 8192;
        private readonly Dictionary<int, ElementSpec> _handles = new Dictionary<int, ElementSpec>();
        private readonly Queue<int> _handleAge = new Queue<int>();
        private readonly Dictionary<int, StringBuilder> _pendingOps = new Dictionary<int, StringBuilder>();
        private int _nextHandle = 1;

        private int AddHandle(ElementSpec spec)
        {
            int handle = _nextHandle++;
            _handles[handle] = spec;
            _handleAge.Enqueue(handle);
            while (_handleAge.Count > MaxHandles)
                _handles.Remove(_handleAge.Dequeue());
            return handle;
        }

        private ElementSpec Spec(int handle) => handle != 0 && _handles.TryGetValue(handle, out var s) ? s : null;

        public int Query(int panel, string selector)
        {
            if (!_panels.ContainsKey(panel) || string.IsNullOrEmpty(selector))
                return 0;
            return AddHandle(new ElementSpec { PanelId = panel, Selector = selector });
        }

        public string QueryAll(int panel, string selector)
        {
            if (!_panels.ContainsKey(panel) || string.IsNullOrEmpty(selector))
                return string.Empty;

            int count = (int)ReadNumber(new ElementSpec { PanelId = panel, Selector = selector }, "count", null, 0);
            if (count <= 0)
                return string.Empty;

            var sb = new StringBuilder();
            for (int i = 0; i < count; i++)
            {
                if (i > 0)
                    sb.Append(',');
                sb.Append(AddHandle(new ElementSpec { PanelId = panel, Selector = selector, Index = i }));
            }
            return sb.ToString();
        }

        public int ElemQuery(int handle, string selector)
        {
            var spec = Spec(handle);
            if (spec == null || string.IsNullOrEmpty(selector))
                return 0;
            return AddHandle(new ElementSpec { PanelId = spec.PanelId, Selector = selector, Of = spec });
        }

        public int ElemParent(int handle)
        {
            var spec = Spec(handle);
            if (spec == null)
                return 0;
            return AddHandle(new ElementSpec { PanelId = spec.PanelId, Parent = true, Of = spec });
        }

        public void ElemRelease(int handle)
        {
            if (handle != 0)
                _handles.Remove(handle);
        }

        // ---- writes: queued and flushed once per frame

        public void ElemSetText(int handle, string value) => Write(handle, "text", value);
        public void ElemSetHtml(int handle, string value) => Write(handle, "html", value);
        public void ElemInsertHtml(int handle, string where, string html) => Write(handle, "insert", where, html);
        public void ElemSetAttr(int handle, string name, string value) => Write(handle, "attr", name, value);
        public void ElemRemoveAttr(int handle, string name) => Write(handle, "rmattr", name);
        public void ElemSetProp(int handle, string name, string value) => Write(handle, "prop", name, value);
        public void ElemSetBoolProp(int handle, string name, bool value) => Write(handle, "boolprop", name, JsBool(value), quoteB: false);
        public void ElemSetStyle(int handle, string name, string value) => Write(handle, "style", name, value);
        public void ElemAddClass(int handle, string className) => Write(handle, "addcls", className);
        public void ElemRemoveClass(int handle, string className) => Write(handle, "rmcls", className);
        public void ElemFocus(int handle) => Write(handle, "focus");
        public void ElemBlur(int handle) => Write(handle, "blur");
        public void ElemClick(int handle) => Write(handle, "click");
        public void ElemScrollIntoView(int handle) => Write(handle, "scroll");

        public void ElemToggleClass(int handle, string className, int force) => Write(handle, "tglcls", className, force.ToString(CultureInfo.InvariantCulture), quoteB: false);
        public void ElemShowModal(int handle, bool show) => Write(handle, "modal", JsBool(show), quoteA: false);

        public void ElemRemove(int handle)
        {
            Write(handle, "remove");
            ElemRelease(handle);
        }

        // ---- reads: a blocking round trip, so only where the API cannot avoid one

        public string ElemEnsureId(int handle) => ReadString(Spec(handle), "ensureid", null);
        public string ElemGetText(int handle) => ReadString(Spec(handle), "text", null);
        public string ElemGetHtml(int handle) => ReadString(Spec(handle), "html", null);
        public string ElemGetAttr(int handle, string name) => ReadString(Spec(handle), "attr", name);
        public bool ElemHasAttr(int handle, string name) => ReadBool(Spec(handle), "hasattr", name);
        public string ElemGetProp(int handle, string name) => ReadString(Spec(handle), "prop", name);
        public bool ElemGetBoolProp(int handle, string name) => ReadBool(Spec(handle), "boolprop", name);
        public string ElemGetStyle(int handle, string name) => ReadString(Spec(handle), "style", name);
        public bool ElemHasClass(int handle, string className) => ReadBool(Spec(handle), "hascls", className);
        public bool ElemMatches(int handle, string selector) => ReadBool(Spec(handle), "matches", selector);

        public void ElemGetBounds(int handle, float[] outXYWH)
        {
            if (outXYWH == null || outXYWH.Length < 4)
                return;
            outXYWH[0] = outXYWH[1] = outXYWH[2] = outXYWH[3] = 0f;

            var text = ReadString(Spec(handle), "bounds", null);
            if (string.IsNullOrEmpty(text))
                return;
            var parts = text.Split(',');
            for (int i = 0; i < 4 && i < parts.Length; i++)
            {
                float.TryParse(parts[i], NumberStyles.Float, CultureInfo.InvariantCulture, out outXYWH[i]);
            }
        }

        // ---- op plumbing

        /// <summary>
        /// Queues one op. Arguments are JSON-quoted strings unless <paramref name="quoteA"/>/<paramref name="quoteB"/>
        /// say the value is already JSON (a bool or a number).
        /// </summary>
        private void Write(int handle, string op, string a = null, string b = null, bool quoteA = true, bool quoteB = true)
        {
            var spec = Spec(handle);
            if (spec == null)
                return;
            var sb = BeginOp(spec, op);
            if (sb == null)
                return;
            if (a != null)
            {
                sb.Append(",\"a\":");
                AppendArg(sb, a, quoteA);
            }
            if (b != null)
            {
                sb.Append(",\"b\":");
                AppendArg(sb, b, quoteB);
            }
            sb.Append('}');
        }

        private static void AppendArg(StringBuilder sb, string value, bool quote)
        {
            if (quote)
                Json.Quote(value, sb);
            else
                sb.Append(value);
        }

        /// <summary>Opens an op object in the panel's pending batch, or returns null if the panel is gone.</summary>
        private StringBuilder BeginOp(ElementSpec spec, string op)
        {
            if (!_panels.TryGetValue(spec.PanelId, out var panel) || !panel.Ready)
                return null;

            if (!_pendingOps.TryGetValue(spec.PanelId, out var sb))
                _pendingOps[spec.PanelId] = sb = new StringBuilder("[");
            if (sb.Length > 1)
                sb.Append(',');

            sb.Append("{\"o\":");
            Json.Quote(op, sb);
            sb.Append(",\"h\":");
            AppendSpec(sb, spec);
            return sb;
        }

        private static void AppendSpec(StringBuilder sb, ElementSpec spec)
        {
            sb.Append('{');
            bool first = true;
            if (spec.Selector != null)
            {
                sb.Append("\"s\":");
                Json.Quote(spec.Selector, sb);
                first = false;
            }
            if (spec.Index >= 0)
            {
                if (!first)
                    sb.Append(',');
                sb.Append("\"i\":").Append(spec.Index);
                first = false;
            }
            if (spec.Parent)
            {
                if (!first)
                    sb.Append(',');
                sb.Append("\"up\":true");
                first = false;
            }
            if (spec.Of != null)
            {
                if (!first)
                    sb.Append(',');
                sb.Append("\"p\":");
                AppendSpec(sb, spec.Of);
            }
            sb.Append('}');
        }

        /// <summary>Sends every queued write for a panel. Reads call this first so they see their own writes.</summary>
        private void FlushOps(int panelId)
        {
            if (!_pendingOps.TryGetValue(panelId, out var sb) || sb.Length <= 1)
                return;
            sb.Append(']');
            var ops = sb.ToString();
            sb.Clear();
            sb.Append('[');

            if (_panels.TryGetValue(panelId, out var panel) && panel.Ready && Connected)
                Eval(panel, "apply", null, ops);
        }

        private void FlushAllOps()
        {
            foreach (var panelId in _pendingOps.Keys)
                FlushOps(panelId);
        }

        private Dictionary<string, object> ReadRaw(ElementSpec spec, string op, string argument)
        {
            if (spec == null)
                return null;
            if (!_panels.TryGetValue(spec.PanelId, out var panel) || !panel.Ready || !Connected)
                return null;
            FlushOps(spec.PanelId);

            var expression = new StringBuilder("window.__HUI.read(");
            AppendSpec(expression, spec);
            expression.Append(',');
            Json.Quote(op, expression);
            expression.Append(',');
            if (argument == null)
                expression.Append("null");
            else
                Json.Quote(argument, expression);
            expression.Append(')');

            // A short block: the browser is local, so this is a fraction of a millisecond in practice.
            if (!TryWait(EvaluateAsync(_client, panel.SessionId, expression.ToString()), 100, out var reply))
                return null;
            return Json.Dict(reply, "result");
        }

        private string ReadString(ElementSpec spec, string op, string argument)
        {
            var result = ReadRaw(spec, op, argument);
            if (result == null || !result.TryGetValue("value", out var value) || value == null)
                return string.Empty;
            return ValueToString(value);
        }

        private bool ReadBool(ElementSpec spec, string op, string argument)
        {
            var result = ReadRaw(spec, op, argument);
            return result != null && result.TryGetValue("value", out var value) && value is bool b && b;
        }

        private double ReadNumber(ElementSpec spec, string op, string argument, double fallback)
        {
            var result = ReadRaw(spec, op, argument);
            return result != null && result.TryGetValue("value", out var value) && value is double d ? d : fallback;
        }

        public void GetCanvasInfo(float[] outInfo)
        {
            if (outInfo == null || outInfo.Length < 5)
                return;
            outInfo[0] = Screen.width;
            outInfo[1] = Screen.height;
            outInfo[2] = 1f;
            outInfo[3] = Screen.width;
            outInfo[4] = Screen.height;
        }

        // ------------------------------------------------------------------ per-frame

        public void Update()
        {
            while (_mainThread.TryDequeue(out var action))
            {
                try { action(); }
                catch (Exception e) { Debug.LogException(e); }
            }

            if (_client == null)
                return;
            if (_client.Fault != null && !_failed)
            {
                _failed = true;
                Status = "disconnected: " + _client.Fault.Message;
                Debug.LogWarning("[Hiccup] Editor preview lost its connection to Chrome: " + _client.Fault.Message);
            }

            // Dispatching an event can run user code that destroys a document, so snapshot before iterating.
            while (_client.TryDequeueEvent(out var evt))
                HandleEvent(evt);

            // Element writes queued by game code this frame go out as one batch per document.
            FlushAllOps();

            double now = NowSeconds();
            bool haveMouse = EditorPointer.TryGetMouse(out var mousePosition, out bool mouseDown);
            _iteration.Clear();
            _iteration.AddRange(_panels.Values);
            bool pointerInAnyPanel = false;
            foreach (var panel in _iteration)
            {
                ApplyPendingFrame(panel);
                PumpCaptureFallback(panel, now);
                if (haveMouse)
                    pointerInAnyPanel |= PumpPointer(panel, mousePosition, mouseDown);
            }

            // A press that lands in no document takes keyboard focus away, as a click on the page would.
            if (haveMouse)
            {
                if (mouseDown && !_mouseWasDown && !pointerInAnyPanel)
                    _keyboardPanel = null;
                _mouseWasDown = mouseDown;
            }

            PumpKeyboard();
        }

        private void HandleEvent(Dictionary<string, object> evt)
        {
            var method = Json.Str(evt, "method");
            var sessionId = Json.Str(evt, "sessionId");
            var parameters = Json.Dict(evt, "params");

            // Screencast frames never get here: CdpClient hands them to OnScreencastFrame on the receive thread.
            switch (method)
            {
                case "Runtime.bindingCalled":
                {
                    if (sessionId == null || !_bySession.TryGetValue(sessionId, out var panel))
                        return;
                    if (Json.Str(parameters, "name") != CdpBridgeJs.EventBinding)
                        return;
                    var payload = Json.Str(parameters, "payload");
                    if (!string.IsNullOrEmpty(payload))
                        HtmlBackend.DispatchEvent(panel.Id, payload);
                    break;
                }

                case "Runtime.consoleAPICalled":
                {
                    if (!_debug)
                        return;
                    Debug.Log("[Hiccup/page] " + Json.Str(parameters, "type") + ": " + DescribeArgs(parameters));
                    break;
                }

                case "Target.detachedFromTarget":
                {
                    var gone = Json.Str(parameters, "sessionId");
                    if (gone != null && _bySession.TryRemove(gone, out var panel))
                    {
                        panel.Ready = false;
                        panel.Screencasting = false;
                    }
                    break;
                }
            }
        }

        private static string DescribeArgs(Dictionary<string, object> parameters)
        {
            if (parameters == null || !parameters.TryGetValue("args", out var raw) || !(raw is List<object> args))
                return string.Empty;
            var sb = new StringBuilder();
            foreach (var arg in args)
            {
                var a = arg as Dictionary<string, object>;
                if (a == null)
                    continue;
                if (sb.Length > 0)
                    sb.Append(' ');
                sb.Append(a.TryGetValue("value", out var v) && v != null ? v.ToString() : Json.Str(a, "description", ""));
            }
            return sb.ToString();
        }

        // ------------------------------------------------------------------ frames -> textures

        /// <summary>
        /// Receive thread. Routes a screencast frame to its panel's decoder and acknowledges it at once — Chrome
        /// stalls the screencast until each frame is acknowledged, and nothing here needs the main thread.
        /// </summary>
        private void OnScreencastFrame(CdpClient client, string sessionId, int frameId, byte[] image, int imageLength)
        {
            if (sessionId == null)
            {
                FrameBuffers.Return(image);
                return;
            }
            if (image != null)
            {
                if (_bySession.TryGetValue(sessionId, out var panel))
                    SubmitFrame(panel, image, imageLength);
                else
                    FrameBuffers.Return(image);
            }
            client.Send("Page.screencastFrameAck", "{\"sessionId\":" + frameId + "}", sessionId);
        }

        /// <summary>
        /// Any thread. Hands an encoded frame to the panel's decode loop, starting one if none is running.
        /// Only the newest undecoded frame is kept: if Chrome outpaces the decoder, intermediate frames are dropped
        /// and their buffers go back to <see cref="FrameBuffers"/>, as does every frame once it has been decoded.
        /// </summary>
        private static void SubmitFrame(Panel panel, byte[] png, int length)
        {
            byte[] dropped;
            bool start;
            lock (panel.FrameLock)
            {
                dropped = panel.QueuedPng;
                panel.QueuedPng = png;
                panel.QueuedPngLength = length;
                start = !panel.Decoding;
                panel.Decoding = true;
            }
            FrameBuffers.Return(dropped);
            if (start)
                Task.Run(() => DecodeLoop(panel));
        }

        /// <summary>
        /// Pool thread. Decodes frames for one panel until none are queued, so at most one decode per panel is
        /// in flight and the decoder's scratch buffers are never shared. Pixel buffers are recycled through
        /// <see cref="Panel.FreePixels"/> and encoded ones through <see cref="FrameBuffers"/>, so a steady stream
        /// of frames allocates nothing.
        /// </summary>
        private static void DecodeLoop(Panel panel)
        {
            while (true)
            {
                byte[] png;
                int pngLength;
                byte[] pixels = null;
                lock (panel.FrameLock)
                {
                    png = panel.QueuedPng;
                    pngLength = panel.QueuedPngLength;
                    panel.QueuedPng = null;
                    if (png == null)
                    {
                        panel.Decoding = false;
                        return;
                    }
                    if (panel.FreePixels.Count > 0)
                        pixels = panel.FreePixels.Pop();
                }

                var decoder = panel.Decoder ??= new PngDecoder();
                bool decoded;
                int width = 0, height = 0;
                try { decoded = decoder.TryDecode(png, pngLength, ref pixels, out width, out height); }
                catch (Exception) { decoded = false; }

                // Not a PNG this decoder handles; let LoadImage have a go on the main thread. It wants an array
                // that is exactly the image, so the rare rejected frame is copied out of the pooled buffer.
                byte[] rejected = null;
                if (!decoded)
                {
                    rejected = new byte[pngLength];
                    Buffer.BlockCopy(png, 0, rejected, 0, pngLength);
                }

                lock (panel.FrameLock)
                {
                    if (decoded)
                    {
                        var replaced = panel.Pending;
                        panel.Pending = new DecodedFrame { Rgba = pixels, Width = width, Height = height };
                        panel.PendingFrame = null;
                        if (replaced != null)
                            RecyclePixels(panel, replaced.Rgba);
                    }
                    else
                    {
                        if (pixels != null)
                            RecyclePixels(panel, pixels);
                        panel.PendingFrame = rejected;
                    }
                }
                FrameBuffers.Return(png);
            }
        }

        /// <summary>Under <see cref="Panel.FrameLock"/>.</summary>
        private static void RecyclePixels(Panel panel, byte[] pixels)
        {
            if (pixels != null && panel.FreePixels.Count < MaxFreePixelBuffers)
                panel.FreePixels.Push(pixels);
        }

        /// <summary>Main thread. Uploads the newest decoded frame and blits it into the panel's texture.</summary>
        private void ApplyPendingFrame(Panel panel)
        {
            DecodedFrame frame;
            byte[] encoded;
            lock (panel.FrameLock)
            {
                frame = panel.Pending;
                panel.Pending = null;
                encoded = panel.PendingFrame;
                panel.PendingFrame = null;
            }
            if (frame == null && encoded == null)
                return;

            if (frame != null)
            {
                EnsureStaging(panel, frame.Width, frame.Height);
                panel.Staging.LoadRawTextureData(frame.Rgba);
                panel.Staging.Apply(false, false);
                lock (panel.FrameLock)
                    RecyclePixels(panel, frame.Rgba);
            }
            else
            {
                // LoadImage resizes and reformats the texture itself; EnsureStaging puts it back next time.
                EnsureStaging(panel, panel.TextureWidth > 0 ? panel.TextureWidth : 2, panel.TextureHeight > 0 ? panel.TextureHeight : 2);
                if (!panel.Staging.LoadImage(encoded, false))
                    return;
            }
            panel.LastFrameTime = NowSeconds();

            EnsureTarget(panel, panel.Staging.width, panel.Staging.height);
            if (panel.Target == null)
                return;
            BlitToTarget(panel);
        }

        /// <summary>Main thread. Premultiplies (and flips) the staging texture into the panel's target.</summary>
        private void BlitToTarget(Panel panel)
        {
            var material = BlitMaterialForThisFrame();
            bool previousSrgbWrite = GL.sRGBWrite;
            GL.sRGBWrite = false;   // write the premultiplied encoded values through unchanged
            if (material != null)
                Graphics.Blit(panel.Staging, panel.Target, material);
            else
                Graphics.Blit(panel.Staging, panel.Target);
            GL.sRGBWrite = previousSrgbWrite;

            if (panel.Target.useMipMap)
                panel.Target.GenerateMips();
        }

        /// <summary>
        /// The staging texture is flagged linear: that is not a claim about the data, it keeps Unity from applying
        /// an sRGB→linear conversion when the blit samples it, so the browser's encoded bytes reach the shader
        /// untouched and premultiplication happens in the same space the browser would have used.
        /// </summary>
        private static void EnsureStaging(Panel panel, int width, int height)
        {
            if (panel.Staging == null)
            {
                panel.Staging = new Texture2D(width, height, TextureFormat.RGBA32, false, true)
                {
                    name = "Hiccup staging",
                    hideFlags = HideFlags.HideAndDontSave,
                    wrapMode = TextureWrapMode.Clamp,
                };
            }
            else if (panel.Staging.width != width || panel.Staging.height != height || panel.Staging.format != TextureFormat.RGBA32)
            {
                panel.Staging.Reinitialize(width, height, TextureFormat.RGBA32, false);
            }
        }

        /// <summary>Makes sure the target exists with the given size and the panel's mipmap setting; same sampler set-up as the runtime's own textures.</summary>
        private void EnsureTarget(Panel panel, int width, int height)
        {
            if (panel.Target != null && panel.TextureWidth == width && panel.TextureHeight == height &&
                panel.Target.useMipMap == panel.Mipmaps)
                return;

            if (panel.Target != null)
            {
                panel.Target.Release();
                UnityEngine.Object.DestroyImmediate(panel.Target);
            }

            bool mipmaps = panel.Mipmaps;
            panel.TextureWidth = width;
            panel.TextureHeight = height;
            panel.Target = new RenderTexture(width, height, 0, GraphicsFormat.R8G8B8A8_SRGB)
            {
                name = "Hiccup preview " + panel.Id,
                hideFlags = HideFlags.HideAndDontSave,
                useMipMap = mipmaps,
                autoGenerateMips = false,
                filterMode = mipmaps ? FilterMode.Trilinear : FilterMode.Bilinear,
                anisoLevel = mipmaps ? 16 : 1,
                wrapMode = TextureWrapMode.Clamp,
            };
            panel.Target.Create();
        }

        private Material EnsureBlitMaterial()
        {
            if (_blit != null)
                return _blit;
            var shader = Shader.Find(BlitShaderName);
            if (shader == null)
            {
                Debug.LogWarning($"[Hiccup] {BlitShaderName} is missing; preview colors will not be premultiplied.");
                return null;
            }
            _blit = new Material(shader) { hideFlags = HideFlags.HideAndDontSave };
            return _blit;
        }

        private static readonly int s_FlipY = Shader.PropertyToID("_FlipY");

        private Material BlitMaterialForThisFrame()
        {
            var material = EnsureBlitMaterial();
            // Which way is up depends on the graphics API's texture origin, so it stays user-switchable.
            material?.SetFloat(s_FlipY, HtmlEditorPreview.FlipY ? 1f : 0f);
            return material;
        }

        private static void ReleaseTextures(Panel panel)
        {
            if (panel.Target != null)
            {
                panel.Target.Release();
                UnityEngine.Object.DestroyImmediate(panel.Target);
                panel.Target = null;
            }
            if (panel.Staging != null)
            {
                UnityEngine.Object.DestroyImmediate(panel.Staging);
                panel.Staging = null;
            }
            panel.TextureWidth = panel.TextureHeight = 0;
            byte[] queued;
            lock (panel.FrameLock)
            {
                queued = panel.QueuedPng;
                panel.QueuedPng = null;
                panel.Pending = null;
                panel.PendingFrame = null;
                panel.FreePixels.Clear();
            }
            FrameBuffers.Return(queued);
        }

        /// <summary>
        /// Some Chrome configurations never emit screencast frames (notably when the page is fully occluded).
        /// If nothing has arrived shortly after start-up, fall back to polling Page.captureScreenshot.
        /// </summary>
        private void PumpCaptureFallback(Panel panel, double now)
        {
            if (!panel.Ready || !Connected)
                return;

            if (!panel.UseCapturePolling)
            {
                if (panel.LastFrameTime > 0 || now - panel.ReadyTime < CaptureFallbackDelay)
                    return;
                panel.UseCapturePolling = true;
                Debug.Log("[Hiccup] Editor preview: no screencast frames, falling back to screenshot polling.");
                _client.Send("Page.stopScreencast", null, panel.SessionId);
                panel.Screencasting = false;
            }

            if (panel.CaptureInFlight || now - panel.LastCaptureRequest < CapturePollInterval)
                return;
            panel.CaptureInFlight = true;
            panel.LastCaptureRequest = now;

            var task = _client.SendAsync("Page.captureScreenshot",
                "{\"format\":\"png\",\"captureBeyondViewport\":false,\"fromSurface\":true}", panel.SessionId);
            task.ContinueWith(t =>
            {
                // Still on the pool: decode the base64 here and join the same worker decode path as the screencast.
                if (!t.IsFaulted && t.Result != null)
                {
                    var data = Json.Str(t.Result, "data");
                    if (!string.IsNullOrEmpty(data))
                    {
                        try
                        {
                            var png = Convert.FromBase64String(data);
                            SubmitFrame(panel, png, png.Length);
                        }
                        catch (FormatException) { }
                    }
                }
                Post(() => panel.CaptureInFlight = false);
            }, TaskScheduler.Default);
        }

        // ------------------------------------------------------------------ pointer input

        /// <summary>Returns whether the pointer is over the panel this frame.</summary>
        private bool PumpPointer(Panel panel, Vector2 screenPosition, bool buttonDown)
        {
            if (!panel.Ready || !Connected || !panel.HasGeometry || !panel.Visible)
                return false;

            bool inside = TryProjectToPanel(panel, screenPosition, out var documentPoint);
            documentPoint.x = Mathf.Clamp(documentPoint.x, 0, panel.Width);
            documentPoint.y = Mathf.Clamp(documentPoint.y, 0, panel.Height);

            // Track the pointer while it is over the panel, and while a press started there.
            if (!inside && !panel.PointerDown)
            {
                if (panel.PointerInside)
                {
                    panel.PointerInside = false;
                    DispatchMouse(panel, "mouseMoved", documentPoint, 0, false);
                }
                return false;
            }
            panel.PointerInside = inside;

            if (documentPoint != panel.LastPointer)
            {
                panel.LastPointer = documentPoint;
                DispatchMouse(panel, "mouseMoved", documentPoint, panel.PointerDown ? 1 : 0, panel.PointerDown);
            }

            if (buttonDown && !panel.PointerDown)
            {
                panel.PointerDown = true;
                _keyboardPanel = panel;
                DispatchMouse(panel, "mousePressed", documentPoint, 1, true);
            }
            else if (!buttonDown && panel.PointerDown)
            {
                panel.PointerDown = false;
                DispatchMouse(panel, "mouseReleased", documentPoint, 0, true);
            }
            return inside;
        }

        private void DispatchMouse(Panel panel, string type, Vector2 point, int buttons, bool leftButton)
        {
            _client.Send("Input.dispatchMouseEvent",
                "{\"type\":" + Json.Quote(type) +
                ",\"x\":" + Json.Number(point.x) +
                ",\"y\":" + Json.Number(point.y) +
                ",\"button\":" + (leftButton ? "\"left\"" : "\"none\"") +
                ",\"buttons\":" + buttons +
                ",\"clickCount\":" + (type == "mousePressed" || type == "mouseReleased" ? 1 : 0) +
                "}", panel.SessionId);
        }

        // ------------------------------------------------------------------ keyboard input

        /// <summary>Forwards the frame's key presses to the document the last click landed in.</summary>
        private void PumpKeyboard()
        {
            if (!_keyboardCapturing)
            {
                if (!Application.isPlaying)
                    return;
                HtmlBackend.SetKeyboardCapture(true);
                _keyboardCapturing = true;
                return;
            }

            _keys.Clear();
            HtmlBackend.DrainKeyPresses(_keys);
            if (_keys.Count == 0)
                return;

            var panel = _keyboardPanel;
            if (panel != null && panel.Ready && Connected && _panels.ContainsKey(panel.Id))
            {
                foreach (var press in _keys)
                    DispatchKey(panel, press);
            }
            _keys.Clear();
        }

        /// <summary>
        /// Turns an IMGUI key event into the DevTools key events Chrome expects. The shape follows what Puppeteer
        /// sends: a <c>keyDown</c> carrying <c>text</c> inserts the character and fires the page's keydown, keypress
        /// and input events; a <c>rawKeyDown</c> fires keydown alone, for keys that produce no text.
        /// </summary>
        private void DispatchKey(Panel panel, HtmlKeyPress press)
        {
            string key, code, text = null;
            int virtualKey;

            if (press.Character != '\0' && !char.IsControl(press.Character))
            {
                // A typed character. Where IMGUI also reports the key itself as a separate event, that event has
                // no character and falls through the branches below without producing anything.
                key = press.Character.ToString();
                text = key;
                DescribeCharacter(press.Character, out code, out virtualKey);
            }
            else if (TryDescribeSpecialKey(press.Key, out key, out code, out virtualKey, out text))
            {
            }
            else if ((press.Ctrl || press.Meta) && press.Key >= KeyCode.A && press.Key <= KeyCode.Z)
            {
                // Shortcuts: select-all, copy, paste, undo. Chrome maps these from the virtual key and modifiers.
                char letter = (char)('a' + (press.Key - KeyCode.A));
                key = letter.ToString();
                DescribeCharacter(letter, out code, out virtualKey);
            }
            else
            {
                return;
            }

            int modifiers = (press.Alt ? 1 : 0) | (press.Ctrl ? 2 : 0) | (press.Meta ? 4 : 0) | (press.Shift ? 8 : 0);
            var common = ",\"key\":" + Json.Quote(key) +
                         ",\"code\":" + Json.Quote(code) +
                         ",\"windowsVirtualKeyCode\":" + virtualKey +
                         ",\"nativeVirtualKeyCode\":" + virtualKey +
                         ",\"modifiers\":" + modifiers;

            var down = new StringBuilder("{\"type\":").Append(text != null ? "\"keyDown\"" : "\"rawKeyDown\"").Append(common);
            if (text != null)
            {
                down.Append(",\"text\":");
                Json.Quote(text, down);
                down.Append(",\"unmodifiedText\":");
                Json.Quote(text, down);
            }
            down.Append('}');
            _client.Send("Input.dispatchKeyEvent", down.ToString(), panel.SessionId);
            _client.Send("Input.dispatchKeyEvent", "{\"type\":\"keyUp\"" + common + "}", panel.SessionId);
        }

        private static void DescribeCharacter(char c, out string code, out int virtualKey)
        {
            if (c >= 'a' && c <= 'z')
            {
                code = "Key" + char.ToUpperInvariant(c);
                virtualKey = char.ToUpperInvariant(c);
            }
            else if (c >= 'A' && c <= 'Z')
            {
                code = "Key" + c;
                virtualKey = c;
            }
            else if (c >= '0' && c <= '9')
            {
                code = "Digit" + c;
                virtualKey = c;
            }
            else if (c == ' ')
            {
                code = "Space";
                virtualKey = 32;
            }
            else
            {
                code = string.Empty;
                virtualKey = 0;
            }
        }

        /// <summary>Keys that produce no character but that pages and form controls react to.</summary>
        private static bool TryDescribeSpecialKey(KeyCode keyCode, out string key, out string code, out int virtualKey, out string text)
        {
            text = null;
            switch (keyCode)
            {
                case KeyCode.Backspace: key = code = "Backspace"; virtualKey = 8; return true;
                case KeyCode.Tab: key = code = "Tab"; virtualKey = 9; return true;
                case KeyCode.Return:
                case KeyCode.KeypadEnter: key = code = "Enter"; virtualKey = 13; text = "\r"; return true;
                case KeyCode.Escape: key = code = "Escape"; virtualKey = 27; return true;
                case KeyCode.PageUp: key = code = "PageUp"; virtualKey = 33; return true;
                case KeyCode.PageDown: key = code = "PageDown"; virtualKey = 34; return true;
                case KeyCode.End: key = code = "End"; virtualKey = 35; return true;
                case KeyCode.Home: key = code = "Home"; virtualKey = 36; return true;
                case KeyCode.LeftArrow: key = code = "ArrowLeft"; virtualKey = 37; return true;
                case KeyCode.UpArrow: key = code = "ArrowUp"; virtualKey = 38; return true;
                case KeyCode.RightArrow: key = code = "ArrowRight"; virtualKey = 39; return true;
                case KeyCode.DownArrow: key = code = "ArrowDown"; virtualKey = 40; return true;
                case KeyCode.Insert: key = code = "Insert"; virtualKey = 45; return true;
                case KeyCode.Delete: key = code = "Delete"; virtualKey = 46; return true;
                default: key = code = null; virtualKey = 0; return false;
            }
        }

        /// <summary>
        /// Inverts the document's pixel-to-clip matrix for a single screen point. Solving the two projected
        /// equations directly (rather than inverting the matrix) keeps this correct for the perspective
        /// matrices <see cref="HtmlWorldSurface"/> produces.
        /// </summary>
        private static bool TryProjectToPanel(Panel panel, Vector2 screenPosition, out Vector2 documentPoint)
        {
            documentPoint = Vector2.zero;
            float screenWidth = Mathf.Max(1, Screen.width), screenHeight = Mathf.Max(1, Screen.height);
            float ndcX = screenPosition.x / screenWidth * 2f - 1f;
            float ndcY = screenPosition.y / screenHeight * 2f - 1f;

            var m = panel.PixelToClip;
            Vector4 c0 = m.GetColumn(0), c1 = m.GetColumn(1), c3 = m.GetColumn(3);

            // clip = px*c0 + py*c1 + c3, and ndc = clip.xy / clip.w.
            float a11 = c0.x - ndcX * c0.w, a12 = c1.x - ndcX * c1.w, b1 = ndcX * c3.w - c3.x;
            float a21 = c0.y - ndcY * c0.w, a22 = c1.y - ndcY * c1.w, b2 = ndcY * c3.w - c3.y;

            float det = a11 * a22 - a12 * a21;
            if (Mathf.Abs(det) < 1e-12f)
                return false;

            float px = (b1 * a22 - a12 * b2) / det;
            float py = (a11 * b2 - b1 * a21) / det;
            documentPoint = new Vector2(px, py);
            return px >= 0f && py >= 0f && px <= panel.Width && py <= panel.Height;
        }

        private static double NowSeconds() => UnityEditor.EditorApplication.timeSinceStartup;
    }
}
