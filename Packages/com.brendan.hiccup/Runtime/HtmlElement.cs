using System;
using UnityEngine;

namespace Hiccup
{
    /// <summary>
    /// Handle to a DOM element inside an <see cref="HtmlDocument"/>. Modelled loosely after UI Toolkit's VisualElement,
    /// but every call goes straight to the live DOM. Invalid handles (element not found) are safe to use: all
    /// setters are no-ops and all getters return empty values.
    /// </summary>
    public sealed class HtmlElement : IDisposable
    {
        public static readonly HtmlElement None = new HtmlElement(null, 0);

        private readonly HtmlDocument _doc;
        private int _handle;
        private string _id;
        private static readonly float[] s_bounds = new float[4];

        internal HtmlElement(HtmlDocument doc, int handle)
        {
            _doc = doc;
            _handle = handle;
        }

        public HtmlDocument Document => _doc;
        internal int Handle => _handle;
        public bool IsValid => _handle != 0 && _doc != null;

        /// <summary>The element id. Assigns one if the element has none, so event handlers can be attached.</summary>
        public string Id
        {
            get
            {
                if (_id != null)
                    return _id;
                if (!IsValid)
                    return string.Empty;
                _id = HtmlNative.TakeString(HtmlNative.Hiccup_ElemEnsureId(_handle));
                return _id;
            }
        }

        // ------------------------------------------------------------------ content

        public string Text
        {
            get => IsValid ? HtmlNative.TakeString(HtmlNative.Hiccup_ElemGetText(_handle)) : string.Empty;
            set
            {
                if (IsValid)
                {
                    HtmlNative.Hiccup_ElemSetText(_handle, value ?? string.Empty);
                    _doc.Invalidate();
                }
            }
        }

        public string InnerHtml
        {
            get => IsValid ? HtmlNative.TakeString(HtmlNative.Hiccup_ElemGetHtml(_handle)) : string.Empty;
            set
            {
                if (IsValid)
                {
                    HtmlNative.Hiccup_ElemSetHtml(_handle, value ?? string.Empty);
                    _doc.Invalidate();
                }
            }
        }

        /// <summary>Inserts HTML relative to this element. <paramref name="where"/> is one of beforebegin, afterbegin, beforeend, afterend.</summary>
        public HtmlElement InsertHtml(string where, string html)
        {
            if (IsValid)
            {
                HtmlNative.Hiccup_ElemInsertHtml(_handle, where, html ?? string.Empty);
                _doc.Invalidate();
            }
            return this;
        }
        public HtmlElement Append(string html) => InsertHtml("beforeend", html);
        public HtmlElement Prepend(string html) => InsertHtml("afterbegin", html);

        // ------------------------------------------------------------------ attributes / properties

        public string GetAttribute(string name) => IsValid ? HtmlNative.TakeString(HtmlNative.Hiccup_ElemGetAttr(_handle, name)) : string.Empty;
        public bool HasAttribute(string name) => IsValid && HtmlNative.Hiccup_ElemHasAttr(_handle, name) != 0;
        public HtmlElement SetAttribute(string name, string value)
        {
            if (IsValid)
            {
                HtmlNative.Hiccup_ElemSetAttr(_handle, name, value ?? string.Empty);
                _doc.Invalidate();
            }
            return this;
        }
        public HtmlElement SetAttribute(string name, bool present)
        {
            return present ? SetAttribute(name, string.Empty) : RemoveAttribute(name);
        }
        public HtmlElement RemoveAttribute(string name)
        {
            if (IsValid)
            {
                HtmlNative.Hiccup_ElemRemoveAttr(_handle, name);
                _doc.Invalidate();
            }
            return this;
        }

        /// <summary>Reads a JS property (e.g. "value", "selectedIndex", "validationMessage").</summary>
        public string GetProperty(string name) => IsValid ? HtmlNative.TakeString(HtmlNative.Hiccup_ElemGetProp(_handle, name)) : string.Empty;
        public HtmlElement SetProperty(string name, string value)
        {
            if (IsValid)
            {
                HtmlNative.Hiccup_ElemSetProp(_handle, name, value ?? string.Empty);
                _doc.Invalidate();
            }
            return this;
        }
        public bool GetBoolProperty(string name) => IsValid && HtmlNative.Hiccup_ElemGetBoolProp(_handle, name) != 0;
        public HtmlElement SetBoolProperty(string name, bool value)
        {
            if (IsValid)
            {
                HtmlNative.Hiccup_ElemSetBoolProp(_handle, name, value ? 1 : 0);
                _doc.Invalidate();
            }
            return this;
        }

        /// <summary>Form control value.</summary>
        public string Value
        {
            get => GetProperty("value");
            set => SetProperty("value", value);
        }
        public bool Checked
        {
            get => GetBoolProperty("checked");
            set => SetBoolProperty("checked", value);
        }
        public bool Disabled
        {
            get => GetBoolProperty("disabled");
            set => SetBoolProperty("disabled", value);
        }
        /// <summary>Maps to the HTML hidden attribute (display:none, removed from the accessibility tree).</summary>
        public bool Hidden
        {
            get => GetBoolProperty("hidden");
            set => SetBoolProperty("hidden", value);
        }
        /// <summary>Convenience: shows or hides the element via the hidden attribute.</summary>
        public HtmlElement SetVisible(bool visible) { Hidden = !visible; return this; }

        /// <summary>Sets a numeric value using invariant culture formatting.</summary>
        public HtmlElement SetValue(float value, string format = "0.##") => SetProperty("value", value.ToString(format, System.Globalization.CultureInfo.InvariantCulture));

        // ------------------------------------------------------------------ style / classes

        public HtmlElement SetStyle(string property, string value)
        {
            if (IsValid)
            {
                HtmlNative.Hiccup_ElemSetStyle(_handle, property, value ?? string.Empty);
                _doc.Invalidate();
            }
            return this;
        }
        public string GetComputedStyle(string property) => IsValid ? HtmlNative.TakeString(HtmlNative.Hiccup_ElemGetStyle(_handle, property)) : string.Empty;

        public HtmlElement AddClass(string className)
        {
            if (IsValid)
            {
                HtmlNative.Hiccup_ElemAddClass(_handle, className);
                _doc.Invalidate();
            }
            return this;
        }
        public HtmlElement RemoveClass(string className)
        {
            if (IsValid)
            {
                HtmlNative.Hiccup_ElemRemoveClass(_handle, className);
                _doc.Invalidate();
            }
            return this;
        }
        public HtmlElement ToggleClass(string className)
        {
            if (IsValid)
            {
                HtmlNative.Hiccup_ElemToggleClass(_handle, className, -1);
                _doc.Invalidate();
            }
            return this;
        }
        public HtmlElement EnableClass(string className, bool enabled)
        {
            if (IsValid)
            {
                HtmlNative.Hiccup_ElemToggleClass(_handle, className, enabled ? 1 : 0);
                _doc.Invalidate();
            }
            return this;
        }
        public bool HasClass(string className) => IsValid && HtmlNative.Hiccup_ElemHasClass(_handle, className) != 0;

        // ------------------------------------------------------------------ behaviour

        public HtmlElement Focus()
        {
            if (IsValid)
                HtmlNative.Hiccup_ElemFocus(_handle);
            return this;
        }
        public HtmlElement Blur()
        {
            if (IsValid)
                HtmlNative.Hiccup_ElemBlur(_handle);
            return this;
        }
        public HtmlElement Click()
        {
            if (IsValid)
                HtmlNative.Hiccup_ElemClick(_handle);
            return this;
        }
        public HtmlElement ScrollIntoView()
        {
            if (IsValid)
                HtmlNative.Hiccup_ElemScrollIntoView(_handle);
            return this;
        }

        /// <summary>For &lt;dialog&gt; elements: opens as a modal (focus trapped, inert background).</summary>
        public HtmlElement ShowModal()
        {
            if (IsValid)
            {
                HtmlNative.Hiccup_ElemShowModal(_handle, 1);
                _doc.Invalidate();
            }
            return this;
        }
        public HtmlElement CloseModal()
        {
            if (IsValid)
            {
                HtmlNative.Hiccup_ElemShowModal(_handle, 0);
                _doc.Invalidate();
            }
            return this;
        }

        /// <summary>Removes the element from the DOM and releases the handle.</summary>
        public void Remove()
        {
            if (!IsValid)
                return;
            HtmlNative.Hiccup_ElemRemove(_handle);
            _doc.Invalidate();
            _handle = 0;
        }

        /// <summary>Bounds in panel CSS pixels (x, y, width, height; top-left origin).</summary>
        public Rect Bounds
        {
            get
            {
                if (!IsValid)
                    return Rect.zero;
                HtmlNative.Hiccup_ElemGetBounds(_handle, s_bounds);
                return new Rect(s_bounds[0], s_bounds[1], s_bounds[2], s_bounds[3]);
            }
        }

        public bool Matches(string selector) => IsValid && HtmlNative.Hiccup_ElemMatches(_handle, selector) != 0;

        /// <summary>Finds the first descendant matching a CSS selector.</summary>
        public HtmlElement Q(string selector) => IsValid ? new HtmlElement(_doc, HtmlNative.Hiccup_ElemQuery(_handle, selector)) : None;

        public HtmlElement Parent => IsValid ? new HtmlElement(_doc, HtmlNative.Hiccup_ElemParent(_handle)) : None;

        // ------------------------------------------------------------------ events

        /// <summary>Registers a handler for a DOM event dispatched on this element or bubbling from its descendants.</summary>
        public HtmlElement On(string eventType, Action<HtmlEvent> handler)
        {
            if (IsValid)
                _doc.On(Id, eventType, handler);
            return this;
        }
        public HtmlElement Off(string eventType, Action<HtmlEvent> handler)
        {
            if (IsValid)
                _doc.Off(Id, eventType, handler);
            return this;
        }
        public HtmlElement OnClick(Action<HtmlEvent> handler) => On("click", handler);
        public HtmlElement OnInput(Action<HtmlEvent> handler) => On("input", handler);
        public HtmlElement OnChange(Action<HtmlEvent> handler) => On("change", handler);
        public HtmlElement OnSubmit(Action<HtmlEvent> handler) => On("submit", handler);
        public HtmlElement OnKeyDown(Action<HtmlEvent> handler) => On("keydown", handler);

        /// <summary>Releases the JS-side handle. The DOM element is not removed.</summary>
        public void Dispose()
        {
            if (_handle != 0 && _doc != null)
                HtmlNative.Hiccup_ElemRelease(_handle);
            _handle = 0;
        }

        public override string ToString() => IsValid ? $"HtmlElement(#{_handle} id=\"{_id ?? "?"}\")" : "HtmlElement(none)";
    }
}
