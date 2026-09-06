using System.Globalization;
using UnityEngine;

namespace Hiccup.Samples
{
    /// <summary>
    /// Owns the monitor's document. Loads the three.js page into the screen's iframe through <c>srcdoc</c> (so the
    /// frame shares the build's origin and HTML-in-Canvas may paint it) and forwards the desk mouse to it as
    /// <c>data-*</c> attributes on the iframe element, which the page polls every frame. Attribute writes are
    /// batched by the bridge and never block, unlike <see cref="HtmlDocument.Eval"/>, so this is the right channel
    /// for per-frame input.
    /// </summary>
    public class DeskScreenController : MonoBehaviour
    {
        public HtmlDocument Document;
        [Tooltip("The page shown on the screen (ThreeScene.html). Same-origin because it goes in as srcdoc.")]
        public TextAsset ScenePage;

        private bool _wired;
        private bool _dirty = true;
        private float _x = 0.5f, _y = 0.5f;
        private bool _down;
        private int _clicks;
        private float _wheel;

        private void OnEnable()
        {
            if (Document == null)
                Document = GetComponent<HtmlDocument>();
            if (Document == null)
                return;
            if (Document.IsCreated)
                Wire(Document);
            else
                Document.Created += Wire;
        }

        private void OnDisable()
        {
            if (Document != null)
                Document.Created -= Wire;
        }

        private void Wire(HtmlDocument doc)
        {
            if (_wired)
                return;
            _wired = true;
            using (var frame = doc.Q("#screen"))
                frame.SetAttribute("srcdoc", ScenePage != null ? ScenePage.text : "<p>ThreeScene.html is missing.</p>");
            _dirty = true;
        }

        /// <summary>Cursor position, 0..1 from the screen's top-left.</summary>
        public void MoveTo(float x, float y) { _x = Mathf.Clamp01(x); _y = Mathf.Clamp01(y); _dirty = true; }
        public void Press() { _down = true; _dirty = true; }
        public void Release(bool click)
        {
            _down = false;
            if (click)
                _clicks++;
            _dirty = true;
        }
        public void Wheel(float delta) { _wheel += delta; _dirty = true; }

        private void LateUpdate()
        {
            if (!_wired || !_dirty || Document == null || !Document.IsCreated)
                return;
            _dirty = false;
            using (var frame = Document.Q("#screen"))
            {
                frame.SetAttribute("data-x", F(_x))
                     .SetAttribute("data-y", F(_y))
                     .SetAttribute("data-down", _down ? "1" : "0")
                     .SetAttribute("data-clicks", _clicks.ToString(CultureInfo.InvariantCulture))
                     .SetAttribute("data-wheel", F(_wheel));
            }
        }

        private static string F(float v) => v.ToString("0.####", CultureInfo.InvariantCulture);
    }
}
