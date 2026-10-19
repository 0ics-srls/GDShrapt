using System.IO;
using System.Linq;
using GDShrapt.TestHelpers;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace GDShrapt.Semantics.Tests.Refactoring;

/// <summary>
/// D5: an autoload singleton name (project.godot [autoload]) has references: its project.godot key plus every
/// unshadowed use in scripts; rename edits both.
/// </summary>
[TestClass]
public class GDAutoloadReferencesTests
{
    // 0-based line 6 = the autoload key
    private const string ProjectGodot =
        "[gd_resource type=\"ProjectSettings\" format=3]\n\nconfig_version=5\n\n[autoload]\n\nglobal=\"*res://global.gd\"\n";

    private const int AutoloadKeyLine = 6;

    private const string GlobalGd = "extends Node\n\nfunc goto_scene(path: String) -> void:\n\tpass\n";

    private const string SceneAGd = "extends Control\n\nfunc _on_pressed() -> void:\n\tglobal.goto_scene(\"res://b.tscn\")\n";

    private const string ShadowGd = "extends Node\n\nfunc f() -> void:\n\tvar global := 3\n\tprint(global)\n";

    private GDTempProject? _tmp;
    private GDProjectSemanticModel? _model;

    [TestCleanup]
    public void Cleanup()
    {
        _model?.Dispose();
        _tmp?.Dispose();
    }

    private void Load(string projectGodot)
    {
        _tmp = GDTempProject.Create(("project.godot", projectGodot), ("global.gd", GlobalGd), ("scene_a.gd", SceneAGd), ("shadow.gd", ShadowGd));
        _model = new GDProjectSemanticModel(_tmp.Project!);
        _tmp.Project!.AnalyzeAll();
    }

    private (string File, int Line)[] Refs(string name) =>
        new GDSymbolReferenceCollector(_tmp!.Project!, _model).CollectReferences(name, _tmp.PathOf("scene_a.gd")).References
            .Select(r => (Path.GetFileName(r.FilePath ?? ""), r.Line)).OrderBy(r => r.Item1).ThenBy(r => r.Line).ToArray();

    [TestMethod]
    public void AutoloadName_ReferencesAreProjectGodotKeyAndScriptUsage()
    {
        Load(ProjectGodot);

        CollectionAssert.AreEqual(new[] { ("project.godot", AutoloadKeyLine), ("scene_a.gd", 3) }, Refs("global"));
    }

    [TestMethod]
    public void AutoloadName_ShadowingLocalIsNotAReference()
    {
        Load(ProjectGodot);

        Assert.IsFalse(Refs("global").Any(r => r.File == "shadow.gd"));
    }

    [TestMethod]
    public void UndeclaredNonAutoloadName_NoReferences()
    {
        Load(ProjectGodot);

        Assert.AreEqual(0, Refs("not_declared_anywhere").Length);
    }

    [TestMethod]
    public void RenameAutoload_EditsProjectGodotKeyAndUsage_NotShadow()
    {
        Load(ProjectGodot);

        var result = new GDRenameService(_tmp!.Project!, _model).PlanRename("global", "game_state", _tmp.PathOf("scene_a.gd"));
        var edits = result.StrictEdits.Select(e => (Path.GetFileName(e.FilePath), e.Line, e.Column, e.OldText)).ToArray();

        CollectionAssert.Contains(edits, ("project.godot", AutoloadKeyLine + 1, 1, "global"), "project.godot key; edits: " + string.Join(" ", edits));
        Assert.IsTrue(edits.Any(e => e.Item1 == "scene_a.gd" && e.Line == 4), "scene_a.gd line 4");
        Assert.IsFalse(edits.Any(e => e.Item1 == "shadow.gd"), "shadow.gd local must not be renamed");

        var applied = new GDRenameService(_tmp.Project!, _model)
            .ApplyEdits(File.ReadAllText(_tmp.PathOf("project.godot")), result.StrictEdits.Where(e => Path.GetFileName(e.FilePath) == "project.godot"));
        StringAssert.Contains(applied, "game_state=\"*res://global.gd\"", "value unchanged");
    }

    [TestMethod]
    public void AutoloadWithoutSingletonPrefix_NoReferences()
    {
        Load(ProjectGodot.Replace("\"*res://", "\"res://"));

        Assert.AreEqual(0, Refs("global").Length);
    }

    [TestMethod]
    public void ProjectGodotLocator_FindsKeyInRequestedSectionOnly()
    {
        const string text = "[application]\n\nconfig/name=\"x\"\nglobal=\"not this one\"\n\n[autoload]\n\nglobal=\"*res://global.gd\"\n";
        var path = Path.Combine(Path.GetTempPath(), "gds-loc-" + System.Guid.NewGuid().ToString("N") + ".godot");
        File.WriteAllText(path, text);
        try
        {
            var location = GDProjectGodotLocator.FindKey(path, "autoload", "global");

            Assert.IsNotNull(location);
            Assert.AreEqual(8, location!.Value.Line);
            Assert.AreEqual(1, location.Value.Column);
            Assert.IsNull(GDProjectGodotLocator.FindKey(path, "autoload", "missing"));
        }
        finally { File.Delete(path); }
    }
}
