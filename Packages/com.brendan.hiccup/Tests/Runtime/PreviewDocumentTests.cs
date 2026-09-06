using System;
using System.Collections;
using System.Threading.Tasks;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;

namespace Hiccup.Tests
{
    /// <summary>
    /// Play-mode tests against the Editor preview: a real Chrome behind <see cref="HtmlBackend.Current"/>. They
    /// exercise the whole path from the C# API through the DevTools protocol to the page and back, and are
    /// ignored where no preview is running (no Chrome, or the preview turned off under Window > Hiccup).
    /// </summary>
    public class PreviewDocumentTests
    {
        private const float ReadyTimeout = 60f;   // includes Chrome's start on the first test
        private const float StepTimeout = 10f;

        private GameObject _go;
        private HtmlDocument _doc;

        [TearDown]
        public void TearDown()
        {
            if (_go != null)
                UnityEngine.Object.Destroy(_go);
        }

        private static void RequirePreview()
        {
            if (HtmlBackend.Current == null)
                Assert.Ignore("The Editor preview is not running: Chrome was not found, or it is disabled under Window > Hiccup.");
        }

        private HtmlDocument NewDocument(string html, TextAsset[] scripts = null)
        {
            _go = new GameObject("Preview test document");
            _go.SetActive(false);   // configure before OnEnable creates the panel
            _doc = _go.AddComponent<HtmlDocument>();
            _doc.Size = new Vector2Int(320, 200);
            _doc.Html = new TextAsset(html);
            if (scripts != null)
                _doc.Scripts = scripts;
            _go.SetActive(true);
            return _doc;
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

        private IEnumerator Ready() => Until(() => _doc.IsCreated, ReadyTimeout, "the document to be created");

        private static IEnumerator Await(Task task) => Until(() => task.IsCompleted, StepTimeout, "the task to complete");

        [UnityTest]
        public IEnumerator ElementsReadAndWrite()
        {
            RequirePreview();
            NewDocument("<ul id='list'><li class='row' data-id='a'>A</li><li class='row' data-id='b'>B</li></ul>" +
                        "<select id='sel'></select><p id='text'>Hello</p>");
            yield return Ready();

            Assert.AreEqual("Hello", _doc.Q("#text").Text);
            _doc.Q("#text").Text = "Bye";
            Assert.AreEqual("Bye", _doc.Q("#text").Text, "a read sees the writes queued before it");

            Assert.AreEqual(2, _doc.Q("#list").Children.Count);
            Assert.AreEqual(2, _doc.QAll(".row").Count);
            Assert.AreEqual("b", _doc.QAll(".row")[1].GetData("id"));
            Assert.AreEqual("list", _doc.Q(".row").Closest("ul").Id);
            Assert.IsFalse(_doc.Q(".row").Closest(".nothing").IsValid);
            Assert.IsFalse(_doc.Q("#list").Parent.IsValid, "the content root is never handed out");

            _doc.Q("#sel").SetOptions(new[] { ("a", "Low"), ("b", "High") }, "b");
            Assert.AreEqual(2, _doc.Q("#sel").Children.Count);
            Assert.AreEqual(1, _doc.Q("#sel").SelectedIndex);
            Assert.AreEqual("b", _doc.Q("#sel").Value);

            _doc.Q("#text").SetAttribute("data-n", 3).AddClass("big");
            Assert.AreEqual("3", _doc.Q("#text").GetData("n"));
            Assert.IsTrue(_doc.Q("#text").HasClass("big"));
            Assert.AreEqual("x", _doc.Q("#text").Append("<i>x</i>").Q("i").Text);
        }

        [UnityTest]
        public IEnumerator ClickReachesTheActionHandler()
        {
            RequirePreview();
            NewDocument("<button id='btn' data-action='go' data-slot='2'>Go</button>");
            yield return Ready();

            HtmlEvent got = null;
            _doc.OnAction("go", e => got = e);
            _doc.Q("#btn").Click();
            yield return Until(() => got != null, StepTimeout, "the click event");

            Assert.AreEqual("btn", got.id);
            Assert.AreEqual("button", got.tag);
            Assert.AreEqual("2", got.GetData("slot"));
            Assert.AreEqual("0", got.detail, "a scripted click() carries no click count; a real click reports 1");
        }

        [UnityTest]
        public IEnumerator MessagesFromThePageReachHandlers()
        {
            RequirePreview();
            NewDocument("<p>m</p>");
            yield return Ready();

            string objectData = null, stringData = null;
            _doc.OnMessage("obj", m => objectData = m.Data);
            _doc.OnMessage("str", m => stringData = m.Data);
            _doc.Eval("HUI.send('obj', { n: 2 }); HUI.send('str', 'plain');");
            yield return Until(() => objectData != null && stringData != null, StepTimeout, "the messages");

            Assert.AreEqual("{\"n\":2}", objectData);
            Assert.AreEqual("plain", stringData);
        }

        [UnityTest]
        public IEnumerator EvalAndEvalAsync()
        {
            RequirePreview();
            NewDocument("<p id='p'>v</p>");
            yield return Ready();

            Assert.AreEqual("v", _doc.Eval("return root.querySelector('#p').textContent;"));
            Assert.AreEqual("{\"a\":[1,true]}", _doc.Eval("return { a: [1, true] };"), "objects come back as JSON");
            Assert.AreEqual("true", _doc.Eval("return true;"));

            var ok = _doc.EvalAsync("await new Promise(r => setTimeout(r, 20)); return { v: 21 * 2 };");
            yield return Await(ok);
            Assert.AreEqual("{\"v\":42}", ok.Result);

            var bad = _doc.EvalAsync("throw new Error('nope');");
            yield return Await(bad);
            Assert.IsTrue(bad.IsFaulted);
            var ex = bad.Exception?.InnerException;
            Assert.IsInstanceOf<HtmlEvalException>(ex);
            StringAssert.Contains("nope", ex.Message);
        }

        [UnityTest]
        public IEnumerator ScriptsRunBeforeCreated()
        {
            RequirePreview();
            var script = new TextAsset("root.insertAdjacentHTML('beforeend', '<i id=\"s\">' + (typeof HUI.send === 'function' ? 'ok' : 'no send') + '</i>');");
            string seenAtCreated = null;
            NewDocument("<p>s</p>", new[] { script });
            _doc.Created += d => seenAtCreated = d.Q("#s").Text;
            yield return Ready();

            Assert.AreEqual("ok", seenAtCreated, "the script's work is visible to Created handlers");
        }

        [UnityTest]
        public IEnumerator ImagesBindToElements()
        {
            RequirePreview();
            NewDocument("<img id='im' data-hui-image='pic'><div id='later'></div>");
            yield return Ready();

            var tex = new Texture2D(2, 3, TextureFormat.RGBA32, false);
            tex.SetPixels32(new Color32[6]);
            tex.Apply();
            try
            {
                _doc.SetImage("pic", tex);
                yield return Until(() => _doc.Q("#im").GetProperty("naturalWidth") == "2", StepTimeout, "the image to load");
                Assert.AreEqual("3", _doc.Q("#im").GetProperty("naturalHeight"));

                // An element added afterwards is bound by the observer.
                _doc.Q("#later").InnerHtml = "<img id='im2' data-hui-image='pic'>";
                yield return Until(() => _doc.Q("#im2").GetProperty("naturalWidth") == "2", StepTimeout, "the later image to load");

                _doc.RemoveImage("pic");
                yield return Until(() => !_doc.Q("#im").HasAttribute("src"), StepTimeout, "the image to be cleared");
            }
            finally
            {
                UnityEngine.Object.Destroy(tex);
            }
        }

        [UnityTest]
        public IEnumerator FocusFlagsTrackTheFocusedField()
        {
            RequirePreview();
            NewDocument("<input id='f' type='text'><button id='b'>x</button>");
            yield return Ready();

            _doc.Q("#f").Focus();
            yield return Until(() => _doc.TextInputFocused, StepTimeout, "the text field to take focus");
            Assert.IsTrue(HtmlRuntime.TextInputFocused);
            Assert.AreSame(_doc, HtmlRuntime.FocusedDocument);

            _doc.Q("#b").Focus();
            yield return Until(() => _doc.HasFocus && !_doc.TextInputFocused, StepTimeout, "focus to move to the button");

            _doc.Q("#b").Blur();
            yield return Until(() => !_doc.HasFocus, StepTimeout, "focus to leave the document");
            Assert.IsFalse(HtmlRuntime.HasFocus);
        }
    }
}
