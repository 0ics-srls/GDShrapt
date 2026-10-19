using System;
using System.IO;

namespace GDShrapt.LSP.Tests.Server;

/// <summary>
/// D8f: the performance trace is opt-in (GDSHRAPT_LSP_TRACE / Configure) — no fixed shared file in $TMPDIR.
/// </summary>
[TestClass]
public class GDTraceOptInTests
{
    private static readonly string DefaultPath = Path.Combine(Path.GetTempPath(), "gdshrapt-lsp-trace.log");

    [TestCleanup]
    public void Cleanup() => GDLspPerformanceTrace.Configure(Environment.GetEnvironmentVariable("GDSHRAPT_LSP_TRACE"));

    [TestMethod]
    public void Configure_NullWritesNothing_PathAppendsWithoutTruncation()
    {
        var file = Path.Combine(Path.GetTempPath(), "gds-trace-" + Guid.NewGuid().ToString("N") + ".log");
        try
        {
            GDLspPerformanceTrace.Configure(null);
            GDLspPerformanceTrace.Log("t", "nothing");
            Assert.IsFalse(File.Exists(file));

            File.WriteAllText(file, "existing\n");
            GDLspPerformanceTrace.Configure(file);
            GDLspPerformanceTrace.Log("t", "first");
            GDLspPerformanceTrace.Log("t", "second");

            var lines = File.ReadAllLines(file);
            Assert.AreEqual("existing", lines[0], "existing content must not be truncated");
            Assert.AreEqual(3, lines.Length);
            StringAssert.Contains(lines[1], "first");
            StringAssert.Contains(lines[2], "second");
        }
        finally { File.Delete(file); }
    }

    [TestMethod]
    public void StaticDefault_WithoutEnv_DoesNotWriteSharedTmpFile()
    {
        Assert.IsNull(Environment.GetEnvironmentVariable("GDSHRAPT_LSP_TRACE"), "test requires the env var to be unset");
        if (File.Exists(DefaultPath)) File.Delete(DefaultPath);
        GDLspPerformanceTrace.Configure(Environment.GetEnvironmentVariable("GDSHRAPT_LSP_TRACE"));

        GDLspPerformanceTrace.Log("t", "should not be written");

        Assert.IsFalse(File.Exists(DefaultPath), $"{DefaultPath} must not be written by default");
    }
}
