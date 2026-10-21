using FluentAssertions;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace GDShrapt.Semantics.Tests;

/// <summary>
/// LSAI#53: callHierarchy/incomingCalls found 3 of 38 calls of Loadout.selected_weapon in flight-sim. The call-site
/// registry typed a receiver only when it was an identifier with a declared type, so `c.loadout.selected_weapon()`,
/// `me.loadout().take_selected()` and a `:=` local were duck-typed "*" and never callers of Loadout.
/// textDocument/references found them all: the registry now asks the semantic model for the receiver's type.
/// </summary>
[TestClass]
public class GDCallSiteChainedReceiverTests
{
    private static string CreateTempProject(params (string name, string content)[] scripts)
    {
        var tempPath = Path.Combine(Path.GetTempPath(), "gdshrapt_chain_test_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempPath);
        File.WriteAllText(Path.Combine(tempPath, "project.godot"),
            "[gd_resource type=\"ProjectSettings\" format=3]\n\nconfig_version=5\n\n[application]\nconfig/name=\"Chain\"\n");
        foreach (var (name, content) in scripts)
            File.WriteAllText(Path.Combine(tempPath, name), content);
        return tempPath;
    }

    [TestMethod]
    public void Callers_ThroughFieldChain_MethodChain_AndInferredLocal_AreCallersOfTheType()
    {
        var root = CreateTempProject(
            ("loadout.gd", "class_name Loadout\nextends RefCounted\n\nfunc take_selected() -> int:\n\treturn 1\n"),
            ("combatant.gd", "class_name Combatant\nextends RefCounted\n\nvar loadout: Loadout = Loadout.new()\n\nfunc loadout_of() -> Loadout:\n\treturn loadout\n"),
            ("battle.gd", "extends Node\n\n" +
                          "func by_field(c: Combatant) -> void:\n\tc.loadout.take_selected()\n\n" +
                          "func by_method(me: Combatant) -> void:\n\tme.loadout_of().take_selected()\n\n" +
                          "func by_inferred_local() -> void:\n\tvar l := Loadout.new()\n\tl.take_selected()\n\n" +
                          "func by_typed_param(l: Loadout) -> void:\n\tl.take_selected()\n"));
        try
        {
            using var project = GDProjectLoader.LoadProject(root, new GDScriptProjectOptions { EnableCallSiteRegistry = true });
            project.BuildCallSiteRegistry();

            var callers = project.CallSiteRegistry!.GetCallersOf("Loadout", "take_selected");

            callers.Select(c => c.SourceMethodName).Should().BeEquivalentTo(
                ["by_field", "by_method", "by_inferred_local", "by_typed_param"]);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [TestMethod]
    public void Callers_AReceiverOfAnotherType_IsNotACaller()
    {
        var root = CreateTempProject(
            ("loadout.gd", "class_name Loadout\nextends RefCounted\n\nfunc count() -> int:\n\treturn 1\n"),
            ("chunk.gd", "class_name Chunk\nextends RefCounted\n\nfunc count() -> int:\n\treturn 2\n"),
            ("user.gd", "extends Node\n\nvar chunk: Chunk = Chunk.new()\n\nfunc layers() -> int:\n\treturn self.chunk.count()\n"));
        try
        {
            using var project = GDProjectLoader.LoadProject(root, new GDScriptProjectOptions { EnableCallSiteRegistry = true });
            project.BuildCallSiteRegistry();

            project.CallSiteRegistry!.GetCallersOf("Loadout", "count").Should().BeEmpty();
            project.CallSiteRegistry!.GetCallersOf("Chunk", "count").Select(c => c.SourceMethodName)
                .Should().Equal("layers");
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }
}
