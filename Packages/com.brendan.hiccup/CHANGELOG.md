# Changelog

## Unreleased

### Added
- Element API additions: `QAll` and `Children` on an element, `Closest(selector)`, `GetData`/`SetData` for
  `data-*` attributes, `ScrollTop`/`ScrollLeft`, `ValueAsFloat`/`ValueAsInt`, `SelectedIndex` and
  `SetOptions(...)` for `<select>`, `Call(method)` for zero-argument DOM methods with `Select()` as a shortcut,
  numeric `SetAttribute` overloads, and `Html.Escape` for building markup from game data. Both bridges gained
  the matching queries; the Editor preview's `Parent` now stops at the content root as the jslib's does.
- Fuller event payloads. `HtmlEvent` gained `meta`, wheel `deltaX`/`deltaY`, `pointerId`/`pointerType`/`pressure`,
  `movementX`/`movementY`, key `repeat` and `isComposing`, `relatedId` (with a `RelatedTarget` element) for focus
  and pointer-over transitions, `editable` (the target takes text input) and `detail` (click count, or a
  CustomEvent's detail as JSON). Both bridges emit the same fields.
- Focus tracking. `HtmlDocument.HasFocus` and `TextInputFocused`, and `HtmlRuntime.FocusedDocument`,
  `HasFocus` and `TextInputFocused`, follow focusin/focusout so a game can stop reading keys as commands
  while the player types in a field. Hiding, disabling or destroying a document clears its focus.
- Unity textures in the page. `HtmlDocument.SetImage(name, texture)` (also a `Sprite`, a pixel rectangle, or
  already-encoded bytes) reads the pixels back, encodes them as PNG or JPEG and hands them to the page, where
  every element with `data-hui-image="name"` receives them, now or when it is added later: an `<img>` as `src`,
  anything else as `background-image`, and CSS can use `var(--hui-image-name)`. Calling it again updates the
  image, so a RenderTexture can be a live feed; `RemoveImage` clears it. The web bridge keeps a blob URL per
  image, the Editor preview a data URL. The Full UI Sample's HUD shows a second camera this way, as a JPEG at 8 Hz.
- Hot reload in play mode. Saving a `.html`, `.css` or `.js` asset a live document uses pushes it into the page
  once Unity reimports it: a style sheet change is applied in place and the page keeps its state, an HTML or
  script change calls `Reload()`. `HtmlDocument.ReloadStyles()` is the new public half of that. Toggle it under
  **Window ▸ Hiccup ▸ Reload Changed Assets in Play Mode**; content set from code is not tracked.
- `HtmlDocument.Scripts`: `.js` TextAssets that run in the page, in order, once the document is created (before
  `Created` fires, so handlers there see what they installed) and again after `Reload()`. Each is a function body
  with `panel`, `root` and `HUI` in scope, run through `EvalAsync`, so `await` is allowed and a script that throws
  is reported in the Unity console with its asset name. `.js` files import as TextAssets through a new
  `JsImporter` with their own icon, selected per asset like `.html` and skipping `WebGLTemplates` and `Plugins`
  folders; **Assets ▸ Create ▸ Hiccup ▸ Script** makes one with a starter template. The Full UI Sample's
  tooltip-dismissal code moved out of a C# string into `GameUI.tooltips.js`.
- Page-to-C# messages. Script run through `HtmlDocument.Eval` / `EvalAsync`, or DOM listeners such script
  installs, calls `HUI.send(name, payload)`; C# receives it with `doc.OnMessage(name, handler)` or the
  `doc.MessageReceived` event as an `HtmlMessage` (`Name`, `Data`, `DataAsFloat`, `DataAsInt`, `DataAsBool`,
  `DataAs<T>()`, `Handled`). A string payload arrives as sent, anything else as JSON. In the jslib the `HUI`
  that `Eval` code sees is now a per-panel view of the bridge (`send`, `panel`, `root`, plus everything the
  bridge had); the Editor preview delivers messages through a second DevTools binding, `HUI_Message`.
- `HtmlDocument.EvalAsync(js)` returns a `Task<string>`. The code runs as an `async` function body with the same
  `panel`, `root` and `HUI` scope as `Eval`, so it can `await` fetches, animations, dialog results and other
  promises; the result is stringified like `Eval`'s. A throw or rejection faults the task with
  `HtmlEvalException`; destroying the panel cancels it. `IHtmlBackend` gained `PanelEvalAsync`, and `HtmlBackend`
  gained `CompleteEval` and `DispatchMessage` for backends to report through.

### Changed
- The uGUI mirror's scroll relay uses the message channel: the capture-phase `scroll` listener sends the
  viewport id and offsets with `HUI.send('ugscroll', …)` instead of re-dispatching a bubbling custom event with
  a `data-scroll` attribute.

### Fixed
- `package.json` declares its dependencies: `com.unity.ugui` 2.0.0 (which carries TextMeshPro on Unity 6) and the
  UI, IMGUI, ImageConversion and JsonSerialize modules. The runtime assembly references `UnityEngine.UI` and
  `Unity.TextMeshPro`, so a project without uGUI could not compile the package before. It also names its
  repository, documentation, changelog and license URLs, and the READMEs show the git URL install
  (`?path=Packages/com.brendan.hiccup`).
- Editor preview: `HtmlDocument.Eval` returning an object, array or boolean came back as .NET's rendering of
  the value (a type name, `True`). It is now stringified in the page exactly as the jslib does (JSON, `true`).

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
