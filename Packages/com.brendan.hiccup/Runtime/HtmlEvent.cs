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
