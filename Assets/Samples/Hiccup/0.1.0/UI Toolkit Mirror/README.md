# UI Toolkit Mirror Sample

Open `Scenes/UitkMirrorSample.unity` and press Play (Editor preview), or build for **Web**. The scene holds one
component, `UitkMirrorSampleBootstrap`, which builds an ordinary UI Toolkit form in code on a runtime panel and
adds an `HtmlUitkMirror` to its `UIDocument`. No HTML is written anywhere in this sample; the only style sheet
is the USS under `Resources/UitkMirror`.

What it shows:

| UI Toolkit | In the DOM |
|---|---|
| USS backgrounds, borders, rounded corners, padding | The same CSS on each element's background, at the rectangle Yoga computed |
| `Label` with rich text, `white-space: normal` | Absolutely positioned text; selectable, findable, read by screen readers |
| `Button` with `:hover` and `:active` rules and a transition | `<button>`; the pointer is forwarded into the panel, so the USS states run and are mirrored |
| `Toggle`, `SliderInt`, `TextField`, `DropdownField` | Native checkbox, range, text input and (with **Dropdown Mode** set to Native Select) `<select>` over the panel's visuals, driving the elements |
| `ScrollView` | The viewport scrolls in the browser; `scrollOffset` follows, and the rows' `:hover` still works |
| `ProgressBar` and a `rotate` that changes every frame | Plain elements and a CSS transform, updated every frame |

Things to try:

* Uncheck **HTML mirror** at the top to see and use the same panel drawn natively by UI Toolkit, then check it
  again to go back to the DOM copy. Disabling the mirror component shows the panel and removes the document;
  enabling it rebuilds the mirror. Compare text rendering, control feel, and Tab/screen-reader behavior.
* Hover the button and the scroll rows: their USS `:hover` colors come from the panel, not from CSS.
* Press **Tab**: focus moves through the button, toggle, slider, input and dropdown with a visible ring.
* Select the subtitle text, or **Ctrl+F** for "Scroll item 17" in a build.
* Type a name: the `TextField`'s change callback updates the label next to it.
* The panel is hidden by giving its root opacity 0. Set **Hide Source** off on the mirror to see both at once.
