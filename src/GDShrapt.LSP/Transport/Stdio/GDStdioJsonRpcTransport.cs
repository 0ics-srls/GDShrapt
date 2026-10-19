using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace GDShrapt.LSP;

/// <summary>
/// JSON-RPC transport over stdio (stdin/stdout).
/// Uses Content-Length header as per LSP specification. The Stream constructor (used by the server)
/// frames in bytes: Content-Length counts UTF-8 bytes. The TextReader constructor is kept for legacy tests.
/// </summary>
public class GDStdioJsonRpcTransport : IGDJsonRpcTransport
{
    private readonly IGDMessageSerializer _serializer;
    private readonly TextReader? _input;
    private readonly TextWriter? _output;
    private readonly Stream? _inStream;
    private readonly Stream? _outStream;
    private readonly byte[] _buf = new byte[64 * 1024];
    private int _bufStart;
    private int _bufEnd;
    private readonly ConcurrentDictionary<string, RequestHandler> _requestHandlers = new();
    private readonly ConcurrentDictionary<string, NotificationHandler> _notificationHandlers = new();
    private readonly ConcurrentDictionary<object, TaskCompletionSource<JsonElement?>> _pendingRequests = new();
    private readonly ConcurrentDictionary<string, CancellationTokenSource> _pendingRequestCts = new();
    private readonly SemaphoreSlim _writeLock = new(1, 1);
    private CancellationTokenSource? _cts;
    private Task? _readTask;
    private int _requestId;
    private bool _disposed;

    /// <summary>
    /// Optional logger for sending errors to the LSP client via window/logMessage.
    /// </summary>
    public GDLspLogger? Logger { get; set; }

    public GDStdioJsonRpcTransport(IGDMessageSerializer serializer)
        : this(serializer, Console.OpenStandardInput(), Console.OpenStandardOutput())
    {
    }

    /// <summary>
    /// Byte-exact transport: headers are ASCII, the body is exactly Content-Length UTF-8 bytes.
    /// </summary>
    public GDStdioJsonRpcTransport(IGDMessageSerializer serializer, Stream input, Stream output)
    {
        _serializer = serializer;
        _inStream = input;
        _outStream = output;
    }

    public GDStdioJsonRpcTransport(IGDMessageSerializer serializer, TextReader input, TextWriter output)
    {
        _serializer = serializer;
        _input = input;
        _output = output;
    }

    /// <summary>
    /// Completes when the read loop ends (end of input or stop).
    /// </summary>
    public Task Completion => _readTask ?? Task.CompletedTask;

    public Task StartAsync(CancellationToken cancellationToken)
    {
        _cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        _readTask = Task.Run(() => ReadLoopAsync(_cts.Token), _cts.Token);
        return Task.CompletedTask;
    }

    public async Task StopAsync()
    {
        _cts?.Cancel();
        if (_readTask != null)
        {
            try
            {
                await _readTask.ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                // Expected
            }
        }
    }

    public async Task<TResult?> SendRequestAsync<TParams, TResult>(string method, TParams parameters, CancellationToken cancellationToken)
    {
        var id = Interlocked.Increment(ref _requestId).ToString();
        // Continuations must not run inline on the reader loop (a handler resumed by a client response
        // would otherwise block reading of every following message).
        var tcs = new TaskCompletionSource<JsonElement?>(TaskCreationOptions.RunContinuationsAsynchronously);
        _pendingRequests[id] = tcs;

        try
        {
            var request = new GDJsonRpcRequest
            {
                Id = id,
                Method = method,
                Params = parameters != null ? JsonSerializer.SerializeToElement(parameters) : null
            };

            await WriteMessageAsync(request).ConfigureAwait(false);

            using var registration = cancellationToken.Register(() => tcs.TrySetCanceled());
            var result = await tcs.Task.ConfigureAwait(false);

            if (result == null)
                return default;

            return result.Value.Deserialize<TResult>();
        }
        finally
        {
            _pendingRequests.TryRemove(id, out _);
        }
    }

    public Task SendNotificationAsync<TParams>(string method, TParams parameters)
    {
        var notification = new
        {
            jsonrpc = "2.0",
            method,
            @params = parameters
        };

        return WriteMessageAsync(notification);
    }

    private static readonly JsonElement _jsonNull = JsonDocument.Parse("null").RootElement.Clone();

    public Task SendResponseAsync(object? id, object? result)
    {
        var response = new GDJsonRpcResponse
        {
            Id = id,
            Result = result ?? _jsonNull
        };

        return WriteMessageAsync(response);
    }

    public Task SendErrorAsync(object? id, int code, string message, object? data = null)
    {
        var response = new GDJsonRpcResponse
        {
            Id = id,
            Error = new GDJsonRpcError
            {
                Code = code,
                Message = message,
                Data = data
            }
        };

        return WriteMessageAsync(response);
    }

    public void OnRequest<TParams, TResult>(string method, Func<TParams, CancellationToken, Task<TResult?>> handler)
    {
        _requestHandlers[method] = new RequestHandler(
            async (paramsJson, ct) =>
            {
                var @params = paramsJson != null
                    ? paramsJson.Value.Deserialize<TParams>()
                    : default;
                return await handler(@params!, ct).ConfigureAwait(false);
            });
    }

    public void OnNotification<TParams>(string method, Func<TParams, Task> handler)
    {
        _notificationHandlers[method] = new NotificationHandler(
            async (paramsJson) =>
            {
                var @params = paramsJson != null
                    ? paramsJson.Value.Deserialize<TParams>()
                    : default;
                await handler(@params!).ConfigureAwait(false);
            });
    }

    private async Task ReadLoopAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                var message = _inStream != null
                    ? await ReadMessageBytesAsync(cancellationToken).ConfigureAwait(false)
                    : await ReadMessageAsync(cancellationToken).ConfigureAwait(false);
                if (message == null)
                    break;
                if (message.Length == 0)
                    continue;   // header block without a valid Content-Length: skipped

                _ = ProcessMessageAsync(message, cancellationToken);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                GDLspPerformanceTrace.Log("transport", $"READ-ERROR: {ex.Message}");
            }
        }
    }

    /// <summary>
    /// Reads one message from the byte stream. Returns null on end of input and an empty string
    /// for a header block without a valid Content-Length.
    /// </summary>
    private async Task<string?> ReadMessageBytesAsync(CancellationToken cancellationToken)
    {
        int contentLength = -1;
        while (true)
        {
            var line = await ReadHeaderLineAsync(cancellationToken).ConfigureAwait(false);
            if (line == null)
                return null;
            if (line.Length == 0)
                break;

            var colon = line.IndexOf(':');
            if (colon > 0
                && line.Substring(0, colon).Trim().Equals("Content-Length", StringComparison.OrdinalIgnoreCase)
                && int.TryParse(line.Substring(colon + 1).Trim(), out var n)
                && n >= 0)
            {
                contentLength = n;
            }
        }

        if (contentLength < 0)
            return string.Empty;

        var body = new byte[contentLength];
        if (!await ReadExactlyAsync(body, cancellationToken).ConfigureAwait(false))
            return null;

        return Encoding.UTF8.GetString(body);
    }

    private async Task<bool> FillBufferAsync(CancellationToken cancellationToken)
    {
        _bufStart = 0;
        _bufEnd = await _inStream!.ReadAsync(_buf.AsMemory(), cancellationToken).ConfigureAwait(false);
        return _bufEnd > 0;
    }

    /// <summary>
    /// Reads an ASCII header line terminated by CRLF (or LF). Null on end of input.
    /// </summary>
    private async Task<string?> ReadHeaderLineAsync(CancellationToken cancellationToken)
    {
        var sb = new StringBuilder();
        while (true)
        {
            if (_bufStart >= _bufEnd && !await FillBufferAsync(cancellationToken).ConfigureAwait(false))
                return null;

            var b = _buf[_bufStart++];
            if (b == (byte)'\n')
            {
                if (sb.Length > 0 && sb[sb.Length - 1] == '\r')
                    sb.Length--;
                return sb.ToString();
            }
            sb.Append((char)b);
        }
    }

    private async Task<bool> ReadExactlyAsync(byte[] target, CancellationToken cancellationToken)
    {
        var filled = 0;
        while (filled < target.Length)
        {
            if (_bufStart >= _bufEnd && !await FillBufferAsync(cancellationToken).ConfigureAwait(false))
                return false;

            var n = Math.Min(target.Length - filled, _bufEnd - _bufStart);
            Buffer.BlockCopy(_buf, _bufStart, target, filled, n);
            _bufStart += n;
            filled += n;
        }
        return true;
    }

    private async Task<string?> ReadMessageAsync(CancellationToken cancellationToken)
    {
        // Read headers
        var headers = new Dictionary<string, string>();
        string? line;

        while ((line = await _input!.ReadLineAsync(cancellationToken).ConfigureAwait(false)) != null)
        {
            if (string.IsNullOrEmpty(line))
                break;

            var colonIndex = line.IndexOf(':');
            if (colonIndex > 0)
            {
                var key = line.Substring(0, colonIndex).Trim();
                var value = line.Substring(colonIndex + 1).Trim();
                headers[key] = value;
            }
        }

        if (line == null)
            return null;

        // Get content length
        if (!headers.TryGetValue("Content-Length", out var contentLengthStr) ||
            !int.TryParse(contentLengthStr, out var contentLength))
        {
            return null;
        }

        // Read content
        var buffer = new char[contentLength];
        var totalRead = 0;

        while (totalRead < contentLength)
        {
            var read = await _input.ReadAsync(buffer, totalRead, contentLength - totalRead).ConfigureAwait(false);
            if (read == 0)
                return null;
            totalRead += read;
        }

        return new string(buffer);
    }

    private async Task ProcessMessageAsync(string json, CancellationToken cancellationToken)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;

            // Check if it's a response (has id but no method)
            if (root.TryGetProperty("id", out var idElement) && !root.TryGetProperty("method", out _))
            {
                await HandleResponseAsync(root, idElement).ConfigureAwait(false);
                return;
            }

            // It's a request or notification
            if (!root.TryGetProperty("method", out var methodElement))
                return;

            var method = methodElement.GetString() ?? string.Empty;
            JsonElement? paramsElement = root.TryGetProperty("params", out var pe) ? pe : null;

            // Handle $/cancelRequest — cancel the running request
            if (method == "$/cancelRequest" && paramsElement != null)
            {
                HandleCancelRequest(paramsElement.Value);
                return;
            }

            // Check if it's a request (has id) or notification (no id)
            if (root.TryGetProperty("id", out idElement))
            {
                await HandleRequestAsync(method, paramsElement, idElement, cancellationToken).ConfigureAwait(false);
            }
            else
            {
                await HandleNotificationAsync(method, paramsElement).ConfigureAwait(false);
            }
        }
        catch (Exception ex)
        {
            GDLspPerformanceTrace.Log("transport", $"PROCESS-ERROR: {ex}");
            if (Logger != null)
                _ = Logger.ErrorAsync($"Error processing message: {ex.Message}");
        }
    }

    private Task HandleResponseAsync(JsonElement root, JsonElement idElement)
    {
        var id = idElement.ValueKind == JsonValueKind.Number
            ? idElement.GetInt32().ToString()
            : idElement.GetString() ?? string.Empty;

        if (_pendingRequests.TryRemove(id, out var tcs))
        {
            if (root.TryGetProperty("error", out var errorElement))
            {
                var message = errorElement.TryGetProperty("message", out var msgElement)
                    ? msgElement.GetString() ?? "Unknown error"
                    : "Unknown error";
                tcs.SetException(new Exception(message));
            }
            else if (root.TryGetProperty("result", out var resultElement))
            {
                tcs.SetResult(resultElement.Clone());
            }
            else
            {
                tcs.SetResult(null);
            }
        }

        return Task.CompletedTask;
    }

    private async Task HandleRequestAsync(string method, JsonElement? paramsElement, JsonElement idElement, CancellationToken cancellationToken)
    {
        var id = idElement.ValueKind == JsonValueKind.Number
            ? (object)idElement.GetInt32()
            : idElement.GetString() ?? string.Empty;

        if (!_requestHandlers.TryGetValue(method, out var handler))
        {
            await SendErrorAsync(id, GDJsonRpcError.MethodNotFound, $"Method not found: {method}").ConfigureAwait(false);
            return;
        }

        var idStr = id.ToString()!;
        using var requestCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        _pendingRequestCts[idStr] = requestCts;

        GDLspPerformanceTrace.Log("request", $">>> {method} id={id}");
        var sw = System.Diagnostics.Stopwatch.StartNew();
        try
        {
            var result = await handler.Handler(paramsElement, requestCts.Token).ConfigureAwait(false);
            await SendResponseAsync(id, result).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (requestCts.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
        {
            await SendErrorAsync(id, GDJsonRpcError.RequestCancelled, "Request cancelled").ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            GDLspPerformanceTrace.Log("request", $"ERROR {method} id={id}: {ex}");
            await SendErrorAsync(id, GDJsonRpcError.InternalError, ex.Message).ConfigureAwait(false);
        }
        finally
        {
            _pendingRequestCts.TryRemove(idStr, out _);
            sw.Stop();
            GDLspPerformanceTrace.Log("request", $"<<< {method} id={id} {sw.ElapsedMilliseconds}ms");
        }
    }

    private void HandleCancelRequest(JsonElement paramsElement)
    {
        string? idStr = null;
        if (paramsElement.TryGetProperty("id", out var idElement))
        {
            idStr = idElement.ValueKind == JsonValueKind.Number
                ? idElement.GetInt32().ToString()
                : idElement.GetString();
        }

        if (idStr != null && _pendingRequestCts.TryGetValue(idStr, out var cts))
        {
            GDLspPerformanceTrace.Log("cancel", $"Cancelling request id={idStr}");
            try { cts.Cancel(); } catch (ObjectDisposedException) { }
        }
    }

    private async Task HandleNotificationAsync(string method, JsonElement? paramsElement)
    {
        if (!_notificationHandlers.TryGetValue(method, out var handler))
            return;

        GDLspPerformanceTrace.Log("notification", $">>> {method}");
        var sw = System.Diagnostics.Stopwatch.StartNew();
        try
        {
            await handler.Handler(paramsElement).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            GDLspPerformanceTrace.Log("notification", $"ERROR {method}: {ex.Message}");
        }
        finally
        {
            sw.Stop();
            GDLspPerformanceTrace.Log("notification", $"<<< {method} {sw.ElapsedMilliseconds}ms");
        }
    }

    private async Task WriteMessageAsync(object message)
    {
        await _writeLock.WaitAsync().ConfigureAwait(false);
        try
        {
            var json = _serializer.Serialize(message);
            var bytes = Encoding.UTF8.GetBytes(json);

            if (_outStream != null)
            {
                var header = Encoding.ASCII.GetBytes($"Content-Length: {bytes.Length}\r\n\r\n");
                await _outStream.WriteAsync(header).ConfigureAwait(false);
                await _outStream.WriteAsync(bytes).ConfigureAwait(false);
                await _outStream.FlushAsync().ConfigureAwait(false);
                return;
            }

            await _output!.WriteAsync($"Content-Length: {bytes.Length}\r\n\r\n").ConfigureAwait(false);
            await _output.WriteAsync(json).ConfigureAwait(false);
            await _output.FlushAsync().ConfigureAwait(false);
        }
        finally
        {
            _writeLock.Release();
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed)
            return;

        _disposed = true;
        await StopAsync().ConfigureAwait(false);
        _cts?.Dispose();
        _writeLock.Dispose();
    }

    private class RequestHandler
    {
        public Func<JsonElement?, CancellationToken, Task<object?>> Handler { get; }

        public RequestHandler(Func<JsonElement?, CancellationToken, Task<object?>> handler)
        {
            Handler = handler;
        }
    }

    private class NotificationHandler
    {
        public Func<JsonElement?, Task> Handler { get; }

        public NotificationHandler(Func<JsonElement?, Task> handler)
        {
            Handler = handler;
        }
    }
}
