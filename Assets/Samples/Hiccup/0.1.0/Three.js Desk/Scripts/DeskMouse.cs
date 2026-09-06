using UnityEngine;
using UnityEngine.InputSystem;

namespace Hiccup.Samples
{
    /// <summary>
    /// The mouse on the desk. Where it sits on the pad is where the cursor sits on the screen, like a tablet.
    /// Right-drag anywhere on the pad slides the mouse without pressing its button, so the cursor can hover and
    /// travel between targets. Left-press on the mouse is the button: drag to press-and-drag, press and release
    /// without moving to click. The scroll wheel scrolls the page. No physics: the desk is a plane and the mouse
    /// is a bounds test.
    /// </summary>
    public class DeskMouse : MonoBehaviour
    {
        public Camera Camera;
        public DeskScreenController Screen;
        [Tooltip("Pad extents on the desk: x is world x, y is world z.")]
        public Rect Pad;
        [Tooltip("World y of the pad surface.")]
        public float PadTop;

        private Renderer _renderer;
        private bool _pressing;    // left button held on the mouse: the page sees a press
        private bool _sliding;     // right button held: the mouse moves, the page sees only motion
        private bool _moved;       // moved since the left press, which decides click versus drag
        private Vector3 _grabOffset;
        private Vector3 _restScale;

        private void Awake()
        {
            _renderer = GetComponent<Renderer>();
            _restScale = transform.localScale;
        }

        private void Start()
        {
            SendPosition();   // put the cursor where the mouse starts
        }

        private void Update()
        {
            var mouse = Mouse.current;
            if (mouse == null || Camera == null)
                return;
            var ray = Camera.ScreenPointToRay(mouse.position.ReadValue());
            bool onPad = TryHitPadPlane(ray, out var hit);

            // Right button: slide. Starts on the mouse or anywhere on the pad, and keeps the offset so the mouse
            // does not jump under the pointer.
            if (!_sliding && mouse.rightButton.wasPressedThisFrame && onPad && (HitsMouse(ray) || InsidePad(hit, 0.02f)))
            {
                _sliding = true;
                if (!_pressing)
                    _grabOffset = transform.position - hit;
            }

            // Left button: press. Starts on the mouse, or anywhere while already sliding.
            if (!_pressing && mouse.leftButton.wasPressedThisFrame && onPad && (_sliding || HitsMouse(ray)))
            {
                _pressing = true;
                _moved = false;
                if (!_sliding)
                    _grabOffset = transform.position - hit;
                transform.localScale = new Vector3(_restScale.x, _restScale.y * 0.8f, _restScale.z);   // "pressed"
                Screen?.Press();
            }

            if ((_pressing || _sliding) && onPad)
            {
                var p = hit + _grabOffset;
                p.x = Mathf.Clamp(p.x, Pad.xMin, Pad.xMax);
                p.z = Mathf.Clamp(p.z, Pad.yMin, Pad.yMax);
                p.y = transform.position.y;
                if ((p - transform.position).sqrMagnitude > 1e-6f)
                    _moved = true;
                transform.position = p;
                SendPosition();
            }

            if (_pressing && mouse.leftButton.wasReleasedThisFrame)
            {
                _pressing = false;
                transform.localScale = _restScale;
                Screen?.Release(click: !_moved);
            }
            if (_sliding && mouse.rightButton.wasReleasedThisFrame)
            {
                _sliding = false;
            }

            float wheel = mouse.scroll.ReadValue().y;
            if (Mathf.Abs(wheel) > 0.01f)
                Screen?.Wheel(wheel);
        }

        private bool HitsMouse(Ray ray)
        {
            var b = _renderer.bounds;
            b.Expand(0.03f);   // a small target; be forgiving
            return b.IntersectRay(ray);
        }

        private bool InsidePad(Vector3 point, float margin)
        {
            return point.x >= Pad.xMin - margin && point.x <= Pad.xMax + margin
                && point.z >= Pad.yMin - margin && point.z <= Pad.yMax + margin;
        }

        private void SendPosition()
        {
            if (Screen == null)
                return;
            var p = transform.position;
            // Far edge of the pad (larger z) is the top of the screen.
            Screen.MoveTo(Mathf.InverseLerp(Pad.xMin, Pad.xMax, p.x), 1f - Mathf.InverseLerp(Pad.yMin, Pad.yMax, p.z));
        }

        private bool TryHitPadPlane(Ray ray, out Vector3 point)
        {
            point = default;
            if (Mathf.Abs(ray.direction.y) < 1e-5f)
                return false;
            float t = (PadTop - ray.origin.y) / ray.direction.y;
            if (t < 0f)
                return false;
            point = ray.GetPoint(t);
            return true;
        }
    }
}
