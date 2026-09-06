# How the runtime works

This is the shipping path: what happens in a Web build, in the browser, once per frame. It covers
`Hiccup.jslib` and the C# that drives it. For the Editor's Chrome-over-DevTools stand-in, see
[EditorPreview.md](EditorPreview.md) — a different mechanism reaching the same end state.

## The idea in one paragraph

The UI is real DOM, living inside the Unity `<canvas>` element as its children. Chrome's
[HTML-in-Canvas API](https://github.com/WICG/html-in-canvas) makes that arrangement useful: `layoutsubtree` on
the canvas gives those children layout and a place in the accessibility tree, `drawable` marks an element that
can be snapshotted, and `texElementImage2D` / `copyElementImageToTexture` copy that snapshot into a texture the
renderer already has. Unity samples the texture and draws the UI wherever it likes; the browser keeps treating
the DOM as DOM. Screen readers, find-in-page, text selection, IME and focus all keep working, because nothing was
ever converted into a mesh.

## Two modes

`HtmlRuntime.Mode` reports which one is live.

**Texture mode (`HtmlRenderMode.Texture`)** is the above. Requires HTML-in-Canvas and a working texture-transfer
entry point.

**Overlay mode (`HtmlRenderMode.Overlay`)** is the fallback for browsers without the API. The identical DOM is
placed in an overlay element that is a sibling of the canvas, sized to its rect and stacked as the canvas is
(below a loading screen, for instance). The UI is fully accessible and interactive. `HtmlDocument.Texture` is
null. Where the overlay sits depends on the canvas:

* **Opaque canvas** (the default WebGL context): the overlay is inserted just *after* the canvas and the DOM is
  drawn over the frame. World panels are placed with a perspective `matrix3d`, but nothing in the scene can
  occlude them, and surfaces disable their renderers.
* **Transparent canvas** (`webglContextAttributes: { alpha: true }`, which the package template sets): the
  overlay is inserted just *before* the canvas and `HtmlRuntime.OverlayCutout` is true. Surfaces then draw a
  cutout instead of a texture: `HtmlWorldSurface` uses `Hiccup/Overlay Cutout`, an opaque pass that writes
  depth and color+alpha 0, and `HtmlScreenSurface` uses `Hiccup/UI Overlay Cutout`, which does the same
  through uGUI. The DOM shows through those pixels, so a panel is occluded by nearer geometry exactly as a
  textured one would be. Two consequences: anything that rewrites alpha after the scene (some post-processing
  stacks) closes the hole, and pixels the scene itself leaves at alpha 0 show the page background. Pointer
  events are routed by the bridge: while the pointer is over a panel element under the canvas, the canvas stops
  receiving them so the DOM gets them natively; the routing uses the panel's projected shape, not scene depth.

Mode is chosen once in `HUI.init` and never changes. `HtmlRuntime.ForceOverlay = true` (before the first
document) selects overlay deliberately, which is useful for comparing behavior.

## Layers

```
HtmlDocument / HtmlElement / HtmlEvent          C# API
HtmlScreenSurface / HtmlWorldSurface            placement + geometry
HtmlRuntime                                     one tick per frame, event routing
        |
   HtmlNative                                   flat C ABI, ~55 entry points
        |  [DllImport("__Internal")]
   Hiccup.jslib  ($HUI)                         DOM, textures, geometry, input
        |
   Chrome
```

The ABI is deliberately narrow: ints, floats, C strings and one function pointer. Strings returned from JS are
`_malloc`'d by `HUI.cstr` and freed by the caller through `Hiccup_Free`, which is what `HtmlNative.TakeString`
does. The jslib is written in ES5 on purpose, because the Emscripten JS pre-processor is not a full parser and
has rejected newer syntax across Unity 6 releases.

## Initialization and feature detection

`HtmlRuntime.Initialize` runs the first time any document is created. It tells JS the backend (1 = WebGL2,
2 = WebGPU, from `SystemInfo.graphicsDeviceType`), whether the project is in linear color space, and the
overlay/debug flags, and hands over the event callback pointer.

`HUI.init` then probes, in order:

1. **The canvas** — `Module.canvas`, else `#unity-canvas`, else the first `<canvas>`.
2. **HTML-in-Canvas** — `typeof canvas.requestPaint === 'function' || 'onpaint' in canvas`.
3. **A texture entry point**, per backend:
   * WebGL2: `gl.texElementSubImage2D`, else `gl.texElementImage2D`.
   * WebGPU: `GPUQueue.prototype.drawElementImageToTexture`, else `copyElementImageToTexture` — and then only if
     a `GPUDevice` can actually be found from JS, because without one nothing can be uploaded later.

Texture mode requires all three. The results are published as `HtmlRuntime.Features` (`HtmlFeatures`), which also
records which geometry API is in use, the device pixel ratio and the user agent — worth logging from a build,
since the answer varies by Chrome version.

In texture mode the canvas gets `layoutsubtree` and the `paint` event is bound. In overlay mode the overlay
container is created instead. Either way the base stylesheet is injected and Unity's input handlers are wrapped
(see [Input isolation](#input-isolation)).

## What a panel is

`HtmlDocument.Create` calls `Hiccup_PanelCreate`, which builds this, once per document:

```html
<div class="hui-panel" drawable data-hui-panel="1" data-hui-pointer="children"
     style="width:800px; height:600px">
  <style data-hui="panel-style"> /* the document's CSS */ </style>
  <div class="hui-content">      <!-- your HTML fragment -->   </div>
  <div class="hui-live" role="status" aria-live="polite"></div>   <!-- created on first Announce -->
</div>
```

appended to the canvas (texture mode) or the overlay (fallback mode). The base stylesheet gives `.hui-panel`
absolute positioning at the origin, `transform-origin: 0 0`, a transparent background, `isolation: isolate` and
`contain: layout paint style` — the containment matters, since it bounds what the browser has to re-layout and
re-snapshot when the document changes.

**Sizes are CSS pixels.** The texture is `size × devicePixelRatio × resolutionScale`, rounded, which is why a
world-space panel viewed obliquely wants `resolutionScale = 2`.

`<script>` in your HTML never executes, because the content is assigned through `innerHTML`. `HtmlDocument.Eval`
is the escape hatch, and it runs with `panel` (the `.hui-panel`), `root` (the `.hui-content`) and `HUI` in scope.

### Pointer modes

`HtmlPointerMode` is expressed purely in CSS on the panel's `data-hui-pointer` attribute:

| Mode | Rule | Effect |
| --- | --- | --- |
| `Panel` | (no rule) | The whole panel rectangle swallows pointer input. |
| `ChildrenOnly` | `pointer-events: none` on panel and content, `auto` on content's direct children | Clicks on empty space fall through to Unity. This is the default and is usually what a HUD wants. |
| `None` | `pointer-events: none` on everything | Display only. |

## The paint model

The browser knows when a `drawable` subtree's snapshot changed; Unity does not. The `paint` event carries
`changedElements`, and `bindPaint` marks the panels containing them dirty:

```
DOM changes  ->  browser repaints  ->  canvas 'paint' {changedElements}  ->  panel.dirty = true
                                                                                  |
Unity LateUpdate -> Hiccup_Update() -> for each dirty visible panel: upload -> panel.dirty = false
```

`HUI.requestPaint()` asks for a snapshot when the bridge changed something itself (new HTML, resize, visibility)
or when an upload failed. It is debounced by a `paintRequested` flag cleared on the next paint.

`HtmlRuntime.UpdateMode` overrides the policy:

* `Auto` (default) — paint-driven, but if no `paint` event has *ever* fired after 120 frames, switch to
  uploading every frame. That is the safety net for browsers that expose the API but not the event.
* `OnPaintEvent` — strictly paint-driven.
* `EveryFrame` — upload unconditionally. Simple, and wasteful.

Only visible panels with a texture are considered. A failed upload leaves the panel dirty and requests a paint,
so it retries next frame rather than showing a stale or black texture — which matters at start-up, where the
first upload commonly fails with "no snapshot recorded yet".

## Texture transport

The two backends differ in *who owns the texture*, which is the single most important asymmetry in the bridge.

### WebGL2 — the browser owns it

`gl.createTexture()` in JS, registered into Emscripten's `GL.textures` table with a fresh id, and that id is
returned to C#. Unity wraps it with `Texture2D.CreateExternalTexture`. The browser owning the texture means it
can re-specify format and size freely, which the `texElementImage2D` variants need.

`allocGLTexture` allocates with `SRGB8_ALPHA8` when the project is in linear color space, otherwise `RGBA8`, and
then, if the document uses mipmaps, calls `generateMipmap` on the still-empty texture. That is not an
optimization: a mipmapped texture without a complete chain is incomplete and samples as opaque black until the
first real upload.

Uploads go through whichever entry point exists:

| Signature | Chrome | Notes |
| --- | --- | --- |
| `texElementSubImage2D(target, level, x, y, element, {width, height})` | current spec | Preferred. Writes into level 0 and keeps our allocation and format. |
| `texElementImage2D(target, internalFormat, element)` | 150+ | Short form. Re-specifies the texture. |
| `texElementImage2D(target, level, internalFormat, format, type, element)` | 138–149 | Long form, mirrors `texImage2D`. Distinguished by `.length === 3`. |

Every upload sets `UNPACK_FLIP_Y_WEBGL = false` (row 0 is the top of the page — hence
`HtmlDocument.TextureIsTopDown`), `UNPACK_PREMULTIPLY_ALPHA_WEBGL` from the document's `PremultipliedAlpha`, and
`UNPACK_ALIGNMENT = 4`. All of that, plus the active texture unit and current binding, is saved and restored
around the call: this code runs in the middle of Unity's frame, on Unity's context, and leaving pixel-store state
modified corrupts unrelated uploads in ways that are miserable to debug.

Mipmaps are regenerated and sampler state reapplied after each upload — trilinear plus up to 16× anisotropy via
`EXT_texture_filter_anisotropic`, so oblique panels do not shimmer.

### WebGPU — Unity owns it

Reversed: C# creates a `RenderTexture` and passes `GetNativeTexturePtr()` to `Hiccup_PanelBindGPUTexture`. The
browser copies into it. Two problems follow.

**Resolving the pointer to a `GPUTexture`.** Unity's WebGPU glue keeps GPU objects in a JS-side table and passes
integer handles into it, and the representation moved: `Module.WebGPU.device` is a `GPUDevice` up to 6000.7.0a4
and a handle from 6000.7.0a5 on. `HUI.gpuObject` resolves either form through the global `wgpu` table, and
`getGPUDevice` / `getGPUTexture` try, in order, `Module.WebGPU.getDevice()`, `Module.WebGPU.device`,
`WebGPU.mgrDevice`/`mgrTexture` managers, `Module.WebGPU.getJsObject`, and finally a scan of `wgpu` for anything
with a `.queue`. A candidate device without a queue is rejected rather than accepted and failed on later.
`wgpu` is probed with `typeof` rather than declared as a jslib dependency, because declaring it would break
linking in a WebGL2-only build.

**Usage flags.** The copy destination needs `RENDER_ATTACHMENT | COPY_DST`. Unity's `RenderTexture` may not have
both. When it does not, the bridge allocates a staging `GPUTexture` with the required usage, copies the element
into that, then `copyTextureToTexture` into Unity's texture. The staging texture is cached per panel and
reallocated only on size or format change.

The copy itself is `drawElementImageToTexture` if available, else `copyElementImageToTexture`. The two take
differently shaped destinations, per Chromium's `gpu_queue.idl`:

* `drawElementImageToTexture({ source }, { texture, premultipliedAlpha, colorSpace, size })` — the destination
  is a `GPUImageCopyTextureTagged` extended with a `size` (`GPUDrawElementImageDestination`), so `texture` sits
  at the top level.
* `copyElementImageToTexture({ source }, { destination: { texture, … }, width, height })` — the destination
  wraps a `GPUImageCopyTextureTagged` under `destination` (`GPUCopyElementImageDestination`).

The bridge tries each form newest first and moves to the next on `TypeError` (the shape was rejected); any other
exception is reported and retried next frame. The older positional `copyElementImageToTexture(element, width,
height, { texture })` form is the last resort. `premultipliedAlpha` follows the document; `colorSpace` is
`'srgb'`.

If the device or texture cannot be resolved, the bridge warns once and the panel stays blank. That is the case
where `HtmlRuntime.ForceOverlay = true` or switching to WebGL2 is the answer.

## Geometry, hit testing and accessibility bounds

A canvas child is not hit-testable until the canvas has been told where it is. A CSS transform alone positions
nothing and catches nothing. This is the subtlest part of the bridge.

Surfaces compute a **pixel-to-clip** matrix each frame — document CSS pixels (origin top-left, y down, z = 0) to
Unity clip space — and pass it to `HtmlDocument.SetGeometry`. `HtmlScreenSurface` builds it from the
RectTransform's world corners projected to screen; `HtmlWorldSurface` builds it as
`projection × view × localToWorld × pixelToLocal`, so it is genuinely projective.

JS composes that with a viewport matrix (NDC → canvas CSS pixels, y flipped) to get element-pixels →
canvas-pixels, wraps it in a `DOMMatrix`, and checks whether the fourth row is `(0,0,0,1)`. If not, the transform
is projective. Then, per `HtmlRuntime.GeometryMode`:

| Situation | Path |
| --- | --- |
| Perspective, or no `updateElementGeometry` | `canvas.getElementTransform(element, matrix)` and apply the returned CSS transform. This is the two-argument form Chrome's own WebGL/WebGPU demos use for full MVP matrices. |
| Affine | `canvas.updateElementGeometry(element, {canvasTransform})`, no CSS transform. |
| Perspective but only `updateElementGeometry` exists | Register with an *identity* canvas transform and let a CSS `matrix3d` do the projective mapping — ordinary DOM hit testing handles that correctly, whereas `updateElementGeometry` only hit-tests affine placement in current builds. |
| `CssTransform` mode, or everything failed | `matrix3d` only. Not hit-testable in texture mode; useful for overlay tests. |

One wrinkle: `layoutsubtree` lays canvas children out with *static positioning*, so a second panel starts below
the first. Any CSS transform applies on top of that layout offset, so `cssTransformFor` measures `offsetLeft` /
`offsetTop` and pre-multiplies a translation to cancel it.

Geometry is only pushed when the matrix actually changed (1e-6 epsilon), which keeps a static HUD from
re-registering every frame.

## Input isolation

Two separate problems.

**Unity must not see events the UI handled.** `BlockUnityInput` attaches `stopPropagation` listeners on the panel
for every pointer, mouse, touch, wheel and key event type.

**But `stopPropagation` is not enough.** Unity registers its input handlers through Emscripten before any C# runs,
sometimes in the capture phase, and calls `preventDefault()` on keys — which kills typing in your `<input>`
elements before a bubble-phase listener ever runs. So `guardUnityInput` walks `JSEvents.eventHandlers` and wraps
each relevant `handlerFunc` with a check that ignores events whose target is inside a blocking panel. Emscripten
looks up `handlerFunc` on every event rather than caching it, so swapping the field is sufficient; re-registering
a listener would run Unity's handler twice. Handlers are marked `__huiWrapped` so re-entry is idempotent, and the
sweep runs again on every panel creation to catch handlers registered late.

## Events

Each panel listens for a default set — `click`, `dblclick`, `input`, `change`, `submit`, `keydown`, `focusin`,
`focusout` — and `HtmlDocument.Listen` (called automatically by `On`) adds more.

`onDomEvent` normalizes the DOM event into a flat, `JsonUtility`-friendly object: type, target id (assigning
`hui-N` if the element has none, so it can be queried later), tag, name, the closest `[data-action]` value, the
control's value and checked state, key/code, pointer position **relative to the panel** in CSS pixels, modifier
flags, the space-separated ids of ancestors up to the panel, and the merged `data-*` attributes of the target and
the action element as `key=value` lines. `submit` gets `preventDefault()` when `PreventFormSubmit` is set, so a
form never navigates the page away from your game.

That JSON crosses to C# through the function pointer registered at init (`[MonoPInvokeCallback]`; the delegate is
held in a static field for the process lifetime because JS holds a raw pointer to it). `HtmlDocument.Dispatch`
then walks handlers in a fixed order, stopping at any point if a handler sets `e.Handled`:

1. `EventReceived`
2. handlers bound to the target's id
3. handlers bound to each ancestor id, nearest first
4. `data-action` handlers (clicks only)
5. handlers bound to the event type

## Messages and async evaluation

Two more paths cross from the page to C#, both registered at init next to the event callback.

`Eval` code sees `HUI` as a per-panel view of the bridge: `Object.create(HUI)` with `panel`, `root` and `send`
on top, so bridge functions still resolve through the prototype and `HUI.send(name, payload)` knows which panel it
belongs to. `send` stringifies the payload (a string as-is, anything else as JSON, `undefined` as empty) and calls
the message callback synchronously, the way a DOM event does. `HtmlDocument.DispatchMessage` runs
`MessageReceived` and then the `OnMessage` handlers for that name until one sets `Handled`.

`EvalAsync` compiles the body with the `AsyncFunction` constructor, fetched through `new Function` so the jslib
itself stays ES5, which is what lets the body `await`. The returned promise settles to a stringified value or an
error message and is reported through the eval callback with the request id `HtmlDocument` allocated, always on
a later turn, never from inside the P/Invoke. The document completes the matching `Task<string>`, faults it with
`HtmlEvalException` on failure, and cancels it if the panel is destroyed first.

## Images

`Hiccup_PanelSetImage` receives encoded bytes (PNG or JPEG from `HtmlImageEncoder`, or whatever the caller
handed over) and wraps them in a `Blob` and an object URL; the Blob takes its own copy, so the heap range is free
once the call returns. `setImage` stores the URL per panel, sets `--hui-image-<name>` on the panel element, and
applies it to every `[data-hui-image=<name>]` under the content root: `src` on an `<img>`, `background-image`
on anything else. A `MutationObserver` on the content root binds elements that arrive afterwards through
`innerHTML` or `insertAdjacentHTML`, and re-binds one whose `data-hui-image` attribute changes, so the HTML
never has to know the URL. Replacing an image revokes the old URL after the new one is applied; destroying the
panel revokes all of them. The encoder reads readable textures on the CPU and everything else through a blit
into an sRGB target and a `ReadPixels`, the same path the uGUI mirror uses.

## Element handles

`HUI.handles` is an array of element references with a free list; `Hiccup_Query` returns an index. The element
caches its own index in `__huiHandle`, so repeated queries for the same element reuse the handle. `QueryAll`
returns handles as a comma-separated string. `HtmlElement.Dispose` releases one; destroying a panel releases
every handle inside it.

Handles are cheap but not free: `HtmlElement` is `IDisposable`, and code that queries in a loop should dispose,
as the Full UI Sample does in `ShowScreen`.

## Accessibility

The point of the exercise, and mostly free — the DOM is DOM, so roles, labels, focus order and ARIA work because
the browser is doing its normal job. Three things the bridge does explicitly:

* `layoutsubtree` on the canvas is what puts panel children in the accessibility tree with real layout.
* Geometry registration (above) is what gives assistive technology correct on-screen bounds, so a screen reader's
  focus highlight lands where the user sees the control — including on a perspective-projected panel.
* `HtmlDocument.Announce` writes into a visually-hidden `role="status"` live region per panel, clearing it first
  and setting the text on the next animation frame so repeated identical messages are still announced.
  Visibility changes set `aria-hidden` alongside `hidden`.

## Color and alpha

In linear color space the texture is `SRGB8_ALPHA8` (WebGL) or copied with `colorSpace: 'srgb'` (WebGPU), so the
sampler linearizes on read. Snapshots are premultiplied by default, which is why the package ships
`Hiccup/UI Premultiplied` and `Hiccup/Unlit Premultiplied` and the surfaces assign them automatically. Turning
`PremultipliedAlpha` off changes the unpack flag and stops the surface applying the premultiplied material —
change both or neither.

## Per-frame order

`HtmlRuntime` carries `[DefaultExecutionOrder(10000)]`, so it runs after everything else:

1. Game code mutates documents and elements — each mutation calls `Invalidate`, which marks the panel dirty and
   requests a paint.
2. Surfaces' `LateUpdate` — compute the pixel-to-clip matrix, call `SetGeometry`, resize the document to match
   the RectTransform or mesh, assign `Document.Texture` to the RawImage or material.
3. `HtmlRuntime.LateUpdate` — refresh canvas size and DPR, then `Hiccup_Update()`: upload every dirty visible
   panel.
4. `HtmlDocument.AfterBridgeUpdate` — regenerate mips for WebGPU render textures that were actually updated
   (`Hiccup_PanelTakeUpdated`).

Uploading after geometry, and last in the frame, means the texture Unity draws this frame is the snapshot that
matches this frame's placement.

## Failure modes

Everything that can fail at runtime warns **once** through `HUI.warnOnce`, keyed so a per-frame failure does not
flood the console. Set `HtmlRuntime.DebugLogging = true` before the first document for the verbose trace,
including the feature report.

| Symptom | Cause |
| --- | --- |
| Overlay mode when texture mode was expected | The flag or Origin Trial token is missing, or no texture entry point was detected, or (WebGPU) no device could be found. Check `HtmlRuntime.Features`. |
| Panel blank in WebGPU texture mode | `getGPUDevice` / `getGPUTexture` returned nothing. Use WebGL2 or force overlay. |
| Panel visible but not clickable | Geometry registration failed and fell back to a bare CSS transform. Check for a `getElementTransform` / `updateElementGeometry` warning. |
| Typing does nothing in an input | `guardUnityInput` did not wrap Unity's key handlers — for example, they were registered after the last panel was created. |
| Texture updates only sometimes | Paint events are not firing for that panel. `HtmlRuntime.UpdateMode = EveryFrame` confirms it. |
| Black or stale panel for the first frames | Normal: no snapshot recorded yet. It retries. |

## Deliberate constraints

* **ES5 in the jslib.** The Emscripten pre-processor is not a full parser.
* **Feature-detect, never version-sniff.** HTML-in-Canvas is in origin trial and its signatures have changed
  three times already; the bridge probes for each variant it knows.
* **Warn once, degrade, retry.** No exception thrown from JS should stop a game frame.
* **Never leave GL state modified.** The bridge runs inside Unity's frame on Unity's context.
