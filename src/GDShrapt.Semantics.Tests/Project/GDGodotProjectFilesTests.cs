using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using GDShrapt.Abstractions;
using GDShrapt.TestHelpers;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace GDShrapt.Semantics.Tests.Project;

/// <summary>
/// Issue LSAI#48: a project's files are the files Godot's editor sees. A subfolder with its own project.godot
/// (a nested project), a .gdignore, or a name starting with '.' is not part of the project — otherwise the root
/// of a repo that also contains II/project.godot analyses both projects (3355 scripts, duplicated class_names).
/// </summary>
[TestClass]
public class GDGodotProjectFilesTests
{
    private const string ProjectGodot = "config_version=5\n";

    private static GDTempProject Layout() => GDTempProject.Create(
        ("project.godot", ProjectGodot),
        ("main.gd", "extends Node\n"),
        ("player/player.gd", "class_name Player\nextends Node\n"),
        ("II/project.godot", ProjectGodot),
        ("II/player.gd", "class_name Player\nextends Node\n"),
        ("II/deep/more.gd", "extends Node\n"),
        ("addons/old/.gdignore", ""),
        ("addons/old/legacy.gd", "extends Node\n"),
        (".hidden/secret.gd", "extends Node\n"),
        (".dotfile.gd", "extends Node\n"));

    private static string[] Rel(GDTempProject tmp, System.Collections.Generic.IEnumerable<string> paths) =>
        paths.Select(p => Path.GetRelativePath(tmp.Root, p).Replace('\\', '/')).OrderBy(p => p).ToArray();

    [TestMethod]
    public void LoadScripts_OnlyTheProjectsOwnScripts()
    {
        using var tmp = Layout();

        var scripts = Rel(tmp, tmp.Project!.ScriptFiles.Select(s => s.FullPath!));

        CollectionAssert.AreEqual(new[] { "main.gd", "player/player.gd" }, scripts);
    }

    [TestMethod]
    public void NestedProjectRoot_SeesItsOwnScripts()
    {
        using var tmp = Layout();

        var nested = GDGodotProjectFiles.Enumerate(new GDDefaultFileSystem(), tmp.PathOf("II"), "*.gd");

        CollectionAssert.AreEqual(new[] { "II/deep/more.gd", "II/player.gd" }, Rel(tmp, nested));
    }

    [TestMethod]
    public void BelongsToProject_FollowsTheSameRules()
    {
        using var tmp = Layout();
        var fs = new GDDefaultFileSystem();

        Assert.IsTrue(GDGodotProjectFiles.BelongsToProject(fs, tmp.Root, tmp.PathOf("player/player.gd")));
        Assert.IsTrue(GDGodotProjectFiles.BelongsToProject(fs, tmp.Root, tmp.PathOf("player/not_yet_created.gd")));
        Assert.IsFalse(GDGodotProjectFiles.BelongsToProject(fs, tmp.Root, tmp.PathOf("II/deep/more.gd")), "nested project");
        Assert.IsFalse(GDGodotProjectFiles.BelongsToProject(fs, tmp.Root, tmp.PathOf("addons/old/legacy.gd")), ".gdignore");
        Assert.IsFalse(GDGodotProjectFiles.BelongsToProject(fs, tmp.Root, tmp.PathOf(".hidden/secret.gd")), "dot folder");
        Assert.IsFalse(GDGodotProjectFiles.BelongsToProject(fs, tmp.Root, tmp.PathOf(".dotfile.gd")), "dot file");
        Assert.IsFalse(GDGodotProjectFiles.BelongsToProject(fs, tmp.PathOf("player"), tmp.PathOf("main.gd")), "outside the root");
    }

    [TestMethod]
    [Timeout(30000)]
    public async Task Watcher_FileCreatedInNestedProject_IsNotAdded()
    {
        using var tmp = Layout();
        var project = tmp.Project!;
        var barrierPath = tmp.PathOf("barrier.gd");
        var barrier = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        project.ScriptCreated += (_, e) =>
        {
            if (string.Equals(e.Script.FullPath, barrierPath, StringComparison.Ordinal))
                barrier.TrySetResult();
        };
        project.EnableFileWatcher();

        // The watcher reports events in order: once barrier.gd (in the project) is seen, the nested
        // project's file — created before it — has been handled too.
        File.WriteAllText(tmp.PathOf("II/created_later.gd"), "extends Node\n");
        File.WriteAllText(barrierPath, "extends Node\n");
        await barrier.Task.WaitAsync(TimeSpan.FromSeconds(20));

        var scripts = Rel(tmp, project.ScriptFiles.Select(s => s.FullPath!));
        CollectionAssert.DoesNotContain(scripts, "II/created_later.gd");
        CollectionAssert.Contains(scripts, "barrier.gd");
    }
}
