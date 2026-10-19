using GDShrapt.Abstractions;
using GDShrapt.Reader;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;

namespace GDShrapt.Semantics;

/// <summary>
/// Service for planning and executing rename operations across a GDScript project.
/// </summary>
public class GDRenameService
{
    private static readonly Encoding Utf8NoBom = new UTF8Encoding(false);

    private readonly GDScriptProject _project;
    private readonly GDProjectSemanticModel? _projectModel;
    private readonly IGDRuntimeProvider? _runtimeProvider;

    public GDRenameService(GDScriptProject project, GDProjectSemanticModel? projectModel = null, IGDRuntimeProvider? runtimeProvider = null)
    {
        _project = project ?? throw new ArgumentNullException(nameof(project));
        _projectModel = projectModel;
        _runtimeProvider = runtimeProvider ?? project.CreateRuntimeProvider();
    }

    /// <summary>
    /// When true (default), a union/shared reference is treated as a strict edit only if every
    /// type in its <see cref="GDSymbolReference.SharedTypes"/> set is covered by a declaration being
    /// renamed in the same operation — so all possible resolutions produce the identical edit and the
    /// rewrite is risk-free. When false, union/shared references are always kept as potential edits
    /// (shown but never auto-applied).
    /// </summary>
    public bool PromoteFullyCoveredUnionReferences { get; set; } = true;

    /// <summary>
    /// Plans a rename operation for a symbol.
    /// </summary>
    /// <param name="symbol">The symbol to rename.</param>
    /// <param name="newName">The new name for the symbol.</param>
    /// <returns>The rename result with all required edits.</returns>
    public GDRenameResult PlanRename(GDSymbolInfo symbol, string newName)
    {
        if (symbol == null)
            return GDRenameResult.Failed("Symbol is null");

        // Validate the new name
        if (!ValidateIdentifier(newName, out var validationError))
            return GDRenameResult.Failed(validationError!);

        // Check for conflicts
        var conflicts = CheckConflicts(symbol, newName);
        if (conflicts.Count > 0)
            return GDRenameResult.WithConflicts(conflicts);

        var oldName = symbol.Name;
        var strictEdits = new List<GDTextEdit>();
        var potentialEdits = new List<GDTextEdit>();
        var filesModified = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        // Use unified collector for all GDScript references
        var collector = new GDSymbolReferenceCollector(_project, _projectModel);
        var containingScript = FindScriptContainingSymbol(symbol);
        var collectedRefs = collector.CollectReferences(symbol, containingScript);

        // Convert unified references to text edits
        var declaringTypeName = containingScript?.TypeName;
        var typesWithMethod = _runtimeProvider?.FindTypesWithMethod(oldName);

        ConvertRefsToEdits(collectedRefs, oldName, newName, declaringTypeName, typesWithMethod,
            strictEdits, potentialEdits, filesModified);

        CollectTscnEdits(oldName, newName, declaringTypeName, strictEdits, potentialEdits, filesModified);

        if (strictEdits.Count == 0 && potentialEdits.Count == 0)
            return GDRenameResult.NoOccurrences(oldName);

        // Deduplicate edits
        strictEdits = DeduplicateEdits(strictEdits);
        potentialEdits = DeduplicateEdits(potentialEdits);
        RemoveStrictDuplicatesFromPotential(strictEdits, potentialEdits);

        // Sort edits by file, then by position (reverse order for applying)
        var sortedStrict = SortEditsReverse(strictEdits);
        var sortedPotential = SortEditsReverse(potentialEdits);

        var warnings = new List<GDRenameWarning>(collectedRefs.StringWarnings);
        warnings.AddRange(CollectReflectionWarnings(oldName, symbol.Kind));
        warnings.AddRange(CollectCSharpInteropWarnings(oldName, containingScript));

        return GDRenameResult.SuccessfulWithConfidence(
            sortedStrict, sortedPotential, filesModified.Count, warnings);
    }

    private static void AddRefsToEdits(
        IEnumerable<(string? FilePath, int Line, int Column, string? Reason)> references,
        List<GDTextEdit> edits,
        HashSet<string> filesModified,
        string oldName,
        string newName,
        GDReferenceConfidence confidence,
        bool skipExistingFiles = false)
    {
        foreach (var (filePath, line, column, reason) in references)
        {
            if (string.IsNullOrEmpty(filePath))
                continue;

            if (skipExistingFiles && filesModified.Contains(filePath))
                continue;

            edits.Add(new GDTextEdit(
                filePath,
                line + 1,
                column + 1,
                oldName,
                newName,
                confidence,
                reason));
            filesModified.Add(filePath);
        }
    }

    private static void AddReferencesToEdits(
        IEnumerable<GDReferenceLocation> references,
        List<GDTextEdit> edits,
        HashSet<string> filesModified,
        string oldName,
        string newName,
        GDReferenceConfidence confidence,
        bool skipExistingFiles = false)
    {
        AddRefsToEdits(
            references.Select(r => (r.FilePath, r.Line, r.Column, r.ConfidenceReason)),
            edits, filesModified, oldName, newName, confidence, skipExistingFiles);
    }

    /// <summary>
    /// Sorts edits in reverse order (by file, then by line desc, then by column desc).
    /// This ensures edits can be applied safely without shifting positions.
    /// </summary>
    private static List<GDTextEdit> SortEditsReverse(List<GDTextEdit> edits)
    {
        return edits
            .OrderBy(e => e.FilePath)
            .ThenByDescending(e => e.Line)
            .ThenByDescending(e => e.Column)
            .ToList();
    }

    /// <summary>
    /// Plans a rename operation by symbol name.
    /// </summary>
    /// <param name="oldName">Current symbol name.</param>
    /// <param name="newName">New symbol name.</param>
    /// <param name="filterFilePath">Optional file path to limit the search.</param>
    /// <returns>The rename result with all required edits.</returns>
    public GDRenameResult PlanRename(string oldName, string newName, string? filterFilePath = null)
    {
        if (string.IsNullOrEmpty(oldName))
            return GDRenameResult.Failed("Old name is empty");

        // Validate the new name
        if (!ValidateIdentifier(newName, out var validationError))
            return GDRenameResult.Failed(validationError!);

        // If filterFilePath is specified, try to find the symbol and use the full PlanRename(symbol, newName) path
        if (!string.IsNullOrEmpty(filterFilePath))
        {
            var fullPath = Path.GetFullPath(filterFilePath).Replace('\\', '/');
            var targetScript = _project.ScriptFiles
                .FirstOrDefault(f => f.FullPath != null &&
                    f.FullPath.Equals(fullPath, StringComparison.OrdinalIgnoreCase));

            if (targetScript != null)
            {
                var model = _projectModel.ResolveModel(targetScript);
                var symbol = model?.FindSymbol(oldName);

                if (symbol != null)
                {
                    // If this symbol is bridge/union-connected to other same-name hierarchies, defer to the
                    // bridge-merge path below so the rename covers all connected declarations (and shared
                    // union references can be promoted). Otherwise rename the single symbol's hierarchy.
                    var bridgeConnected = _projectModel != null
                        && new GDSymbolReferenceCollector(_project, _projectModel)
                            .CollectAllReferences(oldName, filterFilePath).IsBridgeConnected;
                    if (!bridgeConnected)
                        return PlanRename(symbol, newName);
                }
                else if (targetScript.TypeName == oldName)
                {
                    // Check if this is a class_name
                    return PlanClassNameRename(targetScript, oldName, newName);
                }
            }
        }

        // No filter — find all definitions of oldName, group by type hierarchy,
        // and delegate to PlanRename(GDSymbolInfo) for each independent hierarchy.

        // 1. Check for class_name match first
        foreach (var script in _project.ScriptFiles)
        {
            if (script.FullPath != null && script.TypeName == oldName)
                return PlanClassNameRename(script, oldName, newName);
        }

        // 2. Collect all scripts where oldName is defined as a class member
        var definitions = new List<(GDScriptFile Script, GDSymbolInfo Symbol)>();
        GDScriptFile? localOnlyScript = null;
        GDSymbolInfo? localOnlySymbol = null;

        foreach (var script in _project.ScriptFiles)
        {
            if (script.FullPath == null)
                continue;

            var model = _projectModel.ResolveModel(script);
            if (model == null)
                continue;

            var symbol = model.FindSymbol(oldName);
            if (symbol == null)
                continue;

            if (IsClassMemberSymbol(symbol))
                definitions.Add((script, symbol));
            else if (localOnlyScript == null)
            {
                localOnlyScript = script;
                localOnlySymbol = symbol;
            }
        }

        // 3. If class member definitions found, group by type hierarchy.
        //    Process each hierarchy via PlanRename(GDSymbolInfo) independently.
        //    Return only the hierarchy with the most strict edits (the primary one).
        //    Same-named members on unrelated types are excluded from strict edits.
        //    Then augment with duck-typed/has_method member access references.
        if (definitions.Count > 0)
        {
            var hierarchyRoots = FindHierarchyRoots(definitions);

            // Bridge detection: when multiple hierarchies are dynamically connected
            // via duck-typed calls from bridge files, rename must cover ALL hierarchies.
            // Only when filterFilePath is set (user indicated which hierarchy they're in).
            if (hierarchyRoots.Count > 1 && _projectModel != null && !string.IsNullOrEmpty(filterFilePath))
            {
                var collector = new GDSymbolReferenceCollector(_project, _projectModel);
                var allRefs = collector.CollectAllReferences(oldName, filterFilePath);

                if (allRefs.IsBridgeConnected)
                {
                    var strictEdits = new List<GDTextEdit>();
                    var potentialEdits = new List<GDTextEdit>();
                    var filesModified = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

                    // null declaringTypeName → BuildUnrelatedFilesSet returns empty → all refs pass through
                    ConvertRefsToEdits(allRefs.Primary, oldName, newName,
                        null, null, strictEdits, potentialEdits, filesModified);

                    CollectTscnEdits(oldName, newName, null, strictEdits, potentialEdits, filesModified);
                    CollectAllMemberAccessEdits(oldName, newName, strictEdits, potentialEdits, filesModified);

                    strictEdits = DeduplicateEdits(strictEdits);
                    potentialEdits = DeduplicateEdits(potentialEdits);
                    RemoveStrictDuplicatesFromPotential(strictEdits, potentialEdits);
                    var warnings = CollectStringReferenceWarnings(oldName);
                    var symbolKind = definitions.First().Symbol.Kind;
                    warnings.AddRange(CollectReflectionWarnings(oldName, symbolKind));
                    warnings.AddRange(CollectCSharpInteropWarnings(oldName, definitions.First().Script));

                    return GDRenameResult.SuccessfulWithConfidence(
                        SortEditsReverse(strictEdits), SortEditsReverse(potentialEdits), filesModified.Count, warnings);
                }
            }

            // Non-bridge: pick the hierarchy with the most strict edits
            GDRenameResult? bestResult = null;
            foreach (var root in hierarchyRoots)
            {
                var result = PlanRename(root.Symbol, newName);
                if (result.Success && (bestResult == null || result.StrictEdits.Count > bestResult.StrictEdits.Count))
                    bestResult = result;
            }

            if (bestResult == null)
                return GDRenameResult.NoOccurrences(oldName);

            // Augment with duck-typed and has_method() references from member access index
            if (_projectModel != null)
            {
                var strictEdits = new List<GDTextEdit>(bestResult.StrictEdits);
                var potentialEdits = new List<GDTextEdit>(bestResult.PotentialEdits);
                var filesModified = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (var edit in strictEdits.Concat(potentialEdits))
                {
                    if (edit.FilePath != null)
                        filesModified.Add(edit.FilePath);
                }

                CollectAllMemberAccessEdits(oldName, newName, strictEdits, potentialEdits, filesModified);

                strictEdits = DeduplicateEdits(strictEdits);
                potentialEdits = DeduplicateEdits(potentialEdits);
                RemoveStrictDuplicatesFromPotential(strictEdits, potentialEdits);
                var warnings = CollectStringReferenceWarnings(oldName);
                var symbolKind = definitions.First().Symbol.Kind;
                warnings.AddRange(CollectReflectionWarnings(oldName, symbolKind));
                warnings.AddRange(CollectCSharpInteropWarnings(oldName, definitions.First().Script));

                return GDRenameResult.SuccessfulWithConfidence(
                    SortEditsReverse(strictEdits), SortEditsReverse(potentialEdits), filesModified.Count, warnings);
            }

            return bestResult;
        }

        // 4. Local variable only — single-file edits
        if (localOnlyScript != null && localOnlySymbol != null)
            return PlanRename(localOnlySymbol, newName);

        return GDRenameResult.NoOccurrences(oldName);
    }

    /// <summary>
    /// Plans a rename for a class_name type across the project.
    /// </summary>
    private GDRenameResult PlanClassNameRename(GDScriptFile containingScript, string oldName, string newName)
    {
        var strictEdits = new List<GDTextEdit>();
        var filesModified = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        CollectClassNameEdits(containingScript, oldName, newName, strictEdits, filesModified);

        if (strictEdits.Count == 0)
            return GDRenameResult.NoOccurrences(oldName);

        strictEdits = DeduplicateEdits(strictEdits);
        var sortedStrict = SortEditsReverse(strictEdits);

        return GDRenameResult.SuccessfulWithConfidence(sortedStrict, new List<GDTextEdit>(), filesModified.Count);
    }

    /// <summary>
    /// Plans a rename operation at cursor position using GDRefactoringContext.
    /// This method uses GDFindReferencesService to determine scope and find references.
    /// </summary>
    /// <param name="context">The refactoring context with cursor position.</param>
    /// <param name="newName">The new name for the symbol.</param>
    /// <returns>The rename result with all required edits.</returns>
    public GDRenameResult PlanRenameAtCursor(GDRefactoringContext context, string newName)
    {
        if (context == null)
            return GDRenameResult.Failed("Context is null");

        var findRefsService = new GDFindReferencesService(_project, _projectModel);
        var scope = findRefsService.DetermineSymbolScope(context);

        if (scope == null)
            return GDRenameResult.Failed("No symbol at cursor position");

        return PlanRenameInScope(context, scope, newName);
    }

    /// <summary>
    /// Plans a rename operation for a known symbol scope.
    /// </summary>
    /// <param name="context">The refactoring context.</param>
    /// <param name="scope">The symbol scope determined by GDFindReferencesService.</param>
    /// <param name="newName">The new name for the symbol.</param>
    /// <returns>The rename result with all required edits.</returns>
    public GDRenameResult PlanRenameInScope(GDRefactoringContext context, GDSymbolInfo scope, string newName)
    {
        if (context == null)
            return GDRenameResult.Failed("Context is null");

        if (scope == null)
            return GDRenameResult.Failed("Scope is null");

        // Validate the new name
        if (!ValidateIdentifier(newName, out var validationError))
            return GDRenameResult.Failed(validationError!);

        var oldName = scope.Name;

        // Check for reserved keywords
        if (GDNamingUtilities.IsReservedKeyword(newName))
            return GDRenameResult.WithConflicts(new List<GDRenameConflict> {
                new GDRenameConflict(newName, $"'{newName}' is a reserved GDScript keyword", GDRenameConflictType.ReservedKeyword)
            });

        // Find references using the service (delegates to unified collector for cross-file scopes)
        var findRefsService = new GDFindReferencesService(_project, _projectModel);
        var refsResult = findRefsService.FindReferencesForScope(context, scope);

        if (!refsResult.Success)
            return GDRenameResult.Failed(refsResult.ErrorMessage ?? "Failed to find references");

        // Convert references to text edits
        var strictEdits = new List<GDTextEdit>();
        var potentialEdits = new List<GDTextEdit>();
        var filesModified = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        AddReferencesToEdits(refsResult.StrictReferences, strictEdits, filesModified, oldName, newName, GDReferenceConfidence.Strict);
        AddReferencesToEdits(refsResult.PotentialReferences, potentialEdits, filesModified, oldName, newName, GDReferenceConfidence.Potential);

        if (strictEdits.Count == 0 && potentialEdits.Count == 0)
            return GDRenameResult.NoOccurrences(oldName);

        // Sort edits in reverse order for safe application
        var sortedStrict = SortEditsReverse(strictEdits);
        var sortedPotential = SortEditsReverse(potentialEdits);

        return GDRenameResult.SuccessfulWithConfidence(sortedStrict, sortedPotential, filesModified.Count);
    }

    /// <summary>
    /// Plans a rename operation for node paths across GDScript and scene files.
    /// </summary>
    /// <param name="oldNodeName">The current node name.</param>
    /// <param name="newNodeName">The new node name.</param>
    /// <returns>The rename result with all required edits.</returns>
    public GDRenameResult PlanNodePathRename(string oldNodeName, string newNodeName)
    {
        if (string.IsNullOrEmpty(oldNodeName) || string.IsNullOrEmpty(newNodeName))
            return GDRenameResult.Failed("Node name cannot be empty");

        if (oldNodeName == newNodeName)
            return GDRenameResult.NoOccurrences(oldNodeName);

        var finder = new GDNodePathReferenceFinder(_project);
        var allRefs = finder.FindAllReferences(oldNodeName).ToList();

        var strictEdits = new List<GDTextEdit>();
        var filesModified = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var r in allRefs)
        {
            if (r.Type == GDNodePathReference.RefType.GDScript && r.PathSpecifier != null)
            {
                strictEdits.Add(new GDTextEdit(
                    r.FilePath,
                    r.PathSpecifier.StartLine + 1,
                    r.PathSpecifier.StartColumn + 1,
                    oldNodeName,
                    newNodeName,
                    GDReferenceConfidence.Strict,
                    "Node path reference"));
            }
            else if (r.Type == GDNodePathReference.RefType.SceneNodeName ||
                     r.Type == GDNodePathReference.RefType.SceneParentPath)
            {
                strictEdits.Add(new GDTextEdit(
                    r.FilePath,
                    r.LineNumber,
                    r.Column,
                    oldNodeName,
                    newNodeName,
                    GDReferenceConfidence.Strict,
                    r.Type == GDNodePathReference.RefType.SceneNodeName
                        ? "Scene node name" : "Scene parent path"));
            }
            filesModified.Add(r.FilePath);
        }

        if (strictEdits.Count == 0)
            return GDRenameResult.NoOccurrences(oldNodeName);

        return GDRenameResult.SuccessfulWithConfidence(
            SortEditsReverse(strictEdits),
            Array.Empty<GDTextEdit>(),
            filesModified.Count);
    }

    /// <summary>
    /// Checks for naming conflicts before rename.
    /// </summary>
    /// <param name="symbol">The symbol being renamed.</param>
    /// <param name="newName">The proposed new name.</param>
    /// <returns>List of conflicts, empty if none.</returns>
    public IReadOnlyList<GDRenameConflict> CheckConflicts(GDSymbolInfo symbol, string newName)
    {
        var conflicts = new List<GDRenameConflict>();

        // Check reserved keywords
        if (GDNamingUtilities.IsReservedKeyword(newName))
        {
            conflicts.Add(new GDRenameConflict(
                newName,
                $"'{newName}' is a reserved GDScript keyword",
                GDRenameConflictType.ReservedKeyword));
        }

        // Check built-in types
        if (_runtimeProvider?.IsKnownType(newName) == true)
        {
            conflicts.Add(new GDRenameConflict(
                newName,
                $"'{newName}' is a built-in type name",
                GDRenameConflictType.BuiltInType));
        }

        // Find the script containing this symbol
        var containingScript = FindScriptContainingSymbol(symbol);
        if (containingScript?.SemanticModel == null)
            return conflicts;

        // Check if new name already exists in the same scope
        var existingSymbol = containingScript.SemanticModel.FindSymbol(newName);
        if (existingSymbol != null && existingSymbol != symbol)
        {
            conflicts.Add(new GDRenameConflict(
                newName,
                $"A symbol named '{newName}' already exists",
                GDRenameConflictType.NameAlreadyExists,
                existingSymbol));
        }

        return conflicts;
    }

    /// <summary>
    /// Validates that a name is a valid GDScript identifier.
    /// Delegates to GDNamingUtilities for consistent validation.
    /// </summary>
    /// <param name="name">The name to validate.</param>
    /// <param name="errorMessage">Error message if invalid.</param>
    /// <returns>True if valid, false otherwise.</returns>
    public bool ValidateIdentifier(string name, out string? errorMessage)
    {
        return GDNamingUtilities.ValidateIdentifier(name, out errorMessage);
    }

    /// <summary>
    /// Applies edits to a file content string.
    /// </summary>
    /// <param name="content">The original file content.</param>
    /// <param name="edits">The edits to apply (must be sorted in reverse order).</param>
    /// <returns>The modified content.</returns>
    public string ApplyEdits(string content, IEnumerable<GDTextEdit> edits)
    {
        // Detect original line ending style
        var lineEnding = content.Contains("\r\n") ? "\r\n" : "\n";

        var lines = content.Split(new[] { "\r\n", "\n" }, StringSplitOptions.None).ToList();

        foreach (var edit in edits)
        {
            if (edit.Line < 1 || edit.Line > lines.Count)
                continue;

            var lineIndex = edit.Line - 1;
            var line = lines[lineIndex];
            var column = edit.Column - 1;

            if (column < 0 || column >= line.Length)
                continue;

            // Find the identifier at this position
            var endColumn = column + edit.OldText.Length;
            if (endColumn > line.Length)
                continue;

            var found = line.Substring(column, edit.OldText.Length);
            if (found != edit.OldText)
                continue;

            // Replace
            lines[lineIndex] = line.Substring(0, column) + edit.NewText + line.Substring(endColumn);
        }

        return string.Join(lineEnding, lines);
    }

    /// <summary>
    /// Applies edits directly to a file.
    /// </summary>
    /// <param name="filePath">The file to modify.</param>
    /// <param name="edits">The edits to apply.</param>
    public void ApplyEditsToFile(string filePath, IEnumerable<GDTextEdit> edits)
    {
        var content = File.ReadAllText(filePath, Utf8NoBom);
        var modified = ApplyEdits(content, edits);
        File.WriteAllText(filePath, modified, Utf8NoBom);
    }

    #region Private helpers

    /// <summary>
    /// Converts unified GDSymbolReferences into GDTextEdit lists for rename.
    /// Handles confidence classification and provenance enrichment for duck-typed references.
    /// </summary>
    private void ConvertRefsToEdits(
        GDSymbolReferences collectedRefs,
        string oldName,
        string newName,
        string? declaringTypeName,
        IReadOnlyList<string>? typesWithMethod,
        List<GDTextEdit> strictEdits,
        List<GDTextEdit> potentialEdits,
        HashSet<string> filesModified)
    {
        // Build a set of file paths that are in a different type hierarchy than the declaring script.
        // References from these files should be excluded from strict edits.
        var unrelatedFiles = BuildUnrelatedFilesSet(collectedRefs, declaringTypeName);

        // Types whose member declaration is being renamed in this operation. A union/shared reference
        // is promotable to a strict edit only when every one of its shared types is covered here.
        var renamedTypes = PromoteFullyCoveredUnionReferences
            ? BuildRenamedDeclaringTypes(collectedRefs)
            : null;

        foreach (var sref in collectedRefs.References)
        {
            if (sref.FilePath == null) continue;

            // Signal connections in GDScript are informational for find-refs; rename edits .tscn directly
            if (sref.IsSignalConnection)
                continue;

            // References from unrelated hierarchies: skip strict, keep potential
            var isFromUnrelatedHierarchy = unrelatedFiles.Contains(sref.FilePath);

            // Determine the identifier token for precise column placement
            var identToken = sref.IdentifierToken;
            int line, col;

            if (identToken != null)
            {
                line = identToken.StartLine + 1;
                col = identToken is GDStringNode ? identToken.StartColumn + 2 : identToken.StartColumn + 1;
            }
            else
            {
                // sref.Line/Column already point to the identifier position (set by collector)
                line = sref.Line + 1;
                col = sref.Column + 1;
            }

            var isContractString = sref.IsContractString;

            if (isContractString)
            {
                // Contract string references need type-filtered enrichment
                var callerType = sref.CallerTypeName;
                var isUnknown = string.IsNullOrEmpty(callerType)
                    || callerType == GDWellKnownTypes.Variant
                    || callerType == GDWellKnownTypes.Object;

                if (!isUnknown && !string.IsNullOrEmpty(declaringTypeName))
                {
                    if (!IsTypeCompatible(callerType!, declaringTypeName))
                        continue;

                    // From unrelated hierarchy: skip strict contract strings
                    if (isFromUnrelatedHierarchy && sref.Confidence == GDReferenceConfidence.Strict)
                        continue;

                    var targetEdits = sref.Confidence == GDReferenceConfidence.Strict
                        ? strictEdits : potentialEdits;
                    targetEdits.Add(new GDTextEdit(
                        sref.FilePath, line, col, oldName, newName,
                        sref.Confidence, sref.ConfidenceReason)
                    {
                        IsContractString = true
                    });
                }
                else
                {
                    // Duck-typed contract string — enriched reason + provenance
                    var reason = sref.ConfidenceReason ?? "Duck-typed access";
                    IReadOnlyList<GDTypeProvenanceEntry>? provenance = null;
                    string? provenanceVarName = null;

                    // Build provenance if we have the original reference data
                    if (identToken != null)
                    {
                        var file = sref.Script;
                        var refNode = sref.Node;
                        if (refNode != null && file != null)
                        {
                            var gdRef = new GDReference
                            {
                                ReferenceNode = refNode,
                                IdentifierToken = identToken,
                                Confidence = sref.Confidence,
                                ConfidenceReason = sref.ConfidenceReason,
                                CallerTypeName = sref.CallerTypeName
                            };
                            provenance = BuildDuckTypeProvenance(file, gdRef, oldName,
                                declaringTypeName ?? "", typesWithMethod);
                            provenanceVarName = ExtractVariableName(gdRef);
                        }
                    }

                    potentialEdits.Add(new GDTextEdit(
                        sref.FilePath, line, col, oldName, newName,
                        GDReferenceConfidence.Potential, reason)
                    {
                        DetailedProvenance = provenance,
                        ProvenanceVariableName = provenanceVarName,
                        IsContractString = true
                    });
                }
                filesModified.Add(sref.FilePath);
            }
            else
            {
                // From unrelated hierarchy: skip strict references, keep potential (duck-typed)
                if (isFromUnrelatedHierarchy && sref.Confidence == GDReferenceConfidence.Strict)
                    continue;

                // Regular references: declaration, read, write, super call, type usage, override.
                // A union/shared reference is promoted to a strict edit only when every one of its
                // shared types is covered by a declaration being renamed in this operation, so all
                // possible resolutions produce the identical edit (risk-free).
                var isPromotedUnion = sref.Confidence == GDReferenceConfidence.Union
                    && renamedTypes != null
                    && IsUnionReferenceFullyCovered(sref, renamedTypes);

                var effectiveConfidence = isPromotedUnion
                    ? GDReferenceConfidence.Strict
                    : sref.Confidence;

                var targetEdits = effectiveConfidence == GDReferenceConfidence.Strict
                    ? strictEdits : potentialEdits;

                // Duck-typed cross-file references get provenance
                IReadOnlyList<GDTypeProvenanceEntry>? editProvenance = null;
                string? editProvenanceVar = null;
                if (sref.Confidence != GDReferenceConfidence.Strict && sref.Node != null)
                {
                    var gdRef = new GDReference
                    {
                        ReferenceNode = sref.Node,
                        IdentifierToken = identToken,
                        Confidence = sref.Confidence,
                        ConfidenceReason = sref.ConfidenceReason,
                        CallerTypeName = sref.CallerTypeName
                    };
                    editProvenance = BuildDuckTypeProvenance(
                        sref.Script, gdRef, oldName, declaringTypeName ?? "", typesWithMethod);
                    editProvenanceVar = ExtractVariableName(gdRef);
                }

                var editReason = sref.ConfidenceReason;
                if (sref.Kind == GDSymbolReferenceKind.Declaration
                    && string.IsNullOrEmpty(editReason)
                    && sref.Script?.TypeName == oldName)
                {
                    editReason = "class_name declaration";
                }

                if (isPromotedUnion && sref.SharedTypes is { Count: > 0 })
                    editReason = $"Shared across {string.Join("|", sref.SharedTypes)} (all renamed)";

                targetEdits.Add(new GDTextEdit(
                    sref.FilePath, line, col, oldName, newName,
                    effectiveConfidence, editReason)
                {
                    DetailedProvenance = editProvenance,
                    ProvenanceVariableName = editProvenanceVar
                });
                filesModified.Add(sref.FilePath);
            }
        }
    }

    /// <summary>
    /// Builds the set of type names whose member declaration is being renamed in this operation.
    /// Used to decide whether a union/shared reference is fully covered (all resolutions renamed).
    /// </summary>
    private static HashSet<string> BuildRenamedDeclaringTypes(GDSymbolReferences collectedRefs)
    {
        var set = new HashSet<string>(StringComparer.Ordinal);
        foreach (var r in collectedRefs.References)
        {
            if (r.Kind != GDSymbolReferenceKind.Declaration && r.Kind != GDSymbolReferenceKind.Override)
                continue;

            var typeName = r.Script?.TypeName;
            if (!string.IsNullOrEmpty(typeName))
                set.Add(typeName!);
        }
        return set;
    }

    /// <summary>
    /// A union/shared reference is fully covered when every type in its shared set has its member
    /// renamed in this operation (directly, or via inheritance from a renamed declaring type) — so
    /// every possible resolution of the receiver yields the identical edit and the rewrite is safe.
    /// </summary>
    private bool IsUnionReferenceFullyCovered(GDSymbolReference sref, HashSet<string> renamedTypes)
    {
        var shared = sref.SharedTypes;
        if (shared == null || shared.Count == 0)
            return false;

        foreach (var sharedType in shared)
        {
            if (string.IsNullOrEmpty(sharedType))
                return false;

            var covered = renamedTypes.Contains(sharedType)
                || renamedTypes.Any(rt => IsTypeCompatible(sharedType, rt));

            if (!covered)
                return false;
        }
        return true;
    }

    /// <summary>
    /// Collects edits for class_name rename across the project using type usages.
    /// </summary>
    private HashSet<string> BuildUnrelatedFilesSet(GDSymbolReferences collectedRefs, string? declaringTypeName)
    {
        var unrelated = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var declaringScript = collectedRefs.DeclaringScript;
        if (declaringScript?.FullPath == null || string.IsNullOrEmpty(declaringTypeName))
            return unrelated;

        // Collect scripts with independent declarations (not the declaring script itself)
        var otherDeclarationScripts = collectedRefs.References
            .Where(r => r.FilePath != null
                && r.Kind == GDSymbolReferenceKind.Declaration
                && !string.Equals(r.FilePath, declaringScript.FullPath, StringComparison.OrdinalIgnoreCase))
            .Select(r => r.Script)
            .Distinct()
            .ToList();

        var typeSystem = _projectModel?.TypeSystem;

        foreach (var otherScript in otherDeclarationScripts)
        {
            if (otherScript.FullPath == null) continue;
            var otherType = otherScript.TypeName;

            // If no type info, or not in same hierarchy, mark as unrelated
            if (string.IsNullOrEmpty(otherType) || typeSystem == null)
            {
                unrelated.Add(otherScript.FullPath);
                continue;
            }

            var inSameHierarchy =
                typeSystem.IsAssignableTo(otherType, declaringTypeName!) ||
                typeSystem.IsAssignableTo(declaringTypeName!, otherType);

            if (!inSameHierarchy)
                unrelated.Add(otherScript.FullPath);
        }

        // Files that only REFERENCE the name are never marked unrelated here: the caller's own class says
        // nothing about the receiver's type (coin.gd extends Area2D may call p.try_jump() on a typed Player).
        // Each reference's confidence (Strict vs Potential) already reflects the receiver type.

        return unrelated;
    }

    private void CollectClassNameEdits(
        GDScriptFile containingScript,
        string oldName,
        string newName,
        List<GDTextEdit> strictEdits,
        HashSet<string> filesModified)
    {
        if (_projectModel == null)
            return;

        // class_name declaration itself
        var classNameIdent = containingScript.Class?.ClassName?.Identifier;
        if (classNameIdent != null && containingScript.FullPath != null)
        {
            strictEdits.Add(new GDTextEdit(
                containingScript.FullPath,
                classNameIdent.StartLine + 1,
                classNameIdent.StartColumn + 1,
                oldName,
                newName,
                GDReferenceConfidence.Strict,
                "class_name declaration"));
            filesModified.Add(containingScript.FullPath);
        }

        // Find type usages across all project files
        foreach (var script in _project.ScriptFiles)
        {
            if (script.FullPath == null)
                continue;

            var model = _projectModel.GetSemanticModel(script);
            if (model == null)
                continue;

            var usages = model.GetTypeUsages(oldName);
            foreach (var usage in usages)
            {
                strictEdits.Add(new GDTextEdit(
                    script.FullPath,
                    usage.Line + 1,
                    usage.Column + 1,
                    oldName,
                    newName,
                    GDReferenceConfidence.Strict,
                    $"Type usage ({usage.Kind}) in {System.IO.Path.GetFileName(script.FullPath)}"));
                filesModified.Add(script.FullPath);
            }
        }
    }

    /// <summary>
    /// Removes duplicate edits at the same file/line/column position.
    /// </summary>
    private static List<GDTextEdit> DeduplicateEdits(List<GDTextEdit> edits)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var result = new List<GDTextEdit>(edits.Count);

        foreach (var edit in edits)
        {
            var key = $"{edit.FilePath}|{edit.Line}:{edit.Column}";
            if (seen.Add(key))
                result.Add(edit);
        }

        return result;
    }

    /// <summary>
    /// Removes from <paramref name="potentialEdits"/> any edit whose position is already a strict edit.
    /// A position is applied at most once; a strict (or promoted-union) edit always wins over a
    /// potential edit at the same location.
    /// </summary>
    private static void RemoveStrictDuplicatesFromPotential(
        List<GDTextEdit> strictEdits, List<GDTextEdit> potentialEdits)
    {
        if (strictEdits.Count == 0 || potentialEdits.Count == 0)
            return;

        var strictKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var edit in strictEdits)
            strictKeys.Add($"{edit.FilePath}|{edit.Line}:{edit.Column}");

        potentialEdits.RemoveAll(edit => strictKeys.Contains($"{edit.FilePath}|{edit.Line}:{edit.Column}"));
    }

    private GDScriptFile? FindScriptContainingSymbol(GDSymbolInfo symbol)
    {
        foreach (var script in _project.ScriptFiles)
        {
            if (script.SemanticModel == null)
                continue;

            if (script.SemanticModel.Symbols.Contains(symbol))
                return script;
        }
        return null;
    }

    private static bool IsClassMemberSymbol(GDSymbolInfo symbol)
    {
        return symbol.Kind switch
        {
            GDSymbolKind.Method => true,
            GDSymbolKind.Signal => true,
            GDSymbolKind.Variable when symbol.DeclarationNode is GDVariableDeclaration => true,
            GDSymbolKind.Constant when symbol.DeclarationNode is GDVariableDeclaration => true,
            GDSymbolKind.Enum => true,
            GDSymbolKind.EnumValue => true,
            GDSymbolKind.Class => true,
            _ => false
        };
    }

    /// <summary>
    /// Groups same-named class member definitions by type hierarchy and returns
    /// the root (most-base) definition for each independent hierarchy.
    /// </summary>
    private List<(GDScriptFile Script, GDSymbolInfo Symbol)> FindHierarchyRoots(
        List<(GDScriptFile Script, GDSymbolInfo Symbol)> definitions)
    {
        if (definitions.Count == 1 || _projectModel?.TypeSystem == null)
            return definitions;

        var roots = new List<(GDScriptFile Script, GDSymbolInfo Symbol)>();
        var assigned = new HashSet<int>();

        for (int i = 0; i < definitions.Count; i++)
        {
            if (assigned.Contains(i))
                continue;

            var root = definitions[i];
            var rootType = root.Script.TypeName;

            // Find the root of the hierarchy containing this definition
            for (int j = 0; j < definitions.Count; j++)
            {
                if (i == j || assigned.Contains(j))
                    continue;

                var other = definitions[j];
                var otherType = other.Script.TypeName;

                if (rootType == null || otherType == null)
                    continue;

                if (_projectModel.TypeSystem.IsAssignableTo(rootType, otherType))
                {
                    // otherType is a base of rootType → other is more-base
                    assigned.Add(i);
                    root = other;
                    rootType = otherType;
                }
                else if (_projectModel.TypeSystem.IsAssignableTo(otherType, rootType))
                {
                    // rootType is a base of otherType → mark other as covered
                    assigned.Add(j);
                }
            }

            if (!assigned.Contains(i))
            {
                roots.Add(root);
                assigned.Add(i);
            }
        }

        return roots;
    }

    private void CollectAllMemberAccessEdits(
        string oldName,
        string newName,
        List<GDTextEdit> strictEdits,
        List<GDTextEdit> potentialEdits,
        HashSet<string> filesModified)
    {
        foreach (var (file, reference) in _projectModel!.GetAllMemberAccessesForMemberInProject(oldName))
        {
            if (file.FullPath == null)
                continue;

            var identToken = reference.IdentifierToken;
            if (identToken == null)
                continue;

            // String literal tokens (e.g., has_method("name")) need +1 column offset for the opening quote
            var columnOffset = identToken is GDStringNode ? 2 : 1;

            var isContractString = identToken is GDStringNode;

            if (reference.Confidence == GDReferenceConfidence.Strict)
            {
                strictEdits.Add(new GDTextEdit(
                    file.FullPath,
                    identToken.StartLine + 1,
                    identToken.StartColumn + columnOffset,
                    oldName,
                    newName,
                    reference.Confidence,
                    reference.ConfidenceReason)
                {
                    IsContractString = isContractString
                });
            }
            else
            {
                var provenance = BuildDuckTypeProvenance(file, reference, oldName, "", null);
                potentialEdits.Add(new GDTextEdit(
                    file.FullPath,
                    identToken.StartLine + 1,
                    identToken.StartColumn + columnOffset,
                    oldName,
                    newName,
                    GDReferenceConfidence.Potential,
                    reference.ConfidenceReason)
                {
                    DetailedProvenance = provenance,
                    ProvenanceVariableName = ExtractVariableName(reference),
                    IsContractString = isContractString
                });
            }
            filesModified.Add(file.FullPath);
        }
    }

    /// <summary>
    /// Collects string reference warnings (e.g. concatenated strings matching oldName) from all semantic models.
    /// </summary>
    private List<GDRenameWarning> CollectStringReferenceWarnings(string oldName)
    {
        var warnings = new List<GDRenameWarning>();
        if (_projectModel == null)
            return warnings;

        foreach (var script in _project.ScriptFiles)
        {
            var model = _projectModel.GetSemanticModel(script);
            if (model == null)
                continue;

            foreach (var w in model.GetStringReferenceWarnings(oldName))
            {
                warnings.Add(new GDRenameWarning(
                    script.FullPath ?? "",
                    w.Node.StartLine + 1,
                    w.Node.StartColumn + 1,
                    w.Reason));
            }
        }

        return warnings;
    }

    private List<GDRenameWarning> CollectReflectionWarnings(string oldName, GDSymbolKind symbolKind)
    {
        var warnings = new List<GDRenameWarning>();
        if (_projectModel == null)
            return warnings;

        var reflectionKind = MapToReflectionKind(symbolKind);
        if (reflectionKind == null)
            return warnings;

        foreach (var script in _project.ScriptFiles)
        {
            var model = _projectModel.GetSemanticModel(script);
            if (model == null) continue;

            foreach (var site in model.GetReflectionCallSites())
            {
                if (site.Kind != reflectionKind) continue;
                if (!site.Matches(oldName)) continue;

                warnings.Add(new GDRenameWarning(
                    script.FullPath ?? "",
                    site.Line + 1,
                    site.Column + 1,
                    $"Reflection pattern: {FormatReflectionListMethod(site.Kind)} + {site.CallMethod}() may reference '{oldName}' dynamically"));
            }
        }

        return warnings;
    }

    private List<GDRenameWarning> CollectCSharpInteropWarnings(string oldName, GDScriptFile? declaringScript)
    {
        var warnings = new List<GDRenameWarning>();
        if (_projectModel == null || declaringScript == null)
            return warnings;

        if (!_projectModel.CSharpInterop.HasCSharpCode)
            return warnings;

        // Check if declaring script is an autoload
        var autoloadName = _project.AutoloadEntries
            .Where(a => a.IsScript || a.IsScene)
            .FirstOrDefault(a =>
            {
                var resPath = a.Path;
                if (resPath.StartsWith("res://", StringComparison.OrdinalIgnoreCase))
                    resPath = resPath.Substring(6);
                var fullPath = System.IO.Path.GetFullPath(
                    System.IO.Path.Combine(_project.ProjectPath, resPath))
                    .Replace('\\', '/').TrimEnd('/');
                return declaringScript.FullPath != null &&
                    fullPath.Equals(declaringScript.FullPath, StringComparison.OrdinalIgnoreCase);
            })?.Name;

        if (autoloadName == null)
            return warnings;

        if (!oldName.StartsWith("_"))
        {
            warnings.Add(new GDRenameWarning(
                declaringScript.FullPath ?? "",
                0, 0,
                $"Symbol '{oldName}' is on autoload '{autoloadName}' in a mixed GDScript/C# project. " +
                $"C# code may reference it via Call(\"{oldName}\") — update C# callers manually."));
        }

        return warnings;
    }

    private static GDReflectionKind? MapToReflectionKind(GDSymbolKind kind)
    {
        return kind switch
        {
            GDSymbolKind.Method => GDReflectionKind.Method,
            GDSymbolKind.Variable => GDReflectionKind.Property,
            GDSymbolKind.Constant => GDReflectionKind.Property,
            GDSymbolKind.Property => GDReflectionKind.Property,
            GDSymbolKind.Signal => GDReflectionKind.Signal,
            _ => null
        };
    }

    private static string FormatReflectionListMethod(GDReflectionKind kind)
    {
        return kind switch
        {
            GDReflectionKind.Method => "get_method_list()",
            GDReflectionKind.Property => "get_property_list()",
            GDReflectionKind.Signal => "get_signal_list()",
            _ => "reflection"
        };
    }

    /// <summary>
    /// Edits .tscn [connection] attributes for both roles: <c>method="old"</c> (callback, typed by the TO node)
    /// and <c>signal="old"</c> (typed by the FROM node). Node types resolve instanced sub-scenes.
    /// </summary>
    private void CollectTscnEdits(
        string oldName,
        string newName,
        string? declaringTypeName,
        List<GDTextEdit> strictEdits,
        List<GDTextEdit>? potentialEdits,
        HashSet<string> filesModified)
    {
        var sceneProvider = _project.SceneTypesProvider;
        if (sceneProvider == null)
            return;

        foreach (var scene in sceneProvider.AllScenes)
        {
            if (string.IsNullOrEmpty(scene.FullPath))
                continue;

            foreach (var conn in scene.SignalConnections)
            {
                if (conn.Method == oldName)
                {
                    var column = FindMethodColumnInTscn(scene.FullPath, conn.LineNumber, oldName);
                    AddTscnEdit(scene, conn.ToNode, conn.LineNumber, column, "method",
                        oldName, newName, declaringTypeName, strictEdits, potentialEdits, filesModified);
                }

                if (conn.SignalName == oldName)
                {
                    // GDTextEdit columns are 1-based; SignalColumn is 0-based.
                    AddTscnEdit(scene, conn.FromNode, conn.LineNumber, conn.SignalColumn + 1, "signal",
                        oldName, newName, declaringTypeName, strictEdits, potentialEdits, filesModified);
                }
            }
        }
    }

    private void AddTscnEdit(
        GDSceneInfo scene,
        string nodePath,
        int line,
        int column,
        string attribute,
        string oldName,
        string newName,
        string? declaringTypeName,
        List<GDTextEdit> strictEdits,
        List<GDTextEdit>? potentialEdits,
        HashSet<string> filesModified)
    {
        if (string.IsNullOrEmpty(declaringTypeName))
        {
            // No declaring type — all connections are strict
            strictEdits.Add(new GDTextEdit(scene.FullPath, line, column, oldName, newName,
                GDReferenceConfidence.Strict, $".tscn signal connection {attribute}=\"{oldName}\""));
            filesModified.Add(scene.FullPath);
            return;
        }

        var node = scene.Nodes.FirstOrDefault(n => n.Path == nodePath || (nodePath == "." && n.Path == "."));
        var nodeType = node == null
            ? null
            : _projectModel?.ResolveSceneNodeTypeName(scene, node) ?? node.ScriptTypeName ?? node.NodeType;

        if (!string.IsNullOrEmpty(nodeType) && IsTypeCompatible(nodeType!, declaringTypeName))
        {
            strictEdits.Add(new GDTextEdit(scene.FullPath, line, column, oldName, newName,
                GDReferenceConfidence.Strict, $".tscn signal connection {attribute}=\"{oldName}\" (node: {nodeType})"));
            filesModified.Add(scene.FullPath);
        }
        else if (string.IsNullOrEmpty(nodeType) && potentialEdits != null)
        {
            potentialEdits.Add(new GDTextEdit(scene.FullPath, line, column, oldName, newName,
                GDReferenceConfidence.Potential, $".tscn signal connection {attribute}=\"{oldName}\" (node type unknown)"));
            filesModified.Add(scene.FullPath);
        }
    }

    private int FindMethodColumnInTscn(string filePath, int lineNumber, string methodName)
    {
        try
        {
            var lines = File.ReadAllLines(filePath);
            if (lineNumber > 0 && lineNumber <= lines.Length)
            {
                var line = lines[lineNumber - 1];
                var marker = $"method=\"{methodName}\"";
                var idx = line.IndexOf(marker, StringComparison.Ordinal);
                if (idx >= 0)
                    return idx + "method=\"".Length + 1; // 1-based column, inside the quotes
            }
        }
        catch
        {
            // Fall back to column 1 if file can't be read
        }

        return 1;
    }

    private bool IsTypeCompatible(string sourceType, string targetType)
    {
        if (string.IsNullOrEmpty(sourceType) || string.IsNullOrEmpty(targetType))
            return false;

        if (string.Equals(sourceType, targetType, StringComparison.Ordinal))
            return true;

        return _runtimeProvider?.IsAssignableTo(sourceType, targetType) ?? false;
    }


    private IReadOnlyList<GDTypeProvenanceEntry>? BuildDuckTypeProvenance(
        GDScriptFile file,
        GDReference reference,
        string memberName,
        string declaringTypeName,
        IReadOnlyList<string>? typesWithMethod)
    {
        var result = new List<GDTypeProvenanceEntry>();

        // Step 1: Extract variable name and find enclosing method
        var varName = ExtractVariableName(reference);
        var method = FindEnclosingMethod(reference.ReferenceNode);

        if (varName == null || method == null)
            return null;

        var enclosingTypeName = file.TypeName;
        var methodName = method.Identifier?.Sequence;

        if (string.IsNullOrEmpty(enclosingTypeName) || string.IsNullOrEmpty(methodName))
            return null;

        // Step 2: Determine if variable is a parameter
        var paramIndex = FindParameterIndex(method, varName);
        var isParameter = paramIndex >= 0;

        if (isParameter)
        {
            // Level 1: Direct call site evidence
            try
            {
                var collector = new GDCallSiteCollector(_project);
                var callSites = collector.CollectCallSites(enclosingTypeName, methodName);

                foreach (var cs in callSites)
                {
                    var arg = cs.GetArgument(paramIndex);
                    if (arg == null) continue;
                    var argType = arg.InferredType?.DisplayName;
                    if (string.IsNullOrEmpty(argType) || GDSemanticType.FromRuntimeTypeName(argType).IsVariant) continue;

                    string evidenceType = argType;
                    string reason = $"arg at {enclosingTypeName}.{methodName}()";

                    // For containers: extract element type
                    if (GDSemanticType.FromRuntimeTypeName(argType).IsContainer)
                    {
                        var el = GDFlowNarrowingHelper.ExtractElementTypeFromTypeName(argType);
                        if (!string.IsNullOrEmpty(el))
                        {
                            evidenceType = el;
                            reason = $"element of {argType}";
                        }
                    }

                    var innerChain = TraceArgumentOrigin(cs.SourceScript, arg.ExpressionText, arg.Expression);

                    // Try to narrow evidenceType from inner chain (e.g. parameter Node2D → call site passes Area2D)
                    var narrowed = TryNarrowTypeFromChain(innerChain, evidenceType);
                    if (narrowed != null)
                        evidenceType = narrowed;

                    var callSiteEntries = new List<GDCallSiteProvenanceEntry>
                    {
                        new GDCallSiteProvenanceEntry(cs.FilePath, cs.Line + 1, arg.ExpressionText, innerChain)
                    };

                    result.Add(new GDTypeProvenanceEntry(
                        evidenceType, reason, cs.Line,
                        callSites: callSiteEntries,
                        sourceFilePath: cs.FilePath));
                }
            }
            catch
            {
                // Call site collection may fail
            }

            // Level 2: Signal callback parameter evidence
            if (_projectModel != null)
            {
                try
                {
                    var connections = _projectModel.SignalConnectionRegistry
                        .GetSignalsCallingMethod(enclosingTypeName, methodName);

                    foreach (var conn in connections)
                    {
                        if (string.IsNullOrEmpty(conn.EmitterType)) continue;

                        var signalParams = GetSignalParameterTypes(conn.EmitterType, conn.SignalName);
                        if (signalParams != null && paramIndex < signalParams.Count)
                        {
                            var paramType = signalParams[paramIndex];
                            if (!string.IsNullOrEmpty(paramType) && !GDSemanticType.FromRuntimeTypeName(paramType).IsVariant)
                            {
                                var signalCallSite = new GDCallSiteProvenanceEntry(
                                    conn.SourceFilePath ?? file.FullPath ?? "",
                                    conn.Line,
                                    $"{conn.EmitterType}.{conn.SignalName} signal -> {methodName}({varName}: {paramType})");

                                result.Add(new GDTypeProvenanceEntry(
                                    paramType,
                                    $"from {conn.EmitterType}.{conn.SignalName} signal",
                                    conn.Line,
                                    callSites: new[] { signalCallSite },
                                    sourceFilePath: conn.SourceFilePath));
                            }
                        }
                    }
                }
                catch
                {
                    // Signal tracing may fail
                }
            }
        }
        else
        {
            // Not a parameter — check flow-sensitive type (local variable or class member)
            try
            {
                var model = _projectModel.ResolveModel(file);
                if (model != null)
                {
                    var flowType = model.GetFlowVariableType(varName, reference.ReferenceNode);
                    if (flowType?.DeclaredType != null)
                    {
                        var typeName = flowType.DeclaredType.DisplayName;
                        if (!string.IsNullOrEmpty(typeName) && !GDSemanticType.FromRuntimeTypeName(typeName).IsVariant)
                            result.Add(new GDTypeProvenanceEntry(typeName, "type annotation"));
                    }
                    else if (flowType?.CurrentType != null)
                    {
                        var effectiveType = flowType.CurrentType.EffectiveType?.DisplayName;
                        if (!string.IsNullOrEmpty(effectiveType) && !GDSemanticType.FromRuntimeTypeName(effectiveType).IsVariant)
                            result.Add(new GDTypeProvenanceEntry(effectiveType, "flow-inferred type"));
                    }
                }

                // Level 3: Container element type for iteration variables
                if (_projectModel != null && result.Count == 0)
                {
                    // First try direct container profile for varName
                    var containerProfile = _projectModel.GetMergedContainerProfile(enclosingTypeName, varName);
                    string? containerVarName = varName;

                    // If not found, check if varName is a for-loop iteration variable
                    // and trace back to the source container
                    if (containerProfile == null)
                    {
                        var forStmt = FindEnclosingForStatement(reference.ReferenceNode, varName);
                        if (forStmt?.Collection is GDIdentifierExpression collectionIdent)
                        {
                            containerVarName = collectionIdent.Identifier?.Sequence;
                            if (!string.IsNullOrEmpty(containerVarName))
                            {
                                containerProfile = _projectModel.GetMergedContainerProfile(
                                    enclosingTypeName, containerVarName);
                            }
                        }
                    }

                    if (containerProfile != null)
                    {
                        var elementType = containerProfile.ComputeInferredType();
                        if (elementType?.HasElementTypes == true)
                        {
                            var elType = elementType.EffectiveElementType?.DisplayName;
                            if (!string.IsNullOrEmpty(elType) && !GDSemanticType.FromRuntimeTypeName(elType).IsVariant)
                                result.Add(new GDTypeProvenanceEntry(elType, $"element of {containerVarName}"));
                        }

                        // Level 4: If container element type is Variant, trace append sites
                        // to find signal callback parameters that populate the container
                        if (result.Count == 0)
                        {
                            TraceContainerAppendSources(file, containerProfile, enclosingTypeName!, containerVarName!, result);
                        }
                    }
                }
            }
            catch
            {
                // Flow analysis may fail
            }
        }

        // No fallback — if no evidence found, return null (honest)
        return result.Count > 0 ? result : null;
    }

    private static string? ExtractVariableName(GDReference reference)
    {
        // Handle has_method string literal: obj.has_method("take_damage") → "obj"
        if (reference.ReferenceNode is GDStringNode or GDStringExpression)
        {
            var parent = reference.ReferenceNode.Parent;
            while (parent != null && parent is not GDCallExpression)
                parent = parent.Parent;
            if (parent is GDCallExpression call
                && call.CallerExpression is GDMemberOperatorExpression hasMethodMemberOp)
            {
                var callerExpr = hasMethodMemberOp.CallerExpression;
                while (callerExpr is GDMemberOperatorExpression nested)
                    callerExpr = nested.CallerExpression;
                return (callerExpr as GDIdentifierExpression)?.Identifier?.Sequence;
            }
        }

        // Extract from ConfidenceReason: "Duck-typed access on 'varName'"
        var reason = reference.ConfidenceReason;
        if (reason != null)
        {
            var startIdx = reason.IndexOf('\'');
            if (startIdx >= 0)
            {
                var endIdx = reason.IndexOf('\'', startIdx + 1);
                if (endIdx > startIdx)
                    return reason.Substring(startIdx + 1, endIdx - startIdx - 1);
            }
        }

        // Fallback: walk the AST from ReferenceNode to find the caller identifier
        if (reference.ReferenceNode is GDMemberOperatorExpression memberOp)
        {
            var caller = memberOp.CallerExpression;
            while (caller is GDMemberOperatorExpression nested)
                caller = nested.CallerExpression;
            return (caller as GDIdentifierExpression)?.Identifier?.Sequence;
        }

        return null;
    }

    private static GDMethodDeclaration? FindEnclosingMethod(GDNode? node)
        => GDProvenanceTracer.FindEnclosingMethod(node);

    private static GDForStatement? FindEnclosingForStatement(GDNode? node, string varName)
        => GDProvenanceTracer.FindEnclosingForStatement(node, varName);

    private static int FindParameterIndex(GDMethodDeclaration method, string paramName)
        => GDProvenanceTracer.FindParameterIndex(method, paramName);

    private static T? FindParentOfType<T>(GDSyntaxToken? node) where T : GDNode
    {
        var current = node?.Parent;
        while (current != null)
        {
            if (current is T target)
                return target;
            current = current.Parent;
        }
        return null;
    }

    private void TraceContainerAppendSources(
        GDScriptFile file,
        GDContainerUsageProfile containerProfile,
        string enclosingTypeName,
        string containerVarName,
        List<GDTypeProvenanceEntry> result)
    {
        if (_projectModel == null)
            return;

        // Use container profile's ValueUsages to find append sites,
        // then trace each appended value back to its source
        foreach (var usage in containerProfile.ValueUsages)
        {
            if (usage.Node == null)
                continue;

            // Only handle append-like operations
            if (usage.Kind != GDContainerUsageKind.Append
                && usage.Kind != GDContainerUsageKind.PushBack
                && usage.Kind != GDContainerUsageKind.PushFront)
                continue;

            // If the type is already concrete, skip (Level 3 should have caught it)
            if (usage.InferredType != null && !usage.InferredType.IsVariant)
                continue;

            // Find the call expression and extract the appended argument
            var callNode = usage.Node is GDCallExpression callExpr
                ? callExpr
                : FindParentOfType<GDCallExpression>(usage.Node);
            if (callNode == null)
                continue;

            var args = callNode.Parameters?.ToList();
            if (args == null || args.Count == 0)
                continue;

            var appendedExpr = args[0];
            if (appendedExpr is not GDIdentifierExpression appendedIdent)
                continue;

            var appendedVarName = appendedIdent.Identifier?.Sequence;
            if (string.IsNullOrEmpty(appendedVarName))
                continue;

            // Find the enclosing method of the append call
            var method = FindEnclosingMethod(usage.Node);
            if (method == null)
                continue;

            // Check if the appended variable is a parameter of this method
            var paramIdx = FindParameterIndex(method, appendedVarName);
            if (paramIdx < 0)
                continue;

            var appendMethodName = method.Identifier?.Sequence;
            if (string.IsNullOrEmpty(appendMethodName))
                continue;

            // Level 4a: Check signal callback parameters
            try
            {
                var connections = _projectModel.SignalConnectionRegistry
                    .GetSignalsCallingMethod(enclosingTypeName, appendMethodName);

                foreach (var conn in connections)
                {
                    if (string.IsNullOrEmpty(conn.EmitterType))
                        continue;

                    var signalParams = GetSignalParameterTypes(conn.EmitterType, conn.SignalName);
                    if (signalParams != null && paramIdx < signalParams.Count)
                    {
                        var paramType = signalParams[paramIdx];
                        if (!string.IsNullOrEmpty(paramType) && !GDSemanticType.FromRuntimeTypeName(paramType).IsVariant)
                        {
                            var usageLine = (usage.Node.FirstLeafToken?.StartLine ?? 0) + 1;
                            var appendCallSite = new GDCallSiteProvenanceEntry(
                                file.FullPath ?? "", usageLine,
                                $"{containerVarName}.append({appendedVarName}) " +
                                $"<- {appendMethodName}({appendedVarName}: {paramType}) " +
                                $"<- {conn.EmitterType}.{conn.SignalName} signal");

                            result.Add(new GDTypeProvenanceEntry(
                                paramType,
                                $"via {conn.EmitterType}.{conn.SignalName} -> {appendMethodName}() -> {containerVarName}",
                                conn.Line,
                                callSites: new[] { appendCallSite },
                                sourceFilePath: conn.SourceFilePath));
                        }
                    }
                }
            }
            catch
            {
                // Signal tracing may fail
            }

            // Level 4b: Check direct call sites
            if (result.Count == 0)
            {
                try
                {
                    var collector = new GDCallSiteCollector(_project);
                    var callSites = collector.CollectCallSites(enclosingTypeName, appendMethodName);

                    foreach (var cs in callSites)
                    {
                        var arg = cs.GetArgument(paramIdx);
                        if (arg == null) continue;
                        var argType = arg.InferredType?.DisplayName;
                        if (string.IsNullOrEmpty(argType) || GDSemanticType.FromRuntimeTypeName(argType).IsVariant) continue;

                        var innerChain = TraceArgumentOrigin(cs.SourceScript, arg.ExpressionText, arg.Expression);
                        var callSiteEntry = new GDCallSiteProvenanceEntry(
                            cs.FilePath, cs.Line + 1,
                            $"{appendMethodName}({arg.ExpressionText}) -> {containerVarName}", innerChain);

                        result.Add(new GDTypeProvenanceEntry(
                            argType,
                            $"via {appendMethodName}() -> {containerVarName}",
                            cs.Line,
                            callSites: new[] { callSiteEntry },
                            sourceFilePath: cs.FilePath));
                    }
                }
                catch
                {
                    // Call site collection may fail
                }
            }
        }
    }

    private List<GDCallSiteProvenanceEntry> TraceArgumentOrigin(
        GDScriptFile callSiteFile,
        string argVarName,
        GDExpression? argExpr,
        int maxDepth = 3)
    {
        return GDProvenanceTracer.TraceArgumentOrigin(
            _project, _projectModel, _runtimeProvider,
            callSiteFile, argVarName, argExpr, maxDepth);
    }

    private string? TryNarrowTypeFromChain(
        IReadOnlyList<GDCallSiteProvenanceEntry> chain, string currentType)
    {
        return GDProvenanceTracer.TryNarrowTypeFromChain(_runtimeProvider, chain, currentType);
    }

    private IReadOnlyList<string>? GetSignalParameterTypes(string emitterType, string signalName)
        => GDProvenanceTracer.GetSignalParameterTypes(_project, _runtimeProvider, emitterType, signalName);

    #endregion
}
