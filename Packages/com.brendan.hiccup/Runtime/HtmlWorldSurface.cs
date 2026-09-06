using UnityEngine;

namespace Hiccup
{
    /// <summary>
    /// Draws an <see cref="HtmlDocument"/> on a mesh (typically a Quad) in the 3D scene. Every frame the document's
    /// pixel-to-clip transform is derived from the camera and the mesh bounds, so the browser can hit test the
    /// perspective-projected DOM and report correct bounds to assistive technology.
    /// </summary>
    /// <remarks>
    /// In texture mode the mesh samples the document's texture. In overlay mode behind a transparent canvas
    /// (<see cref="HtmlRuntime.OverlayCutout"/>) the mesh instead writes colour and alpha 0 with depth, cutting a
    /// hole in the frame through which the DOM shows, so nearer geometry covers the panel as it should.
    /// </remarks>
    [AddComponentMenu("Hiccup/HTML World Surface")]
    [RequireComponent(typeof(MeshRenderer), typeof(MeshFilter))]
    public class HtmlWorldSurface : MonoBehaviour
    {
        [SerializeField] private HtmlDocument document;
        [Tooltip("Camera the UI is projected for. Defaults to Camera.main.")]
        [SerializeField] private Camera targetCamera;
        [Tooltip("If > 0, the document size (CSS px) is derived from the mesh bounds multiplied by this value.")]
        [SerializeField] private float pixelsPerUnit = 0f;
        [SerializeField] private bool doubleSided = false;

        private MeshRenderer _renderer;
        private MeshFilter _filter;
        private Material _material;      // texture mode: premultiplied sample of the document texture
        private Material _cutout;        // overlay mode behind a transparent canvas: alpha-0 hole with depth
        private bool _usingCutout;
        private static readonly int s_FlipY = Shader.PropertyToID("_FlipY");
        private static readonly int s_Cull = Shader.PropertyToID("_Cull");

        public HtmlDocument Document { get => document; set => document = value; }
        public Camera TargetCamera { get => targetCamera; set => targetCamera = value; }
        /// <summary>If &gt; 0, the document size in CSS pixels is derived from the mesh bounds times this value, every frame.</summary>
        public float PixelsPerUnit { get => pixelsPerUnit; set => pixelsPerUnit = value; }

        private void OnEnable()
        {
            _renderer = GetComponent<MeshRenderer>();
            _filter = GetComponent<MeshFilter>();
            if (document == null)
                document = GetComponent<HtmlDocument>();
            _material = CreateMaterial("Hiccup/Unlit Premultiplied", "Hiccup Unlit Premultiplied (instance)");
            _usingCutout = false;
            if (_material != null)
                _renderer.material = _material;
        }

        private void OnDisable()
        {
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

        private Material CreateMaterial(string shaderName, string instanceName)
        {
            var shader = Shader.Find(shaderName);
            if (shader == null)
                return null;
            var m = new Material(shader) { name = instanceName, hideFlags = HideFlags.HideAndDontSave };
            m.SetFloat(s_Cull, doubleSided ? 0f : 2f);
            return m;
        }

        private void LateUpdate()
        {
            if (document == null || !document.IsCreated)
                return;
            var cam = targetCamera != null ? targetCamera : Camera.main;
            var mesh = _filter.sharedMesh;
            if (cam == null || mesh == null)
                return;

            var b = mesh.bounds;
            var sizeLocal = b.size;
            if (sizeLocal.x <= 0f || sizeLocal.y <= 0f)
                return;

            if (pixelsPerUnit > 0f)
            {
                var ls = transform.lossyScale;
                int w = Mathf.Max(1, Mathf.RoundToInt(sizeLocal.x * ls.x * pixelsPerUnit));
                int h = Mathf.Max(1, Mathf.RoundToInt(sizeLocal.y * ls.y * pixelsPerUnit));
                if (w != document.Size.x || h != document.Size.y)
                    document.SetSize(w, h);
            }

            var docSize = document.Size;
            // Document CSS pixel (0,0) = top-left of the front face; (W,H) = bottom-right; the face is at max z.
            var pixelToLocal = Matrix4x4.identity;
            pixelToLocal.SetColumn(0, new Vector4(sizeLocal.x / docSize.x, 0f, 0f, 0f));
            pixelToLocal.SetColumn(1, new Vector4(0f, -sizeLocal.y / docSize.y, 0f, 0f));
            pixelToLocal.SetColumn(2, new Vector4(0f, 0f, 1f, 0f));
            pixelToLocal.SetColumn(3, new Vector4(b.min.x, b.max.y, b.max.z, 1f));

            var clip = cam.projectionMatrix * cam.worldToCameraMatrix * transform.localToWorldMatrix * pixelToLocal;
            document.SetGeometry(clip);

            bool cutout = document.RenderMode == HtmlRenderMode.Overlay && HtmlRuntime.HasInstance && HtmlRuntime.Instance.OverlayCutout;
            if (cutout != _usingCutout)
            {
                _usingCutout = cutout;
                if (cutout && _cutout == null)
                    _cutout = CreateMaterial("Hiccup/Overlay Cutout", "Hiccup Overlay Cutout (instance)");
                var m = cutout ? _cutout : _material;
                if (m != null)
                    _renderer.material = m;
            }
            if (!cutout && _material != null)
            {
                _material.mainTexture = document.Texture;
                _material.SetFloat(s_FlipY, document.TextureIsTopDown ? 1f : 0f);
            }
            _renderer.enabled = document.Visible && (cutout || document.Texture != null);
        }
    }
}
