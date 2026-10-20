using System;
using System.Collections.Concurrent;
using System.IO;
using System.IO.Pipes;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace GDShrapt.LSP.Tests.Helpers;

/// <summary>
/// In-process LSP client for end-to-end server tests: byte-framed pipes to a real GDLanguageServer,
/// auto-answers server→client requests, records notifications.
/// </summary>
public sealed class GDLspTestSession : IAsyncDisposable
{
    private readonly AnonymousPipeServerStream _toServer = new(PipeDirection.Out);
    private readonly AnonymousPipeClientStream _serverIn;
    private readonly AnonymousPipeServerStream _fromServer = new(PipeDirection.In);
    private readonly AnonymousPipeClientStream _serverOut;
    private readonly GDStdioJsonRpcTransport _transport;
    private readonly GDLanguageServer _server = new();
    private readonly ConcurrentDictionary<int, TaskCompletionSource<JsonElement>> _pending = new();
    private readonly SemaphoreSlim _writeLock = new(1, 1);
    private readonly CancellationTokenSource _cts = new();
    private readonly TaskCompletionSource _analysisDone = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private Task? _readLoop;
    private int _nextId;

    public ConcurrentQueue<JsonElement> Notifications { get; } = new();

    /// <summary>The server's <c>initialize</c> result (capabilities).</summary>
    public JsonElement InitializeResult { get; private set; }

    private GDLspTestSession()
    {
        _serverIn = new AnonymousPipeClientStream(PipeDirection.In, _toServer.ClientSafePipeHandle);
        _serverOut = new AnonymousPipeClientStream(PipeDirection.Out, _fromServer.ClientSafePipeHandle);
        _transport = new GDStdioJsonRpcTransport(new GDSystemTextJsonSerializer(), _serverIn, _serverOut);
    }

    /// <summary>
    /// Starts a server on <paramref name="rootPath"/>, sends initialize + initialized and waits until the
    /// background analysis pipeline reports <c>$/progress</c> end.
    /// </summary>
    public static async Task<GDLspTestSession> StartAsync(string rootPath, TimeSpan? analysisTimeout = null)
    {
        var s = new GDLspTestSession();
        await s._server.InitializeAsync(s._transport, s._cts.Token);
        s._readLoop = Task.Run(s.ReadLoopAsync);
        s.InitializeResult = await s.RequestAsync("initialize", new
        {
            processId = (int?)null,
            rootUri = new Uri(rootPath).AbsoluteUri,
            capabilities = new { window = new { workDoneProgress = true } }
        });
        await s.NotifyAsync("initialized", new { });
        await s._analysisDone.Task.WaitAsync(analysisTimeout ?? TimeSpan.FromSeconds(60));
        return s;
    }

    public async Task<JsonElement> RequestAsync(string method, object @params, TimeSpan? timeout = null)
    {
        var id = Interlocked.Increment(ref _nextId);
        var tcs = new TaskCompletionSource<JsonElement>(TaskCreationOptions.RunContinuationsAsynchronously);
        _pending[id] = tcs;
        await WriteAsync(new { jsonrpc = "2.0", id, method, @params });
        var response = await tcs.Task.WaitAsync(timeout ?? TimeSpan.FromSeconds(30));
        if (response.TryGetProperty("error", out var error))
            throw new InvalidOperationException($"{method} failed: {error}");
        return response.TryGetProperty("result", out var result) ? result : default;
    }

    public Task NotifyAsync(string method, object @params) => WriteAsync(new { jsonrpc = "2.0", method, @params });

    public Task DidOpenAsync(string uri, string text) =>
        NotifyAsync("textDocument/didOpen", new { textDocument = new { uri, languageId = "gdscript", version = 1, text } });

    public Task DidCloseAsync(string uri) =>
        NotifyAsync("textDocument/didClose", new { textDocument = new { uri } });

    public Task<JsonElement> ReferencesAsync(string uri, int line, int character) =>
        RequestAsync("textDocument/references", new
        {
            textDocument = new { uri },
            position = new { line, character },
            context = new { includeDeclaration = true }
        });

    public Task<JsonElement> HoverAsync(string uri, int line, int character) =>
        RequestAsync("textDocument/hover", new { textDocument = new { uri }, position = new { line, character } });

    private async Task WriteAsync(object message)
    {
        var body = JsonSerializer.SerializeToUtf8Bytes(message);
        var header = Encoding.ASCII.GetBytes($"Content-Length: {body.Length}\r\n\r\n");
        await _writeLock.WaitAsync();
        try
        {
            await _toServer.WriteAsync(header);
            await _toServer.WriteAsync(body);
            await _toServer.FlushAsync();
        }
        finally { _writeLock.Release(); }
    }

    private async Task ReadLoopAsync()
    {
        try
        {
            while (!_cts.IsCancellationRequested)
            {
                var msg = await ReadFrameAsync();
                if (msg == null) return;
                var m = msg.Value;
                var hasId = m.TryGetProperty("id", out var idEl);
                if (m.TryGetProperty("method", out var methodEl))
                {
                    var method = methodEl.GetString();
                    if (hasId)
                    {
                        // server → client request: always answer null result
                        var id = idEl.ValueKind == JsonValueKind.Number ? (object)idEl.GetInt32() : idEl.GetString()!;
                        await WriteAsync(new { jsonrpc = "2.0", id, result = (object?)null });
                        continue;
                    }
                    Notifications.Enqueue(m);
                    if (method == "$/progress"
                        && m.GetProperty("params").GetProperty("value").TryGetProperty("kind", out var kind)
                        && kind.GetString() == "end")
                        _analysisDone.TrySetResult();
                }
                else if (hasId && idEl.ValueKind == JsonValueKind.Number && _pending.TryRemove(idEl.GetInt32(), out var tcs))
                {
                    tcs.TrySetResult(m);
                }
            }
        }
        catch (Exception) when (_cts.IsCancellationRequested) { }
        catch (IOException) { }
    }

    private async Task<JsonElement?> ReadFrameAsync()
    {
        var header = new StringBuilder();
        var one = new byte[1];
        while (!header.ToString().EndsWith("\r\n\r\n"))
        {
            if (await _fromServer.ReadAsync(one) == 0) return null;
            header.Append((char)one[0]);
        }
        var len = int.Parse(header.ToString().Split("\r\n")
            .First(l => l.StartsWith("Content-Length", StringComparison.OrdinalIgnoreCase)).Split(':')[1].Trim());
        var body = new byte[len];
        await _fromServer.ReadExactlyAsync(body);
        return JsonDocument.Parse(body).RootElement.Clone();
    }

    public async ValueTask DisposeAsync()
    {
        _cts.Cancel();
        await _server.DisposeAsync();
        await _transport.DisposeAsync();
        _toServer.Dispose();
        _serverIn.Dispose();
        _fromServer.Dispose();
        _serverOut.Dispose();
        if (_readLoop != null)
            await Task.WhenAny(_readLoop, Task.Delay(2000));
        _cts.Dispose();
    }
}
