using System.Collections.Generic;
using UnityEngine;

namespace Hiccup
{
    /// <summary>A key press seen by the Game view, as IMGUI reports it. Used by backends that relay input to a browser.</summary>
    public struct HtmlKeyPress
    {
        public char Character;
        public KeyCode Key;
        public bool Ctrl, Shift, Alt, Meta;
    }

    /// <summary>
    /// Collects the Game view's keyboard events for an <see cref="IHtmlBackend"/>.
    /// </summary>
    /// <remarks>
    /// IMGUI receives key events from the native event system no matter which input backend the project uses,
    /// so a component with <c>OnGUI</c> is the one way to read keys that needs no reference to the Input System
    /// package and no reflection. It lives in the runtime assembly because Unity will not attach a component
    /// defined in an Editor assembly; it is only ever added by <see cref="HtmlBackend.SetKeyboardCapture"/>, to
    /// the runtime's own driver object, and never in a player build.
    /// <para>
    /// On Windows a typed key arrives as two <c>KeyDown</c> events: one carrying the <see cref="KeyCode"/> and one
    /// carrying the character. Both are queued; the backend decides which of them means what.
    /// </para>
    /// </remarks>
    [AddComponentMenu("")]
    internal sealed class HtmlKeyboardRelay : MonoBehaviour
    {
        /// <summary>Key presses since the last drain, in order.</summary>
        public readonly List<HtmlKeyPress> Pending = new List<HtmlKeyPress>();

        private void OnGUI()
        {
            var e = Event.current;
            if (e == null || e.type != EventType.KeyDown)
                return;
            if (e.keyCode == KeyCode.None && e.character == '\0')
                return;

            Pending.Add(new HtmlKeyPress
            {
                Character = e.character,
                Key = e.keyCode,
                Ctrl = e.control,
                Shift = e.shift,
                Alt = e.alt,
                Meta = e.command,
            });
            // Not consumed: BlockUnityInput is not modeled by the Editor preview, so Unity sees the same keys.
        }
    }
}
