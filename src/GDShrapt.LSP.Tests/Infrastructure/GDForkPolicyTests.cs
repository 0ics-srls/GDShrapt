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
}
