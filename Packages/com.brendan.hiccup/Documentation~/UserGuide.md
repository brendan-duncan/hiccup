# Hiccup — user guide

Build Unity UI for the web out of HTML and CSS, and get the things the web already does well: screen readers,
find-in-page, text selection, IME, native form controls, CSS Grid, web fonts.

This guide is task-oriented. For the mechanism, see [Runtime.md](Runtime.md); for the Editor preview, see
[EditorPreview.md](EditorPreview.md).

## Contents

- [Is this the right tool?](#is-this-the-right-tool)
- [Setup](#setup)
- [Your first HUD](#your-first-hud)
- [Writing the HTML and CSS](#writing-the-html-and-css)
- [Size and placement](#size-and-placement)
- [Reacting to the UI](#reacting-to-the-ui)
- [Updating the UI](#updating-the-ui)
- [Panels in the 3D scene](#panels-in-the-3d-scene)
- [Input and click-through](#input-and-click-through)
- [Accessibility](#accessibility)
- [Working in the Editor](#working-in-the-editor)
- [Performance](#performance)
- [Troubleshooting](#troubleshooting)
- [Limitations](#limitations)

## Is this the right tool?

**Use it when** you are shipping to the web and the UI needs to be accessible, searchable, selectable, or
typed into with an IME; when you want to author with CSS Grid, web fonts and media queries; or when you want the
UI to be inspectable in DevTools and testable with web tooling.

**Do not use it when** you ship to platforms other than the web — there is no bridge on desktop, console or
mobile; when the UI has to run in every browser today, since compositing needs Chrome with a flag or an Origin
Trial token (other browsers get a working overlay, but the UI cannot then sit inside the 3D scene); or when the
UI is a small number of simple elements, where UI Toolkit is less machinery.

It composes fine with UI Toolkit and uGUI — they can coexist in the same project and even the same canvas.

## Setup

**Unity** 6000.0 or newer with the Web platform module. WebGL2 and WebGPU are both supported.

**Chrome** 148 or newer, with one of:

* `chrome://flags/#canvas-draw-element` enabled — for local development, or
* an [Origin Trial token](https://developer.chrome.com/origintrials/#/view_trial/3478467762190286849) for your
  origin — for a deployed build.

Other browsers, and Chrome without either, fall back to overlay mode automatically. The UI still works.

**WebGL template.** Set **Project Settings ▸ Player ▸ Resolution and Presentation ▸ WebGL Template** to `Hiccup`.
It carries a full-window canvas and a placeholder `<meta http-equiv="origin-trial">` tag — paste your token
there. Skipping this is fine for flag-based local testing; the bridge sets the canvas attributes it needs at
runtime either way.

**The sample.** Open **Full UI Sample** from `Assets/Samples/Hiccup` in the Hiccup project. It is a complete game UI — menu, settings
form with ARIA tabs, inventory listbox, HUD, `<dialog>` modals, toasts, themes, and an interactive console on a
3D quad — and it is the fastest way to see what the package expects of you.

## Your first HUD

Three pieces: a `TextAsset` of HTML, a `TextAsset` of CSS, and a GameObject in a Canvas carrying `HtmlDocument`,
`HtmlScreenSurface` and your own script. Nothing else: the bridge (`HtmlRuntime`) creates itself the first time a
document is enabled.

### 1. The content

In the Project window choose **Create ▸ Hiccup ▸ HTML Document** and **Create ▸ Hiccup ▸ Style Sheet** (also
under **Assets ▸ Create**). Each starts as a small template; replace it with the snippets below. A `.html` or
`.css` file copied in from elsewhere works the same way. Both show up with their own icons in the Project window
(orange `<>` for HTML, blue `{}` for CSS) and in the Inspector's object fields.

**`Hud.html`** — a body fragment, not a whole document. No `<html>`, `<head>` or `<body>`.

```html
<div class="hud">
  <span class="label">Score</span>
  <output id="score">0</output>
  <button data-action="pause">Pause</button>
</div>
```

**`Hud.css`** — imported as a `TextAsset` by the package's `.css` importer.

```css
.hud {
  position: absolute; inset: 16px auto auto 16px;
  display: flex; gap: 12px; align-items: center;
  font: 600 16px/1 system-ui, sans-serif; color: #e8ecf3;
}
button { font: inherit; padding: 6px 14px; border-radius: 8px; }
```

### 2. The scene

1. **GameObject ▸ UI ▸ Canvas.** Leave **Render Mode** at *Screen Space - Overlay*. (Screen Space - Camera and
   World Space work too; the surface reads the Canvas' camera.)
2. **GameObject ▸ UI ▸ Raw Image** as a child of the Canvas. Rename it `HUD`. Leave its **Texture** and
   **Material** empty; the surface fills both in at runtime.
3. Make it fill the Canvas: in the Rect Transform anchor preset picker choose the bottom-right *stretch/stretch*
   preset, then set Left/Right/Top/Bottom to 0. This rectangle *is* the document: it is sized to it every
   frame and your CSS positions things relative to its corners (see [Size and placement](#size-and-placement)).
4. **Add Component ▸ Hiccup ▸ HTML Document.** Drag `Hud.html` onto **Html** and `Hud.css` into
   **Style Sheets** (set the array size to 1 first, or drop the asset onto the array header).
5. **Add Component ▸ Hiccup ▸ HTML Screen Surface.** Its **Document** field may be left empty: it picks up the
   `HtmlDocument` on the same GameObject.

The Inspector on `HtmlDocument` shows an info box telling you whether the Editor preview is on; either way the
scene is complete at this point. The remaining fields have sensible defaults for a HUD (**Pointer Mode**
*ChildrenOnly*, so clicks on empty HUD space still reach the game).

### 3. The script

Create `Hud.cs` and add it to the same `HUD` GameObject (step 5 above). It finds the document on its own
GameObject, so there is nothing to drag; if you keep the script somewhere else, assign **Doc** in the Inspector.

```csharp
using UnityEngine;
using Hiccup;

public class Hud : MonoBehaviour
{
    [SerializeField] HtmlDocument doc;   // left empty: uses the HtmlDocument on this GameObject
    int score;

    void OnEnable()
    {
        if (doc == null) doc = GetComponent<HtmlDocument>();

        // Event handlers can be registered at any time; they are kept until the panel exists.
        doc.OnAction("pause", OnPause);

        // Element access (Q, QAll, Eval) needs the browser-side panel, which HtmlDocument creates in its own
        // OnEnable. Component OnEnable order is not guaranteed, so wait for Created if it is not there yet.
        if (doc.IsCreated) Refresh(doc);
        else doc.Created += Refresh;
    }

    void OnDisable()
    {
        doc.Created -= Refresh;
        doc.OffAction("pause", OnPause);
    }

    // Called by your game code, e.g. from a pickup's OnTriggerEnter.
    public void AddScore(int amount)
    {
        score += amount;
        if (doc.IsCreated) Refresh(doc);
    }

    void OnPause(HtmlEvent e) => Time.timeScale = 0f;

    void Refresh(HtmlDocument d) => d.Q("#score").Text = score.ToString();
}
```

Two rules fall out of this, and they apply to every script you write against a document:

* **Register handlers whenever you like.** `On`, `OnAction` and `Listen` queue their subscriptions and apply
  them when the panel is created, so `OnEnable` or `Awake` are both fine.
* **Touch elements only after `IsCreated`.** Before that, `Q` returns a no-op handle and `Eval` returns an empty
  string, silently. Subscribe to `Created` for initial state, as above; the sample's controllers use the same
  `IsCreated ? Wire : Created += Wire` shape. In a web build `Created` fires inside `Create()`; in the Editor
  preview it fires a few frames later, once Chrome has loaded the page, and `IsCreated` stays false until then.
  Code that follows the shape above works the same in both.

### 4. Run it

Press **Play**. With **Window ▸ Hiccup ▸ Editor Preview (Chrome)** on, the Game view shows the real HUD
rendered by Chrome, and the Pause button works. With the preview off the surface draws a placeholder so you can
still check the layout of everything around it.

Then build for Web and open it in Chrome. Press **Tab** — the focus ring appears inside the Unity frame. Press
**Ctrl+F** and search for "Score" — the browser finds it.

### From code instead of the Inspector

Deactivate the GameObject while configuring so `HtmlDocument.OnEnable` sees the finished setup:

```csharp
var go = new GameObject("HUD", typeof(RectTransform), typeof(RawImage));
go.SetActive(false);
go.transform.SetParent(canvas.transform, false);
var rect = go.GetComponent<RectTransform>();
rect.anchorMin = Vector2.zero; rect.anchorMax = Vector2.one;
rect.offsetMin = Vector2.zero; rect.offsetMax = Vector2.zero;
var doc = go.AddComponent<HtmlDocument>();
doc.Html = hudHtml;                       // TextAsset
doc.StyleSheets = new[] { hudCss };       // TextAsset[]
go.AddComponent<HtmlScreenSurface>();
go.AddComponent<Hud>();
go.SetActive(true);
```

`HiccupSampleBootstrap.cs` in the sample builds its whole scene this way, HUD and world panel included.

## Writing the HTML and CSS

Your fragment is placed inside this structure, which the package builds:

```html
<div class="hui-panel">          <!-- the panel; sized in CSS pixels -->
  <style>…your CSS…</style>
  <div class="hui-content">      <!-- your fragment goes here -->
  </div>
</div>
```

So `.hui-content` is your root. It is already `width: 100%; height: 100%`, so position things against it
directly.

**What works:** everything Chrome supports. Grid, flexbox, custom properties, container queries, web fonts,
`@media (prefers-reduced-motion)`, `@media (forced-colors)`, `<dialog>`, `<details>`, `<input type=range>`,
emoji, transitions.

**What does not:**

* **`<script>` tags never run.** Content is assigned through `innerHTML`. Use `HtmlDocument.Eval(js)` when you
  genuinely need script — it runs with `panel`, `root` and `HUI` in scope.
* **Cross-origin `<iframe>` content is not drawn** in texture mode; the browser leaves the area empty. Same-origin
  frames paint in full, WebGL canvases inside them included, and a frame filled through `srcdoc` counts as
  same-origin: the **Three.js Desk** sample runs a whole three.js page on a monitor that way. Overlay mode shows
  cross-origin frames too (set `HtmlRuntime.ForceOverlay = true` before the first document is enabled).
* **External resources** load normally, but a slow web font means a frame or two of fallback text. Prefer
  bundling fonts with the build.

**Backgrounds.** The panel is transparent by default, so the scene shows through. Give `.hui-content` or your own
root a background if you want the UI opaque.

**Multiple stylesheets** are concatenated in array order, then `Extra Css` from the inspector is appended last —
handy for per-instance overrides such as a theme variable block.

## Size and placement

**The Raw Image's rectangle is the document.** `HtmlScreenSurface` measures the RectTransform in screen pixels
every frame and, with **Size Document To Rect** on (the default), resizes the document to match. The browser
lays your HTML out in a box exactly that size, snapshots it into a texture, and the Raw Image draws the texture
back into the same rectangle. So:

* **CSS coordinates start at the rectangle's top-left corner.** `position: absolute; top: 16px; left: 16px`
  is 16 CSS pixels in from the corner of the Raw Image, wherever the Raw Image sits on screen. Your root
  (`.hui-content`) already fills the box, so flexbox and grid work against its edges too.
* **A Raw Image the size of the screen gives you a screen-sized document.** Position the HUD's parts in CSS.
  A small Raw Image in a corner gives you a small document; put a second `HtmlDocument` + `HtmlScreenSurface`
  on another Raw Image for a second, independent panel. Several small panels are cheaper to update than one
  full-screen one (see [Performance](#performance)).
* **The document is measured in CSS pixels.** One CSS pixel is one screen pixel divided by the browser's
  device pixel ratio, so on a 2x display a 1920-pixel-wide rectangle is a 960 CSS pixel document — the same
  numbers a web page sees in that browser. `HtmlRuntime.Instance.CssPerScreenPixel` is the conversion.
* **The Canvas Scaler changes the rectangle, not your CSS.** Under *Scale With Screen Size* a stretched Raw
  Image simply covers more or fewer CSS pixels; text stays the size your CSS says. For UI that should grow with
  the screen, write responsive CSS (`vw`/`vmin` units, `clamp()`, container queries) — or turn **Size Document
  To Rect** off, set **Size** to a fixed design resolution, and let the Raw Image stretch the texture. Hit
  testing and screen-reader bounds follow the stretched rectangle either way.
* **Leave the Raw Image's Color white and its Material empty.** The surface assigns the package's premultiplied
  UI material at runtime; the color tints the whole document.

**World panels** work the other way round: **Size** on the document is the CSS pixel size, and the Quad's scale
is its size in the world. Keep their aspect ratios equal or the texture stretches; **Pixels Per Unit** on the
surface derives the size from the mesh instead. See [Panels in the 3D scene](#panels-in-the-3d-scene).

**Sharpness.** The texture is `size × devicePixelRatio × resolutionScale`.

* `ResolutionScale` supersamples. Leave it at 1 for screen-space UI; use 2 for world panels or anything viewed
  minified or at an angle.
* `Mipmaps` on means trilinear + anisotropic sampling. Keep it for world panels; you can turn it off for a
  pixel-exact full-screen overlay.
* CSS media and container queries respond to the document size, so a HUD resized with its rectangle can
  re-flow rather than shrink.

## Reacting to the UI

Three ways to hook an event, in increasing specificity.

**`data-action`** — the pattern the sample uses throughout, and the one to reach for first. Put the attribute on
a button and route by name; it matches the closest ancestor carrying the attribute, so an icon inside the button
still works.

```html
<button data-action="show" data-screen="settings">Settings</button>
```

```csharp
doc.OnAction("show", e => ShowScreen(e.GetData("screen")));
```

**By element id and type:**

```csharp
doc.On("volume", "input", e => audio.volume = e.ValueAsFloat);
doc.On("player-name", "change", e => profile.Name = e.value);
```

**By type, anywhere in the document:**

```csharp
doc.On("keydown", e => { if (e.IsKey("Escape")) CloseMenu(); });
```

`HtmlEvent` carries `type`, `id`, `tag`, `name`, `action`, `value` (plus `ValueAsFloat` / `ValueAsInt`),
`isChecked`, `key`, `code`, pointer `x`/`y` in panel pixels, `ctrl`/`shift`/`alt`, the ancestor `path`, and
`data-*` attributes via `GetData(name)`. Set `e.Handled = true` to stop further C# dispatch.

Handlers run in order: `EventReceived`, then target-id handlers, then ancestor-id handlers nearest-first, then
`data-action`, then type handlers.

Only `click`, `dblclick`, `input`, `change`, `submit`, `keydown`, `focusin` and `focusout` are forwarded by
default. `On` enables whatever type you ask for; call `doc.Listen("pointerover")` explicitly if you subscribe
through `EventReceived` instead.

## Updating the UI

`Q` and `QAll` take CSS selectors and return `HtmlElement`, which is chainable. A selector that matches nothing
returns a safe no-op object, so null checks are unnecessary.

```csharp
doc.Q("#score").Text = "1370";
doc.Q("#health").SetValue(72).Text = "72%";
doc.Q("#panel").AddClass("visible").RemoveClass("dimmed");
doc.Q("#submit").Disabled = !form.IsValid;
doc.Q("#screen-menu").Hidden = true;
doc.Q("#confirm-dialog").ShowModal();          // <dialog>, with real focus trapping
doc.Q("#toasts").Append("<div class='toast'>Saved</div>");
doc.Q("#old-toast").Remove();

string typed = doc.Q("#search").Value;
bool on     = doc.Q("#bloom").Checked;
Rect where  = doc.Q("#target").Bounds;         // panel CSS pixels
```

Swap the whole document at runtime with `doc.Html = otherTextAsset`, or `doc.SetHtml(string)` /
`doc.SetCss(string)` for generated content. `doc.Reload()` re-applies the serialized assets.

**Dispose handles you create in a loop.** `HtmlElement` is `IDisposable` and each one holds a browser-side slot:

```csharp
foreach (var btn in doc.QAll(".nav-btn"))
{
    btn.SetAttribute("aria-current", btn.GetAttribute("data-screen") == screen);
    btn.Dispose();
}
```

A single `doc.Q("#x").Text = "…"` in an update loop is fine; a hundred undisposed handles per frame is not.

## Panels in the 3D scene

Use `HtmlWorldSurface` instead of `HtmlScreenSurface`. The surface derives the document's screen transform from
the camera every frame, so the browser hit-tests the *projected* DOM — a form on a tilted panel is clickable
where you see it, and a screen reader reports the right bounds.

**In the scene:**

1. **GameObject ▸ 3D Object ▸ Quad.** Scale it to the panel's size in world units; the document is drawn across
   the whole front face. Delete the Mesh Collider unless you want the quad to block raycasts (HTML hit testing
   does not use it).
2. **Add Component ▸ Hiccup ▸ HTML Document.** Assign **Html** and **Style Sheets** as for a HUD, and set
   **Size** to the document's CSS pixel size; the quad's aspect ratio should match it. Set **Resolution Scale**
   to 2 and leave **Mipmaps** on. For a panel that should take every click, set **Pointer Mode** to *Panel*.
3. **Add Component ▸ Hiccup ▸ HTML World Surface.** **Target Camera** defaults to `Camera.main`. The quad's
   material is replaced at runtime by the package's unlit premultiplied material, so whatever is on the
   MeshRenderer does not matter.
4. Add your controller script to the quad. It is written exactly like the HUD script above.

**From code:**

```csharp
var quad = GameObject.CreatePrimitive(PrimitiveType.Quad);
quad.SetActive(false);
Destroy(quad.GetComponent<Collider>());
quad.transform.localScale = new Vector3(3.6f, 2.5f, 1f);
var doc  = quad.AddComponent<HtmlDocument>();
doc.Html = consoleHtml;
doc.StyleSheets = new[] { consoleCss };
doc.Size = new Vector2Int(576, 400);           // same aspect as the quad
doc.ResolutionScale = 2f;                       // supersample; it will be viewed at an angle
var surface = quad.AddComponent<HtmlWorldSurface>();
surface.TargetCamera = Camera.main;             // the default; shown for clarity
quad.SetActive(true);
```

Set **Pixels Per Unit** above zero on the surface to derive the document size from the mesh bounds instead of
setting it by hand. Leave **Mipmaps** on — world panels are minified and will shimmer without them.

## Input and click-through

**Pointer Mode** decides what the panel captures:

| Mode | Behavior |
| --- | --- |
| `ChildrenOnly` *(default)* | Only the direct children of your content capture clicks. Clicking empty space reaches Unity — what a HUD wants. |
| `Panel` | The whole rectangle captures input. For a full-screen menu that should block the game. |
| `None` | Display only. |

**Block Unity Input** (on by default) stops events the UI handled from also reaching Unity's input system. Leave
it on unless you specifically want both to see the same click. It is also what makes typing in an `<input>` work
— without it Unity's key handling swallows keystrokes.

**Prevent Form Submit** (on by default) calls `preventDefault()` on `submit` so a form never navigates the page
away from your game. Submit events still reach your handlers.

## Accessibility

This is the reason the package exists, and most of it is just writing decent HTML. Things worth being deliberate
about:

* **Use real elements.** `<button>`, `<input>`, `<dialog>`, `<output>`, `<fieldset>`. A `<div>` with a click
  handler is invisible to a screen reader and unreachable by keyboard.
* **Label everything.** `<label for>`, or `aria-label` where no visible text exists.
* **Announce state changes** that are not otherwise visible to assistive technology:

  ```csharp
  doc.Announce("Mission complete");                  // polite
  doc.Announce("Shield critical", assertive: true);  // interrupts
  ```

* **Manage focus on navigation.** When you swap screens, move focus to the new heading, as the sample does:

  ```csharp
  doc.Q("#screen-settings h2").SetAttribute("tabindex", "-1").Focus();
  ```

* **`<dialog>` + `ShowModal()`** gives you focus trapping, `Esc` to close and background inertness for free.
  Do not hand-roll a modal.
* **Respect user preferences** in CSS: `@media (prefers-reduced-motion: reduce)` and
  `@media (forced-colors: active)`.

Test with a real screen reader in a real build — NVDA, VoiceOver or ChromeVox. The Editor preview cannot check
any of this.

## Working in the Editor

Play mode renders documents through a real Chrome, so you can iterate without building. Layout, styling, script
behavior, mouse input and events are all genuine. Toggles live under **Window ▸ Hiccup**.

Keys typed in the Game view reach the document the last click landed in, so text fields and shortcuts work; IME
and composed input do not. It cannot show you accessibility or HTML-in-Canvas compositing either. It is a fast
iteration loop, not a substitute for testing a build. See [EditorPreview.md](EditorPreview.md).

## Performance

* **Textures update only when the DOM changes.** A static HUD costs nothing per frame. Avoid CSS animations on
  large panels — every frame of the animation is a fresh snapshot and upload.
* **Panel area is what costs.** A full-screen 4K panel at `resolutionScale = 2` is a large upload every time it
  changes. Prefer several small panels over one full-screen one where the layout allows.
* **Batch DOM work.** Setting `InnerHtml` once beats twenty individual mutations.
* **`contain` is already applied** to panels, so a change in one does not re-layout another.
* **Reads are cheaper than writes** in a build (both are direct calls), but in the Editor preview reads are a
  round trip — keep `Value` and `GetAttribute` out of per-frame code and you will be fast in both.

## Troubleshooting

**The UI renders as a DOM overlay instead of inside the 3D scene.** Overlay mode. Check the Chrome flag or your
Origin Trial token, and log `HtmlRuntime.Instance.Features` from the build to see what was detected.

**Nothing renders at all in the build.** Look for `[Hiccup]` warnings in the browser console. On WebGPU, a
message about resolving the device or texture means the panel cannot be composited — switch to WebGL2, or set
`HtmlRuntime.ForceOverlay = true` before creating documents.

**The panel is visible but clicks do nothing.** Geometry registration failed and the panel fell back to a plain
CSS transform. Check the console for a `getElementTransform` or `updateElementGeometry` warning.

**Typing in a text field does nothing.** Make sure **Block Unity Input** is on.

**Clicks pass through my full-screen menu into the game.** Set **Pointer Mode** to `Panel`.

**The texture only updates sometimes.** Set `HtmlRuntime.UpdateMode = HtmlUpdateMode.EveryFrame`. If that fixes
it, paint events are not firing for that panel — worth reporting, with your Chrome version.

**Colors look wrong or edges are haloed.** `PremultipliedAlpha` and the surface material must agree. Leave the
setting at its default unless you know why you are changing it.

**Everything is blank for the first frame or two.** Normal — there is no snapshot yet. It retries.

Turn on `HtmlRuntime.DebugLogging = true` before creating your first document for a verbose trace.

## Limitations

* **Web builds only.** No bridge exists on desktop, mobile or console.
* **Compositing needs Chrome 148+** with the flag or a token. The API is in origin trial and its signatures have
  changed between versions; the bridge feature-detects each variant it knows about.
* **No `<script>`** in your HTML; use `Eval`.
* **Cross-origin iframes** are not drawn in texture mode; same-origin ones, `srcdoc` included, are. Overlay mode
  shows cross-origin frames.
* **Overlay mode on an opaque canvas cannot be occluded** by scene geometry — it is drawn above the frame. With a
  transparent canvas (the package template sets `webglContextAttributes: { alpha: true }`) the overlay goes behind
  the frame and surfaces cut holes for their panels, so nearer geometry covers them; post-processing that rewrites
  alpha defeats this.
