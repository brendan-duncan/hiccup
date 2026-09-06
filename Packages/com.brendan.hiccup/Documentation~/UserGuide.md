# Hiccup — user guide

Hiccup lets you build the user interface of a Unity web game out of HTML and CSS, the same languages web pages
are written in. Because the interface is a real web page, the browser gives you things for free that are hard
to build in a game engine: screen readers can read it, the browser's find-in-page (Ctrl+F) can search it, text
can be selected and copied, users can type in any language, form controls behave the way people expect, and you
can lay it out with CSS Grid and web fonts.

This guide is organized by task. It assumes you know your way around the Unity Editor but nothing about
Hiccup, and only a little about HTML. If you want to know how it works underneath, read [Runtime.md](Runtime.md).
For details on previewing inside the Editor, read [EditorPreview.md](EditorPreview.md).

## Contents

- [Is this the right tool?](#is-this-the-right-tool)
- [A few words you will see a lot](#a-few-words-you-will-see-a-lot)
- [Setup](#setup)
- [Your first HUD](#your-first-hud)
- [Writing the HTML and CSS](#writing-the-html-and-css)
- [Size and placement](#size-and-placement)
- [Reacting to the UI](#reacting-to-the-ui)
- [Updating the UI](#updating-the-ui)
- [Running JavaScript in the page](#running-javascript-in-the-page)
- [Panels in the 3D scene](#panels-in-the-3d-scene)
- [Mirroring an existing uGUI interface](#mirroring-an-existing-ugui-interface)
- [Input and click-through](#input-and-click-through)
- [Accessibility](#accessibility)
- [Working in the Editor](#working-in-the-editor)
- [Performance](#performance)
- [Troubleshooting](#troubleshooting)
- [Limitations](#limitations)

## Is this the right tool?

**Use Hiccup when** your game runs in a web browser and you need the interface to be accessible, searchable,
selectable, or typeable in any language. It is also a good fit if you would rather lay out your UI with CSS
than with Unity's tools, or if you want to inspect and test the UI with normal web developer tools.

**Do not use Hiccup when:**

* You ship to desktop, console, or mobile. Hiccup only works in a web build. On other platforms there is no
  browser to draw the page.
* The UI must work in every browser today. Drawing the page *inside* the 3D scene needs Chrome with a flag
  turned on, or an Origin Trial token (explained under [Setup](#setup)). Other browsers still show a working
  UI, but it floats on top of the game instead of being part of the scene.
* The UI is a handful of simple elements. Unity's own UI Toolkit is less machinery for that.

Hiccup works alongside UI Toolkit and uGUI. They can live in the same project, and even in the same Canvas.

## A few words you will see a lot

* **HUD** (heads-up display): the score, health bar and buttons drawn over the game.
* **HTML** describes *what* is on a page (a button, a heading, a text box). **CSS** describes *how it looks*
  (colors, sizes, positions).
* **DOM**: the browser's live, in-memory version of the page. "Real DOM" means the UI is made of genuine browser
  elements, not a picture of them.
* **uGUI**: Unity's older, Canvas-based UI system (the one with `Image`, `Text`, `Button` components).
* **Document**: in Hiccup, one piece of HTML plus its CSS, shown in one rectangle. A game can have several.
* **Panel**: the browser-side box that holds one document.
* **TextAsset**: Unity's name for a plain text file stored in your project. Hiccup reads HTML and CSS from
  TextAssets.
* **Texture mode** and **overlay mode**: in texture mode the browser draws the page into an image that Unity
  can place anywhere, including inside the 3D scene. In overlay mode the page floats above the game as an
  ordinary web element. Hiccup picks the best one the browser supports.
* **IME** (input method editor): the system that lets people type Chinese, Japanese, Korean and other
  languages that need more than one keystroke per character.
* **Screen reader**: software that reads a page aloud for people who cannot see it well.

## Setup

**Unity** 6000.0 or newer, with the Web platform module installed. Both WebGL2 and WebGPU work.

**Chrome** 148 or newer. For the page to be drawn inside the 3D scene, Chrome also needs one of:

* the flag `chrome://flags/#canvas-draw-element` turned on. Paste that address into Chrome's address bar. Use
  this while developing on your own machine.
* an [Origin Trial token](https://developer.chrome.com/origintrials/#/view_trial/3478467762190286849) for the
  web address you deploy to. Use this for a published build. An Origin Trial is Chrome's way of letting a
  website opt in to a feature that is not finished yet.

Any other browser, and Chrome without the flag or token, automatically falls back to overlay mode. The UI still
works.

**WebGL template.** Open **Project Settings ▸ Player ▸ Resolution and Presentation** and set **WebGL Template**
to `Hiccup`. This template makes the game canvas fill the browser window and includes a placeholder
`<meta http-equiv="origin-trial">` tag where you paste your token. You can skip this step while testing with
the flag; Hiccup sets the canvas attributes it needs when the game starts.

**The samples.** The Hiccup project has three samples under `Assets/Samples/Hiccup`. Start with
**Full UI Sample**. It is a complete game UI: a menu, a settings form, an inventory list, a HUD, pop-up dialogs,
toast messages, themes, and a working console on a 3D quad. It is the fastest way to see what a finished Hiccup
UI looks like. **uGUI Mirror** shows an existing uGUI form turned into HTML with a single component (see
[Mirroring an existing uGUI interface](#mirroring-an-existing-ugui-interface)). **Three.js Desk** runs a whole
second web page on a monitor in the scene.

## Your first HUD

You need three things: an HTML file, a CSS file, and a GameObject inside a Canvas that carries two Hiccup
components and one script of your own. That is all. The bridge between Unity and the browser (`HtmlRuntime`)
creates itself the first time a document is enabled.

### 1. The content

In the Project window, right-click and choose **Create ▸ Hiccup ▸ HTML Document**, then
**Create ▸ Hiccup ▸ Style Sheet**. (Both are also under the **Assets ▸ Create** menu, next to **Script**, which
makes a `.js` file for page-side code; you will not need one for this HUD.) Each new file starts as a
small template; replace the contents with the snippets below. You can also copy `.html` and `.css` files in from
anywhere else. Both kinds show up with their own icons in the Project window (orange `<>` for HTML, blue `{}`
for CSS) and in Inspector fields.

**`Hud.html`.** This is a *fragment*, not a full page. Do not add `<html>`, `<head>` or `<body>` tags; Hiccup
wraps your fragment in a page for you.

```html
<div class="hud">
  <span class="label">Score</span>
  <output id="score">0</output>
  <button data-action="pause">Pause</button>
</div>
```

**`Hud.css`.** Hiccup includes an importer that turns `.css` files into TextAssets.

```css
.hud {
  position: absolute; inset: 16px auto auto 16px;
  display: flex; gap: 12px; align-items: center;
  font: 600 16px/1 system-ui, sans-serif; color: #e8ecf3;
}
button { font: inherit; padding: 6px 14px; border-radius: 8px; }
```

### 2. The scene

1. **GameObject ▸ UI ▸ Canvas.** Leave **Render Mode** at *Screen Space - Overlay*. (The other two modes,
   *Screen Space - Camera* and *World Space*, work too. Hiccup reads the camera from the Canvas.)
2. **GameObject ▸ UI ▸ Raw Image**, as a child of the Canvas. Rename it `HUD`. Leave its **Texture** and
   **Material** fields empty. Hiccup fills them in when the game runs.
3. Make the Raw Image fill the Canvas. In its Rect Transform, open the anchor preset picker (the square in the
   top-left), choose the bottom-right *stretch/stretch* preset, then set Left, Right, Top and Bottom to 0.
   This rectangle *is* the document. Hiccup resizes the document to match it every frame, and your CSS
   positions things relative to its corners (see [Size and placement](#size-and-placement)).
4. **Add Component ▸ Hiccup ▸ HTML Document.** Drag `Hud.html` onto the **Html** field. Set the **Style Sheets**
   array size to 1 and drag `Hud.css` into it (or drop the asset straight onto the array header).
5. **Add Component ▸ Hiccup ▸ HTML Screen Surface.** Leave its **Document** field empty. It finds the
   `HtmlDocument` on the same GameObject by itself.

The `HtmlDocument` Inspector shows an info box telling you whether the Editor preview is on. Either way, the
scene is complete. The remaining fields have sensible defaults for a HUD. In particular, **Pointer Mode** is
*ChildrenOnly*, which means clicks on empty HUD space still reach the game.

### 3. The script

Create `Hud.cs` and add it to the same `HUD` GameObject. It finds the document on its own GameObject, so there is
nothing to drag. If you put the script somewhere else, drag the document into its **Doc** field instead.

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
        // OnEnable. Unity does not promise which component's OnEnable runs first, so wait for Created if the
        // panel is not there yet.
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

Two rules come out of this script, and they apply to every script you write against a document:

* **Register event handlers whenever you like.** `On`, `OnAction` and `Listen` remember what you asked for and
  apply it once the panel exists. Calling them from `OnEnable` or `Awake` is fine.
* **Only touch elements after `IsCreated` is true.** Before that, `Q` returns a handle that does nothing,
  `Eval` returns an empty string, and `EvalAsync` completes with an empty string too (in the Editor preview it
  fails instead, with a message that says so). None of them warns you otherwise. To set the initial state,
  subscribe to the `Created` event as shown above. The sample's scripts all use the same `IsCreated ? Wire : Created += Wire` shape.
  In a web build `Created` fires right away. In the Editor preview it fires a few frames later, once Chrome has
  loaded the page. Code written in the shape above works the same in both.

### 4. Run it

Press **Play**. If **Window ▸ Hiccup ▸ Editor Preview (Chrome)** is on, the Game view shows the real HUD drawn
by Chrome, and the Pause button works. If the preview is off, Hiccup draws a placeholder in the rectangle so you
can still check the layout of everything around it.

Then build for Web and open the build in Chrome. Press **Tab**: a focus ring appears on the Pause button, inside
the Unity frame. Press **Ctrl+F** and search for "Score": the browser finds it.

### From code instead of the Inspector

You can build the same setup in a script. Deactivate the GameObject while you configure it, so that
`HtmlDocument` sees the finished setup when it is enabled:

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

`HiccupSampleBootstrap.cs` in the Full UI Sample builds its whole scene this way, HUD and world panel included.

## Writing the HTML and CSS

Hiccup places your fragment inside this structure:

```html
<div class="hui-panel">          <!-- the panel; sized in CSS pixels -->
  <style>…your CSS…</style>
  <div class="hui-content">      <!-- your fragment goes here -->
  </div>
</div>
```

So `.hui-content` is your root element. It already has `width: 100%; height: 100%`, so you can position things
against it directly.

**What works:** everything Chrome supports. CSS Grid, flexbox, custom properties, container queries, web fonts,
`@media (prefers-reduced-motion)`, `@media (forced-colors)`, `<dialog>`, `<details>`, `<input type=range>`,
emoji, transitions.

**What does not:**

* **`<script>` tags never run.** Hiccup inserts your content with `innerHTML`, and browsers ignore scripts
  added that way. Put page-side code in `.js` files listed under the document's **Scripts**, or call
  `HtmlDocument.Eval(js)` / `EvalAsync(js)`. Either way the code runs with `panel`, `root` and `HUI` available
  as variables, and `HUI.send` carries results back to C#. See
  [Running JavaScript in the page](#running-javascript-in-the-page).
* **Cross-origin `<iframe>` content is not drawn** in texture mode. (An iframe is a page embedded inside
  another page. "Cross-origin" means it comes from a different website.) The browser leaves that area empty.
  Iframes from your own site draw in full, including any WebGL canvases inside them. A frame filled through the
  `srcdoc` attribute counts as your own site; the **Three.js Desk** sample runs a whole three.js page on a
  monitor that way. Overlay mode shows cross-origin frames too. To force overlay mode, set
  `HtmlRuntime.ForceOverlay = true` before the first document is enabled.
* **External resources** (fonts, images from a URL) load normally, but a slow web font means a frame or two of
  fallback text. Prefer bundling fonts with the build.

**Backgrounds.** The panel is transparent by default, so the game shows through. Give `.hui-content`, or your
own root element, a background color if you want the UI to be opaque.

**Multiple stylesheets** are joined together in array order. Then the **Extra Css** text from the Inspector is
added at the end. This is handy for per-instance overrides, such as a block of theme variables.

## Size and placement

**The Raw Image's rectangle is the document.** Every frame, `HtmlScreenSurface` measures the Rect Transform in
screen pixels. With **Size Document To Rect** on (the default), it resizes the document to match. The browser
lays out your HTML in a box of exactly that size, takes a picture of it, and the Raw Image draws that picture
back into the same rectangle. This has a few consequences:

* **CSS coordinates start at the rectangle's top-left corner.** `position: absolute; top: 16px; left: 16px`
  means 16 CSS pixels in from the corner of the Raw Image, wherever the Raw Image is on screen. Your root
  element (`.hui-content`) already fills the box, so flexbox and grid also work against its edges.
* **A Raw Image the size of the screen gives you a screen-sized document.** Then you position the HUD's parts
  in CSS. A small Raw Image in a corner gives you a small document. For a second, separate panel, put another
  `HtmlDocument` and `HtmlScreenSurface` on another Raw Image. Several small panels are cheaper to update than
  one full-screen one (see [Performance](#performance)).
* **The document is measured in CSS pixels.** A CSS pixel is what a web page calls a pixel. On a normal display
  it is one screen pixel. On a high-resolution "2x" display it is two screen pixels wide, so a 1920-pixel-wide
  rectangle becomes a 960 CSS pixel document. These are the same numbers a web page would see in that browser.
  `HtmlRuntime.Instance.CssPerScreenPixel` gives you the conversion factor.
* **The Canvas Scaler changes the rectangle, not your CSS.** Under *Scale With Screen Size*, a stretched Raw
  Image simply covers more or fewer CSS pixels. Text stays the size your CSS says. If you want the UI to grow
  with the screen, either write responsive CSS (`vw`/`vmin` units, `clamp()`, container queries), or turn
  **Size Document To Rect** off, set **Size** to a fixed design resolution, and let the Raw Image stretch the
  picture. Clicks and screen-reader bounds follow the stretched rectangle either way.
* **Leave the Raw Image's Color white and its Material empty.** Hiccup assigns its own material at runtime.
  The Color tints the whole document.

**World panels** (documents on 3D objects) work the other way round. **Size** on the document is the size in
CSS pixels, and the Quad's scale is its size in the world. Keep the two aspect ratios equal or the picture
stretches. Alternatively, set **Pixels Per Unit** on the surface and it works out the document size from the
mesh. See [Panels in the 3D scene](#panels-in-the-3d-scene).

**Sharpness.** The picture's size in pixels is `document size × device pixel ratio × resolution scale`.

* **Resolution Scale** draws the page larger and shrinks it down, which makes it sharper. Leave it at 1 for
  screen-space UI. Use 2 for world panels, or anything seen at a distance or at an angle.
* **Mipmaps** on means smoother sampling when the picture is shrunk or tilted. Keep it on for world panels.
  You can turn it off for a pixel-exact full-screen overlay.
* CSS media queries and container queries respond to the document size, so a HUD whose rectangle changes size
  can rearrange itself rather than just shrink.

## Reacting to the UI

There are three ways to respond to something happening in the page. They go from the most general to the most
specific.

**`data-action`.** This is the pattern the sample uses throughout, and the one to reach for first. Put a
`data-action` attribute on a button and route by its name. Hiccup looks for the attribute on the clicked element
and its ancestors, so clicking an icon inside the button still works.

```html
<button data-action="show" data-screen="settings">Settings</button>
```

```csharp
doc.OnAction("show", e => ShowScreen(e.GetData("screen")));
```

**By element id and event type:**

```csharp
doc.On("volume", "input", e => audio.volume = e.ValueAsFloat);
doc.On("player-name", "change", e => profile.Name = e.value);
```

**By event type, anywhere in the document:**

```csharp
doc.On("keydown", e => { if (e.IsKey("Escape")) CloseMenu(); });
```

Every handler receives an `HtmlEvent`. It carries the event `type`, the element's `id`, `tag`, `name` and
`action`, its `value` (also as `ValueAsFloat` and `ValueAsInt`), `isChecked` for checkboxes, `key` and `code`
for keyboard events, pointer `x` and `y` in panel pixels, the `ctrl`, `shift` and `alt` modifier flags, the
list of ancestor elements in `path`, and any `data-*` attributes through `GetData(name)`. Set `e.Handled = true`
to stop the event from reaching any further C# handlers.

Handlers run in this order: the `EventReceived` event first, then handlers registered for the target's id, then
handlers for ancestor ids (nearest first), then `data-action` handlers, then handlers registered by type only.

By default only these events are sent from the browser to Unity: `click`, `dblclick`, `input`, `change`,
`submit`, `keydown`, `focusin` and `focusout`. Calling `On` with another type turns that type on automatically.
If you listen through `EventReceived` instead, call `doc.Listen("pointerover")` (for example) to turn the type on
yourself.

## Updating the UI

`Q` finds one element and `QAll` finds all matching elements. Both take a CSS selector (such as `#score` for
the element with id `score`, or `.nav-btn` for every element with class `nav-btn`). They return an
`HtmlElement`, whose methods can be chained. If nothing matches, `Q` returns a safe object that ignores every
call, so you never need a null check.

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

To replace the whole document while the game runs, assign `doc.Html = otherTextAsset`. For content you generate
in code, use `doc.SetHtml(string)` and `doc.SetCss(string)`. `doc.Reload()` goes back to the assets set in the
Inspector.

**Dispose handles you create in a loop.** Each `HtmlElement` holds a slot on the browser side, and it implements
`IDisposable` so you can give the slot back:

```csharp
foreach (var btn in doc.QAll(".nav-btn"))
{
    btn.SetAttribute("aria-current", btn.GetAttribute("data-screen") == screen);
    btn.Dispose();
}
```

A single `doc.Q("#x").Text = "…"` in an update loop is fine. A hundred handles per frame that are never disposed
is not.

## Running JavaScript in the page

Most UIs never need this: `Q`, the element API and the event handlers cover buttons, forms, text and dialogs.
Reach for JavaScript when the page has to do something itself, such as run an animation, talk to a web API,
or drive a widget that lives in the DOM, and when the answer has to come back to C#.

**Scripts are `.js` files attached to the document.** Create one with **Create ▸ Hiccup ▸ Script**, drag it into
the document's **Scripts** list, and it runs in the page as soon as the document exists, before your `Created`
handler, and again after `Reload()`. Several scripts run in the order they are listed. This is where page-side
code belongs: keep it next to the HTML and CSS it works with instead of inside a C# string.

```js
// Tooltips.js: Escape hides tooltips, hovering or focusing a trigger brings them back.
root.addEventListener('keydown', function (e) {
  if (e.key !== 'Escape') return;
  root.querySelectorAll('.tip-wrap').forEach(function (w) { w.classList.add('tip-dismissed'); });
});
```

A script is a function body, so it can `return` early and `await` promises, and it sees the same three variables
as `Eval` below. Listeners it attaches to `root` survive later changes to the HTML; listeners on elements inside
`root` go away with those elements. A script that throws is reported in the Unity console with the asset's name.
Because `Reload()` runs the scripts again, a script that must not run twice can guard itself with a flag on
`root`.

The three ways below run code from C# instead.

**`Eval` runs code now and returns a string.** The code is the body of a function with three things in scope:
`panel` (the document's outer element), `root` (the element your HTML lives in) and `HUI` (Hiccup's bridge).
Whatever it `return`s comes back as a string; objects and arrays come back as JSON.

```csharp
string title = doc.Eval("return root.querySelector('h1').textContent;");
doc.Eval("root.querySelector('#settings-form').reset();");
```

**`EvalAsync` runs code that needs to wait.** The body is an `async` function, so it can `await`, and the result
arrives through a `Task<string>` when the code returns or its promise settles. A throw or a rejected promise
faults the task with `HtmlEvalException`, so wrap it in `try` if the code can fail.

```csharp
async void LoadLeaderboard()
{
    try
    {
        string json = await doc.EvalAsync(@"
            const r = await fetch('/api/leaderboard');
            return await r.json();");
        ShowLeaderboard(JsonUtility.FromJson<Leaderboard>(json));
    }
    catch (HtmlEvalException e) { Debug.LogWarning(e.Message); }
}

// Wait for a CSS animation, then continue.
await doc.EvalAsync("await root.querySelector('#toast').getAnimations()[0].finished;");
```

**`HUI.send` sends a message from the page to C#.** Code that runs through `Eval` or `EvalAsync`, including the
event listeners it installs, can call `HUI.send(name, payload)` at any time. On the C# side, `OnMessage` handles
a name, and `MessageReceived` sees every message. A string payload arrives exactly as sent; anything else is
turned into JSON, which `DataAs<T>()` reads back into a serializable class.

```csharp
doc.Eval(@"
    root.querySelector('#inventory').addEventListener('drop', e => {
        e.preventDefault();
        HUI.send('item-dropped', { item: e.dataTransfer.getData('text'), slot: e.target.dataset.slot });
    });");

[System.Serializable] class Drop { public string item; public string slot; }
doc.OnMessage("item-dropped", m => MoveItem(m.DataAs<Drop>()));

doc.OnMessage("volume", m => audio.volume = m.DataAsFloat);   // HUI.send('volume', 0.4)
```

Messages are delivered as DOM events are: right away in a build, and through the same per-frame pump as clicks
in the Editor preview. Set `m.Handled = true` to stop later handlers for the same name.

`<script>` tags inside your HTML do not run (the browser ignores them when HTML is inserted this way). Put
page-side code in the document's **Scripts** instead, or run it with `Eval` from the `Created` event, which fires
once the page can be written to in both the build and the Editor.

## Panels in the 3D scene

To put a document on an object in the 3D world, use `HtmlWorldSurface` instead of `HtmlScreenSurface`. Every
frame it works out where the object appears on screen and tells the browser. The browser then does its click
testing on the *projected* shape, so a form on a tilted panel is clickable exactly where you see it, and a screen
reader reports the right position.

**In the scene:**

1. **GameObject ▸ 3D Object ▸ Quad.** Scale it to the panel's size in world units. The document is drawn across
   the whole front face. Delete the Mesh Collider unless you want the quad to block physics raycasts; Hiccup does
   not use it for clicks.
2. **Add Component ▸ Hiccup ▸ HTML Document.** Set **Html** and **Style Sheets** as for a HUD. Set **Size** to
   the document's size in CSS pixels; the quad's aspect ratio should match it. Set **Resolution Scale** to 2 and
   leave **Mipmaps** on. If the panel should take every click, set **Pointer Mode** to *Panel*.
3. **Add Component ▸ Hiccup ▸ HTML World Surface.** **Target Camera** defaults to `Camera.main`. Hiccup replaces
   the quad's material at runtime, so whatever is on the Mesh Renderer does not matter.
4. Add your own script to the quad. It is written exactly like the HUD script above.

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
doc.ResolutionScale = 2f;                       // draw sharper; it will be viewed at an angle
var surface = quad.AddComponent<HtmlWorldSurface>();
surface.TargetCamera = Camera.main;             // the default; shown for clarity
quad.SetActive(true);
```

If you would rather not set **Size** by hand, set **Pixels Per Unit** on the surface to a value above zero and
it derives the document size from the mesh. Leave **Mipmaps** on. World panels are usually shown smaller than
their picture, and they shimmer without mipmaps.

## Mirroring an existing uGUI interface

If you already have a UI built with uGUI (Unity's Canvas, `Image`, `Text`, `Button` and friends), you do not
have to rewrite it in HTML. The `HtmlUguiMirror` component copies a uGUI Canvas into a Hiccup document every
frame. uGUI keeps doing the layout, animations and logic, but the result is drawn, clicked and read as real DOM.
Your existing scripts, `onClick` listeners and layout groups keep working without changes.

What you get is the same as for a hand-written document: text that screen readers, find-in-page and selection
can reach; toggles, sliders, text fields and dropdowns that are native browser controls with keyboard focus and
IME support; and a picture that Chrome composites into the scene like any other document.

This feature is experimental. It handles the built-in uGUI components well and stops where uGUI draws custom
meshes. See [What the mirror can and cannot copy](#what-the-mirror-can-and-cannot-copy) below.

### Tutorial: mirror a uGUI form

This walkthrough starts from a uGUI Canvas you already have. If you do not have one, build a quick one with
**GameObject ▸ UI ▸ Button**, **GameObject ▸ UI ▸ Toggle** and **GameObject ▸ UI ▸ Input Field**, which each
create a Canvas for you if the scene has none. Or open the **uGUI Mirror** sample, which builds a whole form in
code.

**1. Add the component.** Select the Canvas GameObject and choose **Add Component ▸ Hiccup ▸ uGUI Mirror**. The
component needs a `Canvas` on the same GameObject; Unity adds one if it is missing. Leave every field at its
default for now.

**2. Press Play.** With the Editor preview on (**Window ▸ Hiccup ▸ Editor Preview (Chrome)**), the Game view
looks almost the same as before. The difference is that what you see is now the browser's copy. Behind the
scenes, the mirror has:

* created a second, full-screen Canvas holding an `HtmlDocument` and an `HtmlScreenSurface`, sorted just above
  your Canvas so it draws on top;
* put a `CanvasGroup` on your Canvas with alpha 0 and raycasts off, so uGUI keeps laying out and running but is
  neither drawn nor clicked;
* walked every active `RectTransform` in your Canvas and created a matching DOM element with the exact
  rectangle uGUI computed for it.

**3. Try it.** Click the button: its `onClick` fires just as before. Drag the slider or type in the input field:
the uGUI component's value changes, and any listener you attached runs. Press **Tab**: focus moves through the
controls with a visible ring. Select some text with the mouse.

**4. Compare the two.** Turn **Hide Source** off in the Inspector and press Play again. Now both copies are
visible at once, the uGUI original and the HTML mirror on top of it, so you can spot differences. Turn it back
on when you are done.

**5. Build for Web** and open the build in Chrome. Press **Ctrl+F** and search for a label in your form: the
browser finds it. Turn on a screen reader (NVDA on Windows, VoiceOver on macOS) and move through the form: each
control is announced as a real checkbox, slider, text field or list, with its current value.

That is the whole setup. Everything below is optional.

### Adding it from code

```csharp
using Hiccup.Ugui;
using UnityEngine.UI;

// canvasGo is a GameObject that already has a Canvas and the UI you built.
var mirror = canvasGo.AddComponent<HtmlUguiMirror>();

// Later, to switch back to native uGUI drawing and remove the document:
mirror.enabled = false;
// ...and to rebuild the mirror:
mirror.enabled = true;
```

Disabling the component shows the uGUI Canvas again and removes the document. Enabling it rebuilds the mirror
from scratch. The sample's **HTML mirror** checkbox does exactly this so you can A/B the two on the fly. Note
that native uGUI needs an `EventSystem` in the scene to take input itself; while the mirror is on, the
`EventSystem` sits idle and the DOM drives the components instead.

### The Inspector fields

| Field | What it does |
| --- | --- |
| **Document** | The `HtmlDocument` to mirror into. Leave it empty and the mirror creates a full-screen one for you. Set it if you want the mirror to draw into a document you placed yourself, for instance on a smaller Raw Image. |
| **Hide Source** | On by default. Hides the uGUI Canvas and stops it receiving clicks, so only the HTML copy is visible and interactive. Turn it off to see both copies at once. |
| **Fallback Fonts** | A CSS font list added after each Unity font name. The browser uses the first one it has. The default is `system-ui, 'Segoe UI', Roboto, Helvetica, Arial, sans-serif`. |
| **Fonts** | Font files to embed so the browser draws text with the same faces as Unity. See [Matching fonts](#matching-fonts). |
| **Render Texture Refresh** | How often, in seconds, a `RawImage` showing a `RenderTexture` is re-copied to the page. Default 0.5. Set it to 0 to copy it once. |
| **Outline Unsupported** | On by default. Draws a dashed magenta outline where a uGUI `Graphic` has no HTML equivalent, so you can see what was skipped. Turn it off for a release build. |
| **Dropdown Mode** | *uGUI List* (default): clicking a Dropdown opens uGUI's own list, which is mirrored like everything else, so it looks exactly as you built it. *Native Select*: an invisible browser `<select>` opens the browser's own picker instead. Screen readers and keyboards understand the native one best. |
| **Dump Exports** | Writes every sprite and texture the mirror sends to the page as a PNG file under `persistentDataPath/HiccupUguiExports`, so you can check what the browser receives. |

### Matching fonts

The browser cannot read Unity `Font` or TextMesh Pro font assets. By default the mirror asks the browser for a
font family with the same name as the Unity font, followed by the **Fallback Fonts** list. Unity's default
`LegacyRuntime` font is mapped to Liberation Sans and Arial, which have the same letter widths, so text lines up.

To use your own font in the browser:

1. Find the original `.ttf`, `.otf` or `.woff2` file for the font.
2. Copy it into your project and rename the file extension to `.bytes`. Unity imports a `.bytes` file as a
   `TextAsset`.
3. On the mirror, add an entry to **Fonts**. Set **Family** to the Unity font's name. For a TextMesh Pro font
   asset, use the asset name without the ` SDF` suffix. Drag the `.bytes` file into **File**.

The mirror embeds the font in the page as an `@font-face` rule, and text drawn with that Unity font now uses the
same glyphs in the browser.

### What the mirror can and cannot copy

**Copied:**

* Every active `RectTransform`, at the exact rectangle uGUI computed, including rotation and scale.
* `Image` in Simple, Sliced, Tiled and Filled modes (horizontal, vertical and Radial360 fills), with its tint.
  Sprites are sent to the page as PNG images.
* `RawImage`, including one showing a `RenderTexture`.
* `Text` and `TMP_Text`, with font, size, style, color, alignment, wrapping and rich text tags.
* `Shadow` and `Outline` on text.
* `Mask`, `RectMask2D` and `CanvasGroup` (opacity and interactivity).
* `Button`, `Toggle`, `Slider`, `Dropdown`, `TMP_Dropdown`, `InputField` and `TMP_InputField`. Each becomes a
  native browser control that writes its value back into the uGUI component.
* `ScrollRect`. The browser scrolls the viewport and writes the offset back to the content, so `onValueChanged`
  and your code still see the right position.
* `Selectable` color and sprite transitions. Hover and press states are forwarded to uGUI, so the tints run
  and are mirrored.

**Not copied:**

* Custom `Graphic` subclasses, custom materials and shaders, gradients, and mesh effects on images. The
  workaround is to render that part to a `RenderTexture` and show it in a `RawImage`, which the mirror copies as
  a periodically refreshed picture.
* Radial90 and Radial180 image fills.
* Exact text line breaks. uGUI decides the rectangle, but the browser draws the text with its own font, so long
  paragraphs may wrap differently. Embedding the same font (see above) gets close. Short labels are fine.
* `Scrollbar` handle dragging. The handle is drawn but does nothing; scroll the viewport instead. Dragging a
  `Slider` handle does work.
* World-space canvases are flattened to a screen rectangle rather than drawn in perspective. For a UI in the 3D
  world, use an `HtmlWorldSurface` document instead.
* A slider's range and step, and an input field's type, character limit and read-only flag, are read once when
  the element is created. Changing them later has no effect until the element is rebuilt.

The sample under `Assets/Samples/Hiccup/0.1.0/uGUI Mirror` builds a form with all of the copied components and
is a good reference for what to expect. For the full component-by-component mapping and how the sync works,
see [UguiMirror.md](UguiMirror.md).

## Input and click-through

**Pointer Mode** on the document decides what the panel captures:

| Mode | Behavior |
| --- | --- |
| `ChildrenOnly` *(default)* | Only the direct children of your content capture clicks. Clicking empty space reaches Unity. This is what a HUD wants. |
| `Panel` | The whole rectangle captures input. Use it for a full-screen menu that should block the game. |
| `None` | Display only. Nothing is clickable. |

**Block Unity Input** (on by default) stops an event the UI handled from also reaching Unity's input system.
Leave it on unless you specifically want both to see the same click. It is also what makes typing in a text
field work. Without it, Unity's key handling swallows the keystrokes.

**Prevent Form Submit** (on by default) stops a `<form>` from navigating the browser away from your game when
it is submitted. Your `submit` handlers still run.

## Accessibility

This is the reason the package exists, and most of it is just writing decent HTML. Things worth being deliberate
about:

* **Use real elements.** `<button>`, `<input>`, `<dialog>`, `<output>`, `<fieldset>`. A `<div>` with a click
  handler is invisible to a screen reader and cannot be reached with the keyboard.
* **Label everything.** Use `<label for="...">`, or an `aria-label` attribute where there is no visible text.
* **Announce state changes** that a screen reader user would otherwise miss:

  ```csharp
  doc.Announce("Mission complete");                  // read when the reader is idle
  doc.Announce("Shield critical", assertive: true);  // interrupts what is being read
  ```

* **Move focus when you change screens.** When you swap to a new screen, put keyboard focus on its heading, as
  the sample does:

  ```csharp
  doc.Q("#screen-settings h2").SetAttribute("tabindex", "-1").Focus();
  ```

* **Use `<dialog>` with `ShowModal()`** for pop-ups. You get focus trapping, `Esc` to close, and the rest of the
  page made inert, all for free. Do not build your own modal.
* **Respect user preferences** in CSS with `@media (prefers-reduced-motion: reduce)` and
  `@media (forced-colors: active)`.

Test with a real screen reader in a real web build. NVDA, VoiceOver and ChromeVox are all free. The Editor
preview cannot check any of this.

## Working in the Editor

In Play mode, Hiccup renders documents through a real copy of Chrome, so you can iterate without making a build.
Layout, styling, script behavior, mouse input and events are all genuine. The toggles live under
**Window ▸ Hiccup**.

Keys typed in the Game view go to the document you last clicked in, so text fields and keyboard shortcuts work.
IME and other composed input do not. The preview also cannot show accessibility features or the in-scene
compositing. Treat it as a fast iteration loop, not as a substitute for testing a build.
See [EditorPreview.md](EditorPreview.md).

## Performance

* **The picture is only updated when the page changes.** A HUD that sits still costs nothing per frame. Avoid
  CSS animations on large panels: every frame of the animation is a fresh picture to copy and upload.
* **Bigger panels cost more.** A full-screen 4K panel at **Resolution Scale** 2 is a large upload every time it
  changes. Prefer several small panels over one full-screen one where the layout allows.
* **Batch your changes.** Setting `InnerHtml` once beats twenty separate small changes.
* **Panels are isolated from each other.** Hiccup applies CSS `contain` to every panel, so a change in one does
  not re-layout another.
* **Reading is cheaper than writing in a build.** Both are direct calls there. In the Editor preview, however,
  every read is a round trip to Chrome. Keep `Value` and `GetAttribute` out of per-frame code and you will be
  fast in both.

## Troubleshooting

**The UI floats on top of the game instead of being inside the 3D scene.** You are in overlay mode. Check the
Chrome flag or your Origin Trial token. Log `HtmlRuntime.Instance.Features` from the build to see what Hiccup
detected.

**Nothing renders at all in the build.** Look for `[Hiccup]` warnings in the browser's developer console
(press F12). On WebGPU, a message about resolving the device or texture means the panel cannot be composited.
Switch to WebGL2, or set `HtmlRuntime.ForceOverlay = true` before creating documents.

**The panel is visible but clicks do nothing.** Registering the panel's position with the browser failed, and it
fell back to a plain CSS transform. Check the console for a `getElementTransform` or `updateElementGeometry`
warning.

**Typing in a text field does nothing.** Make sure **Block Unity Input** is on.

**Clicks pass through my full-screen menu into the game.** Set **Pointer Mode** to `Panel`.

**The picture only updates sometimes.** Set `HtmlRuntime.UpdateMode = HtmlUpdateMode.EveryFrame`. If that fixes
it, the browser is not reporting paints for that panel. That is worth reporting as a bug, with your Chrome
version.

**Colors look wrong, or edges have a halo.** The `PremultipliedAlpha` setting and the surface material must
agree. Leave the setting at its default unless you know why you are changing it.

**Everything is blank for the first frame or two.** This is normal. There is no picture yet, and Hiccup retries.

**The uGUI mirror shows dashed magenta boxes.** Those mark uGUI graphics the mirror cannot copy. See
[What the mirror can and cannot copy](#what-the-mirror-can-and-cannot-copy). Turn off **Outline Unsupported** to
hide the boxes.

**Mirrored text wraps differently from the uGUI original.** The browser is using a different font. Embed the
Unity font under **Fonts** on the mirror. See [Matching fonts](#matching-fonts).

For a detailed trace, set `HtmlRuntime.DebugLogging = true` before creating your first document.

## Limitations

* **Web builds only.** There is no browser on desktop, mobile or console, so there is nothing to draw the page.
* **Drawing inside the scene needs Chrome 148+** with the flag or an Origin Trial token. The browser feature is
  still in trial and has changed between versions. Hiccup detects each version it knows about.
* **No `<script>` in your HTML.** Use the document's **Scripts** list, `Eval` or `EvalAsync`; see
  [Running JavaScript in the page](#running-javascript-in-the-page).
* **Cross-origin iframes** are not drawn in texture mode. Same-origin ones, including `srcdoc`, are. Overlay
  mode shows cross-origin frames.
* **In overlay mode, the UI cannot be hidden behind scene geometry** when the game canvas is opaque, because
  the page is drawn above the whole frame. With a transparent canvas (the Hiccup WebGL template sets
  `webglContextAttributes: { alpha: true }`), the overlay goes behind the frame and each surface cuts a hole for
  its panel, so nearer objects cover it. Post-processing that rewrites the alpha channel defeats this.
* **The uGUI mirror is experimental.** It copies the built-in components and stops at custom meshes. See
  [Mirroring an existing uGUI interface](#mirroring-an-existing-ugui-interface).
