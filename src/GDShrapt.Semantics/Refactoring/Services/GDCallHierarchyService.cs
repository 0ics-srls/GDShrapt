using System.Collections.Generic;
using System.Linq;
using GDShrapt.Abstractions;

namespace GDShrapt.Semantics;

/// <summary>
/// Service for call hierarchy navigation.
/// Uses GDCallSiteRegistry to find callers/callees of methods.
/// </summary>
public class GDCallHierarchyService : GDRefactoringServiceBase
{
    private readonly GDScriptProject _project;
    private readonly GDProjectSemanticModel? _projectModel;

    public GDCallHierarchyService(GDScriptProject project, GDProjectSemanticModel? projectModel = null)
    {
        _project = project;
        _projectModel = projectModel;
    }

    /// <summary>
    /// Resolves the method symbol at the cursor position for call hierarchy preparation.
    /// </summary>
    public GDCallHierarchySymbol? ResolveMethodSymbol(GDRefactoringContext context)
    {
        if (!IsContextValid(context))
            return null;

        var semanticModel = context.Script?.SemanticModel;
        if (semanticModel == null)
            return null;

        var symbol = semanticModel.GetSymbolAtPosition(context.Cursor.Line, context.Cursor.Column);
        if (symbol == null || symbol.Kind != GDSymbolKind.Method)
            return null;

        var filePath = GetFilePath(context);
        if (filePath == null)
            return null;

        var posToken = symbol.PositionToken;
        var line = posToken?.StartLine ?? symbol.DeclarationNode?.StartLine ?? 0;
        var column = posToken?.StartColumn ?? symbol.DeclarationNode?.StartColumn ?? 0;

        return new GDCallHierarchySymbol
        {
            Name = symbol.Name,
            ClassName = context.Script?.TypeName ?? semanticModel.BaseTypeName,
            FilePath = filePath,
            Line = line,
            Column = column
        };
    }

    /// <summary>
    /// Gets all callers of the specified method (incoming calls).
    /// </summary>
    public IReadOnlyList<GDCallHierarchyCallEntry> GetCallers(string? className, string methodName)
    {
        var registry = _project.CallSiteRegistry;
        if (registry == null || string.IsNullOrEmpty(methodName))
            return [];

        var callers = registry.GetCallersOf(className ?? "", methodName);
        if (callers.Count == 0)
            return [];

        // Group by source method to create call hierarchy entries
        var grouped = callers
            .GroupBy(c => (c.SourceFilePath, c.SourceMethodName))
            .ToList();

        var result = new List<GDCallHierarchyCallEntry>();
        foreach (var group in grouped)
        {
            var first = group.First();
            var sourceSymbol = ResolveMethodInFile(first.SourceFilePath, first.SourceMethodName);

            var entry = new GDCallHierarchyCallEntry
            {
                Symbol = sourceSymbol ?? new GDCallHierarchySymbol
                {
                    Name = first.SourceMethodName ?? "<class_level>",
                    FilePath = first.SourceFilePath,
                    Line = first.Line > 0 ? first.Line - 1 : 0,
                    Column = first.Column > 0 ? first.Column - 1 : 0
                },
                CallSites = group.Select(c => new GDCallSiteLocation
                {
                    Line = c.Line > 0 ? c.Line - 1 : 0,
                    Column = c.Column > 0 ? c.Column - 1 : 0
                }).ToList()
            };

            result.Add(entry);
        }

        return result;
    }

    /// <summary>
    /// Gets all methods called by the specified method (outgoing calls).
    /// </summary>
    public IReadOnlyList<GDCallHierarchyCallEntry> GetCallees(string filePath, string? methodName)
    {
        var registry = _project.CallSiteRegistry;
        if (registry == null || string.IsNullOrEmpty(filePath) || string.IsNullOrEmpty(methodName))
            return [];

        var callSites = registry.GetCallSitesInMethod(filePath, methodName);
        if (callSites.Count == 0)
            return [];

        // Group by target method
        var grouped = callSites
            .GroupBy(c => (c.TargetClassName, c.TargetMethodName))
            .ToList();

        var runtime = _project.CreateRuntimeProvider();
        var result = new List<GDCallHierarchyCallEntry>();
        foreach (var group in grouped)
        {
            var first = group.First();
            var targetSymbol = ResolveMethodByName(first.TargetClassName, first.TargetMethodName);

            var entry = new GDCallHierarchyCallEntry
            {
                Symbol = targetSymbol ?? new GDCallHierarchySymbol
                {
                    Name = first.TargetMethodName,
                    ClassName = DescribeUnresolvedTarget(runtime, first.TargetClassName, first.TargetMethodName),
                    FilePath = filePath,
                    Line = first.Line > 0 ? first.Line - 1 : 0,
                    Column = first.Column > 0 ? first.Column - 1 : 0
                },
                CallSites = group.Select(c => new GDCallSiteLocation
                {
                    Line = c.Line > 0 ? c.Line - 1 : 0,
                    Column = c.Column > 0 ? c.Column - 1 : 0
                }).ToList()
            };

            result.Add(entry);
        }

        return result;
    }

    /// <summary>
    /// The class of a callee with no declaration in the project (the item then points at the call site):
    /// "builtin &lt;Type&gt;" for a Godot type's method (Array[Pylon].size → "builtin Array"), "builtin @GlobalScope"
    /// for a global function called unqualified (range), else the registry's class ("*" = receiver type unknown).
    /// LSAI#53: these came back as the caller's class or the raw container type, read as project declarations.
    /// </summary>
    private string? DescribeUnresolvedTarget(IGDRuntimeProvider runtime, string? className, string methodName)
    {
        if (string.IsNullOrEmpty(className) || className == "*")
            return className;

        var bracket = className.IndexOf('[');
        var baseType = bracket > 0 ? className[..bracket] : className;

        // An unqualified call registered on the caller's own class (`range(...)` inside Loadout) that the class
        // does not declare
        if (_project.GetScriptByTypeName(baseType) is { } script
            && script.SemanticModel?.FindSymbol(methodName) is not { Kind: GDSymbolKind.Method }
            && runtime.GetGlobalFunction(methodName) != null)
            return "builtin @GlobalScope";

        if (_project.GetScriptByTypeName(baseType) == null && runtime.GetMember(baseType, methodName) != null)
            return "builtin " + baseType;

        return className;
    }

    private GDCallHierarchySymbol? ResolveMethodInFile(string filePath, string? methodName)
    {
        if (string.IsNullOrEmpty(methodName))
            return null;

        var script = _project.GetScript(filePath);
        if (script?.SemanticModel == null)
            return null;

        var symbol = script.SemanticModel.FindSymbol(methodName);
        if (symbol == null || symbol.Kind != GDSymbolKind.Method)
            return null;

        var posToken = symbol.PositionToken;
        var line = posToken?.StartLine ?? symbol.DeclarationNode?.StartLine ?? 0;
        var column = posToken?.StartColumn ?? symbol.DeclarationNode?.StartColumn ?? 0;

        return new GDCallHierarchySymbol
        {
            Name = symbol.Name,
            ClassName = script.TypeName ?? script.SemanticModel.BaseTypeName,
            FilePath = filePath,
            Line = line,
            Column = column
        };
    }

    private GDCallHierarchySymbol? ResolveMethodByName(string? className, string methodName)
    {
        if (string.IsNullOrEmpty(className))
            return null;

        var script = _project.GetScriptByTypeName(className);
        if (script != null)
            return ResolveMethodInFile(script.FullPath!, methodName);

        return null;
    }
}

/// <summary>
/// Represents a method symbol in the call hierarchy.
/// </summary>
public class GDCallHierarchySymbol
{
    /// <summary>Method name.</summary>
    public required string Name { get; init; }

    /// <summary>Class containing the method.</summary>
    public string? ClassName { get; init; }

    /// <summary>Full file path.</summary>
    public required string FilePath { get; init; }

    /// <summary>Line (0-based).</summary>
    public int Line { get; init; }

    /// <summary>Column (0-based).</summary>
    public int Column { get; init; }
}

/// <summary>
/// A call hierarchy entry with the resolved method and the call site locations.
/// </summary>
public class GDCallHierarchyCallEntry
{
    /// <summary>The method being called (outgoing) or calling (incoming).</summary>
    public required GDCallHierarchySymbol Symbol { get; init; }

    /// <summary>Locations of the call expressions.</summary>
    public required IReadOnlyList<GDCallSiteLocation> CallSites { get; init; }
}

/// <summary>
/// A single call site location within a method.
/// </summary>
public class GDCallSiteLocation
{
    /// <summary>Line (0-based).</summary>
    public int Line { get; init; }

    /// <summary>Column (0-based).</summary>
    public int Column { get; init; }
}
