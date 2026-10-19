using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using GDShrapt.Reader;
using GDShrapt.Semantics;

namespace GDShrapt.LSP;

/// <summary>
/// Handles workspace/symbol requests: every script's class_name class, its members and inner classes, and the
/// autoload singletons declared in project.godot. Locations point at the identifier (the position clients use
/// for references/rename/hierarchy).
/// </summary>
public class GDWorkspaceSymbolHandler
{
    private readonly GDScriptProject _project;

    public GDWorkspaceSymbolHandler(GDScriptProject project)
    {
        _project = project;
    }

    public Task<GDLspSymbolInformation[]?> HandleAsync(GDWorkspaceSymbolParams @params, CancellationToken cancellationToken)
    {
        var query = @params.Query?.ToLowerInvariant() ?? "";
        var results = new List<GDLspSymbolInformation>();

        void Add(string? name, GDLspSymbolKind kind, string uri, int line, int column, string? container = null)
        {
            if (string.IsNullOrEmpty(name) || (query.Length > 0 && !name.ToLowerInvariant().Contains(query)))
                return;
            results.Add(new GDLspSymbolInformation
            {
                Name = name,
                Kind = kind,
                ContainerName = container,
                Location = new GDLspLocation
                {
                    Uri = uri,
                    Range = new GDLspRange(line, column, line, column + name.Length)
                }
            });
        }

        foreach (var script in _project.ScriptFiles)
        {
            if (script.Class == null || script.FullPath == null)
                continue;

            var uri = GDDocumentManager.PathToUri(script.FullPath);
            var className = script.Class.ClassName?.Identifier;
            if (className != null)
                Add(className.Sequence, GDLspSymbolKind.Class, uri, className.StartLine, className.StartColumn);

            AddMembers(script.Class.Members, uri, className?.Sequence, Add);
        }

        // Autoload singletons are declared in project.godot, not in any script.
        var projectGodot = Path.Combine(_project.ProjectPath, "project.godot");
        if (File.Exists(projectGodot))
        {
            var uri = GDDocumentManager.PathToUri(projectGodot);
            foreach (var (key, line, column) in GDProjectGodotLocator.Keys(projectGodot, "autoload"))
                Add(key, GDLspSymbolKind.Variable, uri, line - 1, column - 1, "autoload");
        }

        return Task.FromResult<GDLspSymbolInformation[]?>(results.ToArray());
    }

    private delegate void AddSymbol(string? name, GDLspSymbolKind kind, string uri, int line, int column, string? container);

    private static void AddMembers(IEnumerable<GDClassMember> members, string uri, string? container, AddSymbol add)
    {
        foreach (var member in members)
        {
            switch (member)
            {
                case GDMethodDeclaration m when m.Identifier != null:
                    add(m.Identifier.Sequence, GDLspSymbolKind.Method, uri, m.Identifier.StartLine, m.Identifier.StartColumn, container);
                    break;
                case GDVariableDeclaration v when v.Identifier != null:
                    add(v.Identifier.Sequence, v.IsConstant ? GDLspSymbolKind.Constant : GDLspSymbolKind.Variable,
                        uri, v.Identifier.StartLine, v.Identifier.StartColumn, container);
                    break;
                case GDSignalDeclaration s when s.Identifier != null:
                    add(s.Identifier.Sequence, GDLspSymbolKind.Event, uri, s.Identifier.StartLine, s.Identifier.StartColumn, container);
                    break;
                case GDEnumDeclaration e when e.Identifier != null:
                    add(e.Identifier.Sequence, GDLspSymbolKind.Enum, uri, e.Identifier.StartLine, e.Identifier.StartColumn, container);
                    break;
                case GDInnerClassDeclaration c when c.Identifier != null:
                    add(c.Identifier.Sequence, GDLspSymbolKind.Class, uri, c.Identifier.StartLine, c.Identifier.StartColumn, container);
                    AddMembers(c.Members, uri, c.Identifier.Sequence, add);
                    break;
            }
        }
    }
}
