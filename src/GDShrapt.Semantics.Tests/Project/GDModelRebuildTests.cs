using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using GDShrapt.TestHelpers;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace GDShrapt.Semantics.Tests.Project;

/// <summary>
/// D1/D2: after Reload the semantic model is rebuilt on demand, never served stale or null.
/// </summary>
[TestClass]
public class GDModelRebuildTests
{
    internal const string PlayerGd =
        "class_name Player\nextends CharacterBody2D\n\nconst WALK_SPEED = 300.0\n\nfunc _physics_process(delta: float) -> void:\n\tvar s := WALK_SPEED\n\ttry_jump()\n\nfunc try_jump() -> void:\n\tpass\n";

    internal const string CoinGd =
        "extends Area2D\n\nfunc _on_body_entered(body: Node2D) -> void:\n\t(body as Player).try_jump()\n\nfunc poke(p: Player) -> void:\n\tp.try_jump()\n";

    private GDTempProject _tmp = null!;
    private GDScriptProject _project = null!;
    private GDProjectSemanticModel _model = null!;
    private GDScriptFile _player = null!;
    private GDScriptFile _coin = null!;

    [TestInitialize]
    public void Setup()
    {
        _tmp = GDTempProject.Create(("player.gd", PlayerGd), ("coin.gd", CoinGd));
        _project = _tmp.Project!;
        _model = new GDProjectSemanticModel(_project);
        _project.AnalyzeAll();
        _player = _tmp.Script("player.gd");
        _coin = _tmp.Script("coin.gd");
    }

    [TestCleanup]
    public void Cleanup()
    {
        _model.Dispose();
        _tmp.Dispose();
    }

    private GDSymbolReferences RefsToTryJump() =>
        new GDSymbolReferenceCollector(_project, _model).CollectReferences("try_jump", _player.FullPath);

    private static int DeclLine(GDSemanticModel? model, string name) =>
        model?.FindSymbol(name)?.DeclarationNode?.StartLine ?? -1;

    [TestMethod]
    public void Reload_SameText_ReferencesImmediatelyIncludeCrossFileCall()
    {
        _player.Reload(PlayerGd);

        var refs = RefsToTryJump().References;

        Assert.IsTrue(refs.Any(r => r.FilePath == _player.FullPath && r.Line == 9), "declaration in player.gd");
        Assert.IsTrue(refs.Any(r => r.FilePath == _player.FullPath && r.Line == 7), "self call in player.gd");
        Assert.IsTrue(refs.Any(r => r.FilePath == _coin.FullPath && r.Line == 6), "p.try_jump() in coin.gd line 7");
    }

    [TestMethod]
    public void Reload_WithExtraCall_NewCallSiteIncludedWithoutDelay()
    {
        _player.Reload(PlayerGd + "\nfunc extra() -> void:\n\ttry_jump()\n");

        var refs = RefsToTryJump().References;

        Assert.IsTrue(refs.Any(r => r.FilePath == _player.FullPath && r.Line == 13), "new call in extra()");
    }

    [TestMethod]
    public void Reload_CurrentModelNull_GetterBuildsFromNewContent()
    {
        _player.Reload("\n\n" + PlayerGd);

        Assert.IsNull(_player.CurrentSemanticModel);
        var model = _player.SemanticModel;
        Assert.IsNotNull(model);
        Assert.AreEqual(5, DeclLine(model, "WALK_SPEED"));
    }

    [TestMethod]
    public void NeverAnalyzedScript_NoFactory_SemanticModelStaysNull()
    {
        var path = Path.Combine(_tmp.Root, "standalone.gd");
        File.WriteAllText(path, "extends Node\n");
        var file = new GDScriptFile(new GDScriptReference(path));
        file.Reload();

        Assert.IsNull(file.SemanticModel);
    }

    [TestMethod]
    [Timeout(20000)]
    public void ConcurrentReadsAfterReload_BuildOnce_SameInstance()
    {
        var builds = 0;
        _model.LazyBuildHook = _ => Interlocked.Increment(ref builds);
        _player.Reload(PlayerGd);

        using var start = new Barrier(8);
        var results = new GDSemanticModel?[8];
        var threads = Enumerable.Range(0, 8).Select(i => new Thread(() =>
        {
            start.SignalAndWait();
            results[i] = _player.SemanticModel;
        })).ToList();
        threads.ForEach(t => t.Start());
        threads.ForEach(t => t.Join());

        Assert.AreEqual(1, builds);
        Assert.IsNotNull(results[0]);
        Assert.IsTrue(results.All(r => ReferenceEquals(r, results[0])));
    }

    [TestMethod]
    public void CachedModel_AfterReload_GetAndTryGetReturnModelOfNewText()
    {
        var before = _model.GetSemanticModel(_player);
        Assert.IsNotNull(before);

        _player.Reload("\n\n" + PlayerGd);
        var after = _model.GetSemanticModel(_player);
        var tryGet = _model.TryGetCachedSemanticModel(_player);

        Assert.IsNotNull(after);
        Assert.AreNotSame(before, after);
        Assert.AreSame(after, tryGet);
        Assert.AreEqual(5, DeclLine(after, "WALK_SPEED"));
    }

    [TestMethod]
    [Timeout(20000)]
    public void ReentrantReadDuringOwnBuild_InnerGetsNull_NoRecursion()
    {
        GDSemanticModel? inner = null;
        var innerCalled = false;
        _model.LazyBuildHook = f =>
        {
            if (f != _player) return;
            innerCalled = true;
            inner = _player.SemanticModel;
        };
        _player.Reload(PlayerGd);

        var outer = _player.SemanticModel;

        Assert.IsTrue(innerCalled);
        Assert.IsNull(inner);
        Assert.IsNotNull(outer);
    }

    [TestMethod]
    [Timeout(20000)]
    public async Task CrossReadingBuildsOnTwoThreads_NoDeadlock()
    {
        _model.LazyBuildHook = f =>
        {
            var other = f == _player ? _coin : _player;
            Thread.Sleep(50);
            _ = other.SemanticModel;
        };
        _player.Reload(PlayerGd);
        _coin.Reload(CoinGd);

        var a = Task.Factory.StartNew(() => _player.SemanticModel, TaskCreationOptions.LongRunning);
        var b = Task.Factory.StartNew(() => _coin.SemanticModel, TaskCreationOptions.LongRunning);

        await Task.WhenAll(a, b).WaitAsync(TimeSpan.FromSeconds(5));
        Assert.IsNotNull(a.Result);
        Assert.IsNotNull(b.Result);
    }
}
