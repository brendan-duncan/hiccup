using System.Collections.Generic;
using UnityEngine;

namespace Hiccup
{
    /// <summary>
    /// An alternative implementation of the browser bridge, used where <c>Hiccup.jslib</c> cannot run.
    /// The Editor registers one that drives a real Chrome over the DevTools Protocol
    /// (see <c>Hiccup.Editor.CdpHtmlBackend</c>), so documents render and respond in the Game view.
    /// </summary>
    /// <remarks>
    /// Only the panel-level surface is modeled. Element queries and the DOM mutation API return empty
    /// results on a backend that does not implement them, which leaves <see cref="HtmlElement.None"/>
    /// behavior intact rather than throwing.
    /// </remarks>
    public interface IHtmlBackend
    {
        /// <summary>Returns the <see cref="HtmlRenderMode"/> the backend runs in, as an int.</summary>
        int Init(int backend, int linear, int forceOverlay, int debug);
        void Shutdown();

        /// <summary>Fills width, height, devicePixelRatio, pixelWidth, pixelHeight.</summary>
        void GetCanvasInfo(float[] outInfo);
        /// <summary>Pumps the transport and refreshes textures. Called once per frame from <see cref="HtmlRuntime"/>.</summary>
        void Update();

        int PanelCreate(int width, int height);
        void PanelDestroy(int panel);
        /// <summary>
        /// True once the panel's page exists and element reads and writes reach it. A backend that brings its page
        /// up asynchronously returns false until then; <see cref="HtmlDocument"/> holds <c>IsCreated</c> and the
        /// <c>Created</c> event back until this flips, so user code sees the same contract as in a web build.
        /// Return true for a panel that will never become ready (the backend failed) so documents still wire up.
        /// </summary>
        bool PanelIsReady(int panel);
        void PanelSetHtml(int panel, string html);
        void PanelSetCss(int panel, string css);
        void PanelSetSize(int panel, int width, int height);
        void PanelSetVisible(int panel, bool visible);
        void PanelSetResolutionScale(int panel, float scale);
        /// <summary>Whether the panel texture carries a mip chain that is regenerated after each update.</summary>
        void PanelSetMipmaps(int panel, bool enabled);
        void PanelListen(int panel, string eventType, bool enabled);
        void PanelInvalidate(int panel);
        void PanelSetGeometry(int panel, float[] pixelToClipColumnMajor);
        void PanelAnnounce(int panel, string text, bool assertive);
        /// <summary>
        /// Stores encoded image bytes under a name and binds them to the panel's <c>[data-hui-image=name]</c>
        /// elements and <c>--hui-image-name</c> custom property. A null or empty <paramref name="data"/> removes it.
        /// </summary>
        void PanelSetImage(int panel, string name, byte[] data, int length, string mime);
        string PanelEval(int panel, string javascript);
        /// <summary>
        /// Runs <paramref name="javascript"/> as an async function body and reports the outcome through
        /// <see cref="HtmlBackend.CompleteEval"/> with the same <paramref name="requestId"/>, never synchronously
        /// from inside this call.
        /// </summary>
        void PanelEvalAsync(int panel, string javascript, int requestId);

        /// <summary>Texture holding the rendered panel, or null until the first frame arrives. Owned by the backend.</summary>
        Texture PanelGetTexture(int panel);
        void PanelGetTextureSize(int panel, int[] outWidthHeight);

        // ---- Elements. Handles are opaque ints; 0 means "no element" and every call on it is a no-op.

        int Query(int panel, string selector);
        /// <summary>Comma-separated element handles, matching the jslib's Hiccup_QueryAll encoding.</summary>
        string QueryAll(int panel, string selector);
        int ElemQuery(int handle, string selector);
        /// <summary>Comma-separated handles of the matching descendants.</summary>
        string ElemQueryAll(int handle, string selector);
        /// <summary>The element or its nearest matching ancestor inside the content root, else 0.</summary>
        int ElemClosest(int handle, string selector);
        int ElemParent(int handle);
        void ElemRelease(int handle);

        string ElemEnsureId(int handle);
        string ElemGetText(int handle);
        void ElemSetText(int handle, string value);
        string ElemGetHtml(int handle);
        void ElemSetHtml(int handle, string value);
        void ElemInsertHtml(int handle, string where, string html);

        string ElemGetAttr(int handle, string name);
        bool ElemHasAttr(int handle, string name);
        void ElemSetAttr(int handle, string name, string value);
        void ElemRemoveAttr(int handle, string name);

        string ElemGetProp(int handle, string name);
        void ElemSetProp(int handle, string name, string value);
        bool ElemGetBoolProp(int handle, string name);
        void ElemSetBoolProp(int handle, string name, bool value);

        void ElemSetStyle(int handle, string name, string value);
        string ElemGetStyle(int handle, string name);
        void ElemAddClass(int handle, string className);
        void ElemRemoveClass(int handle, string className);
        /// <summary><paramref name="force"/> is 1 to add, 0 to remove, -1 to toggle.</summary>
        void ElemToggleClass(int handle, string className, int force);
        bool ElemHasClass(int handle, string className);

        void ElemFocus(int handle);
        void ElemBlur(int handle);
        void ElemClick(int handle);
        void ElemRemove(int handle);
        void ElemShowModal(int handle, bool show);
        void ElemScrollIntoView(int handle);
        /// <summary>Calls a zero-argument DOM method on the element, if it has one.</summary>
        void ElemCall(int handle, string method);

        /// <summary>Fills x, y, width, height in panel CSS pixels.</summary>
        void ElemGetBounds(int handle, float[] outXYWH);
        bool ElemMatches(int handle, string selector);
    }

    /// <summary>
    /// Registry for the active <see cref="IHtmlBackend"/>. Null in player builds, where
    /// <see cref="HtmlNative"/> talks to <c>Hiccup.jslib</c> directly.
    /// </summary>
    public static class HtmlBackend
    {
        private static IHtmlBackend s_current;

        /// <summary>The backend driving documents, or null when there is none (player builds, or no Editor preview).</summary>
        public static IHtmlBackend Current => s_current;

        /// <summary>Raised after <see cref="Current"/> changes so live documents can rebuild themselves.</summary>
        public static event System.Action Changed;

        public static void Register(IHtmlBackend backend)
        {
            if (ReferenceEquals(s_current, backend))
                return;
            s_current?.Shutdown();
            s_current = backend;
            Changed?.Invoke();
        }

        public static void Unregister(IHtmlBackend backend)
        {
            if (!ReferenceEquals(s_current, backend))
                return;
            s_current?.Shutdown();
            s_current = null;
            Changed?.Invoke();
        }

        /// <summary>Delivers a DOM event payload from the backend to the document that owns the panel.</summary>
        public static void DispatchEvent(int panel, string json) => HtmlRuntime.DispatchToPanel(panel, json);

        /// <summary>Delivers a page message (<c>HUI.send(name, payload)</c>) to the document that owns the panel.</summary>
        public static void DispatchMessage(int panel, string name, string data) => HtmlRuntime.DispatchMessageToPanel(panel, name, data);

        /// <summary>
        /// Completes an <see cref="IHtmlBackend.PanelEvalAsync"/> request. <paramref name="error"/> is null on success;
        /// otherwise it is the message the returned task faults with.
        /// </summary>
        public static void CompleteEval(int panel, int requestId, string result, string error) => HtmlRuntime.CompleteEvalOnPanel(panel, requestId, result, error);

        // ---- keyboard capture, for backends that relay the Game view's keys to a browser

        private static HtmlKeyboardRelay s_keyboard;

        /// <summary>
        /// Starts or stops collecting the Game view's key presses. Keys come from IMGUI, so this works under either
        /// input backend; the relay component is added to the runtime's own driver object and removed again on
        /// stop. Read presses back with <see cref="DrainKeyPresses"/>.
        /// </summary>
        public static void SetKeyboardCapture(bool enabled)
        {
            if (enabled)
            {
                if (s_keyboard != null || !HtmlRuntime.HasInstance)
                    return;
                s_keyboard = HtmlRuntime.Instance.gameObject.AddComponent<HtmlKeyboardRelay>();
            }
            else if (s_keyboard != null)
            {
                if (Application.isPlaying)
                    UnityEngine.Object.Destroy(s_keyboard);
                else
                    UnityEngine.Object.DestroyImmediate(s_keyboard);
                s_keyboard = null;
            }
        }

        /// <summary>Appends the key presses seen since the last call to <paramref name="into"/>. Nothing unless capture is on.</summary>
        public static void DrainKeyPresses(List<HtmlKeyPress> into)
        {
            if (s_keyboard == null || into == null)
                return;
            into.AddRange(s_keyboard.Pending);
            s_keyboard.Pending.Clear();
        }
    }
}
