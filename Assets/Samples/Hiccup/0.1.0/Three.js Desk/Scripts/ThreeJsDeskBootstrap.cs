using UnityEngine;


namespace Hiccup.Samples
{
    /// <summary>
    /// Builds a desk with a monitor whose screen is an <see cref="HtmlDocument"/> on a Quad. The document holds a
    /// same-origin iframe (loaded through <c>srcdoc</c>) running a three.js scene, so HTML-in-Canvas paints it into
    /// the texture: a WebGL canvas from the page ends up composited inside Unity's own. The mouse on the desk is a
    /// Unity object; sliding it around the pad moves the cursor in that page.
    /// </summary>
    public class ThreeJsDeskBootstrap : MonoBehaviour
    {
        [Tooltip("Screen size in meters (16:9).")]
        [SerializeField] private Vector2 screenSize = new Vector2(0.64f, 0.36f);

        [Tooltip("Document resolution: CSS pixels per meter of screen. 2000 makes the default screen 1280 x 720.")]
        [SerializeField] private float pixelsPerMetre = 2000f;

        private void Awake()
        {
            // The page repaints every frame (it is a running three.js scene); upload on every frame rather than
            // waiting for paint notifications, which may coalesce or lag for content inside an iframe.
            HtmlRuntime.UpdateMode = HtmlUpdateMode.EveryFrame;

            // ---- Camera: seated at the desk, looking at the monitor
            var camGo = new GameObject("Main Camera") { tag = "MainCamera" };
            var cam = camGo.AddComponent<Camera>();
            camGo.AddComponent<AudioListener>();
            cam.clearFlags = CameraClearFlags.SolidColor;
            cam.backgroundColor = new Color(0.05f, 0.05f, 0.07f);
            cam.fieldOfView = 48f;
            cam.nearClipPlane = 0.05f;
            camGo.transform.position = new Vector3(0.18f, 1.32f, -0.62f);
            camGo.transform.LookAt(new Vector3(0.06f, 1.0f, 0.35f));

            // ---- Lights: a soft window light and a warm desk lamp
            var keyGo = new GameObject("Window light");
            var key = keyGo.AddComponent<Light>();
            key.type = LightType.Directional;
            key.intensity = 0.7f;
            key.color = new Color(0.8f, 0.85f, 1f);
            keyGo.transform.rotation = Quaternion.Euler(50f, -35f, 0f);

            var lampGo = new GameObject("Desk lamp");
            var lamp = lampGo.AddComponent<Light>();
            lamp.type = LightType.Point;
            lamp.range = 3f;
            lamp.intensity = 1.6f;
            lamp.color = new Color(1f, 0.85f, 0.65f);
            lampGo.transform.position = new Vector3(-0.65f, 1.35f, 0.45f);

            // ---- Materials. See HiccupSampleBootstrap: a code-built scene ships no lit material of its own,
            // so a Standard template lives in Resources; URP projects use their pipeline's Lit shader instead.
            var litShader = Shader.Find("Universal Render Pipeline/Lit");
            var litTemplate = Resources.Load<Material>("ThreeJsDesk/Lit");
            Material Lit(Color color)
            {
                var m = litShader != null ? new Material(litShader) : litTemplate != null ? new Material(litTemplate) : null;
                if (m != null)
                    m.color = color;
                return m;
            }

            // ---- Room
            var floor = Primitive("Plane");
            floor.name = "Floor";
            Paint(floor, Lit(new Color(0.16f, 0.14f, 0.13f)));

            var wall = Primitive("Cube");
            wall.name = "Wall";
            wall.transform.position = new Vector3(0f, 1.5f, 1.2f);
            wall.transform.localScale = new Vector3(8f, 3f, 0.1f);
            Paint(wall, Lit(new Color(0.32f, 0.33f, 0.36f)));

            // ---- Desk: top plus four legs. The top surface is at y = 0.76.
            var wood = Lit(new Color(0.55f, 0.4f, 0.26f));
            var top = Primitive("Cube");
            top.name = "Desk";
            top.transform.position = new Vector3(0f, 0.74f, 0.35f);
            top.transform.localScale = new Vector3(1.8f, 0.04f, 0.8f);
            Paint(top, wood);
            var legMat = Lit(new Color(0.2f, 0.2f, 0.22f));
            foreach (var sx in new[] { -0.85f, 0.85f })
            {
                foreach (var sz in new[] { 0f, 0.7f })
                {
                    var leg = Primitive("Cylinder");
                    leg.name = "Leg";
                    leg.transform.position = new Vector3(sx, 0.36f, sz);
                    leg.transform.localScale = new Vector3(0.03f, 0.36f, 0.03f);   // a cylinder is 2 units tall at scale 1
                    Paint(leg, legMat);
                }
            }

            // ---- Monitor: base, neck, bezel, then the screen itself
            const float screenZ = 0.6f;
            const float screenY = 1.12f;
            var plastic = Lit(new Color(0.08f, 0.08f, 0.09f));
            var stand = Primitive("Cylinder");
            stand.name = "Monitor base";
            stand.transform.position = new Vector3(0f, 0.77f, screenZ);
            stand.transform.localScale = new Vector3(0.2f, 0.01f, 0.2f);
            Paint(stand, plastic);
            var neck = Primitive("Cube");
            neck.name = "Monitor neck";
            neck.transform.position = new Vector3(0f, 0.9f, screenZ + 0.02f);
            neck.transform.localScale = new Vector3(0.05f, 0.24f, 0.02f);
            Paint(neck, plastic);
            var bezel = Primitive("Cube");
            bezel.name = "Monitor";
            bezel.transform.position = new Vector3(0f, screenY, screenZ);
            bezel.transform.localScale = new Vector3(screenSize.x + 0.05f, screenSize.y + 0.05f, 0.03f);
            Paint(bezel, plastic);

            var screen = Primitive("Quad");   // a Quad faces -Z, toward the chair
            screen.name = "Screen (HTML)";
            screen.SetActive(false);          // configure before OnEnable creates the browser-side panel
            screen.transform.position = new Vector3(0f, screenY, screenZ - 0.0165f);
            screen.transform.localScale = new Vector3(screenSize.x, screenSize.y, 1f);

            var doc = screen.AddComponent<HtmlDocument>();
            doc.Html = Resources.Load<TextAsset>("ThreeJsDesk/Screen");
            doc.StyleSheets = new[] { Resources.Load<TextAsset>("ThreeJsDesk/Screen.style") };
            doc.PointerMode = HtmlPointerMode.None;   // the desk mouse is the only pointer this screen has
            doc.ResolutionScale = 1f;
            doc.Mipmaps = true;
            var surface = screen.AddComponent<HtmlWorldSurface>();
            surface.TargetCamera = cam;
            surface.PixelsPerUnit = pixelsPerMetre;
            var controller = screen.AddComponent<DeskScreenController>();
            controller.Document = doc;
            controller.ScenePage = Resources.Load<TextAsset>("ThreeJsDesk/ThreeScene");
            screen.SetActive(true);

            // ---- Keyboard, mouse pad, mouse, mug
            var keyboard = Primitive("Cube");
            keyboard.name = "Keyboard";
            keyboard.transform.position = new Vector3(-0.06f, 0.77f, 0.16f);
            keyboard.transform.localScale = new Vector3(0.44f, 0.02f, 0.15f);
            Paint(keyboard, Lit(new Color(0.14f, 0.14f, 0.16f)));

            var pad = new Rect(0.24f, 0.04f, 0.30f, 0.24f);   // x and z extents on the desk
            const float padTop = 0.766f;
            var padGo = Primitive("Cube");
            padGo.name = "Mouse pad";
            padGo.transform.position = new Vector3(pad.center.x, padTop - 0.003f, pad.center.y);
            padGo.transform.localScale = new Vector3(pad.width, 0.006f, pad.height);
            Paint(padGo, Lit(new Color(0.1f, 0.13f, 0.22f)));

            var mouse = Primitive("Sphere");
            mouse.name = "Mouse";
            mouse.transform.position = new Vector3(pad.center.x, padTop + 0.018f, pad.center.y);
            mouse.transform.localScale = new Vector3(0.062f, 0.036f, 0.11f);
            Paint(mouse, Lit(new Color(0.82f, 0.82f, 0.8f)));
            var deskMouse = mouse.AddComponent<DeskMouse>();
            deskMouse.Camera = cam;
            deskMouse.Screen = controller;
            deskMouse.Pad = pad;
            deskMouse.PadTop = padTop;

            var mug = Primitive("Cylinder");
            mug.name = "Mug";
            mug.transform.position = new Vector3(-0.5f, 0.81f, 0.32f);
            mug.transform.localScale = new Vector3(0.045f, 0.05f, 0.045f);
            Paint(mug, Lit(new Color(0.85f, 0.3f, 0.25f)));
        }

        private void OnDestroy()
        {
            HtmlRuntime.UpdateMode = HtmlUpdateMode.Auto;   // a static; do not leak the choice into another scene
        }

        private static void Paint(GameObject go, Material material)
        {
            if (material != null)
                go.GetComponent<Renderer>().sharedMaterial = material;
        }

        // GameObject.CreatePrimitive attaches a Collider, which needs the Physics module; the sample uses no
        // physics, so build the same meshes from Unity's built-in resources instead.
        private static GameObject Primitive(string mesh)
        {
            var go = new GameObject(mesh, typeof(MeshFilter), typeof(MeshRenderer));
            go.GetComponent<MeshFilter>().sharedMesh = Resources.GetBuiltinResource<Mesh>(mesh + ".fbx");
            return go;
        }
    }
}
