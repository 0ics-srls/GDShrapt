using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using GDShrapt.LSP.Tests.Helpers;
using GDShrapt.TestHelpers;

namespace GDShrapt.LSP.Tests.Server;

/// <summary>
/// D1/D2 end to end: queries sent right after didOpen/didClose see a fresh semantic model.
/// </summary>
[TestClass]
public class GDDidOpenQueryTests
{
    private const string PlayerGd =
        "class_name Player\nextends CharacterBody2D\n\nconst WALK_SPEED = 300.0\n\nfunc _physics_process(delta: float) -> void:\n\tvar s := WALK_SPEED\n\ttry_jump()\n\nfunc try_jump() -> void:\n\tpass\n";

    private const string CoinGd =
        "extends Area2D\n\nfunc _on_body_entered(body: Node2D) -> void:\n\t(body as Player).try_jump()\n\nfunc poke(p: Player) -> void:\n\tp.try_jump()\n";

    private GDTempProject _tmp = null!;
    private GDLspTestSession _lsp = null!;

    [TestInitialize]
    public async Task Setup()
    {
        _tmp = GDTempProject.WriteFiles(("player.gd", PlayerGd), ("coin.gd", CoinGd));
        _lsp = await GDLspTestSession.StartAsync(_tmp.Root);
    }

    [TestCleanup]
    public async Task Cleanup()
    {
        await _lsp.DisposeAsync();
        _tmp.Dispose();
    }

    private static bool HasLocation(JsonElement locations, string uriSuffix, int line) =>
        locations.ValueKind == JsonValueKind.Array && locations.EnumerateArray().Any(l =>
            l.GetProperty("uri").GetString()!.EndsWith(uriSuffix) &&
            l.GetProperty("range").GetProperty("start").GetProperty("line").GetInt32() == line);

    [TestMethod]
    [Timeout(90000)]
    public async Task DidOpen_ThenReferencesWithoutDelay_ContainsCrossFileLocation()
    {
        var uri = _tmp.UriOf("player.gd");
        await _lsp.DidOpenAsync(uri, PlayerGd);

        var refs = await _lsp.ReferencesAsync(uri, 9, 6);

        Assert.IsTrue(HasLocation(refs, "/coin.gd", 6), $"coin.gd line 7 expected in {refs}");
    }

    [TestMethod]
    [Timeout(90000)]
    public async Task DidOpen_ThenHoverOnConstantInTabIndentedLine_ShowsConstant()
    {
        var uri = _tmp.UriOf("player.gd");
        await _lsp.DidOpenAsync(uri, PlayerGd);

        var hover = await _lsp.HoverAsync(uri, 6, 12);
        var text = hover.ToString();

        StringAssert.Contains(text, "WALK_SPEED");
        Assert.IsFalse(text.Contains("_physics_process"), $"hover fell back to the enclosing method: {text}");
    }

    [TestMethod]
    [Timeout(90000)]
    public async Task DidClose_ThenReferences_StillContainsCrossFileLocations()
    {
        var uri = _tmp.UriOf("player.gd");
        await _lsp.DidOpenAsync(uri, PlayerGd);
        await _lsp.DidCloseAsync(uri);

        var refs = await _lsp.ReferencesAsync(uri, 9, 6);

        Assert.IsTrue(HasLocation(refs, "/coin.gd", 6), $"coin.gd line 7 expected in {refs}");
    }
}
