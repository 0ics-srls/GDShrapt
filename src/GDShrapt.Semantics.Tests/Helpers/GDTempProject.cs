using System;
using System.IO;
using GDShrapt.Abstractions;
using GDShrapt.Semantics;

namespace GDShrapt.TestHelpers;

/// <summary>
/// Temporary Godot project on disk (project.godot + given files), deleted on Dispose.
/// Shared by Semantics.Tests, CLI.Tests and LSP.Tests (linked source file).
/// </summary>
public sealed class GDTempProject : IDisposable
{
    public string Root { get; }

    /// <summary>Loaded project, or null when created with <see cref="WriteFiles"/>.</summary>
    public GDScriptProject? Project { get; }

    private GDTempProject(string root, GDScriptProject? project)
    {
        Root = root;
        Project = project;
    }

    /// <summary>Writes the files and loads them as a project (scripts + scenes, no watcher, no analysis).</summary>
    public static GDTempProject Create(params (string RelPath, string Text)[] files)
    {
        var root = Write(files);
        var project = new GDScriptProject(new GDDefaultProjectContext(root), new GDScriptProjectOptions());
        project.LoadScripts();
        project.LoadScenes();
        return new GDTempProject(root, project);
    }

    /// <summary>Writes the files only (for tests where the language server loads the project).</summary>
    public static GDTempProject WriteFiles(params (string RelPath, string Text)[] files) => new(Write(files), null);

    public string PathOf(string relPath) => Path.Combine(Root, relPath.Replace('/', Path.DirectorySeparatorChar));

    public string UriOf(string relPath) => new Uri(PathOf(relPath)).AbsoluteUri;

    public GDScriptFile Script(string relPath) =>
        Project?.GetScript(PathOf(relPath)) ?? throw new InvalidOperationException($"script not loaded: {relPath}");

    private static string Write((string RelPath, string Text)[] files)
    {
        var root = Path.Combine(Path.GetTempPath(), "gds-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        File.WriteAllText(Path.Combine(root, "project.godot"),
            "[gd_resource type=\"ProjectSettings\" format=3]\n\nconfig_version=5\n\n[application]\nconfig/name=\"TestProject\"\n");
        foreach (var (rel, text) in files)
        {
            var full = Path.Combine(root, rel.Replace('/', Path.DirectorySeparatorChar));
            Directory.CreateDirectory(Path.GetDirectoryName(full)!);
            File.WriteAllText(full, text);
        }
        return root;
    }

    public void Dispose()
    {
        Project?.Dispose();
        try { if (Directory.Exists(Root)) Directory.Delete(Root, recursive: true); }
        catch (IOException) { /* best effort */ }
        catch (UnauthorizedAccessException) { /* best effort */ }
    }
}
