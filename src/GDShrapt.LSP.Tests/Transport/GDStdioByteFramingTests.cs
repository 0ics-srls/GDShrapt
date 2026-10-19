using System;
using System.Collections.Concurrent;
using System.IO;
using System.IO.Pipes;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace GDShrapt.LSP.Tests.Transport;

/// <summary>
/// Byte-exact stdio framing (Content-Length = UTF-8 bytes) and a reader that is never blocked
/// by handler continuations (D8a, D7b).
/// </summary>
[TestClass]
public class GDStdioByteFramingTests
{
    private static readonly TimeSpan Wait = TimeSpan.FromSeconds(5);

    private static byte[] Frame(string json, string? extraHeader = null)
    {
        var body = Encoding.UTF8.GetBytes(json);
        var header = (extraHeader != null ? extraHeader + "\r\n" : "") + $"Content-Length: {body.Length}\r\n\r\n";
        return Encoding.ASCII.GetBytes(header).Concat(body).ToArray();
    }

    private static byte[] Concat(params byte[][] parts) => parts.SelectMany(p => p).ToArray();

    private static string Note(string method, string text) =>
        JsonSerializer.Serialize(new { jsonrpc = "2.0", method, @params = new { text } });

    private static GDStdioJsonRpcTransport Create(Stream input, Stream output) =>
        new(new GDSystemTextJsonSerializer(), input, output);

    private static ConcurrentQueue<string> RecordNotes(GDStdioJsonRpcTransport t, string method)
    {
        var q = new ConcurrentQueue<string>();
        t.OnNotification<JsonElement>(method, p =>
        {
            q.Enqueue(p.GetProperty("text").GetString()!);
            return Task.CompletedTask;
        });
        return q;
    }

    private static async Task WaitUntil(Func<bool> condition)
    {
        var until = DateTime.UtcNow + Wait;
        while (!condition())
        {
            if (DateTime.UtcNow > until) Assert.Fail("condition not reached in time");
            await Task.Delay(20);
        }
    }

    [TestMethod]
    [Timeout(10000)]
    public async Task NonAsciiBody_ByteContentLength_DispatchedExactly_AndNextMessageToo()
    {
        var input = new MemoryStream(Concat(Frame(Note("test/note", "perché é 日本語")), Frame(Note("test/note", "next"))));
        await using var t = Create(input, new LockedMemoryStream());
        var notes = RecordNotes(t, "test/note");

        await t.StartAsync(CancellationToken.None);
        await WaitUntil(() => notes.Count == 2);

        CollectionAssert.AreEqual(new[] { "perché é 日本語", "next" }, notes.ToArray());
    }

    [TestMethod]
    [Timeout(10000)]
    public async Task LargeMultibyteBody_SplitAcrossSmallReads_ReadCompletely()
    {
        var text = string.Concat(Enumerable.Repeat("日本語é-", 70 * 1024 / 12 + 1));
        Assert.IsTrue(Encoding.UTF8.GetByteCount(text) > 70 * 1024);
        var input = new ChunkedReadStream(Frame(Note("test/note", text)), chunkSize: 7);
        await using var t = Create(input, new LockedMemoryStream());
        var notes = RecordNotes(t, "test/note");

        await t.StartAsync(CancellationToken.None);
        await WaitUntil(() => notes.Count == 1);

        Assert.AreEqual(text, notes.Single());
    }

    [TestMethod]
    [Timeout(10000)]
    public async Task ExtraHeaders_BeforeBlankLine_AreIgnored()
    {
        var input = new MemoryStream(Frame(Note("test/note", "hdr"), "Content-Type: application/vscode-jsonrpc; charset=utf-8"));
        await using var t = Create(input, new LockedMemoryStream());
        var notes = RecordNotes(t, "test/note");

        await t.StartAsync(CancellationToken.None);
        await WaitUntil(() => notes.Count == 1);

        Assert.AreEqual("hdr", notes.Single());
    }

    [TestMethod]
    [Timeout(10000)]
    public async Task WrittenMessage_ContentLengthIsUtf8ByteCount_BodyWithoutBom()
    {
        // Raw UTF-8 serializer (no \uXXXX escaping) so the body really contains multibyte characters.
        var output = new LockedMemoryStream();
        await using var t = new GDStdioJsonRpcTransport(new RawUtf8Serializer(), new MemoryStream(), output);

        await t.SendNotificationAsync("test/out", new { text = "perché 日本語" });

        var bytes = output.ToArray();
        var sep = IndexOf(bytes, Encoding.ASCII.GetBytes("\r\n\r\n"));
        Assert.IsTrue(sep > 0, "header terminator not found");
        var header = Encoding.ASCII.GetString(bytes, 0, sep);
        var body = bytes.Skip(sep + 4).ToArray();
        var declared = int.Parse(header.Split(':')[1].Trim());

        Assert.AreEqual(body.Length, declared);
        Assert.IsTrue(declared > Encoding.UTF8.GetString(body).Length, "payload must be multibyte for this test to discriminate");
        Assert.AreEqual((byte)'{', body[0], "body must start with '{' (no UTF-8 BOM)");
        var text = JsonDocument.Parse(body).RootElement.GetProperty("params").GetProperty("text").GetString();
        Assert.AreEqual("perché 日本語", text);
    }

    [TestMethod]
    [Timeout(10000)]
    public async Task MissingOrInvalidContentLength_Skipped_ReaderContinues()
    {
        var input = new MemoryStream(Concat(
            Encoding.ASCII.GetBytes("Content-Length: abc\r\n\r\n"),
            Encoding.ASCII.GetBytes("X-Other: 1\r\n\r\n"),
            Frame(Note("test/note", "valid"))));
        await using var t = Create(input, new LockedMemoryStream());
        var notes = RecordNotes(t, "test/note");

        await t.StartAsync(CancellationToken.None);
        await WaitUntil(() => notes.Count == 1);

        Assert.AreEqual("valid", notes.Single());
    }

    [TestMethod]
    [Timeout(15000)]
    public async Task HandlerContinuationBlocked_AfterClientResponse_ReaderStillAnswersNextRequest()
    {
        using var toServer = new AnonymousPipeServerStream(PipeDirection.Out);
        using var serverIn = new AnonymousPipeClientStream(PipeDirection.In, toServer.ClientSafePipeHandle);
        using var fromServer = new AnonymousPipeServerStream(PipeDirection.In);
        using var serverOut = new AnonymousPipeClientStream(PipeDirection.Out, fromServer.ClientSafePipeHandle);

        await using var t = Create(serverIn, serverOut);
        var gate = new ManualResetEventSlim(false);
        t.OnRequest<JsonElement, object>("test/ask", async (_, ct) =>
        {
            await t.SendRequestAsync<object, JsonElement>("client/q", new { }, ct);
            gate.Wait(TimeSpan.FromSeconds(10));        // blocks the continuation thread
            return new { done = true };
        });
        t.OnRequest<JsonElement, object>("test/ping", (_, _) => Task.FromResult<object?>(new { pong = true }));
        await t.StartAsync(CancellationToken.None);

        var reader = new FrameReader(fromServer);
        await toServer.WriteAsync(Frame("{\"jsonrpc\":\"2.0\",\"id\":1,\"method\":\"test/ask\",\"params\":{}}"));
        await toServer.FlushAsync();

        var serverRequest = await reader.ReadAsync().WaitAsync(Wait);
        Assert.AreEqual("client/q", serverRequest.GetProperty("method").GetString());
        var serverRequestId = serverRequest.GetProperty("id").GetString();

        await toServer.WriteAsync(Frame($"{{\"jsonrpc\":\"2.0\",\"id\":\"{serverRequestId}\",\"result\":{{}}}}"));
        await toServer.WriteAsync(Frame("{\"jsonrpc\":\"2.0\",\"id\":2,\"method\":\"test/ping\",\"params\":{}}"));
        await toServer.FlushAsync();

        try
        {
            var pong = await reader.ReadAsync().WaitAsync(Wait);
            Assert.AreEqual(2, pong.GetProperty("id").GetInt32());
            Assert.IsTrue(pong.GetProperty("result").GetProperty("pong").GetBoolean());
        }
        finally { gate.Set(); }
    }

    [TestMethod]
    [Timeout(10000)]
    public async Task EndOfInputStream_RunLoopCompletesWithoutException()
    {
        await using var t = Create(new MemoryStream(), new LockedMemoryStream());

        await t.StartAsync(CancellationToken.None);
        await t.Completion.WaitAsync(Wait);

        Assert.IsTrue(t.Completion.IsCompletedSuccessfully);
    }

    [TestMethod]
    [Timeout(10000)]
    public async Task CancelRequest_ThroughStreamPath_CancelsHandler_RespondsRequestCancelled()
    {
        var input = new MemoryStream(Concat(
            Frame("{\"jsonrpc\":\"2.0\",\"id\":1,\"method\":\"test/slow\",\"params\":{}}"),
            Frame("{\"jsonrpc\":\"2.0\",\"method\":\"$/cancelRequest\",\"params\":{\"id\":1}}")));
        var output = new LockedMemoryStream();
        await using var t = Create(input, output);
        var cancelled = new TaskCompletionSource<bool>();
        t.OnRequest<JsonElement, object>("test/slow", async (_, ct) =>
        {
            try { await Task.Delay(TimeSpan.FromSeconds(30), ct); cancelled.TrySetResult(false); return null; }
            catch (OperationCanceledException) { cancelled.TrySetResult(true); throw; }
        });

        await t.StartAsync(CancellationToken.None);

        Assert.IsTrue(await cancelled.Task.WaitAsync(Wait));
        await WaitUntil(() => Encoding.UTF8.GetString(output.ToArray()).Contains($"{GDJsonRpcError.RequestCancelled}"));
    }

    private static int IndexOf(byte[] haystack, byte[] needle)
    {
        for (var i = 0; i <= haystack.Length - needle.Length; i++)
            if (haystack.AsSpan(i, needle.Length).SequenceEqual(needle))
                return i;
        return -1;
    }

    private sealed class RawUtf8Serializer : IGDMessageSerializer
    {
        private static readonly JsonSerializerOptions Options = new()
        {
            Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping
        };
        public string Serialize<T>(T value) => JsonSerializer.Serialize(value, Options);
        public T? Deserialize<T>(string json) => JsonSerializer.Deserialize<T>(json, Options);
        public object? Deserialize(string json, Type type) => JsonSerializer.Deserialize(json, type, Options);
    }

    /// <summary>Thread-safe in-memory output stream.</summary>
    private sealed class LockedMemoryStream : MemoryStream
    {
        private readonly object _lock = new();
        public override void Write(byte[] buffer, int offset, int count) { lock (_lock) base.Write(buffer, offset, count); }
        public override void Write(ReadOnlySpan<byte> buffer) { lock (_lock) base.Write(buffer); }
        public override Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken ct) { Write(buffer, offset, count); return Task.CompletedTask; }
        public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken ct = default) { Write(buffer.Span); return ValueTask.CompletedTask; }
        public override byte[] ToArray() { lock (_lock) return base.ToArray(); }
    }

    /// <summary>Read-only stream that hands out at most <c>chunkSize</c> bytes per read.</summary>
    private sealed class ChunkedReadStream(byte[] data, int chunkSize) : Stream
    {
        private int _pos;
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => data.Length;
        public override long Position { get => _pos; set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override int Read(byte[] buffer, int offset, int count) => Read(buffer.AsSpan(offset, count));
        public override int Read(Span<byte> buffer)
        {
            var n = Math.Min(Math.Min(chunkSize, buffer.Length), data.Length - _pos);
            data.AsSpan(_pos, n).CopyTo(buffer);
            _pos += n;
            return n;
        }
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct = default) => new(Read(buffer.Span));
        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken ct) => Task.FromResult(Read(buffer, offset, count));
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }

    /// <summary>Test-side byte-exact LSP frame reader.</summary>
    private sealed class FrameReader(Stream stream)
    {
        public async Task<JsonElement> ReadAsync()
        {
            var header = new StringBuilder();
            var one = new byte[1];
            while (!header.ToString().EndsWith("\r\n\r\n"))
            {
                if (await stream.ReadAsync(one) == 0) throw new EndOfStreamException();
                header.Append((char)one[0]);
            }
            var len = int.Parse(header.ToString().Split("\r\n")
                .First(l => l.StartsWith("Content-Length", StringComparison.OrdinalIgnoreCase)).Split(':')[1].Trim());
            var body = new byte[len];
            await stream.ReadExactlyAsync(body);
            return JsonDocument.Parse(body).RootElement.Clone();
        }
    }
}
