using System;
using System.Globalization;
using UnityEngine;

namespace Hiccup
{
    /// <summary>
    /// A DOM event forwarded from the browser. Field names mirror the JSON payload produced by Hiccup.jslib.
    /// </summary>
    [Serializable]
    public class HtmlEvent
    {
        /// <summary>DOM event type, e.g. "click", "input", "change", "submit", "keydown".</summary>
        public string type;
        /// <summary>Id of the event target. Elements without an id get one assigned ("hui-N") so they can be queried.</summary>
        public string id;
        /// <summary>Lower-case tag name of the target.</summary>
        public string tag;
        /// <summary>The target's name attribute (form fields).</summary>
        public string name;
        /// <summary>Value of the closest [data-action] attribute, or empty.</summary>
        public string action;
        /// <summary>The target's value (inputs, selects, textareas).</summary>
        public string value;
        /// <summary>Checked state for checkboxes/radios, open state for details/dialog, aria-pressed/aria-checked for custom widgets.</summary>
        public bool isChecked;
        public string key;
        public string code;
        /// <summary>Pointer position in panel CSS pixels (top-left origin).</summary>
        public float x;
        public float y;
        public int button;
        public bool ctrl;
        public bool shift;
        public bool alt;
        public bool meta;
        /// <summary>Wheel events: scroll deltas in the browser's units (pixels for a mouse wheel in Chrome).</summary>
        public float deltaX;
        public float deltaY;
        /// <summary>Pointer events: the pointer's id, its kind ("mouse", "pen" or "touch") and pressure from 0 to 1.</summary>
        public int pointerId;
        public string pointerType;
        public float pressure;
        /// <summary>Pointer and mouse move events: movement since the previous event, in CSS pixels.</summary>
        public float movementX;
        public float movementY;
        /// <summary>Key events: true while a held key auto-repeats.</summary>
        public bool repeat;
        /// <summary>Key and input events: true while an IME composition is in progress, when the key is not final.</summary>
        public bool isComposing;
        /// <summary>focusin/focusout and pointerover/pointerout: id of the other element involved, if it is inside the panel.</summary>
        public string relatedId;
        /// <summary>True when the target takes text input: a text-like input, a textarea, a select or a contenteditable element.</summary>
        public bool editable;
        /// <summary>A mouse event's click count, or a CustomEvent's detail (objects as JSON).</summary>
        public string detail;
        /// <summary>Ids of the ancestors between the target and the panel root, nearest first, space separated.</summary>
        public string path;
        /// <summary>The target's data-* attributes, one "key=value" per line.</summary>
        public string dataset;

        [NonSerialized] public HtmlDocument Document;
        [NonSerialized] private HtmlElement _target;

        /// <summary>True when a handler wants to stop further C# dispatch (bubbling to ancestor handlers).</summary>
        [NonSerialized] public bool Handled;

        /// <summary>The DOM element the event was dispatched on.</summary>
        public HtmlElement Target => _target ??= (Document != null && !string.IsNullOrEmpty(id) ? Document.Q("#" + id) : HtmlElement.None);

        /// <summary>The element focus or the pointer came from or went to (see <see cref="relatedId"/>), or <see cref="HtmlElement.None"/>.</summary>
        public HtmlElement RelatedTarget => Document != null && !string.IsNullOrEmpty(relatedId) ? Document.Q("#" + relatedId) : HtmlElement.None;

        public float ValueAsFloat => float.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var f) ? f : 0f;
        public int ValueAsInt => (int)Math.Round(ValueAsFloat);

        /// <summary>Returns the value of a data-* attribute on the target, or null.</summary>
        public string GetData(string key)
        {
            if (string.IsNullOrEmpty(dataset))
                return null;
            foreach (var line in dataset.Split('\n'))
            {
                int eq = line.IndexOf('=');
                if (eq <= 0)
                    continue;
                if (string.Equals(line.Substring(0, eq), key, StringComparison.Ordinal))
                    return line.Substring(eq + 1);
            }
            return null;
        }

        public bool IsKey(string k) => string.Equals(key, k, StringComparison.OrdinalIgnoreCase);

        public override string ToString() => $"HtmlEvent({type} on <{tag} id=\"{id}\"> action=\"{action}\" value=\"{value}\")";

        internal static HtmlEvent Parse(string json, HtmlDocument doc)
        {
            HtmlEvent e;
            try { e = JsonUtility.FromJson<HtmlEvent>(json); }
            catch (Exception ex)
            {
                Debug.LogWarning($"[Hiccup] Could not parse event payload: {ex.Message}\n{json}");
                return null;
            }
            if (e == null)
                return null;
            e.Document = doc;
            return e;
        }
    }
}
