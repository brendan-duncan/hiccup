using UnityEditor;

namespace Hiccup.Editor
{
    /// <summary>
    /// Assets ▸ Create ▸ Hiccup menu: new .html fragments and .css style sheets, created with the Project window's
    /// inline rename like any other asset. Both import as TextAssets (.html by Unity, .css by <see cref="CssImporter"/>)
    /// ready to assign to HtmlDocument's Html and Style Sheets fields.
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

        [MenuItem(Menu + "HTML Document", priority = Priority)]
        private static void CreateHtml() => Create("NewHtmlDocument.html", HtmlTemplate);

        [MenuItem(Menu + "Style Sheet", priority = Priority + 1)]
        private static void CreateCss() => Create("NewStyleSheet.css", CssTemplate);

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
