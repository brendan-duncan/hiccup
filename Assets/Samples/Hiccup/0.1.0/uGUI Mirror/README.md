# uGUI Mirror Sample

Open `Scenes/UguiMirrorSample.unity` and press Play (Editor preview), or build for **Web**. The scene holds one
component, `UguiMirrorSampleBootstrap`, which builds an ordinary uGUI form in code and adds an `HtmlUguiMirror`
to its canvas. No HTML is written anywhere in this sample.

What it shows:

| uGUI | In the DOM |
|---|---|
| `Image` (sliced, rounded sprite made in memory) | `border-image` from a tinted PNG export |
| `Text` with rich text, `VerticalLayoutGroup`, `HorizontalLayoutGroup` | Absolutely positioned text with the rectangles uGUI computed; selectable, findable, read by screen readers |
| `Button` with color transitions | `<button>`; hover and press still tint the uGUI image through `Selectable` |
| `Toggle`, `Slider`, `InputField`, `Dropdown` | Native checkbox, range, text input and `<select>` over the uGUI visuals, driving the uGUI components |
| `ScrollRect` + `ContentSizeFitter` | The viewport scrolls in the browser; the content's `anchoredPosition` follows |
| `Image.Type.Filled` (radial) and a rotating `RectTransform` | `conic-gradient` mask and a CSS transform, updated every frame |

Things to try:

* Uncheck **HTML mirror** at the top to see and use the same canvas drawn natively by uGUI, then check it again
  to go back to the DOM copy. Disabling the mirror component restores the canvas and removes the document;
  enabling it rebuilds the mirror. Compare text rendering, control feel, and Tab/screen-reader behavior in
  each mode. An `EventSystem` with the Input System UI module is in the scene so native mode takes input.
* Press **Tab**: focus moves through the button, toggle, slider, input and dropdown with a visible ring.
* Select the subtitle text, or **Ctrl+F** for "Scroll item 17" in a build.
* Type a name: the uGUI `InputField.onValueChanged` updates the label next to it.
* The canvas is hidden with a `CanvasGroup`. Set **Hide Source** off on the mirror to see both at once.
