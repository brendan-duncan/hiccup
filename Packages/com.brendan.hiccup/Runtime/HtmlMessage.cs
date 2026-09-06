using System;
using System.Globalization;
using UnityEngine;

namespace Hiccup
{
    /// <summary>
    /// A message sent from JavaScript in the page to C# with <c>HUI.send(name, payload)</c>. Strings arrive
    /// as they were sent; any other payload arrives as JSON text in <see cref="Data"/>.
    /// </summary>
    public sealed class HtmlMessage
    {
        /// <summary>The name the page sent it under.</summary>
        public readonly string Name;
        /// <summary>The payload: a string as-is, anything else as JSON.</summary>
        public readonly string Data;
        public readonly HtmlDocument Document;

        /// <summary>True when a handler wants to stop further C# dispatch.</summary>
        public bool Handled;

        internal HtmlMessage(HtmlDocument document, string name, string data)
        {
            Document = document;
            Name = name ?? string.Empty;
            Data = data ?? string.Empty;
        }

        public float DataAsFloat => float.TryParse(Data, NumberStyles.Float, CultureInfo.InvariantCulture, out var f) ? f : 0f;
        public int DataAsInt => (int)Math.Round(DataAsFloat);
        public bool DataAsBool => string.Equals(Data, "true", StringComparison.OrdinalIgnoreCase);

        /// <summary>Deserializes a JSON payload into a serializable class or struct with <see cref="JsonUtility"/>.</summary>
        public T DataAs<T>()
        {
            try { return JsonUtility.FromJson<T>(Data); }
            catch (Exception ex)
            {
                Debug.LogWarning($"[Hiccup] Message \"{Name}\" could not be read as {typeof(T).Name}: {ex.Message}\n{Data}");
                return default;
            }
        }

        public override string ToString() => $"HtmlMessage({Name}: {Data})";
    }
}
