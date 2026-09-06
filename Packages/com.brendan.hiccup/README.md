# Hiccup — HTML-in-Canvas Components for Unity Web

**Hiccup** (**H**TML-**i**n-**C**anvas **C**omponents **U**nity **P**ackage) defines Unity UIs in **HTML and CSS** for WebGL2 and WebGPU builds.
The package hosts real DOM inside the Unity
canvas and uses Chrome's [HTML-in-Canvas API](https://github.com/WICG/html-in-canvas) to composite that DOM
into Unity textures, so you get the parts UI Toolkit cannot give you on the web:

* **Accessibility** — the UI is ordinary DOM: screen readers, the accessibility tree, ARIA, focus order.
* **Browser features** — find-in-page, text selection and copy, IME/composition, spell check, native form
  controls, `<dialog>` focus trapping, context menus, DevTools inspection, extensions.
* **Web layout** — CSS Grid/Flexbox, web fonts, media queries (`prefers-reduced-motion`, `forced-colors`), emoji.
* **Anywhere in the scene** — full-screen HUDs through uGUI, or perspective-projected panels on 3D meshes with
  correct hit testing.

Browsers without the API get the same DOM in an overlay layer above the canvas (still accessible and
interactive, just not composited into the Unity frame). The Editor shows placeholders.

## Documentation

| Document | For |
| --- | --- |
| [Documentation~/UserGuide.md](Documentation~/UserGuide.md) | Building UI with the package: setup, authoring, events, world panels, accessibility, troubleshooting. |
| [Documentation~/Runtime.md](Documentation~/Runtime.md) | How the web build works: the bridge, the paint model, texture transport on WebGL2 and WebGPU, geometry and hit testing, input isolation. |
| [Documentation~/EditorPreview.md](Documentation~/EditorPreview.md) | How the Editor renders documents through a real Chrome over the DevTools Protocol. |
| [Documentation~/UguiMirror.md](Documentation~/UguiMirror.md) | Experimental: mirroring an existing uGUI canvas into a document, component by component, and where that stops. |

## Requirements

* Unity 6000.0+ with the **Web** platform module (WebGL2 or WebGPU). The package depends on `com.unity.ugui` 2.0+
  and the UI, IMGUI, ImageConversion and JsonSerialize modules, and declares them, so the Package Manager installs them.
* Chrome 148+ with `chrome://flags/#canvas-draw-element`, or an
  [Origin Trial token](https://developer.chrome.com/origintrials/#/view_trial/3478467762190286849) for your origin
  (the API is in origin trial in Chrome 148–150; signatures may still change — the bridge feature-detects each
  variant it knows about).

## Quick start

1. Add the package: in this repository it is embedded at `Packages/com.brendan.hiccup`; in another project add
   `"com.brendan.hiccup": "https://github.com/brendan-duncan/hiccup.git?path=Packages/com.brendan.hiccup"` to
   `Packages/manifest.json`. It pulls in uGUI (with TextMeshPro) and the modules it uses.
2. Optional but recommended: set **Project Settings ▸ Player ▸ Resolution and Presentation ▸ WebGL Template** to
   `Hiccup` (copied to `Assets/WebGLTemplates/Hiccup`). It adds the Origin Trial `<meta>` tag placeholder and a
   full-window canvas with `layoutsubtree`. The bridge sets the attribute at runtime anyway.
3. Open the **Full UI Sample** (a complete game UI) or **Three.js Desk** (a three.js page on a monitor, in an
   iframe) from `Assets/Samples/Hiccup` in the Hiccup project and build it, or create your own content with
   **Assets ▸ Create ▸ Hiccup ▸ HTML Document** / **Style Sheet** / **Script** and wire it up:

```csharp
// A full-screen HUD: RawImage + HtmlDocument + HtmlScreenSurface
var go = new GameObject("HUD", typeof(RectTransform), typeof(RawImage));
go.SetActive(false);                                   // configure before OnEnable
go.transform.SetParent(canvas.transform, false);
var doc = go.AddComponent<HtmlDocument>();
doc.Html = hudHtml;                                    // TextAsset (.html)
doc.StyleSheets = new[] { hudCss };                    // TextAssets (.css, imported by the package)
doc.Scripts = new[] { hudJs };                         // TextAssets (.js) run in the page once it exists
go.AddComponent<HtmlScreenSurface>();                  // sizes the document to the RectTransform
go.SetActive(true);

doc.OnAction("start", e => StartGame());               // <button data-action="start">
doc.On("volume", "input", e => SetVolume(e.ValueAsFloat));
doc.Q("#score").Text = "42";
doc.Q("#pause-dialog").ShowModal();
doc.Announce("Level complete");                        // aria-live
```

For a panel on a 3D object use a Quad with `HtmlDocument` + `HtmlWorldSurface`; the surface derives the
document's screen transform from the camera every frame so the browser hit-tests the projected DOM.

## Components

| Component | Purpose |
|---|---|
| `HtmlDocument` | Owns one DOM panel (a `drawable` element inside the canvas), its texture and its event handlers. Size is in CSS pixels. |
| `HtmlScreenSurface` | Draws the document through a uGUI `RawImage` (requires the `Hiccup/UI Premultiplied` shader, assigned automatically) and syncs geometry to the RectTransform. |
| `HtmlWorldSurface` | Draws the document on a mesh with `Hiccup/Unlit Premultiplied` and syncs perspective geometry. |
| `HtmlRuntime` | Auto-created singleton that ticks the bridge; exposes `Mode`, `Features`, canvas size and DPR. |
| `HtmlUguiMirror` | Experimental. Mirrors a uGUI `Canvas` into a document every frame: rectangles, images, text, native controls for Toggle/Slider/InputField/Dropdown, browser scrolling for ScrollRect. See [Documentation~/UguiMirror.md](Documentation~/UguiMirror.md). |

## API sketch

* Query: `doc.Q(selector)`, `doc.QAll(selector)`, `element.Q(selector)`, `element.Parent`, `element.Matches(...)`.
* Content: `Text`, `InnerHtml`, `Append(html)`, `Prepend(html)`, `InsertHtml(where, html)`, `Remove()`.
* State: `Value`, `Checked`, `Disabled`, `Hidden`, `GetAttribute/SetAttribute/RemoveAttribute`,
  `GetProperty/SetProperty`, `SetStyle`, `GetComputedStyle`, `AddClass/RemoveClass/ToggleClass/EnableClass`.
* Behavior: `Focus()`, `Blur()`, `Click()`, `ShowModal()/CloseModal()`, `ScrollIntoView()`, `Bounds`.
* Events: `doc.On(type, h)`, `doc.On(elementId, type, h)`, `element.On(type, h)`, `doc.OnAction(name, h)`
  (for `data-action`), `doc.EventReceived`. `HtmlEvent` carries type, target id/tag/name, value, checked state,
  key/code, pointer position in panel pixels, modifiers, ancestor path and `data-*` attributes. Set
  `e.Handled = true` to stop further C# dispatch. Only a default set of event types is forwarded; call
  `doc.Listen("pointerover")` (done automatically by `On`) for others.
* Images: `doc.SetImage("portrait", texture)` (or a `Sprite`, a rect, or encoded bytes) shows a Unity texture in
  every element with `data-hui-image="portrait"` and as `var(--hui-image-portrait)` in CSS; call again to update,
  `RemoveImage` to clear.
* Page-side code: `doc.Scripts` (`.js` TextAssets) run in the page in order when the document is created and after
  `Reload()`, each as a function body with `panel`, `root` and `HUI` in scope.
* Escape hatch: `doc.Eval(js)` runs JavaScript with `panel`, `root` and `HUI` in scope. `doc.EvalAsync(js)` runs
  the same kind of body as an `async` function (so `await` works) and returns a `Task<string>` that faults with
  `HtmlEvalException` on a throw or rejection.
* Page to C#: script calls `HUI.send("name", payload)`; C# handles it with `doc.OnMessage("name", m => ...)`
  (`m.Data` is the string as sent, or JSON for anything else; `m.DataAs<T>()` deserializes it) or `doc.MessageReceived`.
* Options: `PointerMode` (Panel / ChildrenOnly / None), `BlockUnityInput`, `PremultipliedAlpha`,
  `HtmlRuntime.ForceOverlay`, `HtmlRuntime.UpdateMode`, `HtmlRuntime.DebugLogging`.

## How it works

```
Unity canvas (layoutsubtree)
 └─ <div drawable class="hui-panel">   ← one per HtmlDocument, laid out by the browser
      <style>…your CSS…</style>
      <div class="hui-content">…your HTML…</div>

paint event ─► bridge marks panel dirty ─► LateUpdate:
   WebGL2 : gl.texElementSubImage2D / texElementImage2D into a GL texture wrapped by Texture2D.CreateExternalTexture
   WebGPU : queue.drawElementImageToTexture / copyElementImageToTexture into a RenderTexture (via wgpu handle)
surfaces ─► HtmlDocument.SetGeometry(pixel→clip matrix) ─► canvas.updateElementGeometry / getElementTransform
            so DOM hit testing and accessibility bounds match where Unity draws the texture.
```

Input that targets the DOM is kept away from Unity (configurable via `BlockUnityInput`): the bridge stops
propagation on the panel and also wraps the Emscripten keyboard/mouse/touch handlers Unity registered at start-up
so they ignore events whose target is inside a panel (Unity would otherwise `preventDefault()` key events and
swallow typing). Clicks on empty panel space fall through when `PointerMode` is `ChildrenOnly` (or via
`pointer-events` in your CSS).

[Documentation~/Runtime.md](Documentation~/Runtime.md) covers all of this properly: feature detection, the paint
and dirty-tracking model, who owns the texture on each backend and why, the WebGPU handle-resolution and staging
paths, the three geometry strategies, and the failure modes each one produces.

Canvas children are not hit testable until the canvas is told where they are. In `Auto` mode
(`HtmlRuntime.GeometryMode`) affine panels go through `canvas.updateElementGeometry()`, perspective panels through
the two-argument `canvas.getElementTransform()` that Chrome's WebGL/WebGPU demos use (falling back to an identity
geometry plus a CSS `matrix3d`). `layoutsubtree` lays panels out with static positioning, so any CSS transform the
bridge applies also cancels the panel's layout offset.

## Editor preview

Play mode in the Editor renders documents through a real Chrome, driven over the DevTools Protocol. Each document
gets its own browser target sized to the document; DevTools screencasts it and each frame becomes the
`HtmlDocument.Texture` that surfaces sample, so screen and world surfaces both work. Pointer input is projected
back through the document's pixel-to-clip matrix, so clicking a button in the Game view produces the same
`HtmlEvent` a build would.

It is on by default and needs Chrome on PATH or in a standard install location; set `HICCUP_CHROME` to override.
Toggles live under **Window > Hiccup**:

| Menu item | Effect |
| --- | --- |
| Editor Preview (Chrome) | Turns the preview off; documents fall back to the placeholder texture. |
| Run Chrome Headless | Off runs a visible (off-screen) browser window, which is useful when debugging the page. |
| Log Browser Console | Forwards the page's `console` output to the Unity console. |
| Flip Preview Vertically | Corrects the frame orientation if it comes out upside down on your graphics API. |
| Reload Changed Assets in Play Mode | On by default. A saved `.css` is applied to live documents in place; a saved `.html` or `.js` reloads them. |

What the preview gives you is genuine, because it is genuinely Chrome: layout, CSS, fonts, script behavior and
event payloads. What it cannot give you is the part that only exists in a web build — accessibility (screen
readers, find-in-page, text selection), IME, and HTML-in-Canvas compositing itself. A document that looks and
behaves correctly in the Game view still has to be checked in a build.

`Q`, `QAll` and the whole `HtmlElement` API work. A handle is a description of how to find the element rather
than a reference to it, so `Q()` costs nothing; the browser resolves it per operation. Writes (text, attributes,
classes, properties, `InnerHtml`, `ShowModal`, `Remove`) are queued and sent as one batch per document per frame,
so a per-frame HUD update is a single message. Reads (`Value`, `GetAttribute`, `Checked`, `Bounds`, `QAll`) block
on a round trip, which is a fraction of a millisecond to a local browser but is worth keeping out of `Update`.

Keyboard input goes to the document the last click landed in, so text fields, Enter, Backspace, arrows and
Ctrl shortcuts work from the Game view; IME and composed input do not. Not yet wired up in the preview: pointer
mode and `BlockUnityInput` (Unity receives the same clicks and keys the document does), and mipmap parity with
the WebGL path.

[Documentation~/EditorPreview.md](Documentation~/EditorPreview.md) describes the frame pipeline, the color and
orientation handling, input projection and the element handle model in detail.

## Limitations

* HTML-in-Canvas is experimental: cross-origin iframes are not drawn, and some API names differ between Chrome
  versions (the bridge tries `texElementSubImage2D`, then the 3- and 6-argument `texElementImage2D`;
  `drawElementImageToTexture` with its own destination shape, then both `copyElementImageToTexture` forms).
* `<script>` inside your HTML is not executed (`innerHTML` semantics). Put page-side code in `HtmlDocument.Scripts`,
  or run it with `Eval` / `EvalAsync`.
* On WebGPU the bridge needs the JS-side texture for `RenderTexture.GetNativeTexturePtr()`; it looks it up
  through `wgpu[ptr]` / `Module.WebGPU` and copies through a staging texture when the Unity texture lacks
  `RENDER_ATTACHMENT | COPY_DST` usage. If lookup fails it logs once and the panel stays blank — switch to
  WebGL2 or set `HtmlRuntime.ForceOverlay = true`.
* Builds render only on the Web platform. In the Editor, play mode uses the Chrome preview above; with the
  preview off, documents show a translucent placeholder.
