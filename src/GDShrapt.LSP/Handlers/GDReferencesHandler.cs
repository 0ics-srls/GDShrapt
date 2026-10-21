using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using GDShrapt.Abstractions;
using GDShrapt.CLI.Core;

namespace GDShrapt.LSP;

/// <summary>
/// Handles textDocument/references requests.
/// Thin wrapper over IGDFindRefsHandler from CLI.Core.
/// Uses IGDGoToDefHandler to get symbol name at cursor position.
/// </summary>
public class GDReferencesHandler
{
    private readonly IGDFindRefsHandler _findRefsHandler;
    private readonly IGDGoToDefHandler _goToDefHandler;

    public GDReferencesHandler(IGDFindRefsHandler findRefsHandler, IGDGoToDefHandler goToDefHandler)
    {
        _findRefsHandler = findRefsHandler;
        _goToDefHandler = goToDefHandler;
    }

    public Task<GDLspLocation[]?> HandleAsync(GDReferencesParams @params, CancellationToken cancellationToken)
    {
        var filePath = GDDocumentManager.UriToPath(@params.TextDocument.Uri);

        // Resolve the symbol at the cursor (shared with rename; identical to the CLI path)
        var symbolName = GDLspCursorSymbol.ResolveName(_goToDefHandler, filePath, @params.Position);
        if (symbolName == null)
            return Task.FromResult<GDLspLocation[]?>(null);

        // Delegate to CLI.Core handler
        var groups = _findRefsHandler.FindReferences(symbolName, filePath);
        if (groups == null || groups.Count == 0)
            return Task.FromResult<GDLspLocation[]?>(null);

        // Flatten groups (including nested overrides) into a single list.
        // Reads return the SAME set as the CLI find-refs handler (including potential/duck-typed
        // references) so CLI and LSP are identical. Write operations (rename) stay strict-only.
        IEnumerable<CLI.Core.GDCliReferenceLocation> allRefs = FlattenLocations(groups);

        // LSP carries no confidence: a duck-typed reference (untyped receiver) names the member only, and when an
        // unrelated class declares the same name it is as likely theirs. LSAI#53: Pylon.is_loaded() listed GUT's
        // `coll_script.is_loaded` (CollectedScript has its own). A uniquely named member keeps its duck-typed uses.
        var materialized = allRefs.ToList();
        if (materialized.Any(IsDuckTyped)
            && _findRefsHandler.FindAllReferences(symbolName, filePath).UnrelatedGroups.Count > 0)
            materialized = materialized.Where(r => !IsDuckTyped(r)).ToList();
        allRefs = materialized;

        // Filter results based on IncludeDeclaration
        if (!@params.Context.IncludeDeclaration)
            allRefs = allRefs.Where(r => !r.IsDeclaration);

        // Sort so declarations come first (VS Code highlights the first match as definition)
        allRefs = allRefs.OrderByDescending(r => r.IsDeclaration);

        // Convert CLI.Core results to LSP locations
        var locations = new List<GDLspLocation>();
        foreach (var reference in allRefs)
        {
            // GDCliReferenceLocation uses 1-based line and 1-based column
            // Convert column to 0-based for ToLspRange which expects 0-based columns
            var col0 = reference.Column - 1;
            locations.Add(new GDLspLocation
            {
                Uri = GDDocumentManager.PathToUri(reference.FilePath),
                Range = GDLocationAdapter.ToLspRange(
                    reference.Line,
                    col0,
                    reference.Line,
                    col0 + symbolName.Length)
            });
        }

        return Task.FromResult<GDLspLocation[]?>(locations.ToArray());
    }

    private static bool IsDuckTyped(CLI.Core.GDCliReferenceLocation r) =>
        r.Confidence is GDReferenceConfidence.Potential or GDReferenceConfidence.NameMatch && !r.IsContractString;

    private static IEnumerable<CLI.Core.GDCliReferenceLocation> FlattenLocations(IEnumerable<GDReferenceGroup> groups)
    {
        foreach (var group in groups)
        {
            foreach (var loc in group.Locations)
                yield return loc;

            foreach (var loc in FlattenLocations(group.Overrides))
                yield return loc;
        }
    }
}
