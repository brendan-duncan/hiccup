using System.Collections.Generic;
using Hiccup.Uitk;
using UnityEngine;
using UnityEngine.UIElements;

namespace Hiccup.Samples
{
    /// <summary>
    /// Builds an ordinary UI Toolkit form in code — panel, labels, button, toggle, slider, text field, dropdown,
    /// progress bar, a rotating element and a scroll view — on a runtime panel, and puts an
    /// <see cref="HtmlUitkMirror"/> on its UIDocument. The panel is hidden and what you see is the browser's DOM
    /// copy; every control still drives the UI Toolkit element, and USS hover and active styles still run.
    /// </summary>
    public class UitkMirrorSampleBootstrap : MonoBehaviour
    {
        [SerializeField] private int rows = 24;

        private HtmlUitkMirror _mirror;
        private Label _modeLabel, _clickLabel, _toggleLabel, _sliderLabel, _nameLabel, _dropLabel;
        private ProgressBar _progress;
        private VisualElement _spinner;
        private int _clicks;

        private void Awake()
        {
            var camGo = new GameObject("Main Camera") { tag = "MainCamera" };
            var cam = camGo.AddComponent<Camera>();
            camGo.AddComponent<AudioListener>();
            cam.clearFlags = CameraClearFlags.SolidColor;
            cam.backgroundColor = new Color(0.02f, 0.03f, 0.06f);

            // A runtime panel needs a PanelSettings with a theme; both are ordinary assets, made here in code so the
            // sample stays self-contained. The theme file is the one the Editor creates under Create > UI Toolkit.
            var settings = ScriptableObject.CreateInstance<PanelSettings>();
            settings.themeStyleSheet = Resources.Load<ThemeStyleSheet>("UitkMirror/UnityDefaultRuntimeTheme");
            settings.scaleMode = PanelScaleMode.ScaleWithScreenSize;
            settings.referenceResolution = new Vector2Int(1280, 720);
            settings.match = 0.5f;

            var docGo = new GameObject("UI Toolkit Document");
            docGo.transform.SetParent(transform, false);
            var uiDocument = docGo.AddComponent<UIDocument>();
            uiDocument.panelSettings = settings;

            var root = uiDocument.rootVisualElement;
            root.styleSheets.Add(Resources.Load<StyleSheet>("UitkMirror/UitkMirrorSample"));
            // LegacyRuntime is Liberation Sans, which the mirror maps to a browser font with the same metrics, so
            // lines break in the same places in the panel and in the page.
            root.style.unityFontDefinition = FontDefinition.FromFont(Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf"));
            root.AddToClassList("screen");

            var panel = new VisualElement { name = "panel" };
            panel.AddToClassList("panel");
            root.Add(panel);

            panel.Add(Text("Title", "UI Toolkit Mirror", "title"));
            panel.Add(Text("Subtitle", "Everything here is a UI Toolkit element on a runtime panel. The panel is hidden; you are looking at " +
                                       "the browser's DOM copy, so select the text, press <b>Tab</b>, or turn on a screen reader.", "subtitle"));

            // ---- A/B switch: the same panel, drawn natively or as the HTML mirror
            var row = Row(panel);
            var mode = new Toggle("HTML mirror") { value = true };
            mode.AddToClassList("switch");
            row.Add(mode);
            _modeLabel = Text("Mode label", "Rendering: HTML mirror", "note", "accent");
            row.Add(_modeLabel);
            mode.RegisterValueChangedCallback(e =>
            {
                // Disabling the mirror shows the panel again and removes the document; enabling rebuilds it.
                if (_mirror != null)
                    _mirror.enabled = e.newValue;
                _modeLabel.text = e.newValue ? "Rendering: HTML mirror" : "Rendering: native UI Toolkit";
                _modeLabel.EnableInClassList("accent", e.newValue);
                _modeLabel.EnableInClassList("warm", !e.newValue);
            });

            // ---- Button: its USS :hover and :active rules run because the mirror forwards the pointer into the panel
            row = Row(panel);
            var button = new Button(() => { _clicks++; _clickLabel.text = $"Clicked {_clicks} time{(_clicks == 1 ? "" : "s")}"; }) { text = "Click me" };
            button.AddToClassList("primary");
            row.Add(button);
            _clickLabel = Text("Click label", "Clicked 0 times", "note");
            row.Add(_clickLabel);

            // ---- Toggle
            row = Row(panel);
            var toggle = new Toggle("Glow");
            toggle.AddToClassList("switch");
            row.Add(toggle);
            _toggleLabel = Text("Toggle label", "Glow is off", "note");
            row.Add(_toggleLabel);
            toggle.RegisterValueChangedCallback(e =>
            {
                _toggleLabel.text = e.newValue ? "Glow is on" : "Glow is off";
                panel.EnableInClassList("glow", e.newValue);
            });

            // ---- Slider
            row = Row(panel);
            var slider = new SliderInt(0, 100) { value = 50 };
            slider.AddToClassList("volume");
            row.Add(slider);
            _sliderLabel = Text("Slider label", "Volume 50", "note");
            row.Add(_sliderLabel);
            slider.RegisterValueChangedCallback(e => _sliderLabel.text = $"Volume {e.newValue}");

            // ---- Text field
            row = Row(panel);
            var input = new TextField { maxLength = 24 };
            input.textEdition.placeholder = "Your name";
            input.AddToClassList("name");
            row.Add(input);
            _nameLabel = Text("Name label", "Hello, stranger", "note");
            row.Add(_nameLabel);
            input.RegisterValueChangedCallback(e => _nameLabel.text = string.IsNullOrWhiteSpace(e.newValue) ? "Hello, stranger" : $"Hello, {e.newValue}");

            // ---- Dropdown
            row = Row(panel);
            var dropdown = new DropdownField(new List<string> { "Low", "Medium", "High", "Ultra" }, 1);
            dropdown.AddToClassList("quality");
            row.Add(dropdown);
            _dropLabel = Text("Dropdown label", "Quality: Medium", "note");
            row.Add(_dropLabel);
            dropdown.RegisterValueChangedCallback(e => _dropLabel.text = "Quality: " + e.newValue);

            // ---- Progress bar and a rotating element, changed every frame
            row = Row(panel);
            row.Add(Text("Progress label", "A progress bar and a rotated element", "note"));
            _progress = new ProgressBar { lowValue = 0f, highValue = 1f };
            _progress.AddToClassList("progress");
            row.Add(_progress);
            _spinner = new VisualElement();
            _spinner.AddToClassList("spinner");
            row.Add(_spinner);

            // ---- Scroll view
            var scroll = new ScrollView(ScrollViewMode.Vertical);
            scroll.AddToClassList("list");
            panel.Add(scroll);
            for (int i = 1; i <= rows; i++)
            {
                var item = new VisualElement();
                item.AddToClassList("list-row");
                item.Add(Text(null, $"Scroll item {i} — <color=#5cc8fa>wheel</color> or drag the page to scroll; the ScrollView's offset follows", "row-text"));
                scroll.Add(item);
            }

            // ---- The mirror. Everything above is plain UI Toolkit; this one component puts it in the DOM.
            _mirror = docGo.AddComponent<HtmlUitkMirror>();
        }

        private void Update()
        {
            if (_progress != null)
                _progress.value = Mathf.Repeat(Time.time * 0.25f, 1f);
            if (_spinner != null)
                _spinner.style.rotate = new Rotate(Angle.Degrees(Time.time * 60f));
        }

        private static VisualElement Row(VisualElement parent)
        {
            var row = new VisualElement();
            row.AddToClassList("row");
            parent.Add(row);
            return row;
        }

        private static Label Text(string name, string text, params string[] classes)
        {
            var label = new Label(text) { name = name };
            foreach (var c in classes)
                label.AddToClassList(c);
            return label;
        }
    }
}
