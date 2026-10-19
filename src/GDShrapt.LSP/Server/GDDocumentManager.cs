using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using GDShrapt.Semantics;

namespace GDShrapt.LSP;

/// <summary>
/// Manages open documents in the language server.
/// </summary>
public class GDDocumentManager
{
    private readonly ConcurrentDictionary<string, GDOpenDocument> _documents = new();
    private readonly GDScriptProject _project;
    private GDProjectSemanticModel? _projectModel;
    private volatile bool _initialAnalysisComplete;

    public GDDocumentManager(GDScriptProject project)
    {
        _project = project;
    }

    public void SetProjectModel(GDProjectSemanticModel? projectModel)
    {
        _projectModel = projectModel;
    }

    public bool IsInitialAnalysisComplete => _initialAnalysisComplete;

    public void SetInitialAnalysisComplete()
    {
        _initialAnalysisComplete = true;
    }

    /// <summary>
    /// Opens a document.
    /// </summary>
    public void OpenDocument(string uri, string content, int version)
    {
        var filePath = UriToPath(uri);
        var doc = new GDOpenDocument(uri, filePath, content, version);
        _documents[uri] = doc;

        // Get the script and reload with new content
        var script = _project.GetScript(filePath);
        if (script != null)
        {
            script.Reload(content);
            InvalidateSemanticModel(script);
            return;
        }

        // A .gd file the project does not know yet (created after startup, missed by the watcher):
        // the client delivers it through didOpen, so add it here.
        if (filePath.EndsWith(".gd", StringComparison.OrdinalIgnoreCase) && IsUnderProject(filePath))
        {
            var added = _project.AddScript(filePath, content);
            _projectModel?.AttachScript(added);
            _projectModel?.InvalidateFile(filePath);
        }
    }

    private bool IsUnderProject(string filePath)
    {
        var comparison = OperatingSystem.IsWindows() || OperatingSystem.IsMacOS()
            ? StringComparison.OrdinalIgnoreCase
            : StringComparison.Ordinal;
        var root = Path.GetFullPath(_project.ProjectPath).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        return Path.GetFullPath(filePath).StartsWith(root, comparison);
    }

    /// <summary>
    /// Updates a document's content.
    /// </summary>
    public void UpdateDocument(string uri, string content, int version)
    {
        if (_documents.TryGetValue(uri, out var doc))
        {
            doc.Content = content;
            doc.Version = version;

            var filename = System.IO.Path.GetFileName(doc.FilePath);

            // Reload the script with new content
            var script = _project.GetScript(doc.FilePath);
            if (script != null)
            {
                GDLspPerformanceTrace.Log("reload", $"START {filename} v{version}");
                var sw = System.Diagnostics.Stopwatch.StartNew();
                script.Reload(content);
                sw.Stop();
                GDLspPerformanceTrace.Log("reload", $"END {filename} {sw.ElapsedMilliseconds}ms");
                InvalidateSemanticModel(script);
            }
        }
    }

    /// <summary>
    /// Applies a sequence of (possibly incremental) content changes to an open document, then reloads.
    /// A change with a null Range replaces the whole document; otherwise the Range is spliced.
    /// </summary>
    public void ApplyChanges(string uri, GDTextDocumentContentChangeEvent[] changes, int version)
    {
        if (!_documents.TryGetValue(uri, out var doc))
            return;

        var content = doc.Content;
        foreach (var change in changes)
        {
            if (change.Range == null)
            {
                content = change.Text ?? string.Empty;
            }
            else
            {
                var start = PositionToOffset(content, change.Range.Start.Line, change.Range.Start.Character);
                var end = PositionToOffset(content, change.Range.End.Line, change.Range.End.Character);
                if (end < start)
                    end = start;
                content = content.Substring(0, start) + (change.Text ?? string.Empty) + content.Substring(end);
            }
        }

        UpdateDocument(uri, content, version);
    }

    /// <summary>
    /// Converts an LSP 0-based (line, character) position to a character offset in the content.
    /// Handles LF and CRLF line endings; clamps gracefully for out-of-range positions.
    /// </summary>
    internal static int PositionToOffset(string content, int line, int character)
    {
        int i = 0;
        int currentLine = 0;
        while (i < content.Length && currentLine < line)
        {
            var c = content[i++];
            if (c == '\n')
            {
                currentLine++;
            }
            else if (c == '\r')
            {
                currentLine++;
                if (i < content.Length && content[i] == '\n')
                    i++;
            }
        }

        var col = 0;
        while (i < content.Length && col < character && content[i] != '\n' && content[i] != '\r')
        {
            i++;
            col++;
        }

        return i;
    }

    private void InvalidateSemanticModel(GDScriptFile script)
    {
        if (script.Class == null)
            return;

        var filePath = script.FullPath;
        if (string.IsNullOrEmpty(filePath))
            return;

        var filename = System.IO.Path.GetFileName(filePath);

        if (_projectModel != null)
        {
            GDLspPerformanceTrace.Log("invalidate", $"START {filename} hasModel={_projectModel != null}");
            var sw = System.Diagnostics.Stopwatch.StartNew();
            _projectModel.InvalidateFile(filePath);
            sw.Stop();
            GDLspPerformanceTrace.Log("invalidate", $"END {filename} {sw.ElapsedMilliseconds}ms");
        }
        else if (_initialAnalysisComplete)
        {
            // Fallback: rebuild per-file model directly
            GDLspPerformanceTrace.Log("invalidate", $"FALLBACK-ANALYZE {filename}");
            var sw = System.Diagnostics.Stopwatch.StartNew();
            try
            {
                script.Analyze(_project.CreateRuntimeProvider(), _project.CreateNodeTypeInjector());
            }
            catch (Exception ex)
            {
                GDLspPerformanceTrace.Log("invalidate", $"ERROR {filename}: {ex.Message}");
            }
            sw.Stop();
            GDLspPerformanceTrace.Log("invalidate", $"FALLBACK-DONE {filename} {sw.ElapsedMilliseconds}ms");
        }
    }

    /// <summary>
    /// Closes a document.
    /// </summary>
    public void CloseDocument(string uri)
    {
        if (_documents.TryRemove(uri, out var doc))
        {
            // Reload from disk
            var script = _project.GetScript(doc.FilePath);
            if (script != null)
            {
                script.Reload();
                InvalidateSemanticModel(script);
            }
        }
    }

    /// <summary>
    /// Gets an open document.
    /// </summary>
    public GDOpenDocument? GetDocument(string uri)
    {
        _documents.TryGetValue(uri, out var doc);
        return doc;
    }

    /// <summary>
    /// Gets all open documents.
    /// </summary>
    public IEnumerable<GDOpenDocument> GetAllDocuments()
    {
        return _documents.Values;
    }

    /// <summary>
    /// Converts a file URI to a local path.
    /// </summary>
    public static string UriToPath(string uri)
    {
        if (uri.StartsWith("file:///", StringComparison.OrdinalIgnoreCase))
        {
            var path = Uri.UnescapeDataString(uri.Substring(8));
            // Handle Windows paths (file:///C:/...)
            if (path.Length > 2 && path[1] == ':')
            {
                return path.Replace('/', '\\');
            }
            // Unix paths
            return "/" + path;
        }
        return uri;
    }

    /// <summary>
    /// Converts a local path to a file URI.
    /// </summary>
    public static string PathToUri(string path)
    {
        path = path.Replace('\\', '/');
        if (!path.StartsWith("/"))
        {
            // Windows path — encode using Uri class for correct percent-encoding
            return new Uri("file:///" + path).AbsoluteUri;
        }
        // Unix path
        return new Uri("file://" + path).AbsoluteUri;
    }
}

/// <summary>
/// Represents an open document.
/// </summary>
public class GDOpenDocument
{
    public string Uri { get; }
    public string FilePath { get; }
    public string Content { get; set; }
    public int Version { get; set; }

    public GDOpenDocument(string uri, string filePath, string content, int version)
    {
        Uri = uri;
        FilePath = filePath;
        Content = content;
        Version = version;
    }
}
