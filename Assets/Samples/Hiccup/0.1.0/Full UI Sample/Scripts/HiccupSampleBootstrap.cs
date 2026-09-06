using UnityEngine;
using UnityEngine.UI;

namespace Hiccup.Samples
{
    /// <summary>
    /// Builds the whole sample at runtime so the scene file stays trivial: a camera, lights, clickable cubes,
    /// a full-screen HTML HUD (uGUI RawImage + HtmlScreenSurface) and an interactive HTML console on a 3D quad
    /// (HtmlWorldSurface). Content comes from Resources/HiccupSample.
    /// </summary>
    public class HiccupSampleBootstrap : MonoBehaviour
    {
        [SerializeField] private int cubeCount = 8;

        private void Awake()
        {
            // ---- Camera
            var camGo = new GameObject("Main Camera") { tag = "MainCamera" };
            var cam = camGo.AddComponent<Camera>();
            camGo.AddComponent<AudioListener>();
            cam.transform.position = new Vector3(0f, 2.5f, -9f);
            cam.transform.LookAt(new Vector3(0f, 1.2f, 1f));
            cam.clearFlags = CameraClearFlags.SolidColor;
            cam.backgroundColor = new Color(0.02f, 0.03f, 0.06f);
            cam.fieldOfView = 60f;

            // ---- Light
            var sunGo = new GameObject("Sun");
            var sun = sunGo.AddComponent<Light>();
            sun.type = LightType.Directional;
            sun.intensity = 1.3f;
            sun.color = new Color(1f, 0.96f, 0.9f);
            sunGo.transform.rotation = Quaternion.Euler(48f, -32f, 0f);

            // ---- Floor
            var floor = Primitive("Plane");
            floor.name = "Deck";
            floor.transform.position = new Vector3(0f, -1.5f, 1f);
            floor.transform.localScale = new Vector3(3f, 1f, 3f);
            // A code-built scene ships no material assets, so in a player every lit
            // shader is stripped and Shader.Find returns null. The sample therefore ships Standard-shader
            // templates in Resources/HiccupSample (Lit + LitEmissive; the latter keeps
            // the _EMISSION variants in the build). URP projects keep their own Lit
            // shader alive through the pipeline asset, so prefer it when present.
            var litShader = Shader.Find("Universal Render Pipeline/Lit");
            var litTemplate = Resources.Load<Material>("HiccupSample/Lit");
            var emissiveTemplate = Resources.Load<Material>("HiccupSample/LitEmissive") ?? litTemplate;
            var floorMat = litShader != null ? new Material(litShader)
                : litTemplate != null ? new Material(litTemplate)
                : new Material(Shader.Find("Hidden/InternalErrorShader"));
            floorMat.color = new Color(0.10f, 0.12f, 0.16f);
            floor.GetComponent<Renderer>().sharedMaterial = floorMat;

            // ---- Game state
            var game = gameObject.AddComponent<SampleGame>();
            game.Camera = cam;
            game.Sun = sun;

            // ---- Cubes
            var cubeMat = litShader != null ? new Material(litShader)
                : emissiveTemplate != null ? new Material(emissiveTemplate)
                : new Material(Shader.Find("Hidden/InternalErrorShader"));
            cubeMat.color = Color.white;
            cubeMat.EnableKeyword("_EMISSION");
            cubeMat.globalIlluminationFlags = MaterialGlobalIlluminationFlags.None;
            for (int i = 0; i < cubeCount; i++)
            {
                var cube = Primitive("Cube");
                cube.name = $"Debris {i + 1}";
                cube.GetComponent<Renderer>().sharedMaterial = cubeMat;
                var target = cube.AddComponent<SalvageTarget>();
                target.Game = game;
                game.Targets.Add(target);
            }

            // ---- Full-screen HTML HUD
            var canvasGo = new GameObject("HUD Canvas", typeof(Canvas), typeof(CanvasScaler), typeof(GraphicRaycaster));
            var canvas = canvasGo.GetComponent<Canvas>();
            canvas.renderMode = RenderMode.ScreenSpaceOverlay;
            canvasGo.GetComponent<CanvasScaler>().uiScaleMode = CanvasScaler.ScaleMode.ConstantPixelSize;

            var hudGo = new GameObject("Game UI (HTML)", typeof(RectTransform), typeof(RawImage));
            hudGo.SetActive(false); // configure before OnEnable creates the browser-side panel
            hudGo.transform.SetParent(canvasGo.transform, false);
            var rect = hudGo.GetComponent<RectTransform>();
            rect.anchorMin = Vector2.zero;
            rect.anchorMax = Vector2.one;
            rect.offsetMin = Vector2.zero;
            rect.offsetMax = Vector2.zero;

            var hudDoc = hudGo.AddComponent<HtmlDocument>();
            hudDoc.Html = Resources.Load<TextAsset>("HiccupSample/GameUI");
            hudDoc.StyleSheets = new[] { Resources.Load<TextAsset>("HiccupSample/GameUI.style") };
            hudDoc.Scripts = new[] { Resources.Load<TextAsset>("HiccupSample/GameUI.tooltips") };
            hudDoc.PointerMode = HtmlPointerMode.ChildrenOnly; // the sample CSS decides which regions take input
            hudGo.AddComponent<HtmlScreenSurface>();
            var hudController = hudGo.AddComponent<GameUIController>();
            hudController.Game = game;
            hudController.Document = hudDoc;
            hudGo.SetActive(true);

            // ---- World-space HTML console on a quad
            var quad = Primitive("Quad");
            quad.name = "Drone Console (HTML)";
            quad.SetActive(false);
            quad.transform.position = new Vector3(4.6f, 1.6f, 2.2f);
            quad.transform.rotation = Quaternion.Euler(0f, 28f, 0f);
            quad.transform.localScale = new Vector3(3.6f, 2.5f, 1f);

            var worldDoc = quad.AddComponent<HtmlDocument>();
            worldDoc.Html = Resources.Load<TextAsset>("HiccupSample/WorldPanel");
            worldDoc.StyleSheets = new[] { Resources.Load<TextAsset>("HiccupSample/WorldPanel.style") };
            worldDoc.Size = new Vector2Int(576, 400);
            worldDoc.ResolutionScale = 2f;   // supersample: the quad is viewed at an angle, mips + aniso do the rest
            worldDoc.Mipmaps = true;
            worldDoc.PointerMode = HtmlPointerMode.Panel;
            var worldSurface = quad.AddComponent<HtmlWorldSurface>();
            worldSurface.TargetCamera = cam;
            var worldController = quad.AddComponent<WorldPanelController>();
            worldController.Game = game;
            worldController.Document = worldDoc;
            quad.SetActive(true);
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
