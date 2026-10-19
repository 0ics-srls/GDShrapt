using System;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using GDShrapt.Abstractions;
using GDShrapt.CLI.Core;
using GDShrapt.LSP.Tests.Helpers;
using GDShrapt.Semantics;
using GDShrapt.TestHelpers;

namespace GDShrapt.LSP.Tests.Server;

/// <summary>
/// D6: disk changes reach the models the way LSAI delivers them — didOpen/didChange for created/modified
/// scripts, the server's own watcher for never-opened edits, deletes and .tscn changes.
/// </summary>
[TestClass]
public class GDDiskChangeTests
{
    private const string PlayerGd =
        "class_name Player\nextends CharacterBody2D\n\nconst WALK_SPEED = 300.0\n\nfunc _physics_process(delta: float) -> void:\n\tvar s := WALK_SPEED\n\ttry_jump()\n\nfunc try_jump() -> void:\n\tpass\n";

    private const string CoinGd =
        "extends Area2D\n\nfunc _on_body_entered(body: Node2D) -> void:\n\t(body as Player).try_jump()\n\nfunc poke(p: Player) -> void:\n\tp.try_jump()\n";

    private const string LevelGd = "extends Node\n\nfunc _on_coin() -> void:\n\tpass\n";

    private const string LevelTscn =
        "[gd_scene load_steps=2 format=3]\n\n[ext_resource type=\"Script\" path=\"res://level.gd\" id=\"1\"]\n\n[node name=\"Level\" type=\"Node\"]\nscript = ExtResource(\"1\")\n";

    private static readonly TimeSpan WatcherWait = TimeSpan.FromSeconds(10);

    private GDTempProject? _tmp;
    private GDLspTestSession? _lsp;

    [TestCleanup]
    public async Task Cleanup()
    {
        if (_lsp != null) await _lsp.DisposeAsync();
        _tmp?.Dispose();
    }

    private static bool Has(JsonElement locations, string uriSuffix, int? line = null) =>
        locations.ValueKind == JsonValueKind.Array && locations.EnumerateArray().Any(l =>
            l.GetProperty("uri").GetString()!.EndsWith(uriSuffix) &&
            (line == null || l.GetProperty("range").GetProperty("start").GetProperty("line").GetInt32() == line));

    private static async Task<bool> Eventually(Func<Task<bool>> condition)
    {
        var until = DateTime.UtcNow + WatcherWait;
        while (DateTime.UtcNow < until)
        {
            if (await condition()) return true;
            await Task.Delay(200);
        }
        return await condition();
    }

    private async Task StartLspAsync(params (string, string)[] files)
    {
        _tmp = GDTempProject.WriteFiles(files);
        _lsp = await GDLspTestSession.StartAsync(_tmp.Root);
    }

    private Task<JsonElement> TryJumpRefs() => _lsp!.ReferencesAsync(_tmp!.UriOf("player.gd"), 9, 6);

    private async Task<string[]> WorkspaceSymbolNames(string query)
    {
        var result = await _lsp!.RequestAsync("workspace/symbol", new { query });
        return result.ValueKind == JsonValueKind.Array
            ? result.EnumerateArray().Select(s => s.GetProperty("name").GetString()!).ToArray()
            : [];
    }

    [TestMethod]
    [Timeout(120000)]
    public async Task NeverOpenedFileEditedOnDisk_WatcherInvalidatesCache_ReferencesIncludeNewLine()
    {
        await StartLspAsync(("player.gd", PlayerGd), ("coin.gd", CoinGd));
        Assert.IsTrue(Has(await TryJumpRefs(), "/coin.gd", 6), "warm-up: coin.gd line 7");

        File.AppendAllText(_tmp!.PathOf("coin.gd"), "func poke2(p: Player) -> void:\n\tp.try_jump()\n");

        Assert.IsTrue(await Eventually(async () => Has(await TryJumpRefs(), "/coin.gd", 8)),
            "references must include the new call in coin.gd line 9");
    }

    [TestMethod]
    [Timeout(60000)]
    public async Task NewFileUnseenByWatcher_DidOpen_AddsScript_ReferencesAndWorkspaceSymbols()
    {
        _tmp = GDTempProject.Create(("player.gd", PlayerGd), ("coin.gd", CoinGd));   // no watcher
        var project = _tmp.Project!;
        var registry = new GDServiceRegistry();
        registry.LoadModules(project, new GDBaseModule(deferAnalysis: true));
        var model = registry.GetService<GDProjectSemanticModel>()!;
        project.AnalyzeAll();
        var documents = new GDDocumentManager(project);
        documents.SetProjectModel(model);

        const string genGd = "extends Node\n\nfunc gen_caller(p: Player) -> void:\n\tp.try_jump()\n";
        File.WriteAllText(_tmp.PathOf("gen.gd"), genGd);
        documents.OpenDocument(_tmp.UriOf("gen.gd"), genGd, 1);

        var refs = new GDSymbolReferenceCollector(project, model).CollectReferences("try_jump", _tmp.PathOf("player.gd")).References;
        Assert.IsTrue(refs.Any(r => r.FilePath == _tmp.PathOf("gen.gd") && r.Line == 3), "gen.gd call to try_jump");

        var symbols = await new GDWorkspaceSymbolHandler(project).HandleAsync(new GDWorkspaceSymbolParams { Query = "gen_caller" }, CancellationToken.None);
        Assert.IsTrue(symbols?.Any(s => s.Name == "gen_caller") == true, "workspace/symbol lists gen_caller");
    }

    [TestMethod]
    [Timeout(60000)]
    public void DidOpenOutsideProjectRoot_NoScriptAdded_NoThrow()
    {
        _tmp = GDTempProject.Create(("player.gd", PlayerGd));
        var project = _tmp.Project!;
        var registry = new GDServiceRegistry();
        registry.LoadModules(project, new GDBaseModule(deferAnalysis: true));
        var documents = new GDDocumentManager(project);
        documents.SetProjectModel(registry.GetService<GDProjectSemanticModel>());
        var outside = Path.Combine(Path.GetTempPath(), "gds-outside-" + Guid.NewGuid().ToString("N") + ".gd");

        documents.OpenDocument(new Uri(outside).AbsoluteUri, "extends Node\n", 1);

        Assert.IsNull(project.GetScript(outside));
        Assert.AreEqual(1, project.ScriptFiles.Count());
    }

    [TestMethod]
    [Timeout(120000)]
    public async Task FileDeletedOnDisk_ReferencesAndWorkspaceSymbolsDisappear()
    {
        await StartLspAsync(("player.gd", PlayerGd), ("coin.gd", CoinGd));
        Assert.IsTrue(Has(await TryJumpRefs(), "/coin.gd"));
        Assert.IsTrue((await WorkspaceSymbolNames("poke")).Contains("poke"));

        File.Delete(_tmp!.PathOf("coin.gd"));

        Assert.IsTrue(await Eventually(async () => !Has(await TryJumpRefs(), "/coin.gd")), "coin.gd references must disappear");
        Assert.IsFalse((await WorkspaceSymbolNames("poke")).Contains("poke"), "workspace/symbol must not list poke");
    }

    [TestMethod]
    [Timeout(120000)]
    public async Task SceneChangedOnDisk_NewConnection_ReferencesOnCallbackIncludeSceneLine()
    {
        await StartLspAsync(("player.gd", PlayerGd), ("level.gd", LevelGd), ("level.tscn", LevelTscn));
        var levelUri = _tmp!.UriOf("level.gd");
        Task<JsonElement> CallbackRefs() => _lsp!.ReferencesAsync(levelUri, 2, 6);
        Assert.IsFalse(Has(await CallbackRefs(), "/level.tscn"), "no scene connection yet");

        File.WriteAllText(_tmp.PathOf("level.tscn"),
            LevelTscn + "\n[connection signal=\"coin_collected\" from=\".\" to=\".\" method=\"_on_coin\"]\n");

        Assert.IsTrue(await Eventually(async () => Has(await CallbackRefs(), "/level.tscn", 7)),
            "references on _on_coin must include the new level.tscn connection line");
    }

    [TestMethod]
    [Timeout(60000)]
    public async Task ScriptCreatedByWatcher_GetsLazyModelFactory()
    {
        _tmp = GDTempProject.WriteFiles(("player.gd", PlayerGd));
        using var project = new GDScriptProject(new GDDefaultProjectContext(_tmp.Root), new GDScriptProjectOptions { EnableFileWatcher = true });
        project.LoadScripts();
        using var model = new GDProjectSemanticModel(project);
        var created = new TaskCompletionSource<GDScriptFile>(TaskCreationOptions.RunContinuationsAsynchronously);
        project.ScriptCreated += (_, e) => { if (e.Script != null) created.TrySetResult(e.Script); };

        File.WriteAllText(_tmp.PathOf("late.gd"), "extends Node\n\nfunc late_one() -> void:\n\tpass\n");
        var script = await created.Task.WaitAsync(WatcherWait);

        Assert.IsNotNull(script.SemanticModel, "watcher-created script must build its model on first read");
    }
}
