using System;
using System.Collections;
using Hiccup.Uitk;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;
using UnityEngine.UIElements;

namespace Hiccup.Tests
{
    /// <summary>
    /// Play-mode tests for <see cref="HtmlUitkMirror"/> against the Editor preview: a UI Toolkit panel built in
    /// code is mirrored into a real Chrome page, and the DOM copy is inspected and driven from C#. Ignored where no
    /// preview is running, like <see cref="PreviewDocumentTests"/>.
    /// </summary>
    public class UitkMirrorTests
    {
        private const float ReadyTimeout = 60f;   // includes Chrome's start on the first test
        private const float StepTimeout = 10f;

        private GameObject _go;
        private PanelSettings _settings;
        private UIDocument _uiDocument;
        private HtmlUitkMirror _mirror;

        [TearDown]
        public void TearDown()
        {
            if (_go != null)
                UnityEngine.Object.Destroy(_go);
            if (_settings != null)
                UnityEngine.Object.Destroy(_settings);
        }

        private static void RequirePreview()
        {
            if (HtmlBackend.Current == null)
                Assert.Ignore("The Editor preview is not running: Chrome was not found, or it is disabled under Window > Hiccup.");
        }

        /// <summary>A UIDocument on a fresh, theme-less PanelSettings, with the mirror on it; <paramref name="build"/> fills the root.</summary>
        private HtmlUitkMirror NewMirror(Action<VisualElement> build)
        {
            _settings = ScriptableObject.CreateInstance<PanelSettings>();
            _settings.scaleMode = PanelScaleMode.ConstantPixelSize;
            _go = new GameObject("UI Toolkit mirror test");
            _go.SetActive(false);   // configure before OnEnable creates the panel and the document
            _uiDocument = _go.AddComponent<UIDocument>();
            _uiDocument.panelSettings = _settings;
            _mirror = _go.AddComponent<HtmlUitkMirror>();
            _go.SetActive(true);
            build(_uiDocument.rootVisualElement);
            return _mirror;
        }

        private static IEnumerator Until(Func<bool> condition, float timeout, string what)
        {
            float deadline = Time.realtimeSinceStartup + timeout;
            while (!condition())
            {
                if (Time.realtimeSinceStartup > deadline)
                    Assert.Fail("Timed out after " + timeout + " s waiting for " + what);
                yield return null;
            }
        }

        private IEnumerator Mirrored() =>
            Until(() => _mirror.Document != null && _mirror.Document.IsCreated && _mirror.NodeCount > 1, ReadyTimeout, "the panel to be mirrored");

        [UnityTest]
        public IEnumerator ElementsBecomeDomWithTheirText()
        {
            RequirePreview();
            NewMirror(root =>
            {
                var box = new VisualElement { name = "box" };
                box.style.width = 200;
                box.style.height = 100;
                box.style.backgroundColor = new Color(0.2f, 0.3f, 0.4f);
                box.style.borderTopLeftRadius = 8;
                box.Add(new Label("Hello mirror") { name = "greeting" });
                root.Add(box);
            });
            yield return Mirrored();
            var doc = _mirror.Document;

            // Root, the UIDocument's root, the box and the label.
            Assert.GreaterOrEqual(_mirror.NodeCount, 4);
            Assert.AreEqual("Hello mirror", doc.Q(".ug-txt").Text, "a Label becomes a text span");
            Assert.AreEqual(1, doc.QAll(".ug-txt").Count);

            var bg = doc.QAll(".ug-bg");
            bool found = false;
            foreach (var el in bg)
            {
                var style = el.GetAttribute("style");
                if (style.Contains("background-color:rgba(51,") && style.Contains("border-radius:8px 0px 0px 0px"))
                    found = true;
                el.Dispose();
            }
            Assert.IsTrue(found, "the box's resolved background color and radius reach its background element");
        }

        [UnityTest]
        public IEnumerator ButtonClickAndToggleChangeReachThePanel()
        {
            RequirePreview();
            int clicks = 0;
            Toggle toggle = null;
            NewMirror(root =>
            {
                var button = new Button(() => clicks++) { text = "Press" };
                button.style.width = 120;
                button.style.height = 40;
                root.Add(button);
                toggle = new Toggle("Check");
                toggle.style.width = 120;
                toggle.style.height = 24;
                root.Add(toggle);
            });
            yield return Mirrored();
            var doc = _mirror.Document;

            Assert.AreEqual(1, doc.QAll("button.ug").Count, "a Button is the element itself");
            doc.Q("button.ug").Click();   // a scripted click carries no click count, like a keyboard press: submitted to the panel
            yield return Until(() => clicks == 1, StepTimeout, "the Button's click");

            Assert.AreEqual(1, doc.QAll("input[type=checkbox]").Count, "a Toggle gets a native checkbox");
            doc.Q("input[type=checkbox]").Click();
            yield return Until(() => toggle.value, StepTimeout, "the Toggle's value");
            yield return null;
            Assert.IsTrue(doc.Q("input[type=checkbox]").Checked, "the mirror does not push the old value back");
        }

        [UnityTest]
        public IEnumerator SourceIsHiddenWhileMirroredAndRestoredAfter()
        {
            RequirePreview();
            NewMirror(root => root.Add(new Label("x")));
            yield return Mirrored();
            var panelRoot = _uiDocument.rootVisualElement.panel.visualTree;
            yield return null;   // the panel resolves the inline opacity on its next update
            Assert.AreEqual(0f, panelRoot.resolvedStyle.opacity, 0.001f, "the panel keeps running but is not drawn");

            _mirror.enabled = false;
            Assert.IsNull(_mirror.Document, "disabling removes the document it created");
            yield return null;
            Assert.AreEqual(1f, panelRoot.resolvedStyle.opacity, 0.001f, "disabling shows the panel again");
        }
    }
}
