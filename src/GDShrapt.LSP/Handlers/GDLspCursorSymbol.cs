using GDShrapt.CLI.Core;

namespace GDShrapt.LSP;

/// <summary>
/// Resolves the symbol under an LSP cursor position via go-to-definition.
/// Centralizes the cursor→symbol-name step shared by references and rename so they behave
/// identically to each other and to the CLI (which resolves the symbol the same way).
/// </summary>
internal static class GDLspCursorSymbol
{
    /// <summary>
    /// Returns the symbol name at the cursor, or null if there is no resolvable symbol
    /// (whitespace, a literal, or an info-only location such as a runtime-only node path).
    /// </summary>
    public static string? ResolveName(IGDGoToDefHandler goToDef, string filePath, GDLspPosition position)
    {
        // An [autoload] key in project.godot names the autoload singleton (references/rename target).
        if (string.Equals(System.IO.Path.GetFileName(filePath), "project.godot", System.StringComparison.OrdinalIgnoreCase))
            return Semantics.GDProjectGodotLocator.KeyAtLine(filePath, "autoload", position.Line);

        // LSP 0-based → CLI.Core 1-based
        var definition = goToDef.FindDefinition(filePath, position.Line + 1, position.Character + 1);
        if (definition == null || definition.IsInfoOnly || string.IsNullOrEmpty(definition.SymbolName))
            return null;

        return definition.SymbolName;
    }
}
