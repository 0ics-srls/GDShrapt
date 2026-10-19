using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using GDShrapt.Abstractions;
using GDShrapt.CLI.Core;

namespace GDShrapt.LSP;

/// <summary>
/// Handles textDocument/documentSymbol requests.
/// Thin wrapper over IGDSymbolsHandler from CLI.Core.
/// </summary>
public class GDDocumentSymbolHandler
{
    private readonly IGDSymbolsHandler _handler;

    public GDDocumentSymbolHandler(IGDSymbolsHandler handler)
    {
        _handler = handler;
    }

    public Task<GDLspDocumentSymbol[]?> HandleAsync(GDDocumentSymbolParams @params, CancellationToken cancellationToken)
    {
        var filePath = GDDocumentManager.UriToPath(@params.TextDocument.Uri);

        // Delegate to CLI.Core handler
        var result = _handler.GetSymbols(filePath);
        if (result == null || result.Count == 0)
            return Task.FromResult<GDLspDocumentSymbol[]?>(null);

        // Convert CLI.Core symbols to LSP document symbols
        var symbols = new List<GDLspDocumentSymbol>();
        foreach (var symbol in result)
        {
            symbols.Add(ConvertToLspSymbol(symbol));
        }

        return Task.FromResult<GDLspDocumentSymbol[]?>(symbols.ToArray());
    }

    private static GDLspDocumentSymbol ConvertToLspSymbol(GDDocumentSymbol symbol)
    {
        // CLI.Core positions are 1-based; LSP is 0-based. Range = whole declaration, SelectionRange = name.
        var line = Math.Max(0, symbol.Line - 1);
        var column = Math.Max(0, symbol.Column - 1);
        var endLine = symbol.EndLine > 0 ? Math.Max(line, symbol.EndLine - 1) : line;
        var endColumn = symbol.EndLine > 0 ? Math.Max(0, symbol.EndColumn - 1) : column + symbol.Name.Length;

        var lspSymbol = new GDLspDocumentSymbol
        {
            // LSP forbids empty names: anonymous enums are reported as "enum".
            Name = string.IsNullOrEmpty(symbol.Name) && symbol.Kind == GDSymbolKind.Enum ? "enum" : symbol.Name,
            Kind = ConvertSymbolKind(symbol.Kind),
            Detail = symbol.Type,
            Range = new GDLspRange(line, 0, endLine, endColumn),
            SelectionRange = new GDLspRange(line, column, line, column + symbol.Name.Length)
        };

        // Convert children
        if (symbol.Children != null && symbol.Children.Count > 0)
        {
            var children = new List<GDLspDocumentSymbol>();
            foreach (var child in symbol.Children)
            {
                children.Add(ConvertToLspSymbol(child));
            }
            lspSymbol.Children = children.ToArray();
        }

        return lspSymbol;
    }

    private static GDLspSymbolKind ConvertSymbolKind(GDSymbolKind kind)
    {
        return kind switch
        {
            GDSymbolKind.Class => GDLspSymbolKind.Class,
            GDSymbolKind.Method => GDLspSymbolKind.Method,
            GDSymbolKind.Variable => GDLspSymbolKind.Variable,
            GDSymbolKind.Constant => GDLspSymbolKind.Constant,
            GDSymbolKind.Signal => GDLspSymbolKind.Event,
            GDSymbolKind.Enum => GDLspSymbolKind.Enum,
            GDSymbolKind.EnumValue => GDLspSymbolKind.EnumMember,
            _ => GDLspSymbolKind.Variable
        };
    }
}
