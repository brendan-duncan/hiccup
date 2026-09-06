# Full UI Sample — "Orbital Salvage"

Open `Scenes/HiccupSample.unity` and build for **Web** (WebGL2 or WebGPU). Everything in the scene is created at
runtime by `HiccupSampleBootstrap`, so the scene file only contains that one component.

What it shows:

| Piece | Where | Demonstrates |
|---|---|---|
| Main menu, settings form, inventory, HUD | `Resources/HiccupSample/GameUI.html` + `.style.css` + `.tooltips.js` | Screen switching, `data-action` routing, live form input, ARIA tabs with arrow keys, roving-tabindex listbox, `<dialog>` modals with focus trapping, toasts, `aria-live` announcements, themes (dark/light/high-contrast), `forced-colors` and `prefers-reduced-motion` support |
| Drone console on a quad | `Resources/HiccupSample/WorldPanel.html` + `.style.css` | A perspective-projected, fully interactive HTML form in the 3D scene (`HtmlWorldSurface`), including a text input and a log |
| Game logic | `Scripts/SampleGame.cs`, `SalvageTarget.cs` | Unity gameplay driven from HTML events and pushing state back to the DOM |

Things to try in the build (Chrome 148+ with `chrome://flags/#canvas-draw-element`, or an Origin Trial token):

* Press **Tab** — focus rings appear inside the Unity frame, dialogs trap focus.
* Turn on a screen reader (NVDA, VoiceOver, ChromeVox): the whole UI is announced with correct roles and bounds.
* **Ctrl+F** for "sensitivity" — find-in-page highlights text on the Settings screen.
* Select and copy text, right-click an input for the native context menu, use the IME in the pilot-name field.
* Click the empty HUD area: the click reaches Unity and salvages a cube. Click a button: Unity never sees it.
* Rotate the camera in your head: the console on the quad is hit-tested in perspective.

In browsers without HTML-in-Canvas the same DOM is shown in an overlay above the canvas. The status line in the
top bar tells you which mode is active.
