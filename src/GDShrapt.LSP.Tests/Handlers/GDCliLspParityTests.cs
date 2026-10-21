using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using GDShrapt.Abstractions;
using GDShrapt.CLI.Core;
using GDShrapt.Semantics;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using FluentAssertions;

namespace GDShrapt.LSP.Tests;

/// <summary>
/// Guardrail tests proving the LSP and the CLI.Core handlers produce identical results.
/// References return the same set (reads = all, including potential), rename applies only
/// Strict edits (writes = strict), and prepare-rename covers the whole identifier.
/// </summary>
[TestClass]
public class GDCliLspParityTests
{
    private static readonly string TestProjectPath = GetTestProjectPath();

    private static string GetTestProjectPath()
    {
        var baseDir = System.IO.Directory.GetCurrentDirectory();
        var projectRoot = System.IO.Path.GetFullPath(System.IO.Path.Combine(baseDir, "..", "..", "..", "..", ".."));
        return System.IO.Path.Combine(projectRoot, "testproject", "GDShrapt.TestProject");
    }

    private sealed class Harness
    {
        public required IGDFindRefsHandler FindRefs { get; init; }
        public required IGDGoToDefHandler GoToDef { get; init; }
        public required IGDRenameHandler Rename { get; init; }
    }

    private static Harness Setup()
    {
        var context = new GDDefaultProjectContext(TestProjectPath);
        var project = new GDScriptProject(context, new GDScriptProjectOptions());
        project.LoadScripts();
        project.AnalyzeAll();

        var registry = new GDServiceRegistry();
        registry.LoadModules(project, new GDBaseModule());

        return new Harness
        {
            FindRefs = registry.GetService<IGDFindRefsHandler>()!,
            GoToDef = registry.GetService<IGDGoToDefHandler>()!,
            Rename = registry.GetService<IGDRenameHandler>()!
        };
    }

    private static string ScriptPath(string scriptName)
        => System.IO.Path.Combine(TestProjectPath, "test_scripts", scriptName);

    private static IEnumerable<GDCliReferenceLocation> FlattenCli(IEnumerable<GDReferenceGroup> groups)
    {
        foreach (var g in groups)
        {
            foreach (var loc in g.Locations)
                yield return loc;
            foreach (var loc in FlattenCli(g.Overrides))
                yield return loc;
        }
    }

    // base_entity.gd line 27 (0-based): func take_damage(amount: int, source: Node = null) -> void:
    // "take_damage" starts at col 5, length 11.
    private const string RichScript = "base_entity.gd";
    private const int RichLine0 = 27;
    private const int RichCol0 = 5;
    private const string RichName = "take_damage";

    [TestMethod]
    public async Task References_LspSet_EqualsCliSet_IncludingPotential()
    {
        var h = Setup();
        var filePath = ScriptPath(RichScript);

        // CLI side: flatten all groups + overrides (no confidence filtering).
        var symbolName = h.GoToDef.FindDefinition(filePath, RichLine0 + 1, RichCol0 + 1)!.SymbolName!;
        var cliGroups = h.FindRefs.FindReferences(symbolName, filePath);
        var cliCount = FlattenCli(cliGroups).Count();

        // LSP side: same handler the server uses.
        var lspHandler = new GDReferencesHandler(h.FindRefs, h.GoToDef);
        var lspResult = await lspHandler.HandleAsync(new GDReferencesParams
        {
            TextDocument = new GDLspTextDocumentIdentifier { Uri = GDDocumentManager.PathToUri(filePath) },
            Position = new GDLspPosition(RichLine0, RichCol0),
            Context = new GDReferenceContext { IncludeDeclaration = true }
        }, CancellationToken.None);

        // The CLI set, except duck-typed references when an unrelated class declares the same name: LSP carries no
        // confidence, and those are as likely the other class's (LSAI#53)
        var ambiguous = h.FindRefs.FindAllReferences(symbolName, filePath).UnrelatedGroups.Count > 0;
        var duckTyped = FlattenCli(cliGroups).Count(l =>
            l.Confidence is GDReferenceConfidence.Potential or GDReferenceConfidence.NameMatch && !l.IsContractString);
        var expected = ambiguous ? cliCount - duckTyped : cliCount;

        lspResult.Should().NotBeNull();
        lspResult!.Length.Should().Be(expected,
            "LSP references return the CLI find-refs set, without the duck-typed ones of an ambiguous name");
        cliCount.Should().BeGreaterThanOrEqualTo(3, "this symbol has declaration + overrides + cross-file uses");
    }

    [TestMethod]
    public async Task Rename_LspWorkspaceEdit_EqualsCliStrictEdits_NotAllEdits()
    {
        var h = Setup();
        var filePath = ScriptPath(RichScript);

        var symbolName = h.GoToDef.FindDefinition(filePath, RichLine0 + 1, RichCol0 + 1)!.SymbolName!;
        var cliPlan = h.Rename.Plan(symbolName, "take_damage_renamed", filePath);

        var lspHandler = new GDLspRenameHandler(h.Rename, h.GoToDef);
        var lspResult = await lspHandler.HandleAsync(new GDRenameParams
        {
            TextDocument = new GDLspTextDocumentIdentifier { Uri = GDDocumentManager.PathToUri(filePath) },
            Position = new GDLspPosition(RichLine0, RichCol0),
            NewName = "take_damage_renamed"
        }, CancellationToken.None);

        lspResult.Should().NotBeNull();
        var lspEditCount = lspResult!.Changes!.Sum(kv => kv.Value.Length);

        lspEditCount.Should().Be(cliPlan.StrictEdits.Count,
            "LSP rename must apply exactly the Strict edits (writes = strict)");
        lspEditCount.Should().BeLessThanOrEqualTo(cliPlan.Edits.Count,
            "LSP rename must never apply more than the full edit set");
    }

    [TestMethod]
    public void PrepareRename_RangeCoversWholeIdentifier_RegardlessOfCursorOffset()
    {
        var h = Setup();
        var filePath = ScriptPath(RichScript);

        // 1-based: line 28, identifier starts at column 6.
        var atStart = h.Rename.GetRenameRange(filePath, RichLine0 + 1, RichCol0 + 1);
        var atMid = h.Rename.GetRenameRange(filePath, RichLine0 + 1, RichCol0 + 1 + 3); // cursor 3 chars into the name

        atStart.Should().NotBeNull();
        atMid.Should().NotBeNull();

        atStart!.Placeholder.Should().Be(RichName);
        atMid!.Placeholder.Should().Be(RichName);

        // Same range regardless of where inside the identifier the cursor sits.
        atMid.Line.Should().Be(atStart.Line);
        atMid.Column.Should().Be(atStart.Column);
        atStart.Column.Should().Be(RichCol0 + 1, "range must start at the identifier, not the cursor");
    }
}
