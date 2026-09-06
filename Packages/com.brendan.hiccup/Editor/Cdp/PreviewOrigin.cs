using System;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace Hiccup.Editor.Cdp
{
    /// <summary>
    /// A loopback HTTP server that serves one empty page, so every preview document has a real http origin.
    /// An about:blank page has no origin and sends no Referer, which some embedded content refuses outright
    /// (YouTube's player answers "Error 153"); an origin also gives storage, cookies and postMessage the shape
    /// they have in a build. Nothing but that page is ever served, and only on 127.0.0.1.
    /// </summary>
    internal sealed class PreviewOrigin : IDisposable
    {
        private static readonly byte[] Page = Encoding.UTF8.GetBytes(
            "<!doctype html><html><head><meta charset=\"utf-8\"><title>Hiccup preview</title></head><body></body></html>");

        private readonly TcpListener _listener;
        private readonly CancellationTokenSource _cancel = new CancellationTokenSource();

        /// <summary>The page URL, e.g. <c>http://127.0.0.1:51234/</c>.</summary>
        public string Url { get; }

        public PreviewOrigin()
        {
            _listener = new TcpListener(IPAddress.Loopback, 0);
            _listener.Start();
            Url = "http://127.0.0.1:" + ((IPEndPoint)_listener.LocalEndpoint).Port + "/";
            _ = AcceptLoopAsync();
        }

        private async Task AcceptLoopAsync()
        {
            while (!_cancel.IsCancellationRequested)
            {
                TcpClient client;
                try { client = await _listener.AcceptTcpClientAsync().ConfigureAwait(false); }
                catch (ObjectDisposedException) { return; }
                catch (SocketException)
                {
                    if (_cancel.IsCancellationRequested)
                        return;
                    continue;
                }
                _ = ServeAsync(client);
            }
        }

        private static async Task ServeAsync(TcpClient client)
        {
            using (client)
            {
                try
                {
                    client.NoDelay = true;
                    var stream = client.GetStream();
                    var buf = new byte[4096];
                    int total = 0;
                    while (total < buf.Length)
                    {
                        int n = await stream.ReadAsync(buf, total, buf.Length - total).ConfigureAwait(false);
                        if (n <= 0)
                            break;
                        total += n;
                        if (EndOfHeaders(buf, total))
                            break;
                    }
                    bool favicon = Encoding.ASCII.GetString(buf, 0, Math.Min(total, 32)).StartsWith("GET /favicon.ico", StringComparison.Ordinal);
                    var body = favicon ? Array.Empty<byte>() : Page;
                    var head = Encoding.ASCII.GetBytes(
                        (favicon ? "HTTP/1.1 404 Not Found\r\n" : "HTTP/1.1 200 OK\r\n") +
                        "Content-Type: text/html; charset=utf-8\r\n" +
                        "Content-Length: " + body.Length + "\r\n" +
                        "Cache-Control: no-store\r\n" +
                        "Connection: close\r\n\r\n");
                    await stream.WriteAsync(head, 0, head.Length).ConfigureAwait(false);
                    if (body.Length > 0)
                        await stream.WriteAsync(body, 0, body.Length).ConfigureAwait(false);
                    await stream.FlushAsync().ConfigureAwait(false);
                }
                catch (Exception) { /* a dropped connection; Chrome simply retries */ }
            }
        }

        private static bool EndOfHeaders(byte[] b, int len)
        {
            for (int i = 3; i < len; i++)
            {
                if (b[i - 3] == (byte)'\r' && b[i - 2] == (byte)'\n' && b[i - 1] == (byte)'\r' && b[i] == (byte)'\n')
                    return true;
            }
            return false;
        }

        public void Dispose()
        {
            _cancel.Cancel();
            try { _listener.Stop(); } catch { }
        }
    }
}
