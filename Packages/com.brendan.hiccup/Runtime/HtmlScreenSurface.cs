using UnityEngine;
using UnityEngine.UI;

namespace Hiccup
{
    /// <summary>
    /// Draws an <see cref="HtmlDocument"/> through a uGUI RawImage and keeps the browser-side geometry in sync with the
    /// RectTransform, so DOM hit testing, focus rings and screen-reader bounds line up with what is on screen.
    /// </summary>
    /// <remarks>
    /// In texture mode the RawImage shows the document's texture. In overlay mode behind a transparent canvas
    /// (<see cref="HtmlRuntime.OverlayCutout"/>) it writes colour and alpha 0 instead, so the DOM overlay shows
    /// through its rectangle and anything drawn after it in the UI still covers it.
    /// </remarks>
    [AddComponentMenu("Hiccup/HTML Screen Surface")]
    [RequireComponent(typeof(RawImage))]
    [ExecuteAlways]
    public class HtmlScreenSurface : MonoBehaviour
    {
        [SerializeField] private HtmlDocument document;
        [Tooltip("Resize the document (in CSS pixels) to match this RectTransform every frame.")]
        [SerializeField] private bool sizeDocumentToRect = true;
        [Tooltip("Camera used by the parent Canvas when it is not Screen Space - Overlay. Leave empty to use the Canvas' world camera.")]
        [SerializeField] private Camera uiCamera;

        private RawImage _rawImage;
        private RectTransform _rect;
        private Canvas _canvas;
        private Material _material;      // texture mode: premultiplied sample of the document texture
        private Material _cutout;        // overlay mode behind a transparent canvas: alpha-0 hole
        private bool _usingCutout;
        private readonly Vector3[] _corners = new Vector3[4];

        public HtmlDocument Document
        {
            get => document;
            set => document = value;
        }

        private void OnEnable()
        {
            _rawImage = GetComponent<RawImage>();
            _rect = GetComponent<RectTransform>();
            _rawImage.raycastTarget = false;
            if (document == null)
                document = GetComponent<HtmlDocument>();
            _usingCutout = false;
            if (Application.isPlaying)
                ApplyMaterial(false);
        }

        private void OnDisable()
        {
            if (_rawImage != null && (_rawImage.material == _material || _rawImage.material == _cutout))
                _rawImage.material = null;
            if (_material != null)
            {
                Destroy(_material);
                _material = null;
            }
            if (_cutout != null)
            {
                Destroy(_cutout);
                _cutout = null;
            }
        }

        private void ApplyMaterial(bool cutout)
        {
            if (document == null)
                return;
            Material m;
            if (cutout)
            {
                if (_cutout == null)
                    _cutout = Create("Hiccup/UI Overlay Cutout", "Hiccup UI Overlay Cutout (instance)");
                m = _cutout;
            }
            else
            {
                if (!document.PremultipliedAlpha)
                {
                    _rawImage.material = null;
                    _usingCutout = false;
                    return;
                }
                if (_material == null)
                    _material = Create("Hiccup/UI Premultiplied", "Hiccup UI Premultiplied (instance)");
                m = _material;
            }
            if (m != null)
                _rawImage.material = m;
            _usingCutout = cutout;
        }

        private static Material Create(string shaderName, string instanceName)
        {
            var shader = Shader.Find(shaderName);
            return shader == null ? null : new Material(shader) { name = instanceName, hideFlags = HideFlags.HideAndDontSave };
        }

        private void LateUpdate()
        {
            if (!Application.isPlaying || document == null)
                return;
            if (_canvas == null)
                _canvas = GetComponentInParent<Canvas>();
            if (!document.IsCreated)
                return;

            var runtime = HtmlRuntime.Instance;
            bool cutout = document.RenderMode == HtmlRenderMode.Overlay && runtime.OverlayCutout;
            if (cutout != _usingCutout || (!cutout && _material == null && document.PremultipliedAlpha))
                ApplyMaterial(cutout);

            var cam = (_canvas != null && _canvas.renderMode != RenderMode.ScreenSpaceOverlay) ? (_canvas.worldCamera != null ? _canvas.worldCamera : uiCamera) : null;

            // RectTransform corners in screen pixels: 0 bottom-left, 1 top-left, 2 top-right, 3 bottom-right.
            _rect.GetWorldCorners(_corners);
            Vector2 bl = RectTransformUtility.WorldToScreenPoint(cam, _corners[0]);
            Vector2 tl = RectTransformUtility.WorldToScreenPoint(cam, _corners[1]);
            Vector2 tr = RectTransformUtility.WorldToScreenPoint(cam, _corners[2]);

            if (sizeDocumentToRect)
            {
                float css = runtime.CssPerScreenPixel;
                int w = Mathf.Max(1, Mathf.RoundToInt(Vector2.Distance(tl, tr) * css));
                int h = Mathf.Max(1, Mathf.RoundToInt(Vector2.Distance(tl, bl) * css));
                if (w != document.Size.x || h != document.Size.y)
                    document.SetSize(w, h);
            }

            // Screen pixels -> Unity clip space (y up).
            float sw = Mathf.Max(1, Screen.width), sh = Mathf.Max(1, Screen.height);
            Vector2 ndcTL = new Vector2(tl.x / sw * 2f - 1f, tl.y / sh * 2f - 1f);
            Vector2 ndcTR = new Vector2(tr.x / sw * 2f - 1f, tr.y / sh * 2f - 1f);
            Vector2 ndcBL = new Vector2(bl.x / sw * 2f - 1f, bl.y / sh * 2f - 1f);

            var docSize = document.Size;
            Vector2 dx = (ndcTR - ndcTL) / docSize.x;   // one CSS pixel to the right
            Vector2 dy = (ndcBL - ndcTL) / docSize.y;   // one CSS pixel down

            var m = Matrix4x4.identity;
            m.SetColumn(0, new Vector4(dx.x, dx.y, 0f, 0f));
            m.SetColumn(1, new Vector4(dy.x, dy.y, 0f, 0f));
            m.SetColumn(2, new Vector4(0f, 0f, 1f, 0f));
            m.SetColumn(3, new Vector4(ndcTL.x, ndcTL.y, 0f, 1f));
            document.SetGeometry(m);

            var tex = document.Texture;
            _rawImage.texture = tex;   // null in cutout mode: the RawImage then draws its white default, which the shader ignores
            _rawImage.uvRect = document.TextureIsTopDown ? new Rect(0f, 1f, 1f, -1f) : new Rect(0f, 0f, 1f, 1f);
            _rawImage.enabled = document.Visible && (cutout || tex != null);
        }
    }
}
