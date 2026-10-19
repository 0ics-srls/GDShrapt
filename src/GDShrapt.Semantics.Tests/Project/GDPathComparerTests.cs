using System;
using System.IO;
using System.Linq;
using GDShrapt.TestHelpers;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace GDShrapt.Semantics.Tests.Project;

/// <summary>
/// D8d/D8e: path comparison follows the file system (case-sensitive on Linux) and same-named scripts without
/// class_name do not overwrite each other in call-site enrichment.
/// </summary>
[TestClass]
public class GDPathComparerTests
{
    private GDTempProject? _tmp;

    [TestCleanup]
    public void Cleanup() => _tmp?.Dispose();

    // MSTest 3.7 has no [OSCondition]: OS-specific tests guard at runtime.
    private static void RequireLinux() { if (!OperatingSystem.IsLinux()) Assert.Inconclusive("Linux only"); }

    [TestMethod]
    public void ScriptReference_Linux_CaseSensitive()
    {
        RequireLinux();
        Assert.AreNotEqual(new GDScriptReference("/p/Player.gd"), new GDScriptReference("/p/player.gd"));
        Assert.AreEqual(new GDScriptReference("/p/player.gd"), new GDScriptReference("/p/player.gd"));
    }

    [TestMethod]
    public void ScriptReference_WindowsMac_CaseInsensitive()
    {
        if (!OperatingSystem.IsWindows() && !OperatingSystem.IsMacOS()) Assert.Inconclusive("Windows/macOS only");
        var a = new GDScriptReference(Path.Combine(Path.GetTempPath(), "Player.gd"));
        var b = new GDScriptReference(Path.Combine(Path.GetTempPath(), "player.gd"));
        Assert.AreEqual(a, b);
        Assert.AreEqual(a.GetHashCode(), b.GetHashCode());
    }

    [TestMethod]
    public void Linux_FilesDifferingOnlyByCase_AreTwoScripts()
    {
        RequireLinux();
        _tmp = GDTempProject.Create(("Player.gd", "extends Node\n"), ("player.gd", "extends Node2D\n"));

        Assert.AreEqual(2, _tmp.Project!.ScriptFiles.Count());
        Assert.AreNotSame(_tmp.Script("Player.gd"), _tmp.Script("player.gd"));
    }

    [TestMethod]
    public void SameNamedScriptsWithoutClassName_EnrichmentDoesNotCrossAssign()
    {
        _tmp = GDTempProject.Create(
            ("a/util.gd", "extends Node\n\nfunc fa(x):\n\treturn x\n\nfunc _ready():\n\tfa(1)\n"),
            ("b/util.gd", "extends Node\n\nfunc fb(y):\n\treturn y\n\nfunc _ready():\n\tfb(\"s\")\n"));
        var project = _tmp.Project!;
        project.AnalyzeAll();

        project.EnrichWithCallSiteAnalysis();

        var a = _tmp.Script("a/util.gd").SemanticModel!;
        var b = _tmp.Script("b/util.gd").SemanticModel!;
        Assert.IsNull(b.GetCallSiteTypes("fa", "x"), "a/util.gd's report must not be injected into b/util.gd");
        Assert.IsNull(a.GetCallSiteTypes("fb", "y"), "b/util.gd's report must not be injected into a/util.gd");
    }
}
