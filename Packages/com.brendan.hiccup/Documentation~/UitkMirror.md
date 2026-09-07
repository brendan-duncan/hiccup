# UI Toolkit Mirror

`HtmlUitkMirror` copies a UI Toolkit runtime panel into an `HtmlDocument` every frame, so an existing UI Toolkit
interface is laid out and styled by UI Toolkit but drawn — and interacted with — as real DOM. The point is the
same as for the [uGUI mirror](UguiMirror.md): text that screen readers, find-in-page and selection can reach,
native controls with keyboard focus and IME, and a picture that HTML-in-Canvas composites like any other
document. This is an experiment: it covers the built-in controls and everything USS can express, and stops where
an element draws its own mesh.

## Using it

Add **Hiccup ▸ UI Toolkit Mirror** to the `UIDocument` whose panel you want to mirror and press Play. The whole
panel is mirrored: every `UIDocument` on the same `PanelSettings`, and anything UI Toolkit adds to the panel root
itself, such as a dropdown menu. Use one mirror per `PanelSettings`. With no document assigned it creates a
full-screen overlay canvas holding an `HtmlDocument` and an `HtmlScreenSurface`.

**Hide Source** (default on) gives the panel root opacity 0 and maps every screen position to "outside the
panel", so UI Toolkit keeps resolving styles, laying out and running but neither renders nor picks the pointer.
Turn it off to see both copies at once. Only screen-space overlay panels without a target texture are mirrored;
a world-space or render-texture panel logs a warning once.

Sprites and textures reach the page as PNG data URLs, exactly as for uGUI, and the same **Fonts** field embeds
web fonts. A `FontAsset` is mapped to a CSS `font-family` of its face's family name (`faceInfo.familyName`), a
`Font` to its embedded font name; either is followed by **Fallback Fonts**. **Dump Exports** writes every PNG to
`persistentDataPath/HiccupUitkExports`.

## What maps to what

| UI Toolkit | DOM |
| --- | --- |
| Every element in the visual tree with `display` other than `none` | `<div class="ug">` at `left/top/width/height` from `layout`; `translate`, `rotate` and `scale` about `transform-origin` become a `transform`, composed in the same order. Children live in a `.ug-kids` container so they always draw above the element's own background, control and text. |
| Panel root, `PanelSettings` scale mode | The root element is sized to the panel root's layout and scaled to the screen, so everything inside is in panel units. |
| `background-color`, `border-*-width`, `border-*-color`, `border-*-radius` | The same CSS on the `.ug-bg` element. |
| `background-image` with a `Texture2D`, `Sprite` or `RenderTexture` | `background-image` from a PNG export of the texture (the sprite's rectangle), with `-unity-background-image-tint-color` baked in; `background-size`, `background-position` and `background-repeat` carried over. A `RenderTexture` is re-exported every **Render Texture Refresh** seconds. |
| `-unity-slice-*`, `-unity-slice-scale` | Composed in Unity at the element's device-pixel size, like a sliced uGUI `Image`, so the browser draws one bitmap without seams. |
| `VectorImage`, `generateVisualContent`, Painter2D | Nothing is drawn; with **Outline Unsupported** on, a dashed magenta outline marks the rectangle. |
| `opacity`, `visibility` | `opacity` and `visibility`, which are inherited the same way in USS and CSS. |
| `overflow: hidden` | `overflow:hidden`, with the border radius on the element so children clip to the rounded box. Read through UI Toolkit's internal clip test, since the resolved style does not expose overflow; see [Limits](#limits). |
| `TextElement` (`Label`, `Button` text, ...) | `<span class="ug-txt">` with `font-size`, `color`, `-unity-font-style`, `-unity-text-align`, `letter-spacing`, `word-spacing`, `white-space`, `text-shadow`, `-unity-text-outline` (as a stroke) and `text-overflow: ellipsis`; rich text tags become HTML. The element is a flex container with the USS padding and borders, so the text sits in the content box. |
| `:hover`, `:active`, `:focus`, `:checked`, `:disabled` styles, USS transitions | Read from the resolved style like everything else. With **Forward Pointer** on, `pointerover`, `pointerdown` and `pointerup` on the DOM are re-sent into the panel as pointer events, so the states and transitions run. |
| `Button`, `RepeatButton` | The element itself is a `<button>`; `disabled` follows `enabledInHierarchy`. A pointer click reaches the `Clickable` as the forwarded press and release; a keyboard click is sent as a `NavigationSubmitEvent`. |
| `Toggle` (and so a `Foldout`'s header) | Invisible `<input type=checkbox>` over the element; `change` → `value`. |
| `RadioButton` | Invisible `<input type=radio>`, named after its `RadioButtonGroup` so the browser keeps one checked; `change` → `value`. |
| `Slider`, `SliderInt` | Invisible `<input type=range>` over the drag container; `input` → `value`. `direction` and `inverted` use `writing-mode`/`direction`. |
| `TextField`, `IntegerField`, `LongField`, `FloatField`, `DoubleField`, `UnsignedIntegerField`, `UnsignedLongField` | A visible `<input>`/`<textarea>` placed on the inner text element with its font; that text element is not mirrored. `multiline`, `isPasswordField`, `maxLength`, `isReadOnly` and the placeholder carry over; numeric fields get an `inputmode`. `input` → `value`, or `change` → `value` when `isDelayed`. |
| `DropdownField`, `EnumField` | **Dropdown Mode** decides. *UI Toolkit Menu* (default): an invisible `<button>` over the input part sends a `NavigationSubmitEvent`, the field opens its `GenericDropdownMenu` on the panel root, and the menu is mirrored like everything else — items, hover, checkmark. *Native Select* (`DropdownField` only): an invisible `<select>` with the `choices`; `change` → `index`. |
| `ScrollView` (and so `ListView`, `TreeView`) | The content viewport gets `overflow:auto` per `mode` with hidden scrollbars; the content container is placed at its rest position and `scrollOffset` goes to `scrollTop/Left`. Browser scrolling writes `scrollOffset` back, so the `Scroller`s, callbacks and virtualization follow. |
| `enabledSelf` off | Pointer events off in the subtree; controls `disabled`. |

Focus: the native controls are transparent, so a `:focus-visible` outline is drawn on the mirrored element
instead. Focus is not forwarded into the panel: the DOM holds the real focus, and in the Editor preview Unity
still sees the keyboard, which would submit the panel's focused element as well.

## How the sync works

The DOM side — the document, the per-node diff, HTML emission, texture export, fonts and event routing — is
`HtmlMirror`, shared with the uGUI mirror and described in [UguiMirror.md](UguiMirror.md#how-the-sync-works).
This class walks the panel's `visualTree` through `hierarchy` in `LateUpdate`: runtime panels update styles and
layout in `PreLateUpdate.UIElementsUpdatePanels`, before scripts' `LateUpdate`, and render at the end of the
frame, so the walk sees the frame's final `layout` and `resolvedStyle`. An element added since the panel's last
update has no layout yet and is skipped until the next frame.

Pointer events are re-sent the way UI Toolkit's own runtime input path sends them: as an IMGUI `Event` in panel
coordinates (the DOM position mapped back through the root's placement), turned into a pooled `PointerMoveEvent`,
`PointerDownEvent` or `PointerUpEvent` and dispatched to the element `IPanel.Pick` finds, which respects
`pickingMode`. A move updates the element under the pointer, so `PointerEnter/Leave` and `:hover` follow; a
press and release reach `Clickable`, dropdown menu items, list rows and manipulators. Nodes that carry a native
control keep the pointer to themselves; their `input`/`change` events drive the element instead.

## Limits

* `IResolvedStyle` does not expose `overflow`, so the mirror binds UI Toolkit's internal `ShouldClip()` by
  reflection, the same test the panel uses. Should a Unity version remove it, only an inline `style.overflow`
  and the `hiccup-clip` USS class are seen; the class always forces clipping. ScrollView viewports are always
  handled.
* Text metrics differ: UI Toolkit sets the rectangle, the browser draws the text inside it with its own font. The
  mirror measures the text in Unity's font and only lets the browser wrap where it did not fit on one line, so a
  line that comes out a few pixels wider runs past the rectangle rather than losing its last word. Map fonts to
  their web versions with **Fonts**, and do not expect identical breaks in paragraphs. `-unity-text-auto-size`
  is read as the resolved font size only if UI Toolkit writes it back.
* Content drawn with `generateVisualContent` or `Painter2D`, vector images, `-unity-material`, filters and
  backdrop filters have no DOM form.
* `Scroller` handles and `MinMaxSlider` thumbs are visual only: the browser scrolls the viewport, and only press
  and release are forwarded, not drags.
* A control's range, step, input type, limit, read-only flag and placeholder are read once when its element is
  created.
* World-space and render-texture panels are not mirrored. Put a world-space UI on an `HtmlWorldSurface`
  document instead.
* `-unity-slice-type: tiled` is composed like `sliced`.
* Language direction and per-element cursors are ignored.
