# Changelog

## Unreleased

### Added (experimental)
- `HtmlUguiMirror` (`Hiccup.Ugui`): mirrors a uGUI `Canvas` into an `HtmlDocument` after every layout pass, so an
  existing uGUI interface is drawn and interacted with as DOM while uGUI keeps running underneath. RectTransforms
  become absolutely positioned elements, Images become tinted PNG backgrounds (`border-image` for sliced,
  `clip-path`/conic masks for fills), Text and TMP become styled text with rich-text conversion, Buttons become
  `<button>`, Toggle/Slider/Dropdown/InputField get native controls that write back to the components, ScrollRect
  viewports scroll in the browser with the offset written to `content.anchoredPosition`, and Selectable pointer
  transitions are driven from DOM pointer events. The Hiccup assembly now references `UnityEngine.UI` and
  `Unity.TextMeshPro` explicitly. See `Documentation~/UguiMirror.md` for the mapping and the limits, and the
  **uGUI Mirror** sample under `Assets/Samples/Hiccup`.

### Changed
- `IHtmlBackend` gained `PanelSetMipmaps`, so the Editor preview follows `HtmlDocument.Mipmaps` instead of always
  generating a mip chain per frame. Runtime and Editor hot paths were reworked to allocate nothing on a steady frame:
  the uGUI mirror reuses last frame's strings, caches components, fonts and converted rich text per node, and
  formats numbers in place; the texture cache reads RGBA32 textures without a decoded copy and composes slices per
  column; the jslib no longer calls `getError` outside debug mode; the preview pools screencast buffers and drops
  unwanted DevTools messages before parsing them.

### Removed
- Three.js Desk: the nested Unity build on the three.js scene's monitor. A running build painted through a second
  level of HTML-in-Canvas crashed the renderer (`STATUS_ACCESS_VIOLATION`) after a while with nothing on the
  console, so the scene is now the three.js shapes alone; the desk mouse still orbits, drags and clicks them.

### Fixed
- Editor preview: `HtmlDocument.Eval` wrapped the code as a single expression (`return (code)`), so any script with
  more than one statement, a `var`, or a trailing semicolon failed silently — the Full UI Sample's tooltip
  dismissal and settings reset among them. It is now evaluated as a function body with `panel`, `root` and `HUI`
  parameters, exactly as `Hiccup_PanelEval` does in the jslib; a value comes back through `return`.
- Editor preview: content written from `HtmlDocument.Created` was lost. `Created` fired synchronously inside
  `Create()` while Chrome was still starting, and the CDP backend drops element writes, `Eval` and `Announce`
  until its page is ready, so anything a controller built at wire-up time (the Full UI Sample's inventory grid,
  status text, HUD) never appeared. `IHtmlBackend` gains `PanelIsReady`; `HtmlDocument` now holds `IsCreated`
  and `Created` until the backend reports it, which in a web build is still inside `Create()`.

### Changed
- The samples are no longer shipped inside the package. They live only under `Assets/Samples/Hiccup` in the
  Hiccup project, and `package.json` no longer lists them for the Package Manager.
- Renamed the package to **Hiccup** (HTML-in-Canvas Components Unity Package). Everything that carried the old
  `HtmlUI` name moved with it: package id `com.brendan.hiccup`, namespaces `Hiccup`, `Hiccup.Editor`,
  `Hiccup.Editor.Cdp` and `Hiccup.Samples`, assemblies `Hiccup` and `Hiccup.Editor`, the `Hiccup.jslib` bridge
  and its `Hiccup_*` exports, shaders under `Hiccup/` and `Hidden/Hiccup/`, the `Hiccup` WebGL template,
  the `HICCUP_CHROME` environment variable, the `Hiccup.Preview.*` EditorPrefs keys and the
  **Window ▸ Hiccup**, **Assets ▸ Create ▸ Hiccup** and **Add Component ▸ Hiccup** menus. Class names
  (`HtmlDocument`, `HtmlElement`, `HtmlScreenSurface`, ...) are unchanged; update `using HtmlUI;` to
  `using Hiccup;` and re-select the WebGL template under Player settings.

### Added
- Editor preview: documents render and respond in the Game view during play mode, backed by a real Chrome driven
  over the DevTools Protocol (`Hiccup.Editor.Cdp`). One browser target per document, screencast frames
  premultiplied into a `RenderTexture`, pointer input projected through the document's pixel-to-clip matrix.
- `IHtmlBackend` / `HtmlBackend`: a registration point for bridges other than `Hiccup.jslib`. The Editor stubs in
  `HtmlNative` forward to the registered backend, so nothing above the bridge changed. `HtmlBackend.SetKeyboardCapture`
  and `DrainKeyPresses` let a backend read the Game view's keys through an IMGUI relay on the runtime object.
- Element API in the preview: `Q`, `QAll` and every `HtmlElement` operation. Handles are resolved per operation
  from a selector description, writes are batched once per document per frame, reads are a blocking round trip.
- **Window > Hiccup** menu for the preview (enable, headless, console logging, frame orientation, restart).
- Editor preview frames are decoded off the main thread: screencast messages are base64-decoded straight from the
  socket bytes, PNGs are decoded by `PngDecoder` on a pool thread with recycled buffers, and the main thread only
  uploads pixels. `Texture2D.LoadImage` remains as the fallback for PNGs outside the RGB/RGBA 8-bit subset.
- Editor preview keyboard input: keys typed in the Game view reach the document the last click landed in, via
  IMGUI events relayed as DevTools key events. Text fields, Enter, Backspace, arrows, Escape and Ctrl shortcuts
  work; IME and composed input do not.

- **Assets ▸ Create ▸ Hiccup ▸ HTML Document** and **Style Sheet**: new `.html` fragments and `.css` style sheets
  from the Project window, with the usual inline rename and a small starter template.
- `.html` and `.css` assets carry their own Project window icons (orange `<>`, blue `{}`). `.html` now goes through
  the package's `HtmlImporter`, selected per asset by an `AssetPostprocessor` because Unity's text importer owns the
  extension by default; the result is still a `TextAsset`.

- **Three.js Desk** sample: a monitor on a desk whose screen is a same-origin `<iframe>` (via `srcdoc`) running
  a three.js scene, painted by HTML-in-Canvas into a world-space panel. A mouse on the desk, dragged with the
  real one, drives the page's cursor through batched `data-*` attribute writes on the frame element.
- Three.js Desk: the three.js scene now has a monitor of its own showing the published Unity build
  (`https://brendan-duncan.github.io/hiccup/build/`) in a nested `<iframe>`, painted by HTML-in-Canvas into a
  hidden host canvas that three.js samples as a texture (a `layoutsubtree` canvas is excluded from enclosing
  snapshots, so the WebGL canvas itself must stay plain). The build is fetched and loaded through `srcdoc` so the frame is same-origin
  from any host; a CSS3D overlay is the fallback where the API is missing. The desk mouse is forwarded into the
  nested build as synthetic pointer events.
- Three.js Desk: right-drag on the pad slides the desk mouse without pressing its button, so the cursor can hover
  and move between targets; left-drag is the press-and-drag it always was.
- Editor preview: Chrome is launched with `--enable-features=CanvasDrawElement`, so pages that use
  HTML-in-Canvas themselves have the API in the preview.
- Overlay mode is depth-composited when the canvas has an alpha channel. The overlay is placed behind the
  canvas, `HtmlWorldSurface` and `HtmlScreenSurface` switch to cutout materials (`Hiccup/Overlay Cutout`,
  `Hiccup/UI Overlay Cutout`) that write depth and alpha 0, and the bridge routes pointer events to the DOM while
  the pointer is over a panel. `HtmlRuntime.OverlayCutout` reports it; the WebGL template opts in with
  `webglContextAttributes: { alpha: true }`.
- Editor preview documents are created on a loopback http origin (`PreviewOrigin`, a one-page server on
  127.0.0.1) instead of `about:blank`. Embeds that demand a Referer, YouTube among them, refused the origin-less
  page with "Error 153"; storage, cookies and postMessage also behave as they do in a build.

### Fixed
- WebGPU uploads on current Chrome Canary failed with "Required member is undefined" because
  `drawElementImageToTexture` was given the `copyElementImageToTexture` destination shape. Its destination is a
  `GPUImageCopyTextureTagged` plus `size`, with `texture` at the top level; the bridge now passes that and falls
  back through the other forms on `TypeError`.
- Overlay mode no longer shows the UI over the web template's loading screen. The overlay used to be a
  fixed, z-indexed layer on `<body>`; it is now the canvas's next sibling with no z-index, so it stacks above
  the canvas and below whatever the page draws over the canvas, exactly as texture mode does.
- The samples no longer need the Physics module: scene primitives are built from the built-in meshes without
  colliders, and Orbital Salvage picks cubes with a ray-versus-bounds test instead of `Physics.Raycast`.
- Stopping play mode no longer throws `MissingReferenceException` from `HtmlDocument.AfterBridgeUpdate`: a
  document drops its backend-owned texture once the backend that created it has been unregistered.

## [0.1.0] - 2026-09-01

### Added
- `HtmlDocument`, `HtmlElement`, `HtmlEvent`, `HtmlRuntime` runtime API.
- `HtmlScreenSurface` (uGUI RawImage) and `HtmlWorldSurface` (mesh) presenters with geometry sync.
- HTML-in-Canvas bridge (`Hiccup.jslib`) for WebGL2 (`texElementSubImage2D` / `texElementImage2D`) and WebGPU
  (`drawElementImageToTexture` / `copyElementImageToTexture`), paint-event driven updates, DOM overlay fallback.
- Premultiplied-alpha shaders for uGUI and unlit world surfaces.
- `.css` ScriptedImporter, HtmlDocument inspector, build-time reminders.
- `Hiccup` WebGL template with Origin Trial placeholder.
- Full UI Sample ("Orbital Salvage").
