using System;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;

namespace Hiccup.Editor.Cdp
{
    /// <summary>Locates and starts a Chrome with a DevTools endpoint, in a throwaway profile.</summary>
    internal sealed class ChromeLauncher : IDisposable
    {
        private Process _process;
        private string _profileDir;

        /// <summary>Browser-level DevTools web socket, valid once <see cref="LaunchAsync"/> returns.</summary>
        public string BrowserWebSocketUrl { get; private set; }
        /// <summary>The DevTools HTTP/WebSocket port Chrome was started on.</summary>
        public int DevToolsPort { get; private set; }

        /// <summary>Path Chrome was started from.</summary>
        public string ExecutablePath { get; private set; }

        public bool IsRunning
        {
            get
            {
                try { return _process != null && !_process.HasExited; }
                catch { return false; }
            }
        }

        /// <summary>
        /// Finds a Chrome executable: <c>HICCUP_CHROME</c> or <c>CHROME_PATH</c> first, then the usual install
        /// locations for the platform. Returns null when none exists.
        /// </summary>
        public static string FindChrome()
        {
            foreach (var variable in new[] { "HICCUP_CHROME", "CHROME_PATH" })
            {
                var fromEnv = Environment.GetEnvironmentVariable(variable);
                if (!string.IsNullOrEmpty(fromEnv) && File.Exists(fromEnv))
                    return fromEnv;
            }

            string[] candidates;
#if UNITY_EDITOR_WIN
            var programFiles = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
            var programFilesX86 = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86);
            var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            candidates = new[]
            {
                Path.Combine(programFiles, @"Google\Chrome\Application\chrome.exe"),
                Path.Combine(programFilesX86, @"Google\Chrome\Application\chrome.exe"),
                Path.Combine(localAppData, @"Google\Chrome\Application\chrome.exe"),
                Path.Combine(programFiles, @"Google\Chrome Beta\Application\chrome.exe"),
                Path.Combine(programFiles, @"Google\Chrome Dev\Application\chrome.exe"),
                Path.Combine(programFiles, @"Google\Chrome SxS\Application\chrome.exe"),
                Path.Combine(localAppData, @"Google\Chrome SxS\Application\chrome.exe"),
            };
#elif UNITY_EDITOR_OSX
            candidates = new[]
            {
                "/Applications/Google Chrome.app/Contents/MacOS/Google Chrome",
                "/Applications/Google Chrome Canary.app/Contents/MacOS/Google Chrome Canary",
                "/Applications/Chromium.app/Contents/MacOS/Chromium",
            };
#else
            candidates = new[]
            {
                "/usr/bin/google-chrome",
                "/usr/bin/google-chrome-stable",
                "/usr/bin/google-chrome-unstable",
                "/usr/bin/chromium",
                "/usr/bin/chromium-browser",
                "/snap/bin/chromium",
            };
#endif
            foreach (var path in candidates)
            {
                if (!string.IsNullOrEmpty(path) && File.Exists(path))
                    return path;
            }
            return null;
        }

        /// <summary>
        /// Starts Chrome and resolves its DevTools endpoint. <paramref name="headless"/> uses the modern headless
        /// mode, which is a full browser and still supports screencasting; turn it off to watch the page directly.
        /// </summary>
        public async Task LaunchAsync(bool headless, bool debugLogging, CancellationToken ct)
        {
            ExecutablePath = FindChrome();
            if (ExecutablePath == null)
                throw new FileNotFoundException("No Chrome installation found. Set the HICCUP_CHROME environment variable to a chrome executable.");

            _profileDir = Path.Combine(Path.GetTempPath(), "hiccup-cdp-" + Guid.NewGuid().ToString("N").Substring(0, 8));
            Directory.CreateDirectory(_profileDir);

            // The port is chosen here rather than by Chrome (port 0) so it can be named in --remote-allow-origins:
            // since Chrome 111 the DevTools front end that Chrome serves on this port may only open a socket to it
            // from an allowed origin, and that front end is what the "Open in Chrome DevTools" button loads.
            // Allowing exactly this origin keeps other local pages from reaching the debugging port.
            DevToolsPort = FreePort();
            var args = new System.Text.StringBuilder();
            args.Append("--remote-debugging-port=").Append(DevToolsPort);
            args.Append(" --remote-allow-origins=http://127.0.0.1:").Append(DevToolsPort).Append(",http://localhost:").Append(DevToolsPort);
            args.Append(" --user-data-dir=\"").Append(_profileDir).Append('"');
            args.Append(" --no-first-run --no-default-browser-check --no-service-autorun");
            args.Append(" --disable-extensions --disable-background-networking --disable-sync");
            args.Append(" --disable-component-update --disable-default-apps --metrics-recording-only");
            args.Append(" --mute-audio --hide-scrollbars");
            // Keep renderers alive and painting even when the window is not the foreground one.
            args.Append(" --disable-backgrounding-occluded-windows --disable-renderer-backgrounding");
            args.Append(" --disable-background-timer-throttling");
            // The preview itself paints through the screencast, but pages that use HTML-in-Canvas on their own
            // need the API in this Chrome too. Equivalent to chrome://flags/#canvas-draw-element; unknown feature names
            // are ignored by older builds.
            args.Append(" --enable-features=CanvasDrawElement --enable-blink-features=CanvasDrawElement");
            if (headless)
                args.Append(" --headless=new");
            else
                args.Append(" --window-position=-32000,-32000 --window-size=100,100");
            args.Append(" about:blank");

            var info = new ProcessStartInfo(ExecutablePath, args.ToString())
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardError = true,
                RedirectStandardOutput = true,
            };

            _process = Process.Start(info) ?? throw new Exception("Chrome failed to start.");
            // Draining the pipes keeps Chrome from blocking on a full stderr buffer.
            _process.ErrorDataReceived += (_, e) =>
            {
                if (debugLogging && !string.IsNullOrEmpty(e.Data))
                    UnityEngine.Debug.Log("[Hiccup/chrome] " + e.Data);
            };
            _process.OutputDataReceived += (_, __) => { };
            _process.BeginErrorReadLine();
            _process.BeginOutputReadLine();

            BrowserWebSocketUrl = await ReadDevToolsEndpointAsync(_profileDir, ct).ConfigureAwait(false);
        }

        /// <summary>A port nothing is listening on right now. Chrome binds it moments later; a collision is very unlikely.</summary>
        private static int FreePort()
        {
            var listener = new TcpListener(IPAddress.Loopback, 0);
            listener.Start();
            try { return ((IPEndPoint)listener.LocalEndpoint).Port; }
            finally { listener.Stop(); }
        }

        /// <summary>The DevTools front end for a page target, served by this Chrome. Opens in any Chromium browser.</summary>
        public string DevToolsUrl(string targetId)
        {
            if (DevToolsPort == 0 || string.IsNullOrEmpty(targetId))
                return null;
            return $"http://127.0.0.1:{DevToolsPort}/devtools/inspector.html?ws=127.0.0.1:{DevToolsPort}/devtools/page/{targetId}";
        }

        private async Task<string> ReadDevToolsEndpointAsync(string profileDir, CancellationToken ct)
        {
            var portFile = Path.Combine(profileDir, "DevToolsActivePort");
            var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(20);

            while (DateTime.UtcNow < deadline)
            {
                ct.ThrowIfCancellationRequested();
                if (!IsRunning)
                    throw new Exception("Chrome exited before it published a DevTools port.");

                if (File.Exists(portFile))
                {
                    try
                    {
                        // Two lines: the port, then the browser-level web socket path.
                        using (var stream = new FileStream(portFile, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
                        {
                            using (var reader = new StreamReader(stream))
                            {
                                var port = (await reader.ReadLineAsync().ConfigureAwait(false))?.Trim();
                                var path = (await reader.ReadLineAsync().ConfigureAwait(false))?.Trim();
                                if (!string.IsNullOrEmpty(port) && !string.IsNullOrEmpty(path))
                                    return $"ws://127.0.0.1:{port}{path}";
                            }
                        }
                    }
                    catch (IOException)
                    {
                        // Chrome is still writing the file; try again.
                    }
                }
                await Task.Delay(50, ct).ConfigureAwait(false);
            }
            throw new TimeoutException("Timed out waiting for Chrome to publish its DevTools port.");
        }

        public void Dispose()
        {
            try
            {
                if (IsRunning)
                {
                    _process.Kill();
                    _process.WaitForExit(3000);
                }
            }
            catch { /* the process may already be gone */ }

            try { _process?.Dispose(); } catch { }
            _process = null;

            if (!string.IsNullOrEmpty(_profileDir))
            {
                try { Directory.Delete(_profileDir, true); }
                catch { /* Chrome sometimes still holds a lock; the temp directory will be cleaned up by the OS */ }
                _profileDir = null;
            }
        }
    }
}
