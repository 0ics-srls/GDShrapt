using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;

namespace GDShrapt.LSP.Tests.Infrastructure;

/// <summary>
/// Guards the 0ics fork policy: runtime target and CI triggers (no CI minutes on normal pushes).
/// </summary>
[TestClass]
public class GDForkPolicyTests
{
    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null && !File.Exists(Path.Combine(dir.FullName, "src", "GDShrapt.sln")))
            dir = dir.Parent;
        Assert.IsNotNull(dir, "repo root (src/GDShrapt.sln) not found above test output");
        return dir!.FullName;
    }

    private static IEnumerable<(string File, string Text)> Workflows()
    {
        var wf = Path.Combine(RepoRoot(), ".github", "workflows");
        if (!Directory.Exists(wf)) return [];
        return Directory.EnumerateFiles(wf, "*.y*ml").Select(f => (f, File.ReadAllText(f)));
    }

    [TestMethod]
    public void Runtime_IsNet10() =>
        StringAssert.StartsWith(RuntimeInformation.FrameworkDescription, ".NET 10");

    [TestMethod]
    public void Workflows_HaveNoPullRequestTrigger()
    {
        foreach (var (file, text) in Workflows())
            Assert.IsFalse(Regex.IsMatch(text, @"^\s*pull_request\s*:", RegexOptions.Multiline), file);
    }

    [TestMethod]
    public void Workflows_PushOnlyOnReleaseBranch()
    {
        foreach (var (file, text) in Workflows())
        {
            if (!Regex.IsMatch(text, @"^\s{2}push\s*:", RegexOptions.Multiline)) continue;
            var push = Regex.Match(text, @"^\s{2}push\s*:\s*\r?\n\s{4}branches\s*:\s*\[([^\]]*)\]", RegexOptions.Multiline);
            Assert.IsTrue(push.Success, $"{file}: push trigger without branch filter");
            CollectionAssert.AreEqual(new[] { "release-0ics" },
                push.Groups[1].Value.Split(',').Select(s => s.Trim().Trim('\'', '"')).ToArray(), file);
        }
    }

    private const string CiFilter =
        "TestCategory!=StressTests&TestCategory!=LongRunning&TestCategory!=ManualVerification&TestCategory!=SmokeTests&TestCategory!=Performance&TestCategory!=Memory";

    private static string ReleaseWorkflow()
    {
        var path = Path.Combine(RepoRoot(), ".github", "workflows", "release-0ics.yml");
        Assert.IsTrue(File.Exists(path), "release-0ics.yml must exist");
        return File.ReadAllText(path);
    }

    [TestMethod]
    public void ReleaseWorkflow_TriggersOnlyOnReleaseBranchPushAndManualDispatch()
    {
        var text = ReleaseWorkflow();
        var on = Regex.Match(text, @"^on:\s*\n((?:[ \t]+.*\n|\s*\n)*)", RegexOptions.Multiline);
        Assert.IsTrue(on.Success, "on: block not found");
        var triggers = Regex.Matches(on.Groups[1].Value, @"^\s{2}([a-z_]+)\s*:", RegexOptions.Multiline)
            .Select(m => m.Groups[1].Value).OrderBy(t => t).ToArray();

        CollectionAssert.AreEqual(new[] { "push", "workflow_dispatch" }, triggers);
        StringAssert.Matches(text, new Regex(@"^\s{2}push\s*:\s*\n\s{4}branches\s*:\s*\[release-0ics\]", RegexOptions.Multiline));
    }

    [TestMethod]
    public void ReleaseWorkflow_PublishesFrameworkDependentWithVersionSuffix_AndRunsCiFilter()
    {
        var text = ReleaseWorkflow();

        StringAssert.Contains(text, "--no-self-contained");
        StringAssert.Contains(text, "-p:VersionSuffix=alpha.7.");
        StringAssert.Contains(text, CiFilter);
    }

    [TestMethod]
    public void ReleaseVersionFile_ContainsPositiveInteger()
    {
        var path = Path.Combine(RepoRoot(), "0ics", "VERSION");
        Assert.IsTrue(File.Exists(path), "0ics/VERSION must exist");
        Assert.IsTrue(int.TryParse(File.ReadAllText(path).Trim(), out var n) && n > 0, "0ics/VERSION must be a positive integer");
    }
}
