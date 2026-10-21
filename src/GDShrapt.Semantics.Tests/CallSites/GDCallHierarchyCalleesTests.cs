using FluentAssertions;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace GDShrapt.Semantics.Tests;

/// <summary>
/// LSAI#53 (flight-sim II Loadout.take_selected): outgoingCalls answered `range` with detail Loadout, `size` with
/// Array[Pylon] and `_pylons[i].holds()` / `Pylon.empty()` with "*", all at the call site. A call into a Godot
/// builtin is reported as such ("builtin Array", "builtin @GlobalScope"), a typed-array element call and a static
/// call on a class_name resolve to the declaration.
/// </summary>
[TestClass]
public class GDCallHierarchyCalleesTests
{
    [TestMethod]
    public void Callees_TypedArrayElement_StaticOnClassName_Builtins()
    {
        var root = Path.Combine(Path.GetTempPath(), "gdshrapt_callees_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        File.WriteAllText(Path.Combine(root, "project.godot"), "config_version=5\n");
        File.WriteAllText(Path.Combine(root, "pylon.gd"),
            "class_name Pylon\nextends RefCounted\n\nfunc holds(id: int) -> bool:\n\treturn id > 0\n\nstatic func empty() -> Pylon:\n\treturn Pylon.new()\n");
        File.WriteAllText(Path.Combine(root, "loadout.gd"),
            "class_name Loadout\nextends RefCounted\n\nvar _pylons: Array[Pylon] = []\n\n" +
            "func take_selected(id: int) -> Pylon:\n" +
            "\tfor i in range(_pylons.size()):\n" +
            "\t\tif _pylons[i].holds(id):\n" +
            "\t\t\treturn _pylons[i]\n" +
            "\treturn Pylon.empty()\n");
        try
        {
            using var project = GDProjectLoader.LoadProject(root, new GDScriptProjectOptions { EnableCallSiteRegistry = true });
            project.BuildCallSiteRegistry();

            var callees = new GDCallHierarchyService(project).GetCallees(Path.Combine(root, "loadout.gd"), "take_selected")
                .ToDictionary(e => e.Symbol.Name, e => e.Symbol);

            callees["holds"].ClassName.Should().Be("Pylon");
            callees["holds"].FilePath.Should().EndWith("pylon.gd");
            callees["empty"].ClassName.Should().Be("Pylon");
            callees["empty"].FilePath.Should().EndWith("pylon.gd");
            callees["size"].ClassName.Should().Be("builtin Array");
            callees["range"].ClassName.Should().Be("builtin @GlobalScope");
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }
}
