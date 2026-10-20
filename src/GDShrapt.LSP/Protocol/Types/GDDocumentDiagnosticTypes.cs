using System.Text.Json.Serialization;

namespace GDShrapt.LSP;

/// <summary>
/// LSP 3.17 textDocument/diagnostic request parameters (pull diagnostics).
/// </summary>
public class GDDocumentDiagnosticParams
{
    [JsonPropertyName("textDocument")]
    public GDLspTextDocumentIdentifier TextDocument { get; set; } = new();

    [JsonPropertyName("identifier")]
    public string? Identifier { get; set; }

    [JsonPropertyName("previousResultId")]
    public string? PreviousResultId { get; set; }
}

/// <summary>
/// LSP 3.17 full document diagnostic report: every diagnostic of the document's current text.
/// </summary>
public class GDFullDocumentDiagnosticReport
{
    [JsonPropertyName("kind")]
    public string Kind { get; set; } = "full";

    [JsonPropertyName("items")]
    public GDLspDiagnostic[] Items { get; set; } = [];
}

/// <summary>
/// LSP 3.17 diagnostic provider options (server capability).
/// </summary>
public class GDDiagnosticOptions
{
    [JsonPropertyName("identifier")]
    public string Identifier { get; set; } = "gdshrapt";

    [JsonPropertyName("interFileDependencies")]
    public bool InterFileDependencies { get; set; } = true;

    [JsonPropertyName("workspaceDiagnostics")]
    public bool WorkspaceDiagnostics { get; set; }
}
