using UnityEditor;
using UnityEngine;

namespace Hiccup.Editor
{
    [CustomEditor(typeof(HtmlDocument))]
    [CanEditMultipleObjects]
    public class HtmlDocumentEditor : UnityEditor.Editor
    {
        public override void OnInspectorGUI()
        {
            DrawDefaultInspector();

            EditorGUILayout.Space();
            if (!Application.isPlaying)
            {
                EditorGUILayout.HelpBox(
                    HtmlEditorPreview.Enabled
                        ? "Enter play mode to preview this document in Chrome (Window > Hiccup). Layout, styling and input are real; " +
                          "accessibility and HTML-in-Canvas compositing only exist in a web build.\n" +
                          "Chrome 148+ with chrome://flags/#canvas-draw-element or an Origin Trial token composites the DOM into the texture; " +
                          "other browsers get a DOM overlay."
                        : "The Editor preview is off, so documents render a placeholder here. Turn it on under Window > Hiccup, or build for WebGL/WebGPU.",
                    MessageType.Info);
                return;
            }

            var doc = (HtmlDocument)target;
            var mode = doc.RenderMode;
            var runtime = HtmlRuntime.HasInstance ? HtmlRuntime.Instance : null;
            EditorGUILayout.LabelField("Runtime", EditorStyles.boldLabel);
            EditorGUILayout.LabelField("Render mode", mode.ToString());
            EditorGUILayout.LabelField("Editor preview", HtmlEditorPreview.Status);
            EditorGUILayout.LabelField("Created", doc.IsCreated ? "yes" : "no");
            EditorGUILayout.LabelField("Texture", doc.Texture != null ? $"{doc.TextureSize.x} x {doc.TextureSize.y}" : "none");
            if (runtime != null)
            {
                EditorGUILayout.LabelField("Canvas (CSS px)", $"{runtime.CanvasCssSize.x} x {runtime.CanvasCssSize.y}  @ {runtime.DevicePixelRatio}x");
            }

            using (new EditorGUILayout.HorizontalScope())
            {
                if (GUILayout.Button("Reload content"))
                    doc.Reload();
                if (GUILayout.Button("Invalidate"))
                    doc.Invalidate();
            }

            // The preview's Chrome serves its own DevTools front end; it connects to this document's page over the
            // debugging port, so the DOM, styles, console and network of the live document are all inspectable.
            var devTools = HtmlEditorPreview.DevToolsUrl(doc);
            using (new EditorGUI.DisabledScope(devTools == null))
            using (new EditorGUILayout.HorizontalScope())
            {
                if (GUILayout.Button(new GUIContent("Open in Chrome DevTools",
                        "Opens Chrome's DevTools for this document's preview page in your default browser. " +
                        "The page must open in a Chromium browser; use Copy URL and paste it into Chrome otherwise.")))
                    Application.OpenURL(devTools);
                if (GUILayout.Button(new GUIContent("Copy URL", "Copies the DevTools URL to the clipboard."), GUILayout.Width(80)))
                    EditorGUIUtility.systemCopyBuffer = devTools;
            }
        }
    }
}
