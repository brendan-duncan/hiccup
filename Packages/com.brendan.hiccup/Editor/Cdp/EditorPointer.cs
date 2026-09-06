using System;
using System.Linq.Expressions;
using System.Reflection;
using UnityEngine;

namespace Hiccup.Editor.Cdp
{
    /// <summary>
    /// Reads the mouse without binding the package to either input backend. A project may have the old
    /// input manager, the Input System package, or both, and an assembly reference to a package that is
    /// not installed would not compile — so the Input System path is resolved reflectively, once, into
    /// compiled delegates, and the per-frame read is then a plain call with no boxing.
    /// </summary>
    internal static class EditorPointer
    {
        private static bool s_resolved;

        // Input System package; all null when it is not installed or looks unfamiliar.
        private static Func<object> s_mouseCurrent;           // () => Mouse.current
        private static Func<object, Vector2> s_mousePosition; // mouse => mouse.position.ReadValue()
        private static Func<object, bool> s_leftPressed;      // mouse => mouse.leftButton.isPressed

        // Legacy input manager
        private static bool s_legacyBroken;

        /// <summary>Mouse position in screen pixels (origin bottom-left) and whether the left button is held.</summary>
        public static bool TryGetMouse(out Vector2 position, out bool leftButtonDown)
        {
            position = Vector2.zero;
            leftButtonDown = false;
            if (!Application.isPlaying)
                return false;

            if (!s_resolved)
                Resolve();

            if (s_mouseCurrent != null)
            {
                var mouse = s_mouseCurrent();
                if (mouse != null)
                {
                    position = s_mousePosition(mouse);
                    leftButtonDown = s_leftPressed(mouse);
                    return true;
                }
            }

            if (!s_legacyBroken)
            {
                try
                {
                    position = Input.mousePosition;
                    leftButtonDown = Input.GetMouseButton(0);
                    return true;
                }
                catch (InvalidOperationException)
                {
                    // Thrown when the project is set to the Input System package only.
                    s_legacyBroken = true;
                }
            }

            return false;
        }

        private static void Resolve()
        {
            s_resolved = true;

            var mouseType = Type.GetType("UnityEngine.InputSystem.Mouse, Unity.InputSystem");
            if (mouseType == null)
                return;

            try
            {
                var current = mouseType.GetProperty("current", BindingFlags.Public | BindingFlags.Static);
                var positionProperty = mouseType.GetProperty("position", BindingFlags.Public | BindingFlags.Instance);
                var leftButtonProperty = mouseType.GetProperty("leftButton", BindingFlags.Public | BindingFlags.Instance);
                var readValue = positionProperty?.PropertyType.GetMethod("ReadValue", BindingFlags.Public | BindingFlags.Instance, null, Type.EmptyTypes, null);
                var isPressed = leftButtonProperty?.PropertyType.GetProperty("isPressed", BindingFlags.Public | BindingFlags.Instance);
                if (current == null || readValue == null || readValue.ReturnType != typeof(Vector2) ||
                    isPressed == null || isPressed.PropertyType != typeof(bool))
                    return;   // an unexpected version; the legacy path is all that is left

                var mouse = Expression.Parameter(typeof(object), "mouse");
                var typed = Expression.Convert(mouse, mouseType);
                s_mousePosition = Expression.Lambda<Func<object, Vector2>>(
                    Expression.Call(Expression.Property(typed, positionProperty), readValue), mouse).Compile();
                s_leftPressed = Expression.Lambda<Func<object, bool>>(
                    Expression.Property(Expression.Property(typed, leftButtonProperty), isPressed), mouse).Compile();
                s_mouseCurrent = Expression.Lambda<Func<object>>(
                    Expression.Convert(Expression.Property(null, current), typeof(object))).Compile();
            }
            catch (Exception)
            {
                s_mouseCurrent = null;
                s_mousePosition = null;
                s_leftPressed = null;
            }
        }
    }
}
