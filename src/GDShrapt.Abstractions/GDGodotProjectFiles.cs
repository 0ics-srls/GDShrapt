using System;
using System.Collections.Generic;
using System.IO;

namespace GDShrapt.Abstractions;

/// <summary>
/// The files of a Godot project as the Godot editor sees them (EditorFileSystem: <c>_should_skip_directory</c> and
/// the "names starting with '.' are ignored" rule). Recursion skips a subdirectory whose name starts with '.'
/// (the <c>.godot</c> data folder, VCS folders), or that holds its own <c>project.godot</c> (a nested project — a
/// separate workspace) or a <c>.gdignore</c>. Files whose name starts with '.' are ignored too.
/// </summary>
public static class GDGodotProjectFiles
{
    /// <summary>Files matching <paramref name="pattern"/> that belong to the project rooted at <paramref name="projectRoot"/>.</summary>
    public static IEnumerable<string> Enumerate(IGDFileSystem fs, string projectRoot, string pattern)
    {
        var pending = new Stack<string>();
        pending.Push(projectRoot);
        while (pending.Count > 0)
        {
            var dir = pending.Pop();
            foreach (var file in fs.GetFiles(dir, pattern, recursive: false))
            {
                if (!fs.GetFileName(file).StartsWith(".", StringComparison.Ordinal))
                    yield return file;
            }

            foreach (var sub in fs.GetDirectories(dir))
            {
                if (!IsSkippedDirectory(fs, sub))
                    pending.Push(sub);
            }
        }
    }

    /// <summary>True when Godot ignores <paramref name="directory"/> (and everything below it) inside a project.</summary>
    public static bool IsSkippedDirectory(IGDFileSystem fs, string directory)
    {
        var name = fs.GetFileName(directory.TrimEnd('/', '\\'));
        return name.StartsWith(".", StringComparison.Ordinal)
               || fs.FileExists(fs.CombinePath(directory, "project.godot"))
               || fs.FileExists(fs.CombinePath(directory, ".gdignore"));
    }

    /// <summary>
    /// True when <paramref name="path"/> lies under <paramref name="projectRoot"/> and no directory between them is
    /// skipped — the test for a file-watcher event (a watcher on the root also reports nested projects' files).
    /// </summary>
    public static bool BelongsToProject(IGDFileSystem fs, string projectRoot, string path)
    {
        var root = Path.GetFullPath(projectRoot).TrimEnd('/', '\\');
        var full = Path.GetFullPath(path);
        if (fs.GetFileName(full).StartsWith(".", StringComparison.Ordinal))
            return false;

        var dir = Path.GetDirectoryName(full);
        while (dir != null && dir.Length > root.Length)
        {
            if (IsSkippedDirectory(fs, dir))
                return false;
            dir = Path.GetDirectoryName(dir);
        }

        return dir != null && string.Equals(dir.TrimEnd('/', '\\'), root, StringComparison.Ordinal);
    }
}
