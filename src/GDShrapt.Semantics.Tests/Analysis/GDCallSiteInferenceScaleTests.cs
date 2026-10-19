using System;
using System.Diagnostics;
using System.Linq;
using GDShrapt.TestHelpers;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace GDShrapt.Semantics.Tests.Analysis;

/// <summary>
/// D7a: call-site inference is linear — typed methods skip collection, call names are indexed once.
/// </summary>
[TestClass]
public class GDCallSiteInferenceScaleTests
{
    private const string PlayerGd =
        "class_name Player\nextends CharacterBody2D\n\nconst WALK_SPEED = 300.0\n\nfunc _physics_process(delta: float) -> void:\n\tvar s := WALK_SPEED\n\ttry_jump()\n\nfunc try_jump() -> void:\n\tpass\n";

    private const string CoinGd =
        "extends Area2D\n\nfunc _on_body_entered(body: Node2D) -> void:\n\t(body as Player).try_jump()\n\nfunc poke(p: Player) -> void:\n\tp.try_jump()\n";

    private const string UtilGd =
        "extends Node\n\nfunc scale(x):\n\treturn x * 2\n\nfunc _ready() -> void:\n\tscale(3)\n\tscale(1.5)\n";

    private GDTempProject? _tmp;

    [TestCleanup]
    public void Cleanup() => _tmp?.Dispose();

    private GDMethodSignatureInferenceEngine Engine(params (string, string)[] files)
    {
        _tmp = GDTempProject.Create(files);
        _tmp.Project!.AnalyzeAll();
        var engine = new GDMethodSignatureInferenceEngine(_tmp.Project);
        engine.BuildAll();
        return engine;
    }

    private static string[] UnionNames(GDParameterInferenceReport? p) =>
        p?.InferredUnionType?.Types.Select(t => t.DisplayName).OrderBy(n => n).ToArray() ?? [];

    [TestMethod]
    public void AllParametersTyped_CallSitesNotCollected()
    {
        var engine = Engine(("player.gd", PlayerGd), ("coin.gd", CoinGd), ("util.gd", UtilGd));

        Assert.AreEqual(0, engine.CallSiteCollector.InvocationsFor("coin", "poke"), "poke(p: Player) is fully typed");
        Assert.IsTrue(engine.CallSiteCollector.InvocationsFor("util", "scale") > 0, "scale(x) is untyped and must be collected");
    }

    [TestMethod]
    public void UntypedParameter_CrossFileCall_StillInferredInt()
    {
        const string lib = "class_name Lib\nextends Node\n\nfunc f(x):\n\treturn x\n";
        const string user = "extends Node\n\nfunc _ready() -> void:\n\tLib.new().f(3)\n";

        var report = Engine(("lib.gd", lib), ("user.gd", user)).GetMethodReport("Lib", "f");

        CollectionAssert.AreEqual(new[] { "int" }, UnionNames(report?.GetParameter("x")));
    }

    [TestMethod]
    public void DynamicCallString_StillCollectedForUntypedMethod()
    {
        const string lib = "class_name Lib\nextends Node\n\nfunc f(x):\n\treturn x\n";
        const string user = "extends Node\n\nfunc go(o) -> void:\n\to.call(\"f\", 3)\n";

        var report = Engine(("lib.gd", lib), ("user.gd", user)).GetMethodReport("Lib", "f");

        Assert.IsTrue(report?.GetParameter("x")?.CallSiteArguments.Count >= 1, "o.call(\"f\", 3) is a call site of f");
    }

    [TestMethod]
    public void NameIndex_ListsOnlyScriptsThatCallTheName()
    {
        const string defines = "class_name A\nextends Node\n\nfunc f(x):\n\treturn x\n";
        const string calls = "extends Node\n\nfunc _ready() -> void:\n\tA.new().f(1)\n";
        const string unrelated = "extends Node\n\nfunc g() -> void:\n\tpass\n";

        var engine = Engine(("a.gd", defines), ("b.gd", calls), ("c.gd", unrelated));
        var scripts = engine.CallSiteCollector.ScriptsCalling("f").Select(s => System.IO.Path.GetFileName(s.FullPath)).ToArray();

        CollectionAssert.AreEqual(new[] { "b.gd" }, scripts);
    }

    [TestMethod]
    [Timeout(60000)]
    public void EightHundredFanInFiles_EnrichWithCallSiteAnalysis_UnderTenSeconds()
    {
        var files = new (string, string)[801];
        files[0] = ("player.gd", PlayerGd);
        for (var i = 0; i < 800; i++)
        {
            var helpers = string.Concat(Enumerable.Range(0, 15)
                .Select(j => $"\nfunc helper_{j}(v: int) -> int:\n\treturn v * {j} + {i}\n"));
            files[i + 1] = ($"gen/g{i:0000}.gd", $"extends Node\n\nfunc call_{i}(p: Player) -> void:\n\tp.try_jump()\n{helpers}");
        }
        _tmp = GDTempProject.Create(files);
        _tmp.Project!.AnalyzeAll();

        var sw = Stopwatch.StartNew();
        _tmp.Project.EnrichWithCallSiteAnalysis();
        sw.Stop();

        Assert.IsTrue(sw.Elapsed < TimeSpan.FromSeconds(10), $"EnrichWithCallSiteAnalysis took {sw.Elapsed.TotalSeconds:F1}s");
    }

    [TestMethod]
    public void UntypedParameter_IntAndFloatCalls_InferredUnionIntFloat()
    {
        var report = Engine(("player.gd", PlayerGd), ("coin.gd", CoinGd), ("util.gd", UtilGd)).GetMethodReport("util", "scale");

        CollectionAssert.AreEqual(new[] { "float", "int" }, UnionNames(report?.GetParameter("x")));
    }
}
