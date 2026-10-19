using System.IO;
using System.Linq;
using GDShrapt.TestHelpers;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace GDShrapt.Semantics.Tests.Refactoring;

/// <summary>
/// Callback node instanced through two scene levels (game → menu_variant.tscn whose ROOT instances menu.tscn,
/// which carries the script) — the Godot platformer demo's PauseMenu shape. Headers use the Godot 4.4+
/// attribute order (`unique_id=` before `instance=`).
/// </summary>
[TestClass]
public class GDNestedSceneInstanceRefsTests
{
    private const string MenuGd = "class_name Menu\nextends Control\n\nfunc _on_coin_collected() -> void:\n\tpass\n";
    private const string PlayerGd = "class_name Player\nextends Node2D\n\nsignal coin_collected()\n";

    private const string PlayerTscn =
        "[gd_scene load_steps=2 format=3]\n\n[ext_resource type=\"Script\" path=\"res://player.gd\" id=\"1\"]\n\n[node name=\"Player\" type=\"Node2D\"]\nscript = ExtResource(\"1\")\n";
    private const string MenuTscn =
        "[gd_scene load_steps=2 format=3]\n\n[ext_resource type=\"Script\" path=\"res://menu.gd\" id=\"2\"]\n\n[node name=\"Menu\" type=\"Control\"]\nscript = ExtResource(\"2\")\n";
    private const string MenuVariantTscn =
        "[gd_scene load_steps=2 format=3]\n\n[ext_resource type=\"PackedScene\" path=\"res://menu.tscn\" id=\"1_m\"]\n\n[node name=\"Menu\" instance=ExtResource(\"1_m\")]\n";
    // 0-based line 11 = the connection
    private const string GameTscn =
        "[gd_scene load_steps=3 format=3]\n\n" +
        "[ext_resource type=\"PackedScene\" path=\"res://player.tscn\" id=\"1\"]\n" +
        "[ext_resource type=\"PackedScene\" path=\"res://menu_variant.tscn\" id=\"4_x\"]\n\n" +
        "[node name=\"Game\" type=\"Node\"]\n\n" +
        "[node name=\"Player\" parent=\".\" instance=ExtResource(\"1\")]\n\n" +
        "[node name=\"UI\" type=\"CanvasLayer\" parent=\".\"]\n" +
        "[node name=\"Menu\" parent=\"UI\" unique_id=1388998718 instance=ExtResource(\"4_x\")]\n" +
        "[connection signal=\"coin_collected\" from=\"Player\" to=\"UI/Menu\" method=\"_on_coin_collected\"]\n";

    private GDTempProject _tmp = null!;
    private GDProjectSemanticModel _model = null!;

    [TestInitialize]
    public void Setup()
    {
        _tmp = GDTempProject.Create(("menu.gd", MenuGd), ("player.gd", PlayerGd), ("player.tscn", PlayerTscn),
            ("menu.tscn", MenuTscn), ("menu_variant.tscn", MenuVariantTscn), ("game.tscn", GameTscn));
        _model = new GDProjectSemanticModel(_tmp.Project!);
        _tmp.Project!.AnalyzeAll();
    }

    [TestCleanup]
    public void Cleanup() { _model.Dispose(); _tmp.Dispose(); }

    [TestMethod]
    public void ResolveSceneNodeTypeName_TwoLevelInstance_IsInnermostRootScriptType()
    {
        var game = _tmp.Project!.SceneTypesProvider!.GetSceneInfo("res://game.tscn")!;

        Assert.AreEqual("Menu", _model.ResolveSceneNodeTypeName(game, game.Nodes.Single(n => n.Path == "UI/Menu")));
    }

    [TestMethod]
    public void CallbackReferences_TwoLevelInstance_IncludeConnectionLine()
    {
        var refs = new GDSymbolReferenceCollector(_tmp.Project!, _model)
            .CollectReferences("_on_coin_collected", _tmp.PathOf("menu.gd")).References
            .Where(r => Path.GetFileName(r.FilePath ?? "") == "game.tscn").Select(r => r.Line).ToArray();

        CollectionAssert.AreEqual(new[] { 11 }, refs);
    }
}
