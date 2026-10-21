using FluentAssertions;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace GDShrapt.Semantics.Tests;

/// <summary>
/// LSAI#53 retest (flight-sim, file_refs/context of pylon.gd): textDocument/references of Pylon.weapon() listed
/// SimTest.weapon() calls in tests (inherited from another class), a test's local `var weapon`, and `get(x)` with a
/// non-constant name. The references of a member are the references of THAT declaration.
/// </summary>
[TestClass]
public class GDReferencesSameNameTests
{
    private static string CreateProject(params (string name, string content)[] scripts)
    {
        var root = Path.Combine(Path.GetTempPath(), "gdshrapt_samename_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        File.WriteAllText(Path.Combine(root, "project.godot"), "config_version=5\n");
        foreach (var (name, content) in scripts)
            File.WriteAllText(Path.Combine(root, name), content);
        return root;
    }

    [TestMethod]
    public void References_OfAMember_AreNotTheReferencesOfOtherSameNamedSymbols()
    {
        var root = CreateProject(
            ("pylon.gd", "class_name Pylon\nextends RefCounted\n\nvar _loaded: bool = false\n\nfunc weapon() -> int:\n\treturn 1 if _loaded else 0\n"),
            ("sim_test.gd", "class_name SimTest\nextends Node\n\nstatic func weapon(id: int) -> int:\n\treturn id\n"),
            ("game_test.gd", "class_name GameTest\nextends SimTest\n"),
            ("test_inherited.gd", "extends GameTest\n\nfunc test_a() -> void:\n\tvar w := weapon(1)\n\tprint(w)\n"),
            ("test_local.gd", "extends GameTest\n\nfunc test_b() -> void:\n\tvar weapon: int = 3\n\tprint(weapon)\n"),
            ("pad_buttons.gd", "class_name PadButtons\nextends RefCounted\n\nvar weapon: bool = false\n\nfunc set_it(r: PadButtons) -> void:\n\tr.weapon = true\n"),
            ("prefs.gd", "extends Node\n\nfunc save_it() -> void:\n\tfor prop in get_property_list():\n\t\tvar val = get(prop.name)\n\t\tprint(val)\n"),
            ("user.gd", "extends Node\n\nfunc use(p: Pylon) -> int:\n\treturn p.weapon()\n"));
        try
        {
            using var project = GDProjectLoader.LoadProject(root, new GDScriptProjectOptions { EnableCallSiteRegistry = true });
            project.BuildCallSiteRegistry();
            var collector = new GDSymbolReferenceCollector(project, new GDProjectSemanticModel(project));

            var weaponResult = collector.CollectReferences("weapon", Path.Combine(root, "pylon.gd")).References;
            var weaponRefs = weaponResult.Select(r => $"{Path.GetFileName(r.FilePath)}:{r.Line + 1}").ToList();
            var why = string.Join("; ", weaponResult.Select(r => $"{Path.GetFileName(r.FilePath)}:{r.Line + 1} {r.Confidence} {r.Kind} {r.ConfidenceReason}"));
            var loadedRefs = collector.CollectReferences("_loaded", Path.Combine(root, "pylon.gd")).References
                .Select(r => Path.GetFileName(r.FilePath)).Distinct().ToList();

            weaponRefs.Should().BeEquivalentTo(["pylon.gd:6", "user.gd:4"], why);
            loadedRefs.Should().Equal("pylon.gd");
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }
}
