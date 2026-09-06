using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Net.WebSockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace Hiccup.Editor.Cdp
{
    /// <summary>
    /// A DevTools Protocol connection: JSON-RPC over a single web socket, with flat sessions
    /// (every target attaches onto this one socket and is addressed by <c>sessionId</c>).
    /// </summary>
    /// <remarks>
    /// Sends may come from the Unity main thread; receiving runs on a background task. Command replies
    /// complete their <see cref="Task"/> wherever the receive loop is running, so callers must not block
    /// the main thread on them. Protocol events are queued and drained by the owner in
    /// <see cref="TryDequeueEvent"/> so they are handled on the main thread.
    /// </remarks>
    internal sealed class CdpClient : IDisposable
    {
        private readonly ClientWebSocket _socket = new ClientWebSocket();
        private readonly CancellationTokenSource _cancel = new CancellationTokenSource();
        private readonly SemaphoreSlim _sendLock = new SemaphoreSlim(1, 1);
        private readonly ConcurrentDictionary<int, TaskCompletionSource<Dictionary<string, object>>> _pending
            = new ConcurrentDictionary<int, TaskCompletionSource<Dictionary<string, object>>>();
        private readonly ConcurrentQueue<Dictionary<string, object>> _events
            = new ConcurrentQueue<Dictionary<string, object>>();

        private int _nextId;
        private Task _receiveLoop;
        private volatile bool _closed;

        private static readonly byte[] ScreencastMethod = Encoding.ASCII.GetBytes("\"method\":\"Page.screencastFrame\"");
        private static readonly byte[] DataKey = Encoding.ASCII.GetBytes("\"data\":\"");

        /// <summary>Receives a screencast frame: the target's session id, the frame id to acknowledge, and the encoded image.</summary>
        public delegate void ScreencastFrameCallback(string sessionId, int frameId, byte[] image);

        /// <summary>
        /// Called on the receive thread for every <c>Page.screencastFrame</c>, instead of queueing it as an event.
        /// The image is already base64-decoded. When unset, the frame is queued like any other event, with
        /// <c>params.data</c> holding the decoded <c>byte[]</c> rather than a string.
        /// </summary>
        public ScreencastFrameCallback ScreencastFrameHandler { get; set; }

        /// <summary>Set when the receive loop stops unexpectedly.</summary>
        public Exception Fault { get; private set; }
        public bool IsOpen => !_closed && _socket.State == WebSocketState.Open;

        public static async Task<CdpClient> ConnectAsync(string webSocketUrl, CancellationToken ct)
        {
            var client = new CdpClient();
            await client._socket.ConnectAsync(new Uri(webSocketUrl), ct).ConfigureAwait(false);
            client._receiveLoop = Task.Run(client.ReceiveLoopAsync);
            return client;
        }

        // ------------------------------------------------------------------ sending

        /// <summary>Sends a command and awaits its reply. <paramref name="paramsJson"/> is a raw JSON object body.</summary>
        public Task<Dictionary<string, object>> SendAsync(string method, string paramsJson = null, string sessionId = null)
        {
            int id = Interlocked.Increment(ref _nextId);
            var tcs = new TaskCompletionSource<Dictionary<string, object>>(TaskCreationOptions.RunContinuationsAsynchronously);
            _pending[id] = tcs;

            var frame = BuildFrame(id, method, paramsJson, sessionId);
            _ = SendRawAsync(frame).ContinueWith(t =>
            {
                if (t.IsFaulted && _pending.TryRemove(id, out var pending))
                    pending.TrySetException(t.Exception ?? new Exception("send failed"));
            }, TaskScheduler.Default);

            return tcs.Task;
        }

        /// <summary>Sends a command without waiting for the reply. Failures surface through <see cref="Fault"/>.</summary>
        public void Send(string method, string paramsJson = null, string sessionId = null)
        {
            int id = Interlocked.Increment(ref _nextId);
            _ = SendRawAsync(BuildFrame(id, method, paramsJson, sessionId));
        }

        private static string BuildFrame(int id, string method, string paramsJson, string sessionId)
        {
            var sb = new StringBuilder(128 + (paramsJson?.Length ?? 0));
            sb.Append("{\"id\":").Append(id).Append(",\"method\":");
            Json.Quote(method, sb);
            if (!string.IsNullOrEmpty(paramsJson))
                sb.Append(",\"params\":").Append(paramsJson);
            if (!string.IsNullOrEmpty(sessionId))
            {
                sb.Append(",\"sessionId\":");
                Json.Quote(sessionId, sb);
            }
            sb.Append('}');
            return sb.ToString();
        }

        private async Task SendRawAsync(string text)
        {
            if (_closed)
                return;
            var bytes = Encoding.UTF8.GetBytes(text);
            try { await _sendLock.WaitAsync(_cancel.Token).ConfigureAwait(false); }
            catch (OperationCanceledException) { return; }

            try
            {
                if (_socket.State != WebSocketState.Open)
                    return;
                await _socket.SendAsync(new ArraySegment<byte>(bytes), WebSocketMessageType.Text, true, _cancel.Token)
                    .ConfigureAwait(false);
            }
            catch (Exception e) when (!(e is OperationCanceledException))
            {
                Fault ??= e;
            }
            finally
            {
                _sendLock.Release();
            }
        }

        // ------------------------------------------------------------------ receiving

        private async Task ReceiveLoopAsync()
        {
            // Screencast frames arrive base64-encoded, so single messages routinely run to hundreds of KB.
            var buffer = new byte[64 * 1024];
            var message = new MemoryStream(256 * 1024);

            try
            {
                while (!_cancel.IsCancellationRequested && _socket.State == WebSocketState.Open)
                {
                    message.SetLength(0);
                    WebSocketReceiveResult result;
                    do
                    {
                        result = await _socket.ReceiveAsync(new ArraySegment<byte>(buffer), _cancel.Token).ConfigureAwait(false);
                        if (result.MessageType == WebSocketMessageType.Close)
                            return;
                        message.Write(buffer, 0, result.Count);
                    }
                    while (!result.EndOfMessage);

                    if (TryDispatchScreencastFrame(message.GetBuffer(), (int)message.Length))
                        continue;
                    var text = Encoding.UTF8.GetString(message.GetBuffer(), 0, (int)message.Length);
                    Dispatch(text);
                }
            }
            catch (OperationCanceledException) { }
            catch (Exception e)
            {
                Fault ??= e;
            }
            finally
            {
                FailPending(Fault ?? new Exception("DevTools connection closed"));
            }
        }

        private void Dispatch(string text)
        {
            if (!(Json.Parse(text) is Dictionary<string, object> msg))
                return;

            if (msg.TryGetValue("id", out var rawId) && rawId is double idNum)
            {
                if (_pending.TryRemove((int)idNum, out var tcs))
                {
                    var error = Json.Dict(msg, "error");
                    if (error != null)
                        tcs.TrySetException(new CdpException(Json.Str(error, "message", "CDP error")));
                    else
                        tcs.TrySetResult(Json.Dict(msg, "result") ?? new Dictionary<string, object>());
                }
                return;
            }

            if (msg.ContainsKey("method"))
                _events.Enqueue(msg);
        }

        /// <summary>
        /// Screencast frames are almost entirely one base64 string, and pushing that through the generic path
        /// means a multi-megabyte string, a character-by-character copy of it in the parser, and a third copy for
        /// the decode. This recognizes the message in its raw bytes, decodes the payload straight from them, and
        /// parses only what is left.
        /// </summary>
        private bool TryDispatchScreencastFrame(byte[] buffer, int length)
        {
            // The method name is at the front of the message; a bounded search keeps every other message cheap.
            int methodAt = IndexOf(buffer, ScreencastMethod, 0, Math.Min(length, 128));
            if (methodAt < 0)
                return false;

            int dataAt = IndexOf(buffer, DataKey, methodAt, length);
            if (dataAt < 0)
                return false;
            int start = dataAt + DataKey.Length;
            // Base64 never contains a quote or a backslash, so the first quote ends the payload.
            int end = Array.IndexOf(buffer, (byte)'"', start, length - start);
            if (end < 0)
                return false;

            var image = Base64.Decode(buffer, start, end - start);
            if (image == null)
                return false;

            var rest = Encoding.UTF8.GetString(buffer, 0, start) + Encoding.UTF8.GetString(buffer, end, length - end);
            if (!(Json.Parse(rest) is Dictionary<string, object> msg))
                return false;
            var parameters = Json.Dict(msg, "params");

            var handler = ScreencastFrameHandler;
            if (handler != null)
            {
                handler(Json.Str(msg, "sessionId"), Json.Int(parameters, "sessionId"), image);
                return true;
            }

            if (parameters != null)
                parameters["data"] = image;
            _events.Enqueue(msg);
            return true;
        }

        private static int IndexOf(byte[] haystack, byte[] needle, int start, int end)
        {
            int last = end - needle.Length;
            for (int i = start; i <= last; i++)
            {
                if (haystack[i] != needle[0])
                    continue;
                int j = 1;
                while (j < needle.Length && haystack[i + j] == needle[j])
                    j++;
                if (j == needle.Length)
                    return i;
            }
            return -1;
        }

        /// <summary>Takes the next protocol event, if any. Call from the main thread.</summary>
        public bool TryDequeueEvent(out Dictionary<string, object> evt) => _events.TryDequeue(out evt);

        private void FailPending(Exception e)
        {
            foreach (var key in new List<int>(_pending.Keys))
            {
                if (_pending.TryRemove(key, out var tcs))
                    tcs.TrySetException(e);
            }
        }

        // ------------------------------------------------------------------ teardown

        public void Dispose()
        {
            if (_closed)
                return;
            _closed = true;
            try { _cancel.Cancel(); } catch { /* already gone */ }
            try
            {
                if (_socket.State == WebSocketState.Open)
                    _socket.Abort();   // CloseAsync would need a round trip we are not going to wait for
            }
            catch { /* nothing useful to do while tearing down */ }

            FailPending(new ObjectDisposedException(nameof(CdpClient)));
            try { _socket.Dispose(); } catch { }
            try { _cancel.Dispose(); } catch { }
        }
    }

    internal sealed class CdpException : Exception
    {
        public CdpException(string message) : base(message) { }
    }

    /// <summary>Base64 decoding from a byte range, so a payload never has to become a string first.</summary>
    internal static class Base64
    {
        private static readonly sbyte[] Table = BuildTable();

        private static sbyte[] BuildTable()
        {
            var table = new sbyte[256];
            for (int i = 0; i < table.Length; i++)
                table[i] = -1;
            const string alphabet = "ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789+/";
            for (int i = 0; i < alphabet.Length; i++)
                table[alphabet[i]] = (sbyte)i;
            return table;
        }

        /// <summary>Decodes <paramref name="count"/> bytes of standard base64 at <paramref name="offset"/>. Null when malformed.</summary>
        public static byte[] Decode(byte[] source, int offset, int count)
        {
            if (count == 0)
                return Array.Empty<byte>();
            if ((count & 3) != 0)
                return null;

            int end = offset + count;
            int padding = source[end - 1] == '=' ? (source[end - 2] == '=' ? 2 : 1) : 0;
            var output = new byte[count / 4 * 3 - padding];
            int o = 0;
            int full = end - (padding > 0 ? 4 : 0);

            for (int i = offset; i < full; i += 4)
            {
                int a = Table[source[i]], b = Table[source[i + 1]], c = Table[source[i + 2]], d = Table[source[i + 3]];
                if ((a | b | c | d) < 0)
                    return null;
                output[o++] = (byte)((a << 2) | (b >> 4));
                output[o++] = (byte)((b << 4) | (c >> 2));
                output[o++] = (byte)((c << 6) | d);
            }

            if (padding > 0)
            {
                int a = Table[source[full]], b = Table[source[full + 1]];
                if ((a | b) < 0)
                    return null;
                output[o++] = (byte)((a << 2) | (b >> 4));
                if (padding == 1)
                {
                    int c = Table[source[full + 2]];
                    if (c < 0)
                        return null;
                    output[o] = (byte)((b << 4) | (c >> 2));
                }
            }
            return output;
        }
    }
}
