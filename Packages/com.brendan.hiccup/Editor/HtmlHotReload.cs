using System.Collections.Generic;
using System.IO;
using UnityEditor;
using UnityEngine;

namespace Hiccup.Editor
{
    /// <summary>
    /// Pushes edited .html, .css and .js assets into live documents during play mode. A style sheet change is
    /// applied in place, so the page keeps its state; an HTML or script change reloads the document. Content a
    /// script set with <c>SetHtml</c> or <c>SetCss</c> is not tracked, only the assets assigned to the document.
    /// </summary>
    internal sealed class HtmlHotReload : AssetPostprocessor
    {
        private static void OnPostprocessAllAssets(string[] imported, string[] deleted, string[] moved, string[] movedFrom)
        {
            if (!EditorApplication.isPlaying || !HtmlEditorPreview.HotReload || imported == null || imported.Length == 0)
                return;

            HashSet<string> changed = null;
            foreach (var path in imported)
            {
                if (IsDocumentAsset(path))
                    (changed ??= new HashSet<string>()).Add(path);
            }
            if (changed == null)
                return;

            int reloaded = 0, restyled = 0;
            foreach (var doc in Object.FindObjectsByType<HtmlDocument>(FindObjectsInactive.Include))
            {
                if (!doc.IsCreated)
                    continue;   // not in a page yet; Create() reads the fresh text anyway
                if (References(doc.Html, changed) || References(doc.Scripts, changed))
                {
                    doc.Reload();
                    reloaded++;
                }
                else if (References(doc.StyleSheets, changed))
                {
                    doc.ReloadStyles();
                    restyled++;
                }
            }

            if (reloaded + restyled == 0)
                return;
            var names = new List<string>();
            foreach (var path in changed)
                names.Add(Path.GetFileName(path));
            names.Sort();
            Debug.Log($"[Hiccup] {string.Join(", ", names)} changed: reloaded {reloaded}, restyled {restyled} document(s).");
        }

        private static bool IsDocumentAsset(string path)
        {
            return path.EndsWith(".html", System.StringComparison.OrdinalIgnoreCase) ||
                   path.EndsWith(".htm", System.StringComparison.OrdinalIgnoreCase) ||
                   path.EndsWith(".css", System.StringComparison.OrdinalIgnoreCase) ||
                   path.EndsWith(".js", System.StringComparison.OrdinalIgnoreCase);
        }

        private static bool References(TextAsset asset, HashSet<string> changed)
            => asset != null && changed.Contains(AssetDatabase.GetAssetPath(asset));

        private static bool References(TextAsset[] assets, HashSet<string> changed)
        {
            if (assets == null)
                return false;
            foreach (var asset in assets)
            {
                if (References(asset, changed))
                    return true;
            }
            return false;
        }
    }
}
