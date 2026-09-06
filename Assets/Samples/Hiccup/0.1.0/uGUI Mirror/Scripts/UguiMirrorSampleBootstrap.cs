using Hiccup.Ugui;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.InputSystem.UI;
using UnityEngine.UI;

namespace Hiccup.Samples
{
    /// <summary>
    /// Builds an ordinary uGUI form in code — panel, text, button, toggle, slider, input field, dropdown, scroll
    /// view, a filled image and a spinning one — and puts an <see cref="HtmlUguiMirror"/> on its canvas. The canvas
    /// is hidden and what you see is the browser's DOM copy; every control still drives the uGUI component.
    /// </summary>
    public class UguiMirrorSampleBootstrap : MonoBehaviour
    {
        [SerializeField] private int rows = 24;

        private static readonly Color Accent = new Color(0.36f, 0.78f, 0.98f);
        private static readonly Color Ink = new Color(0.93f, 0.95f, 0.98f);
        private static readonly Color Muted = new Color(0.62f, 0.67f, 0.76f);
        private static readonly Color Well = new Color(0.04f, 0.05f, 0.09f, 0.9f);

        private Font _font;
        private Sprite _rounded, _circle;
        private Text _clickLabel, _toggleLabel, _sliderLabel, _nameLabel, _dropLabel, _modeLabel;
        private Image _ring, _spinner;
        private HtmlUguiMirror _mirror;
        private int _clicks;

        private void Awake()
        {
            var camGo = new GameObject("Main Camera") { tag = "MainCamera" };
            var cam = camGo.AddComponent<Camera>();
            camGo.AddComponent<AudioListener>();
            cam.clearFlags = CameraClearFlags.SolidColor;
            cam.backgroundColor = new Color(0.02f, 0.03f, 0.06f);

            // Native uGUI needs an EventSystem to take input itself. While the mirror is on, the canvas is hidden
            // from raycasts, so this sits idle and the DOM drives the components instead.
            var events = new GameObject("EventSystem", typeof(EventSystem), typeof(InputSystemUIInputModule));
            events.transform.SetParent(transform, false);

            _font = Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf");
            _rounded = RoundedSprite(48, 12, 12);
            _circle = RoundedSprite(32, 16, 0);

            var canvasGo = new GameObject("uGUI Canvas", typeof(Canvas), typeof(CanvasScaler), typeof(GraphicRaycaster));
            canvasGo.GetComponent<Canvas>().renderMode = RenderMode.ScreenSpaceOverlay;
            var scaler = canvasGo.GetComponent<CanvasScaler>();
            scaler.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
            scaler.referenceResolution = new Vector2(1280f, 720f);
            scaler.matchWidthOrHeight = 0.5f;

            // ---- Panel with a vertical layout
            var panel = MakeImage(canvasGo.transform, "Panel", _rounded, new Color(0.08f, 0.1f, 0.16f, 0.94f), Image.Type.Sliced);
            var prt = panel.rectTransform;
            prt.anchorMin = prt.anchorMax = new Vector2(0.5f, 0.5f);
            prt.sizeDelta = new Vector2(600f, 660f);
            var layout = panel.gameObject.AddComponent<VerticalLayoutGroup>();
            layout.padding = new RectOffset(28, 28, 24, 24);
            layout.spacing = 12f;
            layout.childControlWidth = layout.childControlHeight = true;
            layout.childForceExpandWidth = true;
            layout.childForceExpandHeight = false;

            var title = MakeText(prt, "Title", "uGUI Mirror", 30, Ink, TextAnchor.MiddleLeft, FontStyle.Bold);
            Prefer(title.gameObject, -1f, 40f);
            var sub = MakeText(prt, "Subtitle", "Everything here is a uGUI component. The canvas is hidden; you are looking at the browser's DOM copy, " +
                                                "so select the text, press <b>Tab</b>, or turn on a screen reader.", 15, Muted, TextAnchor.UpperLeft);
            Prefer(sub.gameObject, -1f, 46f);

            // ---- A/B switch: the same uGUI, drawn natively or as the HTML mirror
            var row = Row(prt, "Mode row", 32f);
            var mode = MakeToggle(row.transform, "HTML mirror");
            Prefer(mode.gameObject, 150f, 32f);
            mode.isOn = true;
            _modeLabel = MakeText(row.transform, "Mode label", "Rendering: HTML mirror", 16, Accent, TextAnchor.MiddleLeft);
            mode.onValueChanged.AddListener(on =>
            {
                // Disabling the mirror shows the canvas again and removes the document; enabling rebuilds it.
                if (_mirror != null)
                    _mirror.enabled = on;
                _modeLabel.text = on ? "Rendering: HTML mirror" : "Rendering: native uGUI";
                _modeLabel.color = on ? Accent : new Color(0.98f, 0.6f, 0.36f);
            });

            // ---- Button
            row = Row(prt, "Button row", 44f);
            var button = MakeButton(row.transform, "Click me");
            _clickLabel = MakeText(row.transform, "Click label", "Clicked 0 times", 16, Muted, TextAnchor.MiddleLeft);
            button.onClick.AddListener(() => { _clicks++; _clickLabel.text = $"Clicked {_clicks} time{(_clicks == 1 ? "" : "s")}"; });

            // ---- Toggle
            row = Row(prt, "Toggle row", 32f);
            var toggle = MakeToggle(row.transform, "Glow");
            _toggleLabel = MakeText(row.transform, "Toggle label", "Glow is off", 16, Muted, TextAnchor.MiddleLeft);
            toggle.onValueChanged.AddListener(on => { _toggleLabel.text = on ? "Glow is on" : "Glow is off"; panel.color = on ? new Color(0.1f, 0.16f, 0.24f, 0.94f) : new Color(0.08f, 0.1f, 0.16f, 0.94f); });

            // ---- Slider
            row = Row(prt, "Slider row", 28f);
            var slider = MakeSlider(row.transform);
            _sliderLabel = MakeText(row.transform, "Slider label", "Volume 50", 16, Muted, TextAnchor.MiddleLeft);
            slider.onValueChanged.AddListener(v => _sliderLabel.text = $"Volume {v:0}");

            // ---- Input field
            row = Row(prt, "Input row", 40f);
            var input = MakeInput(row.transform, "Your name");
            _nameLabel = MakeText(row.transform, "Name label", "Hello, stranger", 16, Muted, TextAnchor.MiddleLeft);
            input.onValueChanged.AddListener(v => _nameLabel.text = string.IsNullOrWhiteSpace(v) ? "Hello, stranger" : $"Hello, {v}");

            // ---- Dropdown
            row = Row(prt, "Dropdown row", 40f);
            var dropdown = MakeDropdown(row.transform, "Low", "Medium", "High", "Ultra");
            _dropLabel = MakeText(row.transform, "Dropdown label", "Quality: Medium", 16, Muted, TextAnchor.MiddleLeft);
            dropdown.value = 1;
            dropdown.onValueChanged.AddListener(i => _dropLabel.text = "Quality: " + dropdown.options[i].text);

            // ---- Filled + rotating images
            row = Row(prt, "Image row", 32f);
            var ringLabel = MakeText(row.transform, "Ring label", "Radial fill and a rotated RectTransform", 16, Muted, TextAnchor.MiddleLeft);
            Prefer(ringLabel.gameObject, 300f, 32f);
            _ring = MakeImage(row.transform, "Ring", _circle, Accent, Image.Type.Filled);
            _ring.fillMethod = Image.FillMethod.Radial360;
            _ring.fillOrigin = (int)Image.Origin360.Top;
            _ring.fillClockwise = true;
            Prefer(_ring.gameObject, 28f, 28f);
            _spinner = MakeImage(row.transform, "Spinner", _rounded, new Color(0.98f, 0.6f, 0.36f), Image.Type.Sliced);
            Prefer(_spinner.gameObject, 22f, 22f);

            // ---- Scroll view
            MakeScrollView(prt);

            // ---- The mirror. Everything above is plain uGUI; this one component puts it in the DOM.
            _mirror = canvasGo.AddComponent<HtmlUguiMirror>();
        }

        private void Update()
        {
            if (_ring != null)
                _ring.fillAmount = Mathf.Repeat(Time.time * 0.25f, 1f);
            if (_spinner != null)
                _spinner.rectTransform.localRotation = Quaternion.Euler(0f, 0f, Time.time * 60f);
        }

        // ------------------------------------------------------------------ widgets

        private GameObject Row(Transform parent, string name, float height)
        {
            var go = new GameObject(name, typeof(RectTransform), typeof(HorizontalLayoutGroup));
            go.transform.SetParent(parent, false);
            var h = go.GetComponent<HorizontalLayoutGroup>();
            h.spacing = 16f;
            h.childAlignment = TextAnchor.MiddleLeft;
            h.childControlWidth = h.childControlHeight = true;
            h.childForceExpandWidth = false;
            h.childForceExpandHeight = false;
            Prefer(go, -1f, height);
            return go;
        }

        private Button MakeButton(Transform parent, string label)
        {
            var image = MakeImage(parent, "Button", _rounded, Accent, Image.Type.Sliced);
            Prefer(image.gameObject, 160f, 44f);
            var button = image.gameObject.AddComponent<Button>();
            button.targetGraphic = image;
            var colors = button.colors;
            colors.highlightedColor = new Color(0.8f, 0.92f, 1f);
            colors.pressedColor = new Color(0.55f, 0.7f, 0.85f);
            button.colors = colors;
            var text = MakeText(image.rectTransform, "Label", label, 17, new Color(0.03f, 0.08f, 0.14f), TextAnchor.MiddleCenter, FontStyle.Bold);
            Stretch(text.rectTransform, 8f, 4f);
            return button;
        }

        private Toggle MakeToggle(Transform parent, string label)
        {
            var root = new GameObject("Toggle", typeof(RectTransform));
            root.transform.SetParent(parent, false);
            Prefer(root, 160f, 32f);
            var toggle = root.AddComponent<Toggle>();

            var background = MakeImage(root.transform, "Background", _rounded, Well, Image.Type.Sliced);
            var brt = background.rectTransform;
            brt.anchorMin = new Vector2(0f, 0.5f);
            brt.anchorMax = new Vector2(0f, 0.5f);
            brt.pivot = new Vector2(0f, 0.5f);
            brt.anchoredPosition = Vector2.zero;
            brt.sizeDelta = new Vector2(28f, 28f);

            var check = MakeImage(brt, "Checkmark", _circle, Accent, Image.Type.Simple);
            Stretch(check.rectTransform, 6f, 6f);

            var text = MakeText(root.transform, "Label", label, 16, Ink, TextAnchor.MiddleLeft);
            var trt = text.rectTransform;
            trt.anchorMin = new Vector2(0f, 0f);
            trt.anchorMax = new Vector2(1f, 1f);
            trt.offsetMin = new Vector2(38f, 0f);
            trt.offsetMax = Vector2.zero;

            toggle.targetGraphic = background;
            toggle.graphic = check;
            toggle.isOn = false;
            return toggle;
        }

        private Slider MakeSlider(Transform parent)
        {
            var root = new GameObject("Slider", typeof(RectTransform));
            root.transform.SetParent(parent, false);
            Prefer(root, 240f, 24f);
            var slider = root.AddComponent<Slider>();

            var background = MakeImage(root.transform, "Background", _rounded, Well, Image.Type.Sliced);
            var brt = background.rectTransform;
            brt.anchorMin = new Vector2(0f, 0.5f);
            brt.anchorMax = new Vector2(1f, 0.5f);
            brt.sizeDelta = new Vector2(0f, 8f);

            var fillArea = new GameObject("Fill Area", typeof(RectTransform)).GetComponent<RectTransform>();
            fillArea.SetParent(root.transform, false);
            fillArea.anchorMin = new Vector2(0f, 0.5f);
            fillArea.anchorMax = new Vector2(1f, 0.5f);
            fillArea.offsetMin = new Vector2(8f, -4f);
            fillArea.offsetMax = new Vector2(-8f, 4f);
            var fill = MakeImage(fillArea, "Fill", _rounded, Accent, Image.Type.Sliced);
            Stretch(fill.rectTransform, -8f, 0f);

            var handleArea = new GameObject("Handle Slide Area", typeof(RectTransform)).GetComponent<RectTransform>();
            handleArea.SetParent(root.transform, false);
            handleArea.anchorMin = Vector2.zero;
            handleArea.anchorMax = Vector2.one;
            handleArea.offsetMin = new Vector2(10f, 0f);
            handleArea.offsetMax = new Vector2(-10f, 0f);
            var handle = MakeImage(handleArea, "Handle", _circle, Ink, Image.Type.Simple);
            // The slider drives the x anchors from its value; y must span the slide area or the handle has no height.
            handle.rectTransform.anchorMin = new Vector2(0f, 0f);
            handle.rectTransform.anchorMax = new Vector2(0f, 1f);
            handle.rectTransform.sizeDelta = new Vector2(20f, 0f);

            slider.fillRect = fill.rectTransform;
            slider.handleRect = handle.rectTransform;
            slider.targetGraphic = handle;
            slider.direction = Slider.Direction.LeftToRight;
            slider.minValue = 0f;
            slider.maxValue = 100f;
            slider.wholeNumbers = true;
            slider.value = 50f;
            return slider;
        }

        private InputField MakeInput(Transform parent, string placeholder)
        {
            var background = MakeImage(parent, "InputField", _rounded, Well, Image.Type.Sliced);
            Prefer(background.gameObject, 240f, 40f);
            var field = background.gameObject.AddComponent<InputField>();
            field.targetGraphic = background;

            var hint = MakeText(background.rectTransform, "Placeholder", placeholder, 16, Muted, TextAnchor.MiddleLeft, FontStyle.Italic);
            Stretch(hint.rectTransform, 12f, 6f);
            var text = MakeText(background.rectTransform, "Text", string.Empty, 16, Ink, TextAnchor.MiddleLeft);
            text.supportRichText = false;
            Stretch(text.rectTransform, 12f, 6f);

            field.textComponent = text;
            field.placeholder = hint;
            field.caretColor = Accent;
            field.customCaretColor = true;
            field.characterLimit = 24;
            return field;
        }

        private Dropdown MakeDropdown(Transform parent, params string[] options)
        {
            var background = MakeImage(parent, "Dropdown", _rounded, Well, Image.Type.Sliced);
            Prefer(background.gameObject, 200f, 40f);
            var dropdown = background.gameObject.AddComponent<Dropdown>();
            dropdown.targetGraphic = background;

            var caption = MakeText(background.rectTransform, "Label", options[0], 16, Ink, TextAnchor.MiddleLeft);
            Stretch(caption.rectTransform, 12f, 6f);
            caption.rectTransform.offsetMax = new Vector2(-36f, -6f);
            var arrow = MakeImage(background.rectTransform, "Arrow", _rounded, Muted, Image.Type.Sliced);
            var art = arrow.rectTransform;
            art.anchorMin = art.anchorMax = new Vector2(1f, 0.5f);
            art.pivot = new Vector2(1f, 0.5f);
            art.anchoredPosition = new Vector2(-12f, 0f);
            art.sizeDelta = new Vector2(14f, 14f);
            art.localRotation = Quaternion.Euler(0f, 0f, 45f);

            dropdown.captionText = caption;
            dropdown.options.Clear();
            foreach (var o in options)
                dropdown.options.Add(new Dropdown.OptionData(o));

            // List template for native mode. uGUI clones it when the dropdown opens; it stays inactive otherwise,
            // so the mirror never sees it — in HTML mode the browser's <select> shows the list instead.
            var template = MakeImage(background.transform, "Template", _rounded, new Color(0.1f, 0.12f, 0.2f, 0.98f), Image.Type.Sliced).rectTransform;
            template.anchorMin = new Vector2(0f, 0f);
            template.anchorMax = new Vector2(1f, 0f);
            template.pivot = new Vector2(0.5f, 1f);
            template.anchoredPosition = new Vector2(0f, -4f);
            template.sizeDelta = new Vector2(0f, options.Length * 32f + 8f);

            var content = new GameObject("Content", typeof(RectTransform)).GetComponent<RectTransform>();
            content.SetParent(template, false);
            content.anchorMin = new Vector2(0f, 1f);
            content.anchorMax = new Vector2(1f, 1f);
            content.pivot = new Vector2(0.5f, 1f);
            content.anchoredPosition = new Vector2(0f, -4f);
            content.sizeDelta = new Vector2(0f, 32f);

            var item = new GameObject("Item", typeof(RectTransform), typeof(Toggle)).GetComponent<RectTransform>();
            item.SetParent(content, false);
            item.anchorMin = new Vector2(0f, 0.5f);
            item.anchorMax = new Vector2(1f, 0.5f);
            item.sizeDelta = new Vector2(0f, 32f);
            var itemBackground = MakeImage(item, "Item Background", _rounded, new Color(0.14f, 0.17f, 0.25f), Image.Type.Sliced);
            Stretch(itemBackground.rectTransform, 4f, 2f);
            var itemCheck = MakeImage(item, "Item Checkmark", _circle, Accent, Image.Type.Simple);
            var crt = itemCheck.rectTransform;
            crt.anchorMin = crt.anchorMax = new Vector2(0f, 0.5f);
            crt.pivot = new Vector2(0f, 0.5f);
            crt.anchoredPosition = new Vector2(12f, 0f);
            crt.sizeDelta = new Vector2(12f, 12f);
            var itemLabel = MakeText(item, "Item Label", "Option", 16, Ink, TextAnchor.MiddleLeft);
            itemLabel.rectTransform.anchorMin = Vector2.zero;
            itemLabel.rectTransform.anchorMax = Vector2.one;
            itemLabel.rectTransform.offsetMin = new Vector2(32f, 2f);
            itemLabel.rectTransform.offsetMax = new Vector2(-8f, -2f);
            var itemToggle = item.GetComponent<Toggle>();
            itemToggle.targetGraphic = itemBackground;
            itemToggle.graphic = itemCheck;
            itemToggle.isOn = true;

            dropdown.template = template;
            dropdown.itemText = itemLabel;
            template.gameObject.SetActive(false);
            return dropdown;
        }

        private void MakeScrollView(Transform parent)
        {
            var frame = MakeImage(parent, "Scroll View", _rounded, Well, Image.Type.Sliced);
            var le = frame.gameObject.AddComponent<LayoutElement>();
            le.flexibleHeight = 1f;
            le.minHeight = 140f;
            var scroll = frame.gameObject.AddComponent<ScrollRect>();

            var viewport = new GameObject("Viewport", typeof(RectTransform), typeof(RectMask2D)).GetComponent<RectTransform>();
            viewport.SetParent(frame.transform, false);
            Stretch(viewport, 6f, 6f);

            var content = new GameObject("Content", typeof(RectTransform)).GetComponent<RectTransform>();
            content.SetParent(viewport, false);
            content.anchorMin = new Vector2(0f, 1f);
            content.anchorMax = new Vector2(1f, 1f);
            content.pivot = new Vector2(0.5f, 1f);
            content.offsetMin = content.offsetMax = Vector2.zero;
            var vl = content.gameObject.AddComponent<VerticalLayoutGroup>();
            vl.padding = new RectOffset(6, 6, 6, 6);
            vl.spacing = 6f;
            vl.childControlWidth = vl.childControlHeight = true;
            vl.childForceExpandWidth = true;
            vl.childForceExpandHeight = false;
            content.gameObject.AddComponent<ContentSizeFitter>().verticalFit = ContentSizeFitter.FitMode.PreferredSize;

            for (int i = 1; i <= rows; i++)
            {
                var item = MakeImage(content, $"Row {i}", _rounded, new Color(0.14f, 0.17f, 0.25f), Image.Type.Sliced);
                Prefer(item.gameObject, -1f, 36f);
                var text = MakeText(item.rectTransform, "Label", $"Scroll item {i} — <color=#5cc8fa>wheel</color> or drag the page to scroll; uGUI's ScrollRect follows", 15, Ink, TextAnchor.MiddleLeft);
                Stretch(text.rectTransform, 12f, 4f);
            }

            scroll.content = content;
            scroll.viewport = viewport;
            scroll.horizontal = false;
            scroll.vertical = true;
            scroll.movementType = ScrollRect.MovementType.Clamped;
            scroll.scrollSensitivity = 30f;
        }

        // ------------------------------------------------------------------ primitives

        private Image MakeImage(Transform parent, string name, Sprite sprite, Color color, Image.Type type)
        {
            var go = new GameObject(name, typeof(RectTransform), typeof(Image));
            go.transform.SetParent(parent, false);
            var image = go.GetComponent<Image>();
            image.sprite = sprite;
            image.color = color;
            image.type = type;
            // Native mode is hit-tested by the GraphicRaycaster, which only sees raycast targets. The mirror does
            // not care: in HTML mode the browser hit-tests the DOM copy.
            image.raycastTarget = true;
            return image;
        }

        private Text MakeText(Transform parent, string name, string text, int size, Color color, TextAnchor anchor, FontStyle style = FontStyle.Normal)
        {
            var go = new GameObject(name, typeof(RectTransform), typeof(Text));
            go.transform.SetParent(parent, false);
            var t = go.GetComponent<Text>();
            t.font = _font;
            t.text = text;
            t.fontSize = size;
            t.fontStyle = style;
            t.color = color;
            t.alignment = anchor;
            t.horizontalOverflow = HorizontalWrapMode.Wrap;
            t.verticalOverflow = VerticalWrapMode.Truncate;
            t.supportRichText = true;
            t.raycastTarget = false;
            return t;
        }

        private static void Prefer(GameObject go, float width, float height)
        {
            var le = go.GetComponent<LayoutElement>();
            if (le == null)
                le = go.AddComponent<LayoutElement>();
            if (width >= 0f)
                le.preferredWidth = width;
            if (height >= 0f)
                le.preferredHeight = height;
        }

        private static void Stretch(RectTransform rt, float horizontal, float vertical)
        {
            rt.anchorMin = Vector2.zero;
            rt.anchorMax = Vector2.one;
            rt.offsetMin = new Vector2(horizontal, vertical);
            rt.offsetMax = new Vector2(-horizontal, -vertical);
        }

        /// <summary>A white rounded-rectangle sprite with an anti-aliased edge and a 9-slice border, built in memory so the sample ships no art.</summary>
        private static Sprite RoundedSprite(int size, int radius, int border)
        {
            var tex = new Texture2D(size, size, TextureFormat.RGBA32, false) { name = "Rounded", wrapMode = TextureWrapMode.Clamp, filterMode = FilterMode.Bilinear };
            var px = new Color32[size * size];
            float half = size * 0.5f;
            float r = Mathf.Min(radius, half);
            for (int y = 0; y < size; y++)
            {
                for (int x = 0; x < size; x++)
                {
                    // signed distance to a rounded box centred in the texture
                    float qx = Mathf.Abs(x + 0.5f - half) - (half - r), qy = Mathf.Abs(y + 0.5f - half) - (half - r);
                    float d = new Vector2(Mathf.Max(qx, 0f), Mathf.Max(qy, 0f)).magnitude + Mathf.Min(Mathf.Max(qx, qy), 0f) - r;
                    byte a = (byte)Mathf.RoundToInt(Mathf.Clamp01(0.5f - d) * 255f);
                    px[y * size + x] = new Color32(255, 255, 255, a);
                }
            }
            tex.SetPixels32(px);
            tex.Apply(false, false);
            return Sprite.Create(tex, new Rect(0f, 0f, size, size), new Vector2(0.5f, 0.5f), 100f, 0, SpriteMeshType.FullRect, new Vector4(border, border, border, border));
        }
    }
}
