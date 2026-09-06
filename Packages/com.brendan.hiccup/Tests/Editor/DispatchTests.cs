using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;

namespace Hiccup.Tests
{
    /// <summary>
    /// <see cref="HtmlDocument.Dispatch"/> and <see cref="HtmlDocument.DispatchMessage(HtmlMessage)"/> need no
    /// browser: a document that was never created still routes events through its handlers.
    /// </summary>
    public class DispatchTests
    {
        private GameObject _go;
        private HtmlDocument _doc;
        private List<string> _log;

        [SetUp]
        public void SetUp()
        {
            _go = new GameObject("Dispatch test document");
            _doc = _go.AddComponent<HtmlDocument>();
            _log = new List<string>();
        }

        [TearDown]
        public void TearDown() => UnityEngine.Object.DestroyImmediate(_go);

        private static HtmlEvent Click(string id, string path = "", string action = "")
            => new HtmlEvent { type = "click", id = id, path = path, action = action };

        [Test]
        public void HandlersRunInTheDocumentedOrder()
        {
            _doc.EventReceived += e => _log.Add("received");
            _doc.On("click", e => _log.Add("type"));
            _doc.OnAction("go", e => _log.Add("action"));
            _doc.On("grandparent", "click", e => _log.Add("grandparent"));
            _doc.On("parent", "click", e => _log.Add("parent"));
            _doc.On("target", "click", e => _log.Add("target"));

            _doc.Dispatch(Click("target", path: "parent grandparent", action: "go"));

            CollectionAssert.AreEqual(new[] { "received", "target", "parent", "grandparent", "action", "type" }, _log);
        }

        [Test]
        public void HandledStopsEveryLaterHandler()
        {
            _doc.On("target", "click", e => { _log.Add("target"); e.Handled = true; });
            _doc.On("parent", "click", e => _log.Add("parent"));
            _doc.OnAction("go", e => _log.Add("action"));
            _doc.On("click", e => _log.Add("type"));

            _doc.Dispatch(Click("target", path: "parent", action: "go"));

            CollectionAssert.AreEqual(new[] { "target" }, _log);
        }

        [Test]
        public void ActionHandlersOnlySeeClicks()
        {
            _doc.OnAction("go", e => _log.Add("action"));
            _doc.Dispatch(new HtmlEvent { type = "keydown", id = "x", action = "go" });
            _doc.Dispatch(Click("x", action: "go"));
            CollectionAssert.AreEqual(new[] { "action" }, _log);
        }

        [Test]
        public void OffRemovesAHandler()
        {
            Action<HtmlEvent> h = e => _log.Add("type");
            _doc.On("click", h);
            _doc.Off("click", h);
            _doc.Dispatch(Click("x"));
            Assert.IsEmpty(_log);
        }

        [Test]
        public void AThrowingHandlerIsLoggedAndTheRestStillRun()
        {
            _doc.On("click", e => throw new InvalidOperationException("boom"));
            _doc.On("click", e => _log.Add("second"));
            LogAssert.Expect(LogType.Exception, new Regex("boom"));

            _doc.Dispatch(Click("x"));

            CollectionAssert.AreEqual(new[] { "second" }, _log);
        }

        [Serializable]
        private class Payload
        {
            public int n;
            public string s;
        }

        [Test]
        public void MessagesRouteByNameWithTypedPayloads()
        {
            HtmlMessage got = null;
            _doc.MessageReceived += m => _log.Add("received:" + m.Name);
            _doc.OnMessage("ping", m => got = m);
            _doc.OnMessage("other", m => _log.Add("wrong"));

            _doc.DispatchMessage("ping", "{\"n\":2,\"s\":\"x\"}");

            Assert.IsNotNull(got);
            Assert.AreSame(_doc, got.Document);
            Assert.AreEqual(2, got.DataAs<Payload>().n);
            Assert.AreEqual("x", got.DataAs<Payload>().s);
            CollectionAssert.AreEqual(new[] { "received:ping" }, _log);
        }

        [Test]
        public void MessageDataConvertersAndHandled()
        {
            Assert.AreEqual(2.5f, new HtmlMessage(_doc, "m", "2.5").DataAsFloat);
            Assert.AreEqual(3, new HtmlMessage(_doc, "m", "2.6").DataAsInt);
            Assert.IsTrue(new HtmlMessage(_doc, "m", "true").DataAsBool);
            Assert.IsFalse(new HtmlMessage(_doc, "m", "0").DataAsBool);

            _doc.MessageReceived += m => m.Handled = true;
            _doc.OnMessage("m", m => _log.Add("handler"));
            _doc.DispatchMessage("m", "");
            Assert.IsEmpty(_log, "a message marked Handled in MessageReceived reaches no named handler");
        }

        [Test]
        public void FocusFlagsFollowFocusEvents()
        {
            Assert.IsFalse(_doc.HasFocus);
            Assert.IsFalse(HtmlRuntime.HasFocus);

            _doc.Dispatch(new HtmlEvent { type = "focusin", id = "name", editable = true });
            Assert.IsTrue(_doc.HasFocus);
            Assert.IsTrue(_doc.TextInputFocused);
            Assert.AreSame(_doc, HtmlRuntime.FocusedDocument);
            Assert.IsTrue(HtmlRuntime.TextInputFocused);

            _doc.Dispatch(new HtmlEvent { type = "focusout", id = "name" });
            _doc.Dispatch(new HtmlEvent { type = "focusin", id = "button", editable = false });
            Assert.IsTrue(_doc.HasFocus);
            Assert.IsFalse(_doc.TextInputFocused, "a button has focus but takes no text");
            Assert.IsFalse(HtmlRuntime.TextInputFocused);

            _doc.Dispatch(new HtmlEvent { type = "focusout", id = "button" });
            Assert.IsFalse(_doc.HasFocus);
            Assert.IsNull(HtmlRuntime.FocusedDocument);
        }
    }
}
