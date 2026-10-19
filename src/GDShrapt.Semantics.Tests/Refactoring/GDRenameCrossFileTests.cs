using System.Linq;
using GDShrapt.TestHelpers;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace GDShrapt.Semantics.Tests.Refactoring;

/// <summary>
/// D4a/D4d: rename follows strict cross-file references by RECEIVER type (not the caller's class),
/// and `as` casts are type usages.
/// </summary>
[TestClass]
public class GDRenameCrossFileTests
{
    private const string PlayerGd =
        "class_name Player\nextends CharacterBody2D\n\nconst WALK_SPEED = 300.0\n\nfunc _physics_process(delta: float) -> void:\n\tvar s := WALK_SPEED\n\ttry_jump()\n\nfunc try_jump() -> void:\n\tpass\n";

    private const string CoinGd =
        "extends Area2D\n\nfunc _on_body_entered(body: Node2D) -> void:\n\t(body as Player).try_jump()\n\nfunc poke(p: Player) -> void:\n\tp.try_jump()\n";

    private const string EnemyGd =
        "extends CharacterBody2D\n\nfunc try_jump() -> void:\n\tpass\n\nfunc _ready() -> void:\n\ttry_jump()\n";

    private const string DuckGd = "extends Node\n\nfunc d(x) -> void:\n\tx.try_jump()\n";

    private const string TypeUsageGd =
        "extends Node\n\nfunc t(b: Node) -> void:\n\tif b is Player:\n\t\tpass\n\tvar p = b as Player\n";

    private GDTempProject _tmp = null!;
    private GDProjectSemanticModel _model = null!;

    [TestInitialize]
    public void Setup()
    {
        _tmp = GDTempProject.Create(("player.gd", PlayerGd), ("coin.gd", CoinGd), ("enemy.gd", EnemyGd),
            ("duck.gd", DuckGd), ("type_usage.gd", TypeUsageGd));
        _model = new GDProjectSemanticModel(_tmp.Project!);
        _tmp.Project!.AnalyzeAll();
    }

    [TestCleanup]
    public void Cleanup()
    {
        _model.Dispose();
        _tmp.Dispose();
    }

    private GDRenameResult Rename(string oldName, string newName) =>
        new GDRenameService(_tmp.Project!, _model).PlanRename(oldName, newName, _tmp.PathOf("player.gd"));

    private int[] StrictLines(GDRenameResult r, string file) =>
        r.StrictEdits.Where(e => e.FilePath == _tmp.PathOf(file)).Select(e => e.Line).OrderBy(l => l).ToArray();

    [TestMethod]
    public void RenameMethod_EditsDeclaringFileAndTypedCallersInUnrelatedClasses()
    {
        var result = Rename("try_jump", "jump");

        var player = StrictLines(result, "player.gd");
        var coin = StrictLines(result, "coin.gd");
        CollectionAssert.AreEqual(new[] { 8, 10 }, player, "self call + declaration; actual: " + string.Join(",", player));
        CollectionAssert.AreEqual(new[] { 4, 7 }, coin, "(body as Player).try_jump() and p.try_jump(); actual: " + string.Join(",", coin) + " potential: " + string.Join(",", result.PotentialEdits.Where(e => e.FilePath == _tmp.PathOf("coin.gd")).Select(e => e.Line + ":" + e.ConfidenceReason)));
    }

    [TestMethod]
    public void RenameMethod_DoesNotEditSameNamedMemberOfUnrelatedClass()
    {
        var result = Rename("try_jump", "jump");

        Assert.IsFalse(result.StrictEdits.Any(e => e.FilePath == _tmp.PathOf("enemy.gd")), "enemy.gd has its own try_jump");
    }

    [TestMethod]
    public void ClassReferences_IncludeAsCastAndParameterAnnotation()
    {
        var refs = new GDSymbolReferenceCollector(_tmp.Project!, _model)
            .CollectReferences("Player", _tmp.PathOf("player.gd")).References
            .Where(r => r.FilePath == _tmp.PathOf("coin.gd")).Select(r => r.Line).ToHashSet();

        Assert.IsTrue(refs.Contains(3), "coin.gd line 4: body as Player");
        Assert.IsTrue(refs.Contains(5), "coin.gd line 6: p: Player");
    }

    [TestMethod]
    public void RenameClass_EditsClassNameCastAndAnnotation()
    {
        var result = Rename("Player", "Hero");

        CollectionAssert.Contains(StrictLines(result, "player.gd"), 1, "class_name Player");
        var coin = StrictLines(result, "coin.gd");
        CollectionAssert.Contains(coin, 4, "as Player");
        CollectionAssert.Contains(coin, 6, "p: Player");
    }

    [TestMethod]
    public void DuckTypedCall_StaysPotential_NotApplied()
    {
        var result = Rename("try_jump", "jump");
        var duck = _tmp.PathOf("duck.gd");

        Assert.IsFalse(result.StrictEdits.Any(e => e.FilePath == duck));
        Assert.IsTrue(result.PotentialEdits.Any(e => e.FilePath == duck && e.Line == 4), "x.try_jump() is a potential edit");
    }

    [TestMethod]
    public void TypeUsages_AsIsCast_IsIsTypeCheck()
    {
        var usages = _tmp.Script("type_usage.gd").SemanticModel!.GetTypeUsages("Player");

        Assert.IsTrue(usages.Any(u => u.Kind == GDTypeUsageKind.Cast && u.Line == 5), "b as Player");
        Assert.IsTrue(usages.Any(u => u.Kind == GDTypeUsageKind.TypeCheck && u.Line == 3), "b is Player");
    }
}
