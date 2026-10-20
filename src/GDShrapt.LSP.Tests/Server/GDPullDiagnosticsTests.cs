using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using GDShrapt.LSP.Tests.Helpers;
using GDShrapt.TestHelpers;

namespace GDShrapt.LSP.Tests.Server;

/// <summary>
/// LSP 3.17 pull diagnostics: textDocument/diagnostic answers with the full analysis (semantics
/// included) of the document's current text — no waiting for the debounced push tiers.
/// </summary>
[TestClass]
public class GDPullDiagnosticsTests
{
    // 0-based line 3: valid syntax, semantic type mismatch
    private const string BadGd = "extends Node\n\nfunc bad() -> void:\n\tvar x: int = \"text\"\n";
    private const string FixedGd = "extends Node\n\nfunc bad() -> void:\n\tvar x: int = 1\n";
    private const int MismatchLine = 3;

    private GDTempProject _tmp = null!;
    private GDLspTestSession _lsp = null!;

    [TestInitialize]
    public async Task Setup()
    {
        _tmp = GDTempProject.WriteFiles(("bad.gd", FixedGd));
        _lsp = await GDLspTestSession.StartAsync(_tmp.Root);
    }

    [TestCleanup]
    public async Task Cleanup()
    {
        await _lsp.DisposeAsync();
        _tmp.Dispose();
    }

    private Task<JsonElement> PullAsync(string uri) =>
        _lsp.RequestAsync("textDocument/diagnostic", new { textDocument = new { uri } });

    private const string TypeMismatch = "GD3004"; // semantic tier: absent from the fast syntax push

    // Diagnostic codes reported on the mismatch line (a lint warning may stay there after the fix).
    private static string[] CodesOnMismatchLine(JsonElement report) =>
        report.GetProperty("items").EnumerateArray()
            .Where(d => d.GetProperty("range").GetProperty("start").GetProperty("line").GetInt32() == MismatchLine)
            .Select(d => d.GetProperty("code").GetString()!)
            .ToArray();

    [TestMethod]
    public void Initialize_AdvertisesDiagnosticProvider()
    {
        var provider = _lsp.InitializeResult.GetProperty("capabilities").GetProperty("diagnosticProvider");

        Assert.IsTrue(provider.GetProperty("interFileDependencies").GetBoolean());
        Assert.IsFalse(provider.GetProperty("workspaceDiagnostics").GetBoolean());
    }

    [TestMethod]
    [Timeout(90000)]
    public async Task DidOpen_ThenPull_ReturnsFullReportWithSemanticMismatch()
    {
        var uri = _tmp.UriOf("bad.gd");
        await _lsp.DidOpenAsync(uri, BadGd);

        var report = await PullAsync(uri);

        Assert.AreEqual("full", report.GetProperty("kind").GetString());
        CollectionAssert.Contains(CodesOnMismatchLine(report), TypeMismatch, $"type mismatch expected in {report}");
    }

    [TestMethod]
    [Timeout(90000)]
    public async Task DidChange_ThenPull_ReflectsTheNewTextImmediately()
    {
        var uri = _tmp.UriOf("bad.gd");
        await _lsp.DidOpenAsync(uri, BadGd);
        CollectionAssert.Contains(CodesOnMismatchLine(await PullAsync(uri)), TypeMismatch);

        await _lsp.NotifyAsync("textDocument/didChange", new
        {
            textDocument = new { uri, version = 2 },
            contentChanges = new[] { new { text = FixedGd } }
        });
        var report = await PullAsync(uri);

        CollectionAssert.DoesNotContain(CodesOnMismatchLine(report), TypeMismatch, $"the fixed text has no mismatch: {report}");
    }
}
