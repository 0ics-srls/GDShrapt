using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using GDShrapt.CLI.Core;
using GDShrapt.TestHelpers;

namespace GDShrapt.LSP.Tests.Handlers;

/// <summary>
/// D3 at the LSP boundary: full declaration ranges, selection range on the name, symbol kinds.
/// </summary>
[TestClass]
public class GDDocumentSymbolRangeTests
{
    private GDTempProject _tmp = null!;

    [TestCleanup]
    public void Cleanup() => _tmp?.Dispose();

    private async Task<GDLspDocumentSymbol[]> SymbolsAsync(string relPath, string text)
    {
        _tmp = GDTempProject.Create((relPath, text));
        _tmp.Project!.AnalyzeAll();
        var registry = new GDServiceRegistry();
        registry.LoadModules(_tmp.Project, new GDBaseModule());
        var handler = new GDDocumentSymbolHandler(registry.GetService<IGDSymbolsHandler>()!);
        var result = await handler.HandleAsync(new GDDocumentSymbolParams
        {
            TextDocument = new GDLspTextDocumentIdentifier { Uri = GDDocumentManager.PathToUri(_tmp.PathOf(relPath)) }
        }, CancellationToken.None);
        Assert.IsNotNull(result);
        return result!;
    }

    [TestMethod]
    public async Task PlatformerGetNewAnimation_FullRange_SelectionOnName()
    {
        var text = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "TestData", "platformer", "player.gd"));

        var symbol = (await SymbolsAsync("player/player.gd", text)).Single(s => s.Name == "get_new_animation");

        Assert.AreEqual(60, symbol.Range.Start.Line);
        Assert.AreEqual(0, symbol.Range.Start.Character);
        Assert.AreEqual(74, symbol.Range.End.Line);
        Assert.AreEqual(60, symbol.SelectionRange.Start.Line);
        Assert.AreEqual(5, symbol.SelectionRange.Start.Character);
        Assert.AreEqual(5 + "get_new_animation".Length, symbol.SelectionRange.End.Character);
    }

    [TestMethod]
    public async Task Kinds_MapToLspSymbolKinds()
    {
        var symbols = await SymbolsAsync("constructs.gd", GDShrapt.LSP.Tests.Handlers.ConstructsFixture.Text);
        int KindOf(string name) => (int)symbols.Single(s => s.Name == name).Kind;

        Assert.AreEqual(6, KindOf("_ready"));
        Assert.AreEqual(24, KindOf("hit"));
        Assert.AreEqual(10, KindOf("State"));
        Assert.AreEqual(14, KindOf("MAX_HP"));
        Assert.AreEqual(5, KindOf("Inner"));
        Assert.AreEqual(13, KindOf("hp"));
    }
}

internal static class ConstructsFixture
{
    // Verbatim copy of tasks/50-godot/poc/fixture-extra/constructs.gd (LSAI repo).
    internal const string Text = @"class_name Constructs
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
}
