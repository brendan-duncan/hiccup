# How the Editor preview renders

In a web build, `Hiccup.jslib` asks Chrome to composite a DOM subtree straight into a WebGL or WebGPU texture
that Unity already owns. None of that machinery exists in the Editor: there is no page, no canvas, no
HTML-in-Canvas API. The Editor preview reaches the same end state — an `HtmlDocument.Texture` that surfaces can
sample — by driving a real Chrome over the DevTools Protocol and copying frames back.

This document describes how that works, and where it deliberately diverges from the real bridge.

## What the preview is, and is not

It is genuinely Chrome, so layout, CSS, fonts, script behavior, form controls, dialogs and DOM event payloads
are all real. A document that lays out correctly here lays out correctly in a build.

It is not the shipping path. Three things exist only in a web build and cannot be checked here:

* **Accessibility** — screen readers, find-in-page, text selection. The whole point of HTML-in-Canvas is that the
  DOM stays in the page's accessibility tree. In the preview the DOM lives in a separate browser process that no
  assistive technology is pointed at.
* **IME and text composition.**
* **HTML-in-Canvas itself** — `layoutsubtree`, `drawable`, `texElementImage2D`, `copyElementImageToTexture`,
  `updateElementGeometry`, `getElementTransform`. The preview rasterizes the page instead of compositing it, so
  it cannot tell you whether those APIs behaved.

## The seam

Everything above the bridge is unchanged. The runtime talks to `HtmlNative`, a flat C ABI of about 55 functions
taking primitives and strings. In a web build those are `[DllImport("__Internal")]`. Everywhere else they are C#
stubs, and those stubs now forward to a registered `IHtmlBackend`:

```
HtmlDocument / HtmlElement / HtmlScreenSurface / HtmlWorldSurface   (unchanged)
                          |
                      HtmlNative
                     /            \
   [DllImport] Hiccup.jslib     HtmlBackend.Current  ->  CdpHtmlBackend  ->  Chrome
     (web builds)                  (Editor)
```

`HtmlBackend.Current` is null in player builds, so the web path is untouched. `HtmlNative.Available` reports
whether *either* bridge exists, which is what makes `HtmlDocument` allocate a texture instead of a placeholder.

### Files

| File | Role |
| --- | --- |
| `Runtime/HtmlBackend.cs` | `IHtmlBackend`, the registry, and keyboard capture for backends. |
| `Runtime/HtmlKeyboardRelay.cs` | Collects the Game view's key events through IMGUI. Runtime-side because Unity will not attach an Editor-assembly component. |
| `Runtime/HtmlNative.cs` | Editor stubs forward to the backend. |
| `Runtime/HtmlDocument.cs` | A third texture branch for backend-owned textures. |
| `Editor/HtmlEditorPreview.cs` | Settings, menu, and the lifecycle that guarantees Chrome dies with the session. |
| `Editor/Cdp/ChromeLauncher.cs` | Finds and starts Chrome, discovers its DevTools endpoint. |
| `Editor/Cdp/PreviewOrigin.cs` | Loopback HTTP server serving the empty page documents are created on, so they have a real origin. |
| `Editor/Cdp/CdpClient.cs` | JSON-RPC over one web socket. |
| `Editor/Cdp/Json.cs` | Minimal JSON reader/writer for protocol traffic. |
| `Editor/Cdp/PngDecoder.cs` | Decodes screencast PNGs off the main thread. |
| `Editor/Cdp/CdpBridgeJs.cs` | The script injected into every page. |
| `Editor/Cdp/CdpHtmlBackend.cs` | The backend: targets, frames, textures, input, elements. |
| `Editor/Cdp/EditorPointer.cs` | Reads the mouse without binding to either input backend. |
| `Editor/Cdp/Hiccup-EditorPremultiply.shader` | The blit that turns a screencast frame into a runtime texture. |

## Startup

`HtmlEditorPreview` registers the backend from `[RuntimeInitializeOnLoadMethod(BeforeSceneLoad)]`, which runs
before any scene object's `Awake`, so the first document to call `Create()` already has a backend to talk to.

`HtmlRuntime.Initialize` then calls `Hiccup_Init`, which reaches `CdpHtmlBackend.Init`. That returns
`HtmlRenderMode.Texture` **immediately** and kicks off browser startup in the background. Nothing blocks on
Chrome: documents are created, panels are allocated, and their content is buffered until the connection lands.

Chrome is started with `--remote-debugging-port=0` and a throwaway profile directory. Port 0 makes Chrome choose
a free port and write it, along with the browser-level web socket path, to `DevToolsActivePort` in that profile —
which is polled for up to 20 seconds. This avoids both hard-coding a port and racing another instance. By default
it runs `--headless=new`, which is a full browser and still screencasts; turning headless off positions a real
window at `-32000,-32000` instead. Backgrounding, timer throttling and occlusion throttling are all disabled, or
Chrome stops painting a window nobody is looking at.

Set `HICCUP_CHROME` (or `CHROME_PATH`) to override executable discovery.

## One browser target per document

Each `HtmlDocument` gets its own DevTools target. That makes the target's viewport *be* the document, so a
screencast frame is exactly the panel bitmap with no cropping or compositing on our side, and resizing a
document is a viewport change rather than a layout negotiation.

`SetUpPanelAsync` runs entirely off the main thread:

1. `Target.createTarget {url: <loopback page>}` — deliberately without `width`/`height`, which Chrome rejects
   unless it is opening a new window. The viewport is set below instead. The URL comes from `PreviewOrigin`, a
   one-page HTTP server on 127.0.0.1 that the backend starts with Chrome: an `about:blank` document has no origin
   and sends no Referer, which embeds such as YouTube refuse ("Error 153"). Once the bridge script is registered
   (step 6) the task polls `location.href` and `document.readyState` until the served page has replaced the
   initial blank one, because `createTarget` returns before the navigation and everything `FlushPanel` evaluates
   must land in the final document.
2. `Target.attachToTarget {flatten: true}` — every target shares the one web socket and is addressed by
   `sessionId`.
3. `Page.enable`, `Runtime.enable`.
4. `Runtime.addBinding {name: "HUI_Event"}` — the channel DOM events come back on.
5. `Emulation.setDefaultBackgroundColorOverride {a: 0}` — a transparent page, so the document composites over
   the Unity scene the way it does in a build.
6. `Page.addScriptToEvaluateOnNewDocument` with the bridge source, so it survives any navigation the content
   triggers, plus one `Runtime.evaluate` to install it in the page that already exists.

When that completes it posts a single action back to the main thread which flips the panel to ready and calls
`FlushPanel`. Only then is the panel's authoritative state pushed: viewport metrics, `init`, CSS, HTML,
visibility, the set of listened event types, and `Page.startScreencast`. The background task never reads mutable
panel state, so there is nothing to race — whatever the main thread changed while Chrome was starting is what
gets sent.

Panel-level state is buffered that way, but element operations are not: a write queued through `BeginOp`
before the panel is ready is dropped, and a read returns nothing. In a web build the DOM exists the moment the
panel does, so user code that fills a document from `HtmlDocument.Created` never meets that gap. To give it the
same contract here, `IHtmlBackend.PanelIsReady` reports the flip, and `HtmlDocument` holds `IsCreated` and the
`Created` event back until it does (polled from `AfterBridgeUpdate`). A panel whose page failed to set up, or a
backend that failed altogether, reports ready anyway so the scene wires up as it would with no preview at all.

## The frame pipeline

This is the part that replaces HTML-in-Canvas.

```
Chrome paints
   |  Page.screencastFrame  (base64 PNG, over the web socket)
   v
receive thread:  base64 decoded straight from the message bytes
                 + Page.screencastFrameAck  (or Chrome stalls)
   |
   v
pool thread:     PngDecoder -> RGBA32 bytes, bottom-up      (newest frame wins)
   |
   |  main thread, once per frame, from CdpHtmlBackend.Update()
   v
Texture2D.LoadRawTextureData + Apply  straight alpha, sRGB-encoded, bottom-up
   |
   |  Graphics.Blit(staging, target, Hiccup/EditorPremultiply)   with GL.sRGBWrite = false
   v
RenderTexture (R8G8B8A8_SRGB)         premultiplied, sRGB-encoded, top-down
   |
   v
HtmlDocument.Texture  ->  HtmlScreenSurface / HtmlWorldSurface
```

### Delivery

Chrome only emits a frame when the page actually changes, so an idle document costs nothing. Every frame **must**
be acknowledged with `Page.screencastFrameAck` or the screencast stalls after one frame.

A screencast message is almost entirely one base64 string, and a full-viewport frame runs to megabytes. Pushing
that through the generic protocol path meant a multi-megabyte string, a character-by-character copy of it inside
the JSON parser, and a third copy for the base64 decode — per frame, on top of the decode itself. Those
allocations were most of the Editor's hitching. `CdpClient` instead recognizes the message in its raw UTF-8
bytes, decodes the payload directly from them, and parses only the few hundred bytes that remain as JSON. The
frame reaches the backend on the receive thread through `ScreencastFrameHandler`, which acknowledges it at once
and queues it for decoding.

Only the newest undecoded frame is kept per document. If Chrome is painting faster than frames can be decoded,
intermediate frames are dropped rather than queued, which is the right trade for a preview.

### Decode

`Texture2D.LoadImage` can only run on the main thread, and a full-viewport frame costs it tens of milliseconds,
so it is not the primary decoder. Screencast frames are a narrow subset of PNG — 8-bit, RGB or RGBA,
non-interlaced — and `PngDecoder` handles exactly that subset on a pool thread: one decode per document at a
time, scratch buffers kept between frames, pixel buffers recycled through a small per-document pool so a steady
stream of frames allocates nothing. It writes rows bottom-up, matching `LoadImage`'s layout, so nothing after it
knows which decoder ran. The main thread's share is `LoadRawTextureData` plus `Apply` — an upload, not a decode.

A PNG outside that subset is handed to `LoadImage` on the main thread instead, so an unexpected Chrome
configuration degrades to the slow path rather than to a blank panel.

The staging texture is created with `linear: true` — that is not a claim that the data is linear, it is an
instruction that Unity must **not** apply an sRGB→linear conversion when the shader samples it. The blit therefore
sees the browser's encoded bytes verbatim, which matters for the next step.

### The blit

Three things happen in `Hidden/Hiccup/EditorPremultiply`, and each has a reason:

**Premultiply.** The runtime's shaders (`Hiccup/UI Premultiplied`, `Hiccup/Unlit Premultiplied`) blend
premultiplied alpha, because the browser's own snapshots are premultiplied. A screencast PNG is straight alpha,
so the preview has to premultiply — and crucially, *in the same space the browser would have*. The browser
premultiplies encoded values, not linear ones. Because the staging texture is flagged linear the shader reads
encoded values, multiplies `rgb` by `a` there, and the result matches.

**Flip.** `LoadImage` produces a texture whose `v = 0` is the bottom of the image. The runtime contract is
`HtmlDocument.TextureIsTopDown == true`: row 0 is the top of the document, and surfaces compensate with
`uvRect = (0, 1, 1, -1)`. So the blit samples `1 - uv.y` to put the document's top at `v = 0`, keeping the
preview texture interchangeable with the WebGL one. Whether that flip is correct depends on the graphics API's
render-target origin, which is why it is a `_FlipY` shader property wired to a menu toggle rather than a
hard-coded `1 - uv.y`.

**Pass-through, not re-encode.** The target is `GraphicsFormat.R8G8B8A8_SRGB` so that the UI shader linearizes
correctly when it samples the result — the same as the WebGL path. But writing to an sRGB target would normally
apply a linear→sRGB encode on output, which would corrupt values that are already encoded. `GL.sRGBWrite` is set
to `false` around the blit to suppress that, so the raw premultiplied bytes are stored as-is.

Net effect: the `RenderTexture` holds byte-for-byte what the WebGL path would have produced, and carries the same
sRGB flag. Nothing downstream — surfaces, shaders, materials — needs to know which bridge produced it. In a Gamma
color space project the sRGB flags are ignored throughout and the pass-through still yields the right bytes.

### Mipmaps and resizing

The target is created with `useMipMap` and `autoGenerateMips = false`, then `GenerateMips()` is called after each
blit — matching what the WebGL path does in `AfterBridgeUpdate`. Trilinear plus 16× anisotropy, clamped, which is
what world-space panels need.

The target is reallocated whenever the decoded frame's dimensions change. Document resizes go the other way:
`PanelSetSize` sends `Emulation.setDeviceMetricsOverride` and restarts the screencast so its `maxWidth`/`maxHeight`
cap does not silently downscale the new size. `resolutionScale` is the viewport's `deviceScaleFactor`, so
supersampling a world panel is a browser-side concern, as it is in a build.

### When screencasting does not work

Some Chrome configurations never emit frames. If nothing has arrived 2.5 seconds after a panel became ready, the
backend stops the screencast and falls back to polling `Page.captureScreenshot` at 20 Hz, with an in-flight guard
so requests cannot pile up. Screenshots rejoin the same decode pipeline, so only delivery differs. This costs a round
trip per frame and is a safety net, not the intended path.

## Reaching `HtmlDocument`

The backend owns its textures, which is the one place the runtime had to learn something new.

`HtmlDocument.CreateTexture` gains a branch ahead of the WebGPU and GL branches: when a backend is present it
asks for the panel's texture and sets `_ownsTexture = false`, so `ReleaseTexture` will not destroy something it
did not create. The texture will usually be null at that moment, because Chrome has not delivered a frame yet.

`AfterBridgeUpdate` — already called every frame by `HtmlRuntime` — picks up the change: if the backend's texture
is not the one the document is holding, it swaps it in, refreshes `TextureSize`, and raises `TextureChanged`.
That covers both the first frame and every reallocation. Surfaces read `Document.Texture` in their own
`LateUpdate`, so a new texture is visible on the following frame.

## Pointer input

Surfaces already tell the browser where a document is on screen: `HtmlDocument.SetGeometry` receives a
pixel-to-clip matrix each frame, built by `HtmlScreenSurface` from the RectTransform corners, or by
`HtmlWorldSurface` from the camera and mesh bounds. The preview inverts it.

For document pixel `(px, py)`, clip space is `px·c0 + py·c1 + c3` where `cN` are matrix columns, and NDC is
`clip.xy / clip.w`. Given a mouse position in NDC, that is two linear equations in two unknowns:

```
px·(c0.x − ndcX·c0.w) + py·(c1.x − ndcX·c1.w) = ndcX·c3.w − c3.x
px·(c0.y − ndcY·c0.w) + py·(c1.y − ndcY·c1.w) = ndcY·c3.w − c3.y
```

Solving the 2×2 system directly, rather than inverting the matrix and transforming a point, is what keeps this
correct for the perspective matrices `HtmlWorldSurface` produces. A UI panel on a tilted quad is clickable at the
right spot.

The result drives `Input.dispatchMouseEvent` in document CSS pixels — which is also the target's viewport, so no
further conversion. Moves are sent only when the position changes; a press that starts inside the panel keeps
tracking after the pointer leaves, so drags and releases outside still land.

The mouse itself is read through `EditorPointer`, which resolves `UnityEngine.InputSystem.Mouse` reflectively and
falls back to the legacy `Input` class. An assembly reference to a package that may not be installed would not
compile, and a project may be configured for either backend or both.

## Keyboard input

Keys are read through `HtmlKeyboardRelay`, a component whose `OnGUI` sees the Game view's IMGUI key events.
Those come from the native event system whichever input backend the project uses, so unlike the mouse this needs
no reflection into the Input System package. The backend asks for it with `HtmlBackend.SetKeyboardCapture(true)`,
which adds the relay to the runtime's own driver object, and drains it once per frame with `DrainKeyPresses`. The
relay lives in the runtime assembly because Unity refuses to attach a component defined in an Editor assembly;
nothing adds it in a player build.

Keys go to the document the last press landed in; a press outside every document clears that, as clicking the
page background would. Each press becomes the pair of `Input.dispatchKeyEvent` calls Puppeteer would send: a
`keyDown` carrying `text` for a typed character (Chrome inserts it and fires keydown, keypress and input), a
`rawKeyDown` for keys that produce no text (Backspace, Tab, arrows, Escape, Home/End, Page Up/Down, Delete), and a
`keyUp`. Enter carries `"\r"` so it submits forms and fires `change`. Ctrl or Cmd plus a letter goes out as a
shortcut with modifiers, which covers select-all, copy, paste and undo.

`Emulation.setFocusEmulationEnabled` is switched on for every target during setup. Each document is its own
target and none of them is the browser's focused window, so without it a page never believes it has focus: no
caret, no `:focus` styles, and keys land nowhere.

On Windows, IMGUI reports a typed key as two `KeyDown` events — one with the `KeyCode`, one with the character.
Only the one with a printable character inserts text; a `KeyCode` without a character is dispatched only when it
is a special key or a modifier shortcut, so nothing is doubled.

Unity still sees the same keys, as it sees the same clicks — `BlockUnityInput` is not modeled.

## Events

The injected bridge builds the same DOM structure as the jslib — `.hui-panel > style + .hui-content`, with the
same base stylesheet and pointer-mode rules — so selectors and CSS behave identically. It attaches the same
default event set and constructs a byte-identical payload, then hands it to the `HUI_Event` binding:

```json
{"type":"click","id":"go","tag":"button","name":"","action":"start","value":"",
 "isChecked":false,"key":"","code":"","x":53,"y":123,"button":0,
 "ctrl":false,"shift":false,"alt":false,"path":"","dataset":"action=start"}
```

That arrives as `Runtime.bindingCalled`, is routed to a panel by `sessionId`, and goes to
`HtmlBackend.DispatchEvent` → `HtmlRuntime.DispatchToPanel` → `HtmlDocument.DispatchNative`, which is the same
entry point the jslib callback uses. `JsonUtility` deserializes it into `HtmlEvent`, so `On`, `OnAction`,
`e.GetData` and ancestor bubbling all work as documented.

## The element API

`Q`, `QAll` and every `HtmlElement` operation work, but not the way the jslib implements them. The jslib keeps a
table of live element references and hands out indices into it. Doing that over a protocol would mean a blocking
round trip on every `Q()`, and the sample calls `Q()` six times per frame in its HUD update alone.

**A handle is a recipe, not a reference.** `Query` returns a client-side id immediately, with no browser traffic
at all; the backend remembers a description:

```js
{ s: "#screen-hud" }                          // querySelector under .hui-content
{ s: ".nav-btn", i: 1 }                       // the second match  (QAll)
{ s: "p", p: { s: "#screen-menu" } }          // nested query
{ up: true, p: { s: "#score" } }              // parentElement
```

`__HUI.resolve` walks that on each operation. A `querySelector` is cheap, and re-resolving is what keeps handles
correct after an `InnerHtml` replaces a subtree — the failure mode a cached reference would have.

**Writes are batched.** Text, attributes, classes, properties, `InsertHtml`, `ShowModal`, `Focus` and `Remove`
append to a per-document buffer and are flushed once per frame as a single `__HUI.apply([...])`. A HUD update
that touches six elements is one web socket message.

**Reads block**, briefly. `Value`, `GetAttribute`, `Checked`, `HasClass`, `Bounds`, `Matches`, `Id` and `QAll`
need an answer, so they wait up to 100 ms on a `Runtime.evaluate` (sub-millisecond in practice against a local
browser). Every read flushes that document's pending writes first, so a read always observes its own writes.
`HtmlDocument.Eval` works the same way with a 250 ms budget. The code is wrapped as a function body taking
`panel`, `root` and `HUI`, matching the jslib's `new Function('panel','root','HUI', code)`: statements are fine,
and a value comes back only through `return`.

The handle table is bounded at 8192 entries and evicts oldest-first, because `HtmlElement.Dispose` is optional
and `Q()` is cheap enough to call in a loop. Handles are used within a frame of being created, so eviction is not
observable in practice.

## Threading

Sends can come from the Unity main thread. Receiving runs on a background task.

* Command replies complete a `TaskCompletionSource` on the receive thread. They use
  `RunContinuationsAsynchronously`, and blocking reads use `Task.Wait(timeout)` rather than awaiting, so there is
  no continuation posted to Unity's synchronization context and no deadlock.
* Protocol *events* are queued and drained by `Update()` on the main thread, so texture creation and event
  dispatch happen where Unity requires them.
* Screencast frames never touch the main thread until they are pixels: the receive thread routes and acknowledges
  them, a pool thread decodes, and `Update()` uploads the newest result. The per-document frame slots they share
  sit under one lock, and the session→document map is a `ConcurrentDictionary` because the receive thread reads it.
* Async setup work posts its results through a main-thread action queue rather than touching backend state
  directly.
* The panel list is snapshotted before iteration, because dispatching an event can run user code that destroys a
  document.

## Lifecycle

An orphaned Chrome process is the classic failure of this kind of integration, so teardown is hooked in three
places: `AssemblyReloadEvents.beforeAssemblyReload`, `EditorApplication.quitting`, and
`PlayModeStateChange.ExitingPlayMode`. Any of them stops the backend, which disposes the socket, kills the
process and deletes the temporary profile.

The cancellation token source is deliberately never disposed: setup tasks may still hold its token.

## Frame order

`HtmlRuntime` carries `[DefaultExecutionOrder(10000)]`, so by the time it runs, documents and surfaces have
already done their work for the frame:

1. Game code mutates documents — queuing element writes.
2. Surfaces' `LateUpdate` — compute geometry, call `SetGeometry`, read `Document.Texture`.
3. `HtmlRuntime.LateUpdate` → `Hiccup_Update()` → `CdpHtmlBackend.Update()`:
   1. drain the main-thread action queue,
   2. drain protocol events (frames arrive, DOM events dispatch),
   3. flush queued element writes, one batch per document,
   4. per document: upload the newest decoded frame and blit it, service the capture fallback, dispatch pointer input,
   5. dispatch the frame's key presses to the focused document.
4. `HtmlDocument.AfterBridgeUpdate` — pick up a new or resized texture.

## Known gaps

| Gap | Consequence |
| --- | --- |
| IME, dead keys, non-Latin layouts | Keys are relayed one character per press as IMGUI reports them; composed input does not work. |
| `PointerMode` / `BlockUnityInput` ignored | Unity receives the same clicks the document does. |
| `PremultipliedAlpha = false` unsupported | The preview always premultiplies; a document that opts out will blend wrongly. The default is `true`. |
| `Mipmaps = false` ignored | Preview targets always have mipmaps. |
| Edit mode | The preview runs in play mode only; outside it, documents still show the placeholder. |
| Accessibility, IME, HTML-in-Canvas | Not reproducible by construction — see the top of this document. |

## Troubleshooting

**Nothing renders, console says the preview is unavailable.** Chrome was not found. Install it or set
`HICCUP_CHROME`. `HtmlDocument`'s inspector shows the preview status while playing.

**The UI is upside down.** Toggle **Window > Hiccup > Flip Preview Vertically**.

**Colors look washed out or edges are haloed.** The sRGB handling in `ApplyPendingFrame` is the place to look —
specifically the `linear: true` staging texture and the `GL.sRGBWrite = false` around the blit.

**Nothing updates but the page seems alive.** Watch for the screenshot-polling message in the console; if it
appears, screencasting is not working in your Chrome configuration. Turn off headless mode to watch the page
directly, and turn on **Log Browser Console** to see the page's own output.

## Testing the protocol path outside Unity

`CdpClient`, `ChromeLauncher`, `CdpBridgeJs` and `Json` have no Unity dependencies beyond one `Debug.Log` call,
so they compile into a plain console project with a small shim. That is how the pipeline was verified: launch
Chrome, create a target, inject the bridge, apply HTML and CSS, capture a frame to PNG, exercise the element
read/write ops, and dispatch a click to confirm the event payload round-trips. It is much faster than iterating
inside the Editor, and it isolates protocol problems from Unity ones.
