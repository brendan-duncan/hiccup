# uGUI Mirror

`HtmlUguiMirror` copies a uGUI `Canvas` into an `HtmlDocument` every frame, so an existing uGUI interface is laid
out by uGUI but drawn — and interacted with — as real DOM. The point is the same as for hand-written documents:
text that screen readers, find-in-page and selection can reach, native controls with keyboard focus and IME, and a
picture that HTML-in-Canvas composites like any other document. This is an experiment: it covers the built-in
components well and stops where uGUI draws its own meshes.

## Using it

Add **Hiccup ▸ uGUI Mirror** to the `Canvas` you want to mirror and press Play. With no document assigned it
creates a full-screen overlay canvas holding an `HtmlDocument` and an `HtmlScreenSurface`, sorted just above the
source. **Hide Source** (default on) puts a `CanvasGroup` with alpha 0 and raycasts off on the canvas, so uGUI keeps
laying out and running but neither renders nor sees the pointer. Turn it off to see both copies at once.

Sprites and textures reach the page as PNG data URLs. Readable textures (anything created at runtime, or imported
with Read/Write enabled) are copied on the CPU; others are blitted through an sRGB render target and read back.
**Dump Exports** writes every PNG to `persistentDataPath/HiccupUguiExports` so you can check what the page gets.

Fonts: the browser cannot read Unity `Font` or TMP font assets, so by default a Unity font name maps to a CSS
`font-family` of that name followed by **Fallback Fonts**. To get matching glyphs, rename the TTF/OTF/WOFF2 to
`.bytes`, import it as a `TextAsset` and list it under **Fonts** with the family name (the TMP asset name without
` SDF`); it is embedded as an `@font-face`.

## What maps to what

| uGUI | DOM |
| --- | --- |
| Every active `RectTransform` | `<div class="ug">` at `left/top/width/height` computed from `rect` and `localPosition`; rotation and scale become a `transform` about the pivot. Children live in a `.ug-kids` container so they always draw above the element's own background, control and text. |
| `Canvas` rectangle, `CanvasScaler` | The root element is placed at the canvas' screen rectangle and scaled by `scaleFactor`, so everything inside is in canvas units. |
| `Image` (Simple / Sliced / Tiled / Filled) | `.ug-bg` with a `background-image` from a PNG export of the sprite's texture rectangle. Tint is baked into the export (cached per sprite and color, quantized to 128 levels per channel, within one 8-bit step of exact). Sliced images are composed in Unity at the element's device-pixel size with the same border rules as `Image` (borders that do not fit are scaled down together), one bitmap per size, because CSS `border-image` leaves hairline seams between slices at fractional pixel positions. Preserve Aspect → `contain`. Horizontal and vertical fills → `clip-path`, Radial360 → a `conic-gradient` mask. Radial90/180 are not supported. |
| `RawImage` | The same with the whole texture and `uvRect` as `background-size`/`position`. A `RenderTexture` is re-exported every **Render Texture Refresh** seconds. |
| `Text` | `<span class="ug-txt">` with font, size, style, color, alignment, wrapping, line spacing; rich text tags become HTML. The element is a flex container for vertical alignment. Best Fit uses the size the generator settled on. |
| `TMP_Text` | Same, plus weight, underline/strikethrough, case transforms, margins, character spacing. |
| `Shadow`, `Outline` on text | `text-shadow`. Ignored on images. |
| `Mask` | `overflow:hidden` plus `mask-image` from the sprite; `showMaskGraphic` hides the background. |
| `RectMask2D` | `overflow:hidden`. |
| Nested `Canvas` with Override Sorting | `z-index` from its sorting order, so a Dropdown's list and blocker paint above later siblings. |
| `CanvasGroup` | `opacity`; `interactable`/`blocksRaycasts` off disables pointer events in the subtree. |
| `Button` | The element itself is a `<button>`; `disabled` follows `IsInteractable()`. Click → `ISubmitHandler` (`onClick`). |
| `Toggle` | Invisible `<input type=checkbox>` over the rectangle; `change` → `isOn`. |
| `Slider` | Invisible `<input type=range>`; `input` → `value`. Right-to-left and vertical directions use `direction`/`writing-mode`. |
| `Dropdown`, `TMP_Dropdown` | **Dropdown Mode** decides. *uGUI List* (default): the element is a `<button>` whose click calls `Show()`; uGUI instantiates its template list and blocker under the canvas, and they are mirrored like everything else — item Toggles, hover tints, the fade-out — so the list looks exactly as authored. *Native Select*: an invisible `<select>` with the options over the caption; `change` → `value`. Chrome's customizable select is styled with the dropdown's background and caption colors, and screen readers and keyboards get a real listbox. |
| `InputField`, `TMP_InputField` | A visible `<input>`/`<textarea>` placed on the text component's rectangle with its font; the uGUI text component is not mirrored. `input` → `text`, `change` → `onEndEdit`. Content type maps to `type`/`inputmode`; character limit and read-only carry over. |
| `ScrollRect` | The viewport gets `overflow:auto` (per axis) with hidden scrollbars; the content is placed at its rest position and the offset goes to `scrollTop/Left`. Browser scrolling writes `content.anchoredPosition` back, so `onValueChanged`, scrollbars and code reading the position all work. |
| `Selectable` transitions | `pointerover/down/up/leave` on the DOM are forwarded as `IPointerEnter/Exit/Down/UpHandler`, so color tint and sprite swap transitions run and are mirrored. |
| Any other `Graphic` | Nothing is drawn; with **Outline Unsupported** on, a dashed magenta outline marks the rectangle. |

Focus: the native controls are transparent, so a `:focus-visible` outline is drawn on the mirrored element
instead (`.ug:has(> .ug-ctl:focus-visible)`).

## How the sync works

The mirror subscribes to `Canvas.willRenderCanvases`, which fires after `CanvasUpdateRegistry` has rebuilt layout
for the frame, and walks the source hierarchy. Each `RectTransform` has a node holding the last emitted state:
the style string, background style, text HTML and style, control value and the like. A node that is new, or whose
parent or sibling order changed, is emitted as an HTML subtree and inserted after its previous sibling; an existing
node gets only the attribute or property writes whose strings differ. Nodes not visited in a walk are removed.
Everything goes through the normal `HtmlDocument`/`HtmlElement` API, so writes are batched per frame in the
Editor preview and synchronous in a web build.

Scroll is the one place the DOM is a source of truth. `scroll` does not bubble, so a capture-phase listener
installed through `Eval` sends the viewport's id and offsets to C# with `HUI.send('ugscroll', …)`, which the
mirror receives through `HtmlDocument.OnMessage`.

## Limits

* Text metrics differ: uGUI sets the rectangle, the browser draws the text inside it with its own font. To keep
  the two from disagreeing about line breaks, the mirror only lets the browser wrap where Unity's generator
  produced more than one line, and vertical truncation clips only vertically, so a line that comes out a few
  pixels wider runs past the rectangle rather than losing its last word. `LegacyRuntime` maps to Liberation Sans
  and Arial, which share its metrics; embed other fonts to get close, and do not expect identical breaks in
  paragraphs. Best Fit sizes are read from Unity's generator and are approximate under a `CanvasScaler`.
* Custom `Graphic` subclasses (`OnPopulateMesh`), custom materials, `BaseMeshEffect`s on images, gradients and
  procedural UI have no DOM form. The fallback for such content is to render it to a `RenderTexture` shown by a
  `RawImage`, which is mirrored as a periodically refreshed picture.
* Control attributes that rarely change — a slider's range and step, an input field's type, limit and read-only —
  are read once when the element is created.
* `Scrollbar` handles are visual only; scroll the viewport. Dragging a `Slider` handle works because the whole
  slider is the range input.
* World-space canvases are projected as a flat screen rectangle, not in perspective. Put a world-space UI on an
  `HtmlWorldSurface` document instead.
* Nested canvases are mirrored as plain elements; sorting between separate root canvases is not.
* `PointerMode` and `BlockUnityInput` are ignored by the Editor preview, so the source canvas is also hidden from
  raycasts there to stop uGUI reacting to the same clicks.
