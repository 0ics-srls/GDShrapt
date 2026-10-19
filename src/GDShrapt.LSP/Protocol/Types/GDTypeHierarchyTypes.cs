using System.Text.Json.Serialization;

namespace GDShrapt.LSP;

/// <summary>
/// LSP 3.17 type hierarchy item.
/// </summary>
public class GDLspTypeHierarchyItem
{
    [JsonPropertyName("name")]
    public string Name { get; set; } = string.Empty;

    [JsonPropertyName("kind")]
    public int Kind { get; set; } = 5; // SymbolKind.Class

    [JsonPropertyName("uri")]
    public string Uri { get; set; } = string.Empty;

    [JsonPropertyName("range")]
    public GDLspRange Range { get; set; } = new();

    [JsonPropertyName("selectionRange")]
    public GDLspRange SelectionRange { get; set; } = new();

    [JsonPropertyName("detail")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Detail { get; set; }

    [JsonPropertyName("data")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public GDTypeHierarchyItemData? Data { get; set; }
}

/// <summary>
/// Round-trip data: the script that declares the type, or the built-in (engine) type name.
/// </summary>
public class GDTypeHierarchyItemData
{
    [JsonPropertyName("filePath")]
    public string? FilePath { get; set; }

    [JsonPropertyName("builtin")]
    public string? Builtin { get; set; }
}

public class GDTypeHierarchyPrepareParams
{
    [JsonPropertyName("textDocument")]
    public GDLspTextDocumentIdentifier TextDocument { get; set; } = new();

    [JsonPropertyName("position")]
    public GDLspPosition Position { get; set; } = new();
}

public class GDTypeHierarchyItemParams
{
    [JsonPropertyName("item")]
    public GDLspTypeHierarchyItem Item { get; set; } = new();
}
