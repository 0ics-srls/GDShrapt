using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using GDShrapt.Abstractions;
using GDShrapt.Semantics;

namespace GDShrapt.LSP;

/// <summary>
/// textDocument/prepareTypeHierarchy + typeHierarchy/supertypes|subtypes for scripts: a script's type is its
/// class_name (or file name); its supertype is its `extends` (class name, res:// path or engine type); engine types
/// continue up through the runtime provider. Scripts without `extends` derive from RefCounted (GDScript default).
/// </summary>
public class GDTypeHierarchyHandler
{
    private readonly GDScriptProject _project;
    private readonly IGDRuntimeProvider? _runtimeProvider;

    public GDTypeHierarchyHandler(GDScriptProject project, IGDRuntimeProvider? runtimeProvider)
    {
        _project = project;
        _runtimeProvider = runtimeProvider;
    }

    public Task<GDLspTypeHierarchyItem[]?> PrepareAsync(GDTypeHierarchyPrepareParams @params, CancellationToken ct)
    {
        var script = _project.GetScript(GDDocumentManager.UriToPath(@params.TextDocument.Uri));
        var item = script?.Class != null ? ScriptItem(script) : null;
        return Task.FromResult(item != null ? new[] { item } : null);
    }

    public Task<GDLspTypeHierarchyItem[]?> SupertypesAsync(GDTypeHierarchyItemParams @params, CancellationToken ct)
    {
        var data = @params.Item.Data;
        if (data?.Builtin != null)
        {
            var engineBase = _runtimeProvider?.GetBaseType(data.Builtin);
            return Task.FromResult(string.IsNullOrEmpty(engineBase) ? System.Array.Empty<GDLspTypeHierarchyItem>() : new[] { BuiltinItem(engineBase!) })!;
        }

        var script = data?.FilePath != null ? _project.GetScript(data.FilePath) : null;
        if (script?.Class == null)
            return Task.FromResult<GDLspTypeHierarchyItem[]?>(System.Array.Empty<GDLspTypeHierarchyItem>());

        var (baseScript, baseName) = ResolveBase(script);
        var super = baseScript != null ? ScriptItem(baseScript) : BuiltinItem(baseName);
        return Task.FromResult<GDLspTypeHierarchyItem[]?>(new[] { super });
    }

    public Task<GDLspTypeHierarchyItem[]?> SubtypesAsync(GDTypeHierarchyItemParams @params, CancellationToken ct)
    {
        var data = @params.Item.Data;
        var target = data?.FilePath != null ? _project.GetScript(data.FilePath) : null;
        var targetName = data?.Builtin ?? @params.Item.Name;

        var subs = new List<GDLspTypeHierarchyItem>();
        foreach (var script in _project.ScriptFiles)
        {
            if (script.Class == null || ReferenceEquals(script, target))
                continue;
            var (baseScript, baseName) = ResolveBase(script);
            var matches = target != null ? ReferenceEquals(baseScript, target) : baseScript == null && baseName == targetName;
            if (matches)
                subs.Add(ScriptItem(script));
        }
        return Task.FromResult<GDLspTypeHierarchyItem[]?>(subs.ToArray());
    }

    /// <summary>The script's base: a project script (by class_name or res:// path) or an engine type name.</summary>
    private (GDScriptFile? Script, string Name) ResolveBase(GDScriptFile script)
    {
        var raw = script.Class?.Extends?.Type?.ToString()?.Trim().Trim('"', '\'');
        if (string.IsNullOrEmpty(raw))
            return (null, "RefCounted");
        if (raw!.StartsWith("res://", System.StringComparison.Ordinal))
        {
            var byPath = _project.GetScriptByResourcePath(raw);
            return (byPath, byPath?.TypeName ?? Path.GetFileNameWithoutExtension(raw));
        }
        var byName = _project.ScriptFiles.FirstOrDefault(s => s.IsGlobal && s.TypeName == raw);
        return (byName, raw);
    }

    private static GDLspTypeHierarchyItem ScriptItem(GDScriptFile script)
    {
        var id = script.Class?.ClassName?.Identifier;
        var selection = id != null
            ? new GDLspRange(id.StartLine, id.StartColumn, id.StartLine, id.StartColumn + id.Sequence.Length)
            : new GDLspRange(0, 0, 0, 0);
        var lastLine = script.Class?.AllTokens.LastOrDefault()?.EndLine ?? 0;
        return new GDLspTypeHierarchyItem
        {
            Name = script.TypeName ?? Path.GetFileNameWithoutExtension(script.FullPath ?? ""),
            Kind = 5,
            Uri = GDDocumentManager.PathToUri(script.FullPath!),
            Range = new GDLspRange(0, 0, lastLine, 0),
            SelectionRange = selection,
            Data = new GDTypeHierarchyItemData { FilePath = script.FullPath }
        };
    }

    private GDLspTypeHierarchyItem BuiltinItem(string name) => new()
    {
        // Engine types have no source in the project: point at project.godot so clients get a valid file URI.
        Name = name,
        Kind = 5,
        Detail = "engine type",
        Uri = GDDocumentManager.PathToUri(Path.Combine(_project.ProjectPath, "project.godot")),
        Data = new GDTypeHierarchyItemData { Builtin = name }
    };
}
