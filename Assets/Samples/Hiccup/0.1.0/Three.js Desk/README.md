# Three.js Desk

Open `Scenes/ThreeJsDesk.unity` and press Play, or build for **Web** (Chrome 148+ with
`chrome://flags/#canvas-draw-element` or an Origin Trial token). Everything in the scene is created at runtime by
`ThreeJsDeskBootstrap`; the scene file contains only that component.

A desk with a monitor. The monitor's screen is an `HtmlDocument` + `HtmlWorldSurface` on a Quad, and the document
is a small "desktop" whose window is an `<iframe>` running a three.js scene: a WebGL canvas from a web page,
painted by HTML-in-Canvas into a Unity texture and composited into the room like any other surface. The mouse
on the desk is a Unity object. Press it and slide it around the pad and the cursor on the screen follows.

The three.js scene has a monitor of its own, and *its* screen is the published Unity build of this project,
<https://brendan-duncan.github.io/hiccup/build/>, running in a second `<iframe>` and painted by HTML-in-Canvas
into a WebGL texture that three.js draws on a plane. Unity → HTML-in-Canvas → three.js → HTML-in-Canvas → Unity.

**Controls.** Right-drag on the mouse pad to slide the mouse and move the cursor without pressing anything.
Left-press and drag the mouse to press-and-drag: with the cursor over a shape that moves the shape, over empty
space it orbits the three.js camera. Left-press and release without moving to click a shape (it recolors).
Scroll to zoom. Over the big screen the cursor is forwarded into the nested build instead.

| Piece | Where | Demonstrates |
|---|---|---|
| The screen | `Resources/ThreeJsDesk/Screen.html` + `.style.css` | A same-origin `<iframe>` on a world-space panel, painted in texture mode |
| The page in the frame | `Resources/ThreeJsDesk/ThreeScene.html` | A complete three.js page (loaded from a CDN) with its own WebGL canvas; reads its input from `data-*` attributes on the frame element and writes status back into the parent document |
| The monitor in the page | `ThreeScene.html`, `createNestedScreen` | HTML-in-Canvas from plain page script: `drawElementImage` into a hidden host canvas that three.js samples as a `CanvasTexture`, with a CSS3D overlay fallback |
| The desk mouse | `Scripts/DeskMouse.cs`, `DeskScreenController.cs` | Ray-versus-plane dragging without the Physics module; per-frame input to a document through batched attribute writes rather than `Eval` |
| Scene | `Scripts/ThreeJsDeskBootstrap.cs` | `HtmlWorldSurface.PixelsPerUnit`, `HtmlRuntime.UpdateMode`, `HtmlPointerMode.None` |

## Why it works, and why YouTube would not

HTML-in-Canvas only paints what the page could read back itself, so cross-origin embedded content is left out of
the snapshot. A same-origin iframe paints in full, and an iframe filled through `srcdoc` *is* same-origin: it
inherits the origin of the build. That is how the three.js page gets onto the texture without being hosted
anywhere. The three.js library itself is loaded from a CDN, which is fine: script sources are not painted
content. Anything the page draws from cross-origin images or videos without CORS would be excluded, so the scene
uses only geometry and lights.

## How the desk mouse reaches the page

Unity never sends pointer events into the iframe. `DeskMouse` turns the mouse's position on the pad into a 0..1
cursor position, and `DeskScreenController` writes it, together with the button state, a click counter and the
wheel total, as `data-*` attributes on the `<iframe>` element. Those writes go through the bridge's batched
element channel, which is cheap in a build and non-blocking in the Editor preview. The page reads
`window.frameElement.dataset` once per animation frame and does its own raycasting from the cursor. Status text
travels the other way: the page writes into `#status` in the parent document directly, which it may do because
the frame is same-origin.

## The monitor inside the page

`ThreeScene.html` does to the Unity build what the bridge does to `ThreeScene.html`. A hidden host `<canvas>`
behind the WebGL one gets `layoutsubtree`, a `drawable` `<div>` holding the build's `<iframe>` becomes its
child, and every frame the snapshot is drawn into the host with the 2D context's `drawElementImage`; three.js
samples the host as an ordinary `CanvasTexture` on a `MeshBasicMaterial`. The Origin Trial `<meta>` from the
build's `index.html` is cloned into the frame's `<head>` first, because tokens are checked per document.

The host must be a separate canvas. A canvas that carries `layoutsubtree` is left out of any snapshot that
contains it, and this page *is* a snapshot, painted onto the desk's screen by the same API one level up. Put
`layoutsubtree` on the three.js canvas and the scene disappears from the desk while the page's DOM overlays
(cursor, hint, label) stay. The uploading path the bridge uses in a build, `texElementImage2D` straight into a
GL texture, would need the element under the WebGL canvas and so has the same problem here.

That path needs the frame to be **same-origin**, since cross-origin frames are excluded from snapshots, and
the build only is when the sample runs from `brendan-duncan.github.io`. So the page does to the build what the
bridge does to the page: it fetches the build's `index.html` (GitHub Pages sends `Access-Control-Allow-Origin:
*`), makes the template's `buildUrl` and `streamingAssetsUrl` absolute (the loader resolves the latter against
`document.URL`, which is `about:srcdoc` in a srcdoc frame), adds a `<base>` for anything else relative, and
loads it through `srcdoc`. The frame inherits this page's origin and paints in full; the loader, data and wasm
still come from the real site, which is fine because fetched resources are not painted content.

Without the API (or if the fetch fails) the page falls back to *overlay mode*: the same `<iframe>` placed over
the canvas by `CSS3DRenderer` with the same camera, so it still sits on the monitor but nothing in the scene can
occlude it. The corner label on the page says which mode it chose and why. The Editor preview launches its
Chrome with the HTML-in-Canvas feature enabled so the nested build takes the texture path there too.

The desk mouse reaches the nested build too: when the cursor is over the monitor plane its UV is turned into
page pixels and `pointer*` / `mouse*` / `click` / `wheel` events are dispatched into the frame's document at
`elementFromPoint`. Synthetic events are untrusted, but Unity's input and the Hiccup UI listen with ordinary DOM
listeners. This needs `contentDocument`, so it silently does nothing in the rare case the frame is cross-origin.

## Notes

* The page repaints every frame, so the bootstrap sets `HtmlRuntime.UpdateMode = HtmlUpdateMode.EveryFrame`.
* The nested build is the Full UI Sample, not this desk, so the nesting stops at one level. Point `UNITY_URL`
  at a build of *this* scene and every monitor opens another one, without end.
* The Editor preview renders the page through a real Chrome and shows the same thing; there the iframe is
  captured by the screencast rather than by HTML-in-Canvas, so it does not prove what a build will paint.
  Build with the flag on to see the real thing.
* Without internet the three.js import fails and the screen says so.
