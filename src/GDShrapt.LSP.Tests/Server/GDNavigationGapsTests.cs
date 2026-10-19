using System;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using GDShrapt.LSP.Tests.Helpers;
using GDShrapt.TestHelpers;

namespace GDShrapt.LSP.Tests.Server;

/// <summary>
/// Gaps found by LSAI live tests (#50 block 15): workspace/symbol completeness + positions,
/// autoload keys in project.godot as a cursor target, type hierarchy.
/// </summary>
[TestClass]
public class GDNavigationGapsTests
{
    private const string ProjectGodot =
        "[gd_resource type=\"ProjectSettings\" format=3]\n\nconfig_version=5\n\n[autoload]\n\nglobal=\"*res://global.gd\"\n";
    private const int AutoloadLine = 6;

    private const string PlayerGd =
        "class_name Player\nextends CharacterBody2D\n\nfunc try_jump() -> void:\n\tpass\n";
    private const string HeroGd = "class_name Hero\nextends Player\n";
    private const string ByPathGd = "extends \"res://player.gd\"\n";
    private const string GlobalGd = "extends Node\n\nfunc goto_scene(path: String) -> void:\n\tpass\n";
    private const string SceneAGd = "extends Control\n\nfunc _on_pressed() -> void:\n\tglobal.goto_scene(\"res://b.tscn\")\n";

    private GDTempProject _tmp = null!;
    private GDLspTestSession _lsp = null!;

    [TestInitialize]
    public async Task Setup()
    {
        _tmp = GDTempProject.WriteFiles(("project.godot", ProjectGodot), ("player.gd", PlayerGd), ("hero.gd", HeroGd),
            ("by_path.gd", ByPathGd), ("global.gd", GlobalGd), ("scene_a.gd", SceneAGd));
        _lsp = await GDLspTestSession.StartAsync(_tmp.Root);
    }

    [TestCleanup]
    public async Task Cleanup()
    {
        await _lsp.DisposeAsync();
        _tmp.Dispose();
    }

    private async Task<JsonElement[]> WorkspaceSymbols(string query)
    {
        var r = await _lsp.RequestAsync("workspace/symbol", new { query });
        return r.ValueKind == JsonValueKind.Array ? r.EnumerateArray().ToArray() : [];
    }

    private static (int Line, int Char) Start(JsonElement locationOrItem, string rangeName = "range")
    {
        var range = locationOrItem.TryGetProperty("location", out var loc) ? loc.GetProperty(rangeName) : locationOrItem.GetProperty(rangeName);
        var s = range.GetProperty("start");
        return (s.GetProperty("line").GetInt32(), s.GetProperty("character").GetInt32());
    }

    [TestMethod]
    [Timeout(90000)]
    public async Task WorkspaceSymbol_IncludesClassNameClass_AtIdentifier()
    {
        var player = (await WorkspaceSymbols("Player")).Where(s => s.GetProperty("name").GetString() == "Player").ToArray();

        Assert.AreEqual(1, player.Length, "class_name Player must be a workspace symbol");
        Assert.AreEqual(5, player[0].GetProperty("kind").GetInt32());
        Assert.AreEqual((0, 11), Start(player[0]));
    }

    [TestMethod]
    [Timeout(90000)]
    public async Task WorkspaceSymbol_MemberPosition_IsTheIdentifier()
    {
        var tryJump = (await WorkspaceSymbols("try_jump")).Single(s => s.GetProperty("name").GetString() == "try_jump");

        Assert.AreEqual((3, 5), Start(tryJump));
    }

    [TestMethod]
    [Timeout(90000)]
    public async Task WorkspaceSymbol_IncludesAutoload_AtProjectGodotKey()
    {
        var global = (await WorkspaceSymbols("global")).Where(s => s.GetProperty("name").GetString() == "global").ToArray();

        Assert.AreEqual(1, global.Length, "autoload 'global' must be a workspace symbol");
        StringAssert.EndsWith(global[0].GetProperty("location").GetProperty("uri").GetString(), "/project.godot");
        Assert.AreEqual((AutoloadLine, 0), Start(global[0]));
    }

    [TestMethod]
    [Timeout(90000)]
    public async Task References_OnProjectGodotAutoloadKey_ReturnUses()
    {
        var refs = await _lsp.ReferencesAsync(_tmp.UriOf("project.godot"), AutoloadLine, 2);
        var locs = refs.ValueKind == JsonValueKind.Array
            ? refs.EnumerateArray().Select(l => (l.GetProperty("uri").GetString()!.Split('/').Last(), Start(l).Line)).ToArray()
            : [];

        CollectionAssert.Contains(locs, ("scene_a.gd", 3), $"refs: {refs}");
        CollectionAssert.Contains(locs, ("project.godot", AutoloadLine), $"refs: {refs}");
    }

    [TestMethod]
    [Timeout(90000)]
    public async Task Rename_OnProjectGodotAutoloadKey_EditsKeyAndUses()
    {
        var edit = await _lsp.RequestAsync("textDocument/rename", new
        {
            textDocument = new { uri = _tmp.UriOf("project.godot") },
            position = new { line = AutoloadLine, character = 2 },
            newName = "game_state"
        });
        var files = edit.ValueKind == JsonValueKind.Object && edit.TryGetProperty("changes", out var changes)
            ? changes.EnumerateObject().Select(p => p.Name.Split('/').Last()).ToArray()
            : [];

        CollectionAssert.Contains(files, "project.godot", $"edit: {edit}");
        CollectionAssert.Contains(files, "scene_a.gd", $"edit: {edit}");
    }

    [TestMethod]
    [Timeout(90000)]
    public async Task TypeHierarchy_PrepareSupertypesSubtypes()
    {
        var items = await _lsp.RequestAsync("textDocument/prepareTypeHierarchy", new
        {
            textDocument = new { uri = _tmp.UriOf("player.gd") },
            position = new { line = 0, character = 13 }
        });
        Assert.AreEqual(JsonValueKind.Array, items.ValueKind, $"prepare: {items}");
        var player = items.EnumerateArray().Single();
        Assert.AreEqual("Player", player.GetProperty("name").GetString());
        Assert.AreEqual(5, player.GetProperty("kind").GetInt32());

        var supers = await _lsp.RequestAsync("typeHierarchy/supertypes", new { item = player });
        var superNames = supers.EnumerateArray().Select(i => i.GetProperty("name").GetString()).ToArray();
        CollectionAssert.AreEqual(new[] { "CharacterBody2D" }, superNames);

        var engineSuper = await _lsp.RequestAsync("typeHierarchy/supertypes", new { item = supers.EnumerateArray().Single() });
        CollectionAssert.AreEqual(new[] { "PhysicsBody2D" }, engineSuper.EnumerateArray().Select(i => i.GetProperty("name").GetString()).ToArray());

        var subs = await _lsp.RequestAsync("typeHierarchy/subtypes", new { item = player });
        var subNames = subs.EnumerateArray().Select(i => i.GetProperty("name").GetString()!).OrderBy(n => n, StringComparer.Ordinal).ToArray();
        CollectionAssert.AreEqual(new[] { "Hero", "by_path" }, subNames, string.Join(",", subNames));
    }
}
