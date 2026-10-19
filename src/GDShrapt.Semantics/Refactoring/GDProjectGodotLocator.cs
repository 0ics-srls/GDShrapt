using System;
using System.IO;

namespace GDShrapt.Semantics;

/// <summary>
/// Locates a key inside a section of a Godot <c>project.godot</c> (INI-like) file.
/// </summary>
public static class GDProjectGodotLocator
{
    /// <summary>
    /// Returns the 1-based line and column of <paramref name="key"/> in <c>[section]</c>, or null.
    /// The same key text in other sections is ignored.
    /// </summary>
    public static (int Line, int Column)? FindKey(string projectGodotPath, string section, string key)
    {
        string[] lines;
        try
        {
            lines = File.ReadAllLines(projectGodotPath);
        }
        catch (IOException) { return null; }
        catch (UnauthorizedAccessException) { return null; }

        var header = "[" + section + "]";
        var inSection = false;
        for (var i = 0; i < lines.Length; i++)
        {
            var trimmed = lines[i].Trim();
            if (trimmed.StartsWith("[", StringComparison.Ordinal))
            {
                inSection = trimmed == header;
                continue;
            }
            if (!inSection)
                continue;

            var column = lines[i].IndexOf(key, StringComparison.Ordinal);
            if (column < 0 || lines[i].Substring(0, column).Trim().Length > 0)
                continue;
            var rest = lines[i].Substring(column + key.Length).TrimStart();
            if (rest.StartsWith("=", StringComparison.Ordinal))
                return (i + 1, column + 1);
        }
        return null;
    }
}
