using System.Collections.Generic;
using System.Linq;
using GDShrapt.Abstractions;
using GDShrapt.Reader;
using GDShrapt.Semantics;

namespace GDShrapt.CLI.Core;

/// <summary>
/// Handler for extracting document symbols.
/// </summary>
public class GDSymbolsHandler : IGDSymbolsHandler
{
    protected readonly GDScriptProject _project;
    protected readonly IGDRuntimeProvider? _runtimeProvider;

    public GDSymbolsHandler(GDScriptProject project, IGDRuntimeProvider? runtimeProvider = null)
    {
        _project = project;
        _runtimeProvider = runtimeProvider;
    }

    /// <inheritdoc />
    public virtual IReadOnlyList<GDDocumentSymbol> GetSymbols(string filePath)
    {
        // Project scripts: the file's own declarations from the AST (nested, full ranges,
        // no locals/parameters/inherited members). Built-in type files keep the model-based path.
        var file = _project.GetScript(filePath);
        if (file?.Class != null)
            return FromMembers(file.Class.Members);

        if (GDBuiltInFileHelper.IsBuiltInTypeFile(filePath))
        {
            var builtInFile = GDBuiltInFileHelper.GetOrParse(filePath, _runtimeProvider);
            if (builtInFile?.SemanticModel != null)
                return ExtractSymbols(builtInFile.SemanticModel);
        }
        return [];
    }

    private static IReadOnlyList<GDDocumentSymbol> ExtractSymbols(GDSemanticModel model)
    {
        return model.Symbols
            .Select(s => new GDDocumentSymbol
            {
                Name = s.Name,
                Kind = s.Kind,
                Type = s.TypeName,
                Line = s.DeclarationNode?.StartLine ?? 0,
                Column = s.DeclarationNode?.StartColumn ?? 0
            })
            .OrderBy(s => s.Line)
            .ThenBy(s => s.Column)
            .ToList();
    }

    private static IReadOnlyList<GDDocumentSymbol> FromMembers(IEnumerable<GDClassMember> members)
    {
        var list = new List<GDDocumentSymbol>();
        foreach (var member in members)
        {
            var symbol = member switch
            {
                GDMethodDeclaration m when m.Identifier != null =>
                    Make(m, m.Identifier, GDSymbolKind.Method, MethodSignature(m)),
                GDVariableDeclaration v when v.Identifier != null =>
                    Make(v, v.Identifier, v.IsConstant ? GDSymbolKind.Constant : GDSymbolKind.Variable, VariableSignature(v)),
                GDSignalDeclaration s when s.Identifier != null =>
                    Make(s, s.Identifier, GDSymbolKind.Signal, SignalSignature(s)),
                GDEnumDeclaration e =>
                    Make(e, e.Identifier, GDSymbolKind.Enum, e.Identifier != null ? "enum " + e.Identifier.Sequence : "enum"),
                GDInnerClassDeclaration c when c.Identifier != null =>
                    Make(c, c.Identifier, GDSymbolKind.Class, "class " + c.Identifier.Sequence, FromMembers(c.Members)),
                _ => null
            };
            if (symbol != null)
                list.Add(symbol);
        }
        return list;
    }

    private static GDDocumentSymbol Make(GDNode declaration, GDIdentifier? identifier, GDSymbolKind kind, string signature,
        IReadOnlyList<GDDocumentSymbol>? children = null)
    {
        // End of the declaration = last significant token (trailing newlines, indentation, comments excluded).
        GDSyntaxToken? last = null;
        foreach (var token in declaration.AllTokens)
            if (token is not (GDNewLine or GDSpace or GDComment or GDIntendation or GDMultiLineSplitToken))
                last = token;
        last ??= identifier;

        return new GDDocumentSymbol
        {
            Name = identifier?.Sequence ?? "",
            Kind = kind,
            Type = signature,
            Line = (identifier?.StartLine ?? declaration.StartLine) + 1,
            Column = (identifier?.StartColumn ?? declaration.StartColumn) + 1,
            EndLine = (last?.EndLine ?? declaration.StartLine) + 1,
            EndColumn = (last?.EndColumn ?? 0) + 1,
            Children = children
        };
    }

    private static string MethodSignature(GDMethodDeclaration m)
    {
        var sb = new System.Text.StringBuilder();
        if (m.IsStatic) sb.Append("static ");
        sb.Append("func ").Append(m.Identifier!.Sequence).Append('(').Append(m.Parameters?.ToString()?.Trim()).Append(')');
        var ret = m.ReturnType?.ToString()?.Trim();
        if (!string.IsNullOrEmpty(ret)) sb.Append(" -> ").Append(ret);
        return sb.ToString();
    }

    private static string VariableSignature(GDVariableDeclaration v)
    {
        var sb = new System.Text.StringBuilder();
        if (v.IsStatic) sb.Append("static ");
        sb.Append(v.IsConstant ? "const " : "var ").Append(v.Identifier!.Sequence);
        var type = v.Type?.ToString()?.Trim();
        if (!string.IsNullOrEmpty(type)) sb.Append(": ").Append(type);
        return sb.ToString();
    }

    private static string SignalSignature(GDSignalDeclaration s)
    {
        var parameters = s.Parameters?.ToString()?.Trim();
        return s.OpenBracket != null ? $"signal {s.Identifier!.Sequence}({parameters})" : $"signal {s.Identifier!.Sequence}";
    }

    /// <inheritdoc />
    public virtual Semantics.GDSymbolInfo? FindSymbolByName(string symbolName, string filePath)
    {
        if (string.IsNullOrEmpty(symbolName) || string.IsNullOrEmpty(filePath))
            return null;

        var file = _project.GetScript(filePath);
        return file?.SemanticModel?.FindSymbol(symbolName);
    }

    /// <inheritdoc />
    public virtual IReadOnlyList<Semantics.GDSymbolInfo> GetSymbolsOfKind(string filePath, GDSymbolKind kind)
    {
        var file = _project.GetScript(filePath);
        if (file?.SemanticModel == null)
            return [];

        return file.SemanticModel.Symbols
            .Where(s => s.Kind == kind)
            .ToList();
    }

    /// <inheritdoc />
    public virtual IReadOnlyList<GDReference> GetReferencesToSymbol(Semantics.GDSymbolInfo symbol, string filePath)
    {
        if (symbol == null || string.IsNullOrEmpty(filePath))
            return [];

        var file = _project.GetScript(filePath);
        if (file?.SemanticModel == null)
            return [];

        return file.SemanticModel.GetReferencesTo(symbol).ToList();
    }

    /// <inheritdoc />
    public virtual string? GetTypeForNode(GDNode node, string filePath)
    {
        if (node == null || string.IsNullOrEmpty(filePath))
            return null;

        var file = _project.GetScript(filePath);
        var typeInfo = file?.SemanticModel?.TypeSystem.GetType(node);
        return typeInfo?.IsVariant == true ? null : typeInfo?.DisplayName;
    }
}
