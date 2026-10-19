using System.IO;
using System.Linq;
using GDShrapt.TestHelpers;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using F = GDShrapt.TestHelpers.GDSceneSignalFixture;

namespace GDShrapt.Semantics.Tests.Refactoring;

/// <summary>
/// D4b/D4c: .tscn [connection] lines are references for both the signal and the callback method,
/// nodes instanced from sub-scenes are typed by the sub-scene's root script, and rename edits both roles.
/// </summary>
[TestClass]
public class GDSceneConnectionRefsTests
{
    private GDTempProject _tmp = null!;
    private GDProjectSemanticModel _model = null!;

    [TestInitialize]
    public void Setup()
    {
        _tmp = GDTempProject.Create(F.Files);
        _model = new GDProjectSemanticModel(_tmp.Project!);
        _tmp.Project!.AnalyzeAll();
    }

    [TestCleanup]
    public void Cleanup()
    {
        _model.Dispose();
        _tmp.Dispose();
    }

    private GDSceneInfo Scene(string res) => _tmp.Project!.SceneTypesProvider!.GetSceneInfo(res)!;

    private GDSymbolReference[] SceneRefs(string symbol, string declaringFile, string scene) =>
        new GDSymbolReferenceCollector(_tmp.Project!, _model).CollectReferences(symbol, _tmp.PathOf(declaringFile)).References
            .Where(r => r.FilePath != null && Path.GetFileName(r.FilePath) == scene).ToArray();

    [TestMethod]
    public void ParseSignalConnections_SignalColumnSet_MethodColumnUnchanged()
    {
        var conn = Scene("res://game.tscn").SignalConnections.Single(c => c.SignalName == "coin_collected");
        var line = F.GameTscn.Split('\n')[F.GameCoinConnectionLine];

        Assert.AreEqual(line.IndexOf("signal=\"") + "signal=\"".Length, conn.SignalColumn);
        Assert.AreEqual(line.IndexOf("method=\"") + "method=\"".Length, conn.MethodColumn);
    }

    [TestMethod]
    public void ResolveSceneNodeTypeName_InstancedSubScene_IsRootScriptType_PlainNodeIsItsType()
    {
        var game = Scene("res://game.tscn");

        Assert.AreEqual("Player", _model.ResolveSceneNodeTypeName(game, game.Nodes.Single(n => n.Name == "Player")));
        Assert.AreEqual("Timer", _model.ResolveSceneNodeTypeName(game, game.Nodes.Single(n => n.Name == "T")));
    }

    [TestMethod]
    public void SignalReferences_IncludeStrictSceneHitAtSignalColumn()
    {
        var hits = SceneRefs("coin_collected", "player.gd", "game.tscn");
        var line = F.GameTscn.Split('\n')[F.GameCoinConnectionLine];

        Assert.AreEqual(1, hits.Length, "exactly the coin_collected connection (not the Timer timeout)");
        Assert.AreEqual(F.GameCoinConnectionLine, hits[0].Line);
        Assert.AreEqual(line.IndexOf("signal=\"") + "signal=\"".Length, hits[0].Column);
        Assert.AreEqual(GDSymbolReferenceKind.SceneSignalConnection, hits[0].Kind);
        Assert.AreEqual(GDReferenceConfidence.Strict, hits[0].Confidence);
        Assert.IsTrue(hits[0].IsSceneSignal);
    }

    [TestMethod]
    public void SignalReferences_UnrelatedEmitterSkipped_UnknownEmitterPotential()
    {
        Assert.IsFalse(SceneRefs("coin_collected", "player.gd", "game.tscn").Any(r => r.Line == F.GameTimeoutConnectionLine),
            "Timer.timeout connection is not a coin_collected reference");

        var unknown = SceneRefs("coin_collected", "player.gd", "other.tscn");
        Assert.AreEqual(1, unknown.Length);
        Assert.AreEqual(F.OtherConnectionLine, unknown[0].Line);
        Assert.AreEqual(GDReferenceConfidence.Potential, unknown[0].Confidence);
    }

    [TestMethod]
    public void CallbackReferences_BothConnectionLinesExactlyOnce()
    {
        var lines = SceneRefs("_on_coin_collected", "hud.gd", "game.tscn").Select(r => r.Line).OrderBy(l => l).ToArray();

        CollectionAssert.AreEqual(new[] { F.GameCoinConnectionLine, F.GameTimeoutConnectionLine }, lines);
    }

    [TestMethod]
    public void RenameSignal_EditsDeclarationUsagesAndSceneSignalAttribute()
    {
        var result = new GDRenameService(_tmp.Project!, _model).PlanRename("coin_collected", "coin_taken", _tmp.PathOf("player.gd"));
        int[] Lines(string file) => result.StrictEdits.Where(e => Path.GetFileName(e.FilePath) == file).Select(e => e.Line).OrderBy(l => l).ToArray();

        CollectionAssert.AreEqual(new[] { 13, 16 }, Lines("player.gd"), "declaration + emit; actual: " + string.Join(",", Lines("player.gd")));
        CollectionAssert.AreEqual(new[] { 7 }, Lines("hud.gd"), "p.coin_collected.connect; actual: " + string.Join(",", Lines("hud.gd")));
        CollectionAssert.AreEqual(new[] { F.GameCoinConnectionLine + 1 }, Lines("game.tscn"), "signal=\"coin_taken\"; actual: " + string.Join(",", Lines("game.tscn")));
    }

    [TestMethod]
    public void RenameCallback_EditsMethodAndBothSceneMethodAttributes()
    {
        var result = new GDRenameService(_tmp.Project!, _model).PlanRename("_on_coin_collected", "on_coin", _tmp.PathOf("hud.gd"));
        int[] Lines(string file) => result.StrictEdits.Where(e => Path.GetFileName(e.FilePath) == file).Select(e => e.Line).OrderBy(l => l).ToArray();

        CollectionAssert.Contains(Lines("hud.gd"), 3, "declaration; actual: " + string.Join(",", Lines("hud.gd")));
        CollectionAssert.AreEqual(new[] { F.GameCoinConnectionLine + 1, F.GameTimeoutConnectionLine + 1 }, Lines("game.tscn"),
            "both method=\"on_coin\"; actual: " + string.Join(",", Lines("game.tscn")));
    }
}
