using System.IO;
using GDShrapt.CLI.Core;
using GDShrapt.Semantics;
using GDShrapt.TestHelpers;

namespace GDShrapt.CLI.Tests.Handlers;

/// <summary>
/// D3: document symbols come from the file's own AST — nested, 1-based, full declaration ranges.
/// </summary>
[TestClass]
public class GDSymbolsHierarchyTests
{
    // Verbatim copy of tasks/50-godot/poc/fixture-extra/constructs.gd (LSAI repo).
    internal const string ConstructsGd = @"class_name Constructs
extends Node2D
## Synthetic POC fixture: every GDScript 2.0 construct LSAI must see.

signal hit(damage: int, source: Node)
signal died

enum State { IDLE, RUN, JUMP }
enum { ANON_A, ANON_B }

const MAX_HP: int = 100
static var instances: int = 0

## Health in points.
@export var hp: int = MAX_HP
@export_range(0, 10) var level := 1
@onready var player: Player = $Player
var items: Array[String] = []
var lookup: Dictionary[String, int] = {}
var state: State = State.IDLE

var speed: float = 1.0:
	get:
		return speed
	set(value):
		speed = clampf(value, 0.0, 10.0)


class Inner extends RefCounted:
	var value := 0

	func bump() -> int:
		value += 1
		return value


static func make() -> Constructs:
	instances += 1
	return Constructs.new()


func _ready() -> void:
	hit.connect(_on_hit)
	died.connect(func() -> void: print(""died""))
	var doubled := items.map(func(s: String) -> String: return s + s)
	print(doubled)
	player.coin_collected.connect(_on_coin)


func _on_hit(damage: int, _source: Node) -> void:
	hp -= damage
	match state:
		State.IDLE, State.RUN:
			state = State.JUMP
		var other when other == State.JUMP:
			pass
	if hp <= 0:
		died.emit()


func _on_coin() -> void:
	player.try_jump()


func wait_and_heal() -> void:
	await get_tree().create_timer(1.0).timeout
	hp = MAX_HP
	var inner := Inner.new()
	inner.bump()
";

    private GDTempProject _tmp = null!;

    [TestCleanup]
    public void Cleanup() => _tmp?.Dispose();

    private IReadOnlyList<GDDocumentSymbol> Symbols(string relPath, string text)
    {
        _tmp = GDTempProject.Create((relPath, text));
        _tmp.Project!.AnalyzeAll();
        return new GDSymbolsHandler(_tmp.Project).GetSymbols(_tmp.PathOf(relPath));
    }

    private static IEnumerable<GDDocumentSymbol> Flatten(IEnumerable<GDDocumentSymbol> symbols) =>
        symbols.SelectMany(s => new[] { s }.Concat(Flatten(s.Children ?? Array.Empty<GDDocumentSymbol>())));

    [TestMethod]
    public void TopLevel_ContainsExactlyClassMembers_InSourceOrder()
    {
        var names = Symbols("constructs.gd", ConstructsGd).Select(s => s.Name).ToArray();

        CollectionAssert.AreEqual(new[]
        {
            "hit", "died", "State", "", "MAX_HP", "instances", "hp", "level", "player", "items", "lookup",
            "state", "speed", "Inner", "make", "_ready", "_on_hit", "_on_coin", "wait_and_heal"
        }, names);
    }

    [TestMethod]
    public void InnerClass_HasChildren_NotDuplicatedAtTopLevel()
    {
        var symbols = Symbols("constructs.gd", ConstructsGd);
        var inner = symbols.Single(s => s.Name == "Inner");

        CollectionAssert.AreEqual(new[] { "value", "bump" }, inner.Children!.Select(c => c.Name).ToArray());
        Assert.IsFalse(symbols.Any(s => s.Name == "bump"));
    }

    [TestMethod]
    public void NoLocalsParametersBindingsOrInheritedMembers()
    {
        var all = Flatten(Symbols("constructs.gd", ConstructsGd)).Select(s => s.Name).ToHashSet();

        foreach (var name in new[] { "damage", "_source", "doubled", "inner", "other", "get_tree", "move_and_slide" })
            Assert.IsFalse(all.Contains(name), $"unexpected symbol '{name}'");
    }

    [TestMethod]
    public void Method_LineIsFuncLine_EndLineIsLastBodyLine()
    {
        var onHit = Symbols("constructs.gd", ConstructsGd).Single(s => s.Name == "_on_hit");

        Assert.AreEqual(50, onHit.Line);
        Assert.AreEqual(6, onHit.Column);
        Assert.AreEqual(58, onHit.EndLine);
    }

    [TestMethod]
    public void PropertyWithGetSet_RangeCoversSetterBody()
    {
        var speed = Symbols("constructs.gd", ConstructsGd).Single(s => s.Name == "speed");

        Assert.AreEqual(22, speed.Line);
        Assert.AreEqual(26, speed.EndLine);
    }

    [TestMethod]
    public void Type_IsDeclarationSignature()
    {
        var symbols = Symbols("constructs.gd", ConstructsGd);

        Assert.AreEqual("signal hit(damage: int, source: Node)", symbols.Single(s => s.Name == "hit").Type);
        Assert.AreEqual("static func make() -> Constructs", symbols.Single(s => s.Name == "make").Type);
    }

    [TestMethod]
    public void SyntaxErrors_WellFormedMembersStillReturned()
    {
        const string broken = "extends Node\n\nfunc good() -> void:\n\tpass\n\nfunc bad(:\n\tvar = \n\nvar ok := 1\n";

        var names = Symbols("broken.gd", broken).Select(s => s.Name).ToList();

        CollectionAssert.Contains(names, "good");
        CollectionAssert.Contains(names, "ok");
    }

    [TestMethod]
    public void BuiltInTypeFile_NotAProjectScript_UsesBuiltInBranch()
    {
        _tmp = GDTempProject.Create(("main.gd", "extends Node\n"));
        Directory.CreateDirectory(GDBuiltInFileHelper.BuiltInTypesDir);
        var path = Path.Combine(GDBuiltInFileHelper.BuiltInTypesDir, "GDSymbolsHierarchyFake" + Guid.NewGuid().ToString("N") + ".gd");
        File.WriteAllText(path, "extends Object\n\nfunc fake_builtin_method() -> void:\n\tpass\n");
        try
        {
            Assert.IsTrue(GDBuiltInFileHelper.IsBuiltInTypeFile(path));
            var names = new GDSymbolsHandler(_tmp.Project!).GetSymbols(path).Select(s => s.Name).ToList();
            CollectionAssert.Contains(names, "fake_builtin_method");
        }
        finally { File.Delete(path); }
    }
}
