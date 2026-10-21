using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using FluentAssertions;
using GDShrapt.Abstractions;
using GDShrapt.CLI.Core;
using GDShrapt.Semantics;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace GDShrapt.LSP.Tests;

/// <summary>
/// LSAI#53 retest (flight-sim): references of Pylon.is_loaded() listed GUT's `coll_script.is_loaded` — an untyped
/// receiver, and GUT's CollectedScript declares its own `is_loaded`. LSP carries no confidence: a duck-typed
/// reference is kept only when no unrelated class declares the same name; a uniquely named member keeps it.
/// </summary>
[TestClass]
public class GDReferencesAmbiguousDuckTests
{
    internal static IGDFindRefsHandler? LastFindRefs;

    private static (string root, GDReferencesHandler handler) Setup()
    {
        var root = Path.Combine(Path.GetTempPath(), "gdshrapt_ambig_" + System.Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        File.WriteAllText(Path.Combine(root, "project.godot"), "config_version=5\n");
        File.WriteAllText(Path.Combine(root, "pylon.gd"),
            "class_name Pylon\nextends RefCounted\n\nfunc is_loaded() -> bool:\n\treturn true\n\nfunc reload_rack() -> void:\n\tpass\n");
        File.WriteAllText(Path.Combine(root, "collected_script.gd"),
            "class_name CollectedScript\nextends RefCounted\n\nvar is_loaded = false\n");
        File.WriteAllText(Path.Combine(root, "gut.gd"),
            "extends Node\n\nfunc run(coll_script) -> void:\n\tif not coll_script.is_loaded:\n\t\treturn\n\nfunc rack(r) -> void:\n\tr.reload_rack()\n");
        File.WriteAllText(Path.Combine(root, "store_at.gd"),
            "extends Node\n\nfunc held(p: Pylon) -> bool:\n\treturn p.is_loaded()\n");

        var project = new GDScriptProject(new GDDefaultProjectContext(root), new GDScriptProjectOptions());
        project.LoadScripts();
        project.AnalyzeAll();
        var registry = new GDServiceRegistry();
        registry.LoadModules(project, new GDBaseModule());
        LastFindRefs = registry.GetService<IGDFindRefsHandler>();
        return (root, new GDReferencesHandler(registry.GetService<IGDFindRefsHandler>()!, registry.GetService<IGDGoToDefHandler>()!));
    }

    private static async Task<string[]> References(GDReferencesHandler handler, string root, int line, int character)
    {
        var result = await handler.HandleAsync(new GDReferencesParams
        {
            TextDocument = new GDLspTextDocumentIdentifier { Uri = GDDocumentManager.PathToUri(Path.Combine(root, "pylon.gd")) },
            Position = new GDLspPosition(line, character),
            Context = new GDReferenceContext { IncludeDeclaration = false }
        }, CancellationToken.None);
        return (result ?? []).Select(l => Path.GetFileName(GDDocumentManager.UriToPath(l.Uri))).ToArray();
    }

    [TestMethod]
    public async Task AnAmbiguousDuckTypedReference_IsLeftOut()
    {
        var (root, handler) = Setup();
        try
        {
            var all = LastFindRefs!.FindAllReferences("is_loaded", Path.Combine(root, "pylon.gd"));
            var why = "unrelated=" + all.UnrelatedGroups.Count + " primary=" + string.Join(";", all.PrimaryGroups.SelectMany(g => g.Locations)
                .Select(l => $"{Path.GetFileName(l.FilePath)}:{l.Line} {l.Confidence} {l.Reason}"));
            (await References(handler, root, 3, 6)).Should().Equal(new[] { "store_at.gd" }, why);
        }
        finally { Directory.Delete(root, true); }
    }

    [TestMethod]
    public async Task ADuckTypedReferenceToAUniqueName_IsKept()
    {
        var (root, handler) = Setup();
        try
        {
            (await References(handler, root, 6, 6)).Should().Contain("gut.gd");
        }
        finally { Directory.Delete(root, true); }
    }
}
