using UnityEditor;

namespace Hiccup.Editor
{
    /// <summary>
    /// Assets ▸ Create ▸ Hiccup menu: new .html fragments, .css style sheets and .js scripts, created with the
    /// Project window's inline rename like any other asset. All import as TextAssets (<see cref="HtmlImporter"/>,
    /// <see cref="CssImporter"/>, <see cref="JsImporter"/>) ready to assign to HtmlDocument's Html, Style Sheets
    /// and Scripts fields.
    /// </summary>
    internal static class HtmlAssetMenu
    {
        private const string Menu = "Assets/Create/Hiccup/";

        // Same neighborhood as Unity's own text-like assets (C# Script, UI Toolkit files).
        private const int Priority = 81;

        private const string HtmlTemplate =
@"<!-- A body fragment: no <html>, <head> or <body>. Style it from a .css asset in the document's Style Sheets. -->
<div class=""panel"">
  <h1>Title</h1>
  <p>Hello from Hiccup.</p>
  <button type=""button"" data-action=""ok"">OK</button>
</div>
";

        private const string CssTemplate =
@"/* Styles for an HtmlDocument. The document is a positioned box the size of its surface. */
.panel {
  position: absolute;
  inset: 16px;
  display: flex;
  flex-direction: column;
  gap: 12px;
  font: 16px/1.4 system-ui, sans-serif;
  color: #e8ecf3;
}

button {
  font: inherit;
  padding: 6px 14px;
  border-radius: 8px;
}
";

        private const string JsTemplate =
@"// Runs in the page once the document is created, and again after Reload(). This is a function body:
// `panel` is the document's outer element, `root` holds your HTML, `HUI.send(name, payload)` reaches C#
// (doc.OnMessage), and `await` is allowed. <script> tags in the HTML itself never run.
root.addEventListener('click', function (e) {
  var button = e.target.closest('button');
  if (button) HUI.send('clicked', button.textContent);
});
";

        [MenuItem(Menu + "HTML Document", priority = Priority)]
        private static void CreateHtml() => Create("NewHtmlDocument.html", HtmlTemplate);

        [MenuItem(Menu + "Style Sheet", priority = Priority + 1)]
        private static void CreateCss() => Create("NewStyleSheet.css", CssTemplate);

        [MenuItem(Menu + "Script", priority = Priority + 2)]
        private static void CreateJs() => Create("NewScript.js", JsTemplate);

        private static void Create(string defaultName, string content)
        {
#if UNITY_6000_4_OR_NEWER
            // 6000.4 moved asset creation to EntityId; the int-based CreateAssetWithContent is an error from 6000.5.
            ProjectWindowUtil.CreateAssetWithTextContent(defaultName, content);
#else
            ProjectWindowUtil.CreateAssetWithContent(defaultName, content);
#endif
        }
    }
}
