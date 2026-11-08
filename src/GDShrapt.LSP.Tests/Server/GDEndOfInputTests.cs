using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace GDShrapt.LSP.Tests;

/// <summary>
/// 0ics (LSAI): the server ends when its stdin ends. It waited for an `exit` that a client which died or closed
/// without a shutdown never sends, and outlived it (31 orphans on a mac, one per ended LSAI session).
/// </summary>
[TestClass]
public class GDEndOfInputTests
{
    private static string Frame(string json) => $"Content-Length: {System.Text.Encoding.UTF8.GetByteCount(json)}\r\n\r\n{json}";

    [TestMethod]
    [Timeout(10000)]
    public async Task EndOfInput_WithoutExit_EndsTheServer()
    {
        var input = new MemoryStream(System.Text.Encoding.UTF8.GetBytes(Frame(
            "{\"jsonrpc\":\"2.0\",\"id\":1,\"method\":\"initialize\",\"params\":{\"processId\":null,\"capabilities\":{}}}")));
        var transport = new GDStdioJsonRpcTransport(new GDSystemTextJsonSerializer(), input, new MemoryStream());
        await using var server = new GDLanguageServer();

        await server.InitializeAsync(transport, CancellationToken.None);

        // no exit notification, no cancellation: only the end of input
        await server.RunAsync(CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(5));
    }
}
