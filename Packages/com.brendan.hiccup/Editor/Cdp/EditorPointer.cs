using System;
using System.Reflection;
using UnityEngine;

namespace Hiccup.Editor.Cdp
{
    /// <summary>
    /// Reads the mouse without binding the package to either input backend. A project may have the old
    /// input manager, the Input System package, or both, and an assembly reference to a package that is
    /// not installed would not compile — so both paths are resolved reflectively, once.
    /// </summary>
    internal static class EditorPointer
    {
        private static bool s_resolved;

        // Input System package
        private static PropertyInfo s_mouseCurrent;
        private static PropertyInfo s_mousePosition;
        private static PropertyInfo s_mouseLeftButton;
        private static PropertyInfo s_buttonIsPressed;
        private static MethodInfo s_readVector2;

        // Legacy input manager
        private static PropertyInfo s_legacyMousePosition;
        private static MethodInfo s_legacyGetMouseButton;
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
                var mouse = s_mouseCurrent.GetValue(null);
                if (mouse != null)
                {
                    var positionControl = s_mousePosition?.GetValue(mouse);
                    var leftButton = s_mouseLeftButton?.GetValue(mouse);
                    if (positionControl != null && leftButton != null &&
                        s_readVector2 != null && s_buttonIsPressed != null)
                    {
                        position = (Vector2)s_readVector2.Invoke(positionControl, null);
                        leftButtonDown = (bool)s_buttonIsPressed.GetValue(leftButton);
                        return true;
                    }
                }
            }

            if (s_legacyMousePosition != null && !s_legacyBroken)
            {
                try
                {
                    position = (Vector3)s_legacyMousePosition.GetValue(null);
                    leftButtonDown = (bool)s_legacyGetMouseButton.Invoke(null, new object[] { 0 });
                    return true;
                }
                catch (TargetInvocationException)
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
            if (mouseType != null)
            {
                s_mouseCurrent = mouseType.GetProperty("current", BindingFlags.Public | BindingFlags.Static);
                s_mousePosition = mouseType.GetProperty("position", BindingFlags.Public | BindingFlags.Instance);
                s_mouseLeftButton = mouseType.GetProperty("leftButton", BindingFlags.Public | BindingFlags.Instance);

                var vector2Control = Type.GetType("UnityEngine.InputSystem.Controls.Vector2Control, Unity.InputSystem");
                s_readVector2 = vector2Control?.GetMethod("ReadValue", BindingFlags.Public | BindingFlags.Instance, null, Type.EmptyTypes, null);

                var buttonControl = Type.GetType("UnityEngine.InputSystem.Controls.ButtonControl, Unity.InputSystem");
                s_buttonIsPressed = buttonControl?.GetProperty("isPressed", BindingFlags.Public | BindingFlags.Instance);

                if (s_mouseCurrent == null || s_readVector2 == null || s_buttonIsPressed == null)
                    s_mouseCurrent = null;   // an unexpected version; fall through to the legacy path
            }

            var inputType = typeof(Input);
            s_legacyMousePosition = inputType.GetProperty("mousePosition", BindingFlags.Public | BindingFlags.Static);
            s_legacyGetMouseButton = inputType.GetMethod("GetMouseButton", BindingFlags.Public | BindingFlags.Static, null, new[] { typeof(int) }, null);
        }
    }
}
