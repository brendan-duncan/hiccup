# Three.js Desk

Open `Scenes/ThreeJsDesk.unity` and press Play, or build for **Web** (Chrome 148+ with
`chrome://flags/#canvas-draw-element` or an Origin Trial token). Everything in the scene is created at runtime by
`ThreeJsDeskBootstrap`; the scene file contains only that component.

A desk with a monitor. The monitor's screen is an `HtmlDocument` + `HtmlWorldSurface` on a Quad, and the document
is a small "desktop" whose window is an `<iframe>` running a three.js scene: a WebGL canvas from a web page,
painted by HTML-in-Canvas into a Unity texture and composited into the room like any other surface. The mouse
on the desk is a Unity object. Press it and slide it around the pad and the cursor on the screen follows.

**Controls.** Right-drag on the mouse pad to slide the mouse and move the cursor without pressing anything.
Left-press and drag the mouse to press-and-drag: with the cursor over a shape that moves the shape, over empty
space it orbits the three.js camera. Left-press and release without moving to click a shape (it recolors).
Scroll to zoom.

| Piece | Where | Demonstrates |
|---|---|---|
| The screen | `Resources/ThreeJsDesk/Screen.html` + `.style.css` | A same-origin `<iframe>` on a world-space panel, painted in texture mode |
| The page in the frame | `Resources/ThreeJsDesk/ThreeScene.html` | A complete three.js page (loaded from a CDN) with its own WebGL canvas; reads its input from `data-*` attributes on the frame element and writes status back into the parent document |
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

## Notes

* The page repaints every frame, so the bootstrap sets `HtmlRuntime.UpdateMode = HtmlUpdateMode.EveryFrame`.
* The Editor preview renders the page through a real Chrome and shows the same thing; there the iframe is
  captured by the screencast rather than by HTML-in-Canvas, so it does not prove what a build will paint.
  Build with the flag on to see the real thing.
* Without internet the three.js import fails and the screen says so.
