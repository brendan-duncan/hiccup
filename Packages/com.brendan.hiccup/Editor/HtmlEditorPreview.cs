using UnityEditor;
using UnityEngine;
using Hiccup.Editor.Cdp;

namespace Hiccup.Editor
{
    /// <summary>
    /// Owns the Editor's Chrome-backed preview: registers the backend for play mode and makes sure the
    /// browser process dies with the play session, a domain reload, or the Editor itself.
    /// </summary>
    [InitializeOnLoad]
    internal static class HtmlEditorPreview
    {
        private const string EnabledKey = "Hiccup.Preview.Enabled";
        private const string HeadlessKey = "Hiccup.Preview.Headless";
        private const string DebugKey = "Hiccup.Preview.Debug";
        private const string FlipKey = "Hiccup.Preview.FlipY";

        private const string MenuRoot = "Window/Hiccup/";
        private const string MenuEnabled = MenuRoot + "Editor Preview (Chrome)";
        private const string MenuHeadless = MenuRoot + "Run Chrome Headless";
        private const string MenuDebug = MenuRoot + "Log Browser Console";
        private const string MenuFlip = MenuRoot + "Flip Preview Vertically";
        private const string MenuRestart = MenuRoot + "Restart Preview";

        private static CdpHtmlBackend s_backend;

        public static bool Enabled
        {
            get => EditorPrefs.GetBool(EnabledKey, true);
            set => EditorPrefs.SetBool(EnabledKey, value);
        }

        public static bool Headless
        {
            get => EditorPrefs.GetBool(HeadlessKey, true);
            set => EditorPrefs.SetBool(HeadlessKey, value);
        }

        public static bool DebugLogging
        {
            get => EditorPrefs.GetBool(DebugKey, false);
            set => EditorPrefs.SetBool(DebugKey, value);
        }

        /// <summary>
        /// Whether the preview flips frames vertically. The default is right for the usual case; the toggle
        /// exists because the correct answer depends on the graphics API's texture origin.
        /// </summary>
        public static bool FlipY
        {
            get => EditorPrefs.GetBool(FlipKey, true);
            set => EditorPrefs.SetBool(FlipKey, value);
        }

        /// <summary>One-line description of the preview for inspectors.</summary>
        public static string Status =>
            !Enabled ? "disabled" :
            s_backend == null ? "not running" :
            s_backend.Status;

        static HtmlEditorPreview()
        {
            // Any of these would otherwise leave an orphaned Chrome process behind.
            AssemblyReloadEvents.beforeAssemblyReload += Stop;
            EditorApplication.quitting += Stop;
            EditorApplication.playModeStateChanged += change =>
            {
                if (change == PlayModeStateChange.ExitingPlayMode)
                    Stop();
            };
        }

        /// <summary>
        /// Runs before the first scene loads in play mode, which is early enough for documents to find a
        /// backend in their own OnEnable.
        /// </summary>
        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.BeforeSceneLoad)]
        private static void StartForPlayMode()
        {
            if (Enabled)
                Start();
        }

        public static void Start()
        {
            if (s_backend != null)
                return;
            if (ChromeLauncher.FindChrome() == null)
            {
                Debug.LogWarning("[Hiccup] Editor preview needs Chrome. Install it, or point HICCUP_CHROME at an executable.");
                return;
            }
            s_backend = new CdpHtmlBackend(Headless, DebugLogging);
            HtmlBackend.Register(s_backend);
        }

        public static void Stop()
        {
            if (s_backend == null)
                return;
            HtmlBackend.Unregister(s_backend);
            s_backend = null;
        }

        // ------------------------------------------------------------------ menu

        [MenuItem(MenuEnabled, priority = 100)]
        private static void ToggleEnabled()
        {
            Enabled = !Enabled;
            if (!Enabled)
                Stop();
            else if (EditorApplication.isPlaying)
                Start();
        }

        [MenuItem(MenuEnabled, true)]
        private static bool ToggleEnabledValidate()
        {
            Menu.SetChecked(MenuEnabled, Enabled);
            return true;
        }

        [MenuItem(MenuHeadless, priority = 101)]
        private static void ToggleHeadless()
        {
            Headless = !Headless;
            Restart();
        }

        [MenuItem(MenuHeadless, true)]
        private static bool ToggleHeadlessValidate()
        {
            Menu.SetChecked(MenuHeadless, Headless);
            return true;
        }

        [MenuItem(MenuDebug, priority = 102)]
        private static void ToggleDebug()
        {
            DebugLogging = !DebugLogging;
            Restart();
        }

        [MenuItem(MenuDebug, true)]
        private static bool ToggleDebugValidate()
        {
            Menu.SetChecked(MenuDebug, DebugLogging);
            return true;
        }

        [MenuItem(MenuFlip, priority = 103)]
        private static void ToggleFlip() => FlipY = !FlipY;

        [MenuItem(MenuFlip, true)]
        private static bool ToggleFlipValidate()
        {
            Menu.SetChecked(MenuFlip, FlipY);
            return true;
        }

        [MenuItem(MenuRestart, priority = 120)]
        private static void Restart()
        {
            bool wasRunning = s_backend != null;
            Stop();
            if (wasRunning && Enabled && EditorApplication.isPlaying)
                Start();
        }
    }
}
