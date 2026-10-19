using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using GDShrapt.LSP.Tests.Helpers;
using GDShrapt.TestHelpers;
using F = GDShrapt.TestHelpers.GDSceneSignalFixture;

namespace GDShrapt.LSP.Tests.Handlers;

/// <summary>
/// D4b/D4c at the LSP boundary: textDocument/references includes .tscn connection lines.
/// </summary>
[TestClass]
public class GDSceneReferencesTests
{
    private GDTempProject _tmp = null!;
    private GDLspTestSession _lsp = null!;

    [TestInitialize]
    public async Task Setup()
    {
        _tmp = GDTempProject.WriteFiles(F.Files);
        _lsp = await GDLspTestSession.StartAsync(_tmp.Root);
    }

    [TestCleanup]
    public async Task Cleanup()
    {
        await _lsp.DisposeAsync();
        _tmp.Dispose();
    }

    private bool HasGameTscn(JsonElement refs) =>
        refs.ValueKind == JsonValueKind.Array && refs.EnumerateArray().Any(l =>
            l.GetProperty("uri").GetString() == _tmp.UriOf("game.tscn") &&
            l.GetProperty("range").GetProperty("start").GetProperty("line").GetInt32() == F.GameCoinConnectionLine);

    [TestMethod]
    [Timeout(90000)]
    public async Task ReferencesOnSignal_IncludeSceneConnection()
    {
        var refs = await _lsp.ReferencesAsync(_tmp.UriOf("player.gd"), 12, 9);

        Assert.IsTrue(HasGameTscn(refs), $"game.tscn connection expected in {refs}");
    }

    [TestMethod]
    [Timeout(90000)]
    public async Task ReferencesOnCallbackMethod_IncludeSceneConnection()
    {
        var refs = await _lsp.ReferencesAsync(_tmp.UriOf("hud.gd"), 2, 8);

        Assert.IsTrue(HasGameTscn(refs), $"game.tscn connection expected in {refs}");
    }
}
