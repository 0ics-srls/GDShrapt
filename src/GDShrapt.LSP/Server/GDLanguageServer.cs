using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using GDShrapt.Abstractions;
using GDShrapt.CLI.Core;
using GDShrapt.Reader;
using GDShrapt.Semantics;

namespace GDShrapt.LSP;

/// <summary>
/// GDScript Language Server implementation.
/// </summary>
public class GDLanguageServer : IGDLanguageServer
{
    private GDScriptProject? _project;
    private GDServiceRegistry? _registry;
    private GDDocumentManager? _documentManager;
    private GDDiagnosticPublisher? _diagnosticPublisher;
    private GDProjectConfig? _config;
    private IGDJsonRpcTransport? _transport;
    private GDLspLogger? _logger;
    private GDLspTraceLevel _traceLevel = GDLspTraceLevel.Off;
    private GDLspSettings _settings = new();
    private string? _initializationError;
    private TaskCompletionSource? _shutdownTcs;
    private TaskCompletionSource<bool>? _analysisComplete;
    private bool _disposed;

    public bool IsInitialized { get; private set; }
    public bool IsShuttingDown { get; private set; }

    public async Task InitializeAsync(IGDJsonRpcTransport transport, CancellationToken cancellationToken)
    {
        _transport = transport;
        _shutdownTcs = new TaskCompletionSource();

        // Create logger and wire to transport
        _logger = new GDLspLogger(transport);
        if (transport is GDStdioJsonRpcTransport stdioTransport)
            stdioTransport.Logger = _logger;
        else if (transport is GDSocketJsonRpcTransport socketTransport)
            socketTransport.Logger = _logger;

        // Register handlers
        RegisterHandlers();

        // Start transport
        await transport.StartAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task RunAsync(CancellationToken cancellationToken)
    {
        if (_shutdownTcs == null)
            throw new InvalidOperationException("Server not initialized");

        using var registration = cancellationToken.Register(() => _shutdownTcs.TrySetCanceled());
        await _shutdownTcs.Task.ConfigureAwait(false);
    }

    private void RegisterHandlers()
    {
        if (_transport == null)
            return;

        // Lifecycle
        _transport.OnRequest<GDInitializeParams, GDInitializeResult>("initialize", HandleInitializeAsync);
        _transport.OnNotification<object>("initialized", HandleInitializedAsync);
        _transport.OnRequest<object, object>("shutdown", HandleShutdownAsync);
        _transport.OnNotification<object>("exit", HandleExitAsync);

        // Document sync
        _transport.OnNotification<GDDidOpenTextDocumentParams>("textDocument/didOpen", HandleDidOpenAsync);
        _transport.OnNotification<GDDidChangeTextDocumentParams>("textDocument/didChange", HandleDidChangeAsync);
        _transport.OnNotification<GDDidCloseTextDocumentParams>("textDocument/didClose", HandleDidCloseAsync);
        _transport.OnNotification<GDDidSaveTextDocumentParams>("textDocument/didSave", HandleDidSaveAsync);

        // Trace
        _transport.OnNotification<GDSetTraceParams>("$/setTrace", HandleSetTraceAsync);

        // Workspace
        _transport.OnNotification<GDDidChangeConfigurationParams>("workspace/didChangeConfiguration", HandleDidChangeConfigurationAsync);
        _transport.OnRequest<GDWorkspaceSymbolParams, GDLspSymbolInformation[]?>("workspace/symbol", HandleWorkspaceSymbolAsync);
        _transport.OnRequest<GDExecuteCommandParams, object?>("workspace/executeCommand", HandleExecuteCommandAsync);
        _transport.OnRequest<GDRenameFilesParams, GDWorkspaceEdit?>("workspace/willRenameFiles", HandleWillRenameFilesAsync);

        // Language features
        _transport.OnRequest<GDDefinitionParams, GDLspLocationLink[]?>("textDocument/definition", HandleDefinitionAsync);
        _transport.OnRequest<GDReferencesParams, GDLspLocation[]?>("textDocument/references", HandleReferencesAsync);
        _transport.OnRequest<GDHoverParams, GDLspHover?>("textDocument/hover", HandleHoverAsync);
        _transport.OnRequest<GDDocumentSymbolParams, GDLspDocumentSymbol[]?>("textDocument/documentSymbol", HandleDocumentSymbolAsync);
        _transport.OnRequest<GDCompletionParams, GDLspCompletionList?>("textDocument/completion", HandleCompletionAsync);
        _transport.OnRequest<GDRenameParams, GDWorkspaceEdit?>("textDocument/rename", HandleRenameAsync);
        _transport.OnRequest<GDPrepareRenameParams, GDPrepareRenameResult?>("textDocument/prepareRename", HandlePrepareRenameAsync);
        _transport.OnRequest<GDDocumentHighlightParams, GDDocumentHighlight[]?>("textDocument/documentHighlight", HandleDocumentHighlightAsync);
        _transport.OnRequest<GDFoldingRangeParams, GDFoldingRange[]?>("textDocument/foldingRange", HandleFoldingRangeAsync);
        _transport.OnRequest<GDSelectionRangeParams, GDSelectionRange[]?>("textDocument/selectionRange", HandleSelectionRangeAsync);
        _transport.OnRequest<GDDocumentLinkParams, GDDocumentLink[]?>("textDocument/documentLink", HandleDocumentLinkAsync);
        _transport.OnRequest<GDDocumentFormattingParams, GDLspTextEdit[]?>("textDocument/formatting", HandleFormattingAsync);
        _transport.OnRequest<GDDocumentRangeFormattingParams, GDLspTextEdit[]?>("textDocument/rangeFormatting", HandleRangeFormattingAsync);
        _transport.OnRequest<GDCodeActionParams, GDLspCodeAction[]?>("textDocument/codeAction", HandleCodeActionAsync);
        _transport.OnRequest<GDSignatureHelpParams, GDLspSignatureHelp?>("textDocument/signatureHelp", HandleSignatureHelpAsync);
        _transport.OnRequest<GDInlayHintParams, GDLspInlayHint[]?>("textDocument/inlayHint", HandleInlayHintAsync);
        _transport.OnRequest<GDCodeLensParams, GDLspCodeLens[]?>("textDocument/codeLens", HandleCodeLensAsync);
        _transport.OnRequest<GDSemanticTokensParams, GDSemanticTokens?>("textDocument/semanticTokens/full", HandleSemanticTokensFullAsync);

        // Call hierarchy
        _transport.OnRequest<GDCallHierarchyPrepareParams, GDLspCallHierarchyItem[]?>("textDocument/prepareCallHierarchy", HandlePrepareCallHierarchyAsync);
        _transport.OnRequest<GDCallHierarchyIncomingCallsParams, GDLspCallHierarchyIncomingCall[]?>("callHierarchy/incomingCalls", HandleIncomingCallsAsync);
        _transport.OnRequest<GDCallHierarchyOutgoingCallsParams, GDLspCallHierarchyOutgoingCall[]?>("callHierarchy/outgoingCalls", HandleOutgoingCallsAsync);

        // Type hierarchy
        _transport.OnRequest<GDDocumentDiagnosticParams, GDFullDocumentDiagnosticReport>("textDocument/diagnostic", HandleDocumentDiagnosticAsync);
        _transport.OnRequest<GDTypeHierarchyPrepareParams, GDLspTypeHierarchyItem[]?>("textDocument/prepareTypeHierarchy", (p, ct) => TypeHierarchy()?.PrepareAsync(p, ct) ?? Task.FromResult<GDLspTypeHierarchyItem[]?>(null));
        _transport.OnRequest<GDTypeHierarchyItemParams, GDLspTypeHierarchyItem[]?>("typeHierarchy/supertypes", (p, ct) => TypeHierarchy()?.SupertypesAsync(p, ct) ?? Task.FromResult<GDLspTypeHierarchyItem[]?>(null));
        _transport.OnRequest<GDTypeHierarchyItemParams, GDLspTypeHierarchyItem[]?>("typeHierarchy/subtypes", (p, ct) => TypeHierarchy()?.SubtypesAsync(p, ct) ?? Task.FromResult<GDLspTypeHierarchyItem[]?>(null));

        // Type definition and implementation
        _transport.OnRequest<GDDefinitionParams, GDLspLocationLink[]?>("textDocument/typeDefinition", HandleTypeDefinitionAsync);
        _transport.OnRequest<GDDefinitionParams, GDLspLocation[]?>("textDocument/implementation", HandleImplementationAsync);

        // Custom requests
        _transport.OnRequest<GDCodeLensReferencesParams, GDLspLocation[]?>("gdshrapt/codeLensReferences", HandleCodeLensReferencesAsync);
    }

    #region Lifecycle Handlers

    private Task<GDInitializeResult?> HandleInitializeAsync(GDInitializeParams @params, CancellationToken ct)
    {
        // Parse trace level
        _traceLevel = @params.Trace switch
        {
            "messages" => GDLspTraceLevel.Messages,
            "verbose" => GDLspTraceLevel.Verbose,
            _ => GDLspTraceLevel.Off
        };

        // Client-supplied tunables (debounce, operation timeout); defaults preserve behavior.
        _settings = GDLspSettings.FromInitializationOptions(@params.InitializationOptions);

        // Determine project root
        var rootPath = @params.RootUri != null
            ? GDDocumentManager.UriToPath(@params.RootUri)
            : @params.RootPath;

        _ = _logger?.InfoAsync($"[initialize] rootUri={@params.RootUri} rootPath={@params.RootPath} resolved={rootPath}");

        if (!string.IsNullOrEmpty(rootPath))
        {
            // Load the Godot type database (~0.8 s, serial) while the project's scripts are parsed; first users
            // of the shared provider wait for it (Lazy, ExecutionAndPublication). LSAI#48
            _ = Task.Run(() => GDGodotTypesProvider.Shared);

            GDScriptProject? project = null;
            try
            {
                var projectRoot = GDProjectLoader.FindProjectRoot(rootPath);
                _ = _logger?.InfoAsync($"[initialize] projectRoot={projectRoot}");
                if (projectRoot != null)
                {
                    // Load project WITHOUT analysis - analysis will run in background after initialized
                    project = GDProjectLoader.LoadProjectWithoutAnalysis(projectRoot);
                    _documentManager = new GDDocumentManager(project);
                    _config = GDConfigLoader.LoadConfig(project.ProjectPath);
                    _analysisComplete = new TaskCompletionSource<bool>();
                    _diagnosticPublisher = new GDDiagnosticPublisher(
                        _transport!, project,
                        debounceDelay: TimeSpan.FromMilliseconds(_settings.SyntaxDebounceMs),
                        config: _config,
                        analysisReady: _analysisComplete.Task,
                        semanticDebounceDelay: TimeSpan.FromMilliseconds(_settings.SemanticDebounceMs));
                    _project = project;

                    var scriptCount = 0;
                    foreach (var _ in project.ScriptFiles) scriptCount++;
                    _ = _logger?.InfoAsync($"[initialize] loaded {scriptCount} scripts, initializing registry...");

                    // Initialize service registry with deferred analysis (non-blocking).
                    // Handlers are created immediately but return null/empty until AnalyzeAll() runs in background.
                    // NOTE: Pro module is NOT loaded in LSP (by design - Strict mode only)
                    _registry = new GDServiceRegistry();
                    try
                    {
                        _registry.LoadModules(project, new GDBaseModule(deferAnalysis: true));
                        var projectModel = _registry.GetService<GDProjectSemanticModel>();
                        _documentManager.SetProjectModel(projectModel);
                        _diagnosticPublisher.SetProjectModel(projectModel);
                        _ = _logger?.InfoAsync($"[initialize] registry initialized (analysis deferred)");
                    }
                    catch (Exception moduleEx)
                    {
                        _ = _logger?.ErrorAsync($"[initialize] LoadModules failed: {moduleEx}");
                    }

                    project = null; // Successfully transferred ownership
                }
            }
            catch (Exception ex)
            {
                _initializationError = $"Failed to load project: {ex.Message}";
            }
            finally
            {
                // Dispose partially created project on error
                project?.Dispose();
            }
        }

        var result = new GDInitializeResult
        {
            Capabilities = new GDServerCapabilities
            {
                TextDocumentSync = new GDTextDocumentSyncOptions
                {
                    OpenClose = true,
                    Change = GDTextDocumentSyncKind.Incremental,
                    Save = new GDSaveOptions { IncludeText = false }
                },
                HoverProvider = true,
                DefinitionProvider = true,
                ReferencesProvider = true,
                DocumentSymbolProvider = true,
                RenameProvider = new GDRenameOptions { PrepareProvider = true },
                DocumentHighlightProvider = true,
                FoldingRangeProvider = true,
                SelectionRangeProvider = true,
                DocumentFormattingProvider = true,
                DocumentRangeFormattingProvider = true,
                DocumentLinkProvider = new GDDocumentLinkOptions { ResolveProvider = false },
                CompletionProvider = new GDCompletionOptions
                {
                    TriggerCharacters = [".", ":", "(", "$", "/"],
                    ResolveProvider = false
                },
                CodeActionProvider = true,
                SignatureHelpProvider = new GDSignatureHelpOptions
                {
                    TriggerCharacters = ["(", ","],
                    RetriggerCharacters = [","]
                },
                InlayHintProvider = new GDInlayHintOptions
                {
                    ResolveProvider = false
                },
                CodeLensProvider = new GDCodeLensOptions
                {
                    ResolveProvider = false
                },
                SemanticTokensProvider = new GDSemanticTokensOptions
                {
                    Legend = new GDSemanticTokensLegend
                    {
                        TokenTypes = GDLspSemanticTokensHandler.TokenTypes,
                        TokenModifiers = GDLspSemanticTokensHandler.TokenModifiers
                    },
                    Full = true
                },
                CallHierarchyProvider = true,
                TypeHierarchyProvider = true,
                DiagnosticProvider = new GDDiagnosticOptions(),
                TypeDefinitionProvider = true,
                ImplementationProvider = true,
                WorkspaceSymbolProvider = true,
                ExecuteCommandProvider = new GDExecuteCommandOptions
                {
                    Commands = ["gdshrapt.serverStatus"]
                },
                Workspace = new GDWorkspaceServerCapabilities
                {
                    FileOperations = new GDFileOperationsServerCapabilities
                    {
                        WillRename = new GDFileOperationRegistrationOptions
                        {
                            Filters =
                            {
                                new GDFileOperationFilter
                                {
                                    Pattern = new GDFileOperationPattern { Glob = "**/*.{gd,tscn,tres,png,svg,gdshader}" }
                                }
                            }
                        }
                    }
                }
            },
            ServerInfo = new GDServerInfo
            {
                Name = "GDShrapt LSP",
                Version = GDLspVersionInfo.GetVersion()
            }
        };

        return Task.FromResult<GDInitializeResult?>(result);
    }

    private Task HandleInitializedAsync(object? @params)
    {
        IsInitialized = true;

        // Send deferred initialization error if project loading failed
        if (_initializationError != null)
        {
            _ = ShowCriticalErrorAsync(_initializationError);
        }

        // Subscribe to scene-triggered script reanalysis
        if (_project != null)
        {
            _project.SceneScriptsChanged += OnSceneScriptsChanged;
        }

        // Start background analysis with progress reporting
        if (_project != null)
        {
            _ = Task.Run(async () =>
            {
                // Create progress token for VS Code progress bar
                var progressToken = "gdshrapt-analysis";
                var progressSupported = false;
                try
                {
                    if (_transport != null)
                    {
                        using var progressCts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
                        await _transport.SendRequestAsync<GDWorkDoneProgressCreateParams, object?>(
                            "window/workDoneProgress/create",
                            new GDWorkDoneProgressCreateParams { Token = progressToken },
                            progressCts.Token).ConfigureAwait(false);
                        progressSupported = true;
                    }
                }
                catch
                {
                    // Client may not support work done progress — continue without it
                }

                if (progressSupported)
                    await SendProgressAsync(progressToken, "begin", title: "GDShrapt: Analyzing project...", percentage: 0).ConfigureAwait(false);

                try
                {
                    // Phase 1: Per-file semantic analysis
                    _ = _logger?.InfoAsync("[analysis] Starting AnalyzeAll...");
                    _project.AnalyzeAll();
                    _ = _logger?.InfoAsync("[analysis] AnalyzeAll complete");

                    if (progressSupported)
                        await SendProgressAsync(progressToken, "report", message: "Building cross-file index...", percentage: 60).ConfigureAwait(false);

                    // Phase 1 complete — hover, completion, diagnostics, go-to-def now fully functional
                    _documentManager?.SetInitialAnalysisComplete();
                    // Signal analysis ready BEFORE publishing — PublishDiagnosticsAsync awaits this task
                    _analysisComplete?.TrySetResult(true);

                    // Publish diagnostics for open documents
                    if (_diagnosticPublisher != null && _documentManager != null)
                        await _diagnosticPublisher.PublishAllAsync(_documentManager).ConfigureAwait(false);

                    // Phase 2: Cross-file enrichment (references, rename, CodeLens)
                    _ = _logger?.InfoAsync("[analysis] Starting cross-file enrichment...");
                    _project.BuildCallSiteRegistry();
                    _project.EnrichWithCallSiteAnalysis();
                    _project.ResolveTresClassNames();
                    GDProjectInitializer.InjectSceneSignalConnections(_project);

                    if (progressSupported)
                        await SendProgressAsync(progressToken, "report", message: "Finalizing...", percentage: 95).ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    _ = _logger?.ErrorAsync($"[analysis] Background analysis failed: {ex.Message}\n{ex.StackTrace}");
                    _ = ShowCriticalErrorAsync($"Background analysis failed: {ex.Message}");
                }
                finally
                {
                    _analysisComplete?.TrySetResult(true);
                    _ = _logger?.InfoAsync("[analysis] Analysis pipeline complete");

                    if (progressSupported)
                        await SendProgressAsync(progressToken, "end", message: "Analysis complete").ConfigureAwait(false);
                }

                // Refresh CodeLens after analysis completes so reference counts are accurate
                try
                {
                    if (_transport != null)
                        await _transport.SendRequestAsync<object?, object?>("workspace/codeLens/refresh", null, CancellationToken.None).ConfigureAwait(false);
                }
                catch
                {
                    // Client may not support codeLens/refresh — ignore
                }
            });
        }

        return Task.CompletedTask;
    }

    private void OnSceneScriptsChanged(object? sender, GDSceneAffectedScriptsEventArgs e)
    {
        if (_diagnosticPublisher == null)
            return;

        _ = Task.Run(async () =>
        {
            foreach (var script in e.AffectedScripts)
            {
                if (script.FullPath == null)
                    continue;

                var uri = GDDocumentManager.PathToUri(script.FullPath);
                await _diagnosticPublisher.PublishDiagnosticsAsync(uri).ConfigureAwait(false);
            }
        });
    }

    private Task<object?> HandleShutdownAsync(object? @params, CancellationToken ct)
    {
        IsShuttingDown = true;
        return Task.FromResult<object?>(null);
    }

    private Task HandleExitAsync(object? @params)
    {
        _shutdownTcs?.TrySetResult();
        return Task.CompletedTask;
    }

    #endregion

    #region Document Sync Handlers

    /// <summary>
    /// Pull diagnostics: the full analysis (syntax + validator + linter + semantics) of the document's
    /// current text. didOpen/didChange are applied synchronously on the read loop, so a pull always sees
    /// every change sent before it.
    /// </summary>
    private async Task<GDFullDocumentDiagnosticReport> HandleDocumentDiagnosticAsync(
        GDDocumentDiagnosticParams @params, CancellationToken cancellationToken)
    {
        var items = _diagnosticPublisher is null
            ? []
            : await _diagnosticPublisher.ComputeDiagnosticsAsync(@params.TextDocument.Uri).ConfigureAwait(false);
        return new GDFullDocumentDiagnosticReport { Items = items };
    }

    private Task HandleDidOpenAsync(GDDidOpenTextDocumentParams @params)
    {
        _documentManager?.OpenDocument(
            @params.TextDocument.Uri,
            @params.TextDocument.Text,
            @params.TextDocument.Version);

        // Schedule diagnostics update
        _diagnosticPublisher?.ScheduleUpdate(@params.TextDocument.Uri, @params.TextDocument.Version);

        return Task.CompletedTask;
    }

    private Task HandleDidChangeAsync(GDDidChangeTextDocumentParams @params)
    {
        var filename = Path.GetFileName(GDDocumentManager.UriToPath(@params.TextDocument.Uri));
        var sw = System.Diagnostics.Stopwatch.StartNew();

        GDLspPerformanceTrace.Log("didChange", $"START {filename} v{@params.TextDocument.Version}");

        if (@params.ContentChanges.Length > 0)
        {
            // Incremental sync: splice each {range, text} change; a null range replaces the whole doc.
            _documentManager?.ApplyChanges(
                @params.TextDocument.Uri,
                @params.ContentChanges,
                @params.TextDocument.Version);
        }

        var updateMs = sw.ElapsedMilliseconds;

        // Schedule diagnostics update with debouncing
        _diagnosticPublisher?.ScheduleUpdate(@params.TextDocument.Uri, @params.TextDocument.Version);

        GDLspPerformanceTrace.Log("didChange", $"END {filename} update={updateMs}ms total={sw.ElapsedMilliseconds}ms");

        return Task.CompletedTask;
    }

    private async Task HandleDidCloseAsync(GDDidCloseTextDocumentParams @params)
    {
        _documentManager?.CloseDocument(@params.TextDocument.Uri);

        // Clear diagnostics for closed document
        if (_diagnosticPublisher != null)
        {
            await _diagnosticPublisher.ClearDiagnosticsAsync(@params.TextDocument.Uri).ConfigureAwait(false);
        }
    }

    private Task HandleDidSaveAsync(GDDidSaveTextDocumentParams @params)
    {
        _diagnosticPublisher?.ScheduleUpdate(@params.TextDocument.Uri);
        return Task.CompletedTask;
    }

    #endregion

    #region Workspace Handlers

    private async Task HandleDidChangeConfigurationAsync(GDDidChangeConfigurationParams @params)
    {
        // Reload configuration from .gdshrapt.json and refresh diagnostics
        if (_project != null)
        {
            _config = GDConfigLoader.LoadConfig(_project.ProjectPath);
            _diagnosticPublisher?.UpdateConfig(_config);
        }

        if (_diagnosticPublisher != null && _documentManager != null)
        {
            await _diagnosticPublisher.PublishAllAsync(_documentManager).ConfigureAwait(false);
        }
    }

    private GDTypeHierarchyHandler? TypeHierarchy() =>
        _project == null ? null
            : new GDTypeHierarchyHandler(_project, _registry?.GetService<GDProjectSemanticModel>()?.RuntimeProvider);

    private Task<GDLspSymbolInformation[]?> HandleWorkspaceSymbolAsync(GDWorkspaceSymbolParams @params, CancellationToken ct)
    {
        if (_project == null)
            return Task.FromResult<GDLspSymbolInformation[]?>(null);

        var handler = new GDWorkspaceSymbolHandler(_project);
        return handler.HandleAsync(@params, ct);
    }

    private Task HandleSetTraceAsync(GDSetTraceParams @params)
    {
        _traceLevel = @params.Value switch
        {
            "messages" => GDLspTraceLevel.Messages,
            "verbose" => GDLspTraceLevel.Verbose,
            _ => GDLspTraceLevel.Off
        };
        return Task.CompletedTask;
    }

    private Task<object?> HandleExecuteCommandAsync(GDExecuteCommandParams @params, CancellationToken ct)
    {
        return @params.Command switch
        {
            "gdshrapt.serverStatus" => Task.FromResult<object?>(GetServerStatus()),
            _ => Task.FromResult<object?>(null)
        };
    }

    private object GetServerStatus()
    {
        return new
        {
            version = GDLspVersionInfo.GetVersion(),
            platform = RuntimeInformation.OSDescription,
            projectPath = _project?.ProjectPath,
            initialized = IsInitialized
        };
    }

    #endregion

    #region Language Feature Handlers

    private async Task<GDLspLocationLink[]?> HandleDefinitionAsync(GDDefinitionParams @params, CancellationToken ct)
    {
        _logger?.Debug($"[Definition] START uri={@params.TextDocument.Uri} L{@params.Position.Line}:{@params.Position.Character}");

        if (_registry == null)
            return null;

        var coreHandler = _registry.GetService<IGDGoToDefHandler>();
        if (coreHandler == null)
            return null;

        var handler = new GDDefinitionHandler(coreHandler);
        var (links, infoMessage) = await handler.HandleAsync(@params, ct);

        _logger?.Debug($"[Definition] END found={links?.Length ?? 0}");

        if (infoMessage != null && _transport != null)
        {
            await _transport.SendNotificationAsync("window/showMessage", new GDShowMessageParams
            {
                Type = GDLspMessageType.Info,
                Message = infoMessage
            });
        }

        return links;
    }

    private Task<GDLspLocation[]?> HandleReferencesAsync(GDReferencesParams @params, CancellationToken ct)
    {
        _logger?.Debug($"[References] START uri={@params.TextDocument.Uri} L{@params.Position.Line}:{@params.Position.Character}");

        if (_registry == null)
            return Task.FromResult<GDLspLocation[]?>(null);

        var findRefsHandler = _registry.GetService<IGDFindRefsHandler>();
        var goToDefHandler = _registry.GetService<IGDGoToDefHandler>();
        if (findRefsHandler == null || goToDefHandler == null)
            return Task.FromResult<GDLspLocation[]?>(null);

        var handler = new GDReferencesHandler(findRefsHandler, goToDefHandler);
        return handler.HandleAsync(@params, ct);
    }

    private async Task<GDLspHover?> HandleHoverAsync(GDHoverParams @params, CancellationToken ct)
    {
        var filename = Path.GetFileName(GDDocumentManager.UriToPath(@params.TextDocument.Uri));
        _logger?.Debug($"[Hover] START {filename} L{@params.Position.Line}:{@params.Position.Character}");

        if (_registry == null)
            return null;

        var coreHandler = _registry.GetService<IGDHoverHandler>();
        if (coreHandler == null)
            return null;

        var sw = System.Diagnostics.Stopwatch.StartNew();
        var handler = new GDLspHoverHandler(coreHandler);
        var result = await handler.HandleAsync(@params, ct).ConfigureAwait(false);
        sw.Stop();

        _logger?.Debug($"[Hover] END {filename} {sw.ElapsedMilliseconds}ms hasResult={result != null}");

        // When analysis is still in progress and handler returned null, show a loading indicator
        // But first try keyword/operator hover — these don't need semantic analysis
        if (result == null && _documentManager != null && !_documentManager.IsInitialAnalysisComplete)
        {
            var filePath = GDDocumentManager.UriToPath(@params.TextDocument.Uri);
            var script = _project?.GetScript(filePath);
            if (script?.Class != null)
            {
                if (script.Class.TryGetTokenByPosition(@params.Position.Line, @params.Position.Character, out var token) && token != null)
                {
                    // Try keyword/operator hover — doesn't require semantic analysis
                    var keywordHover = GDKeywordHoverProvider.GetKeywordHover(token);
                    if (keywordHover != null)
                    {
                        return new GDLspHover
                        {
                            Contents = GDLspMarkupContent.Markdown(keywordHover.Content)
                        };
                    }

                    // For literals and comments, don't show "Analysis in progress" — they never have semantic hover
                    if (token is GDLiteralToken or GDComment)
                        return null;

                    return new GDLspHover
                    {
                        Contents = GDLspMarkupContent.Markdown($"`{token}`\n\n*\u23f3 Analysis in progress...*")
                    };
                }
            }

            return new GDLspHover
            {
                Contents = GDLspMarkupContent.Markdown("*\u23f3 Analysis in progress...*")
            };
        }

        return result;
    }

    private Task<GDLspDocumentSymbol[]?> HandleDocumentSymbolAsync(GDDocumentSymbolParams @params, CancellationToken ct)
    {
        var filePath = GDDocumentManager.UriToPath(@params.TextDocument.Uri);
        _ = _logger?.InfoAsync($"[documentSymbol] uri={@params.TextDocument.Uri} path={filePath} registry={_registry != null} project={_project != null}");

        if (_project != null)
        {
            var script = _project.GetScript(filePath);
            _ = _logger?.InfoAsync($"[documentSymbol] script={script != null} semanticModel={script?.SemanticModel != null} class={script?.Class != null}");
            if (script == null)
            {
                // Log available scripts for debugging
                var count = 0;
                foreach (var s in _project.ScriptFiles)
                {
                    if (count < 5)
                        _ = _logger?.InfoAsync($"[documentSymbol] available script: {s.FullPath}");
                    count++;
                }
                _ = _logger?.InfoAsync($"[documentSymbol] total scripts: {count}");
            }
        }

        if (_registry == null)
            return Task.FromResult<GDLspDocumentSymbol[]?>(null);

        var coreHandler = _registry.GetService<IGDSymbolsHandler>();
        if (coreHandler == null)
            return Task.FromResult<GDLspDocumentSymbol[]?>(null);

        var handler = new GDDocumentSymbolHandler(coreHandler);
        return handler.HandleAsync(@params, ct);
    }

    private Task<GDLspCompletionList?> HandleCompletionAsync(GDCompletionParams @params, CancellationToken ct)
    {
        _logger?.Debug($"[Completion] START uri={@params.TextDocument.Uri} L{@params.Position.Line}:{@params.Position.Character} trigger={@params.Context?.TriggerCharacter}");

        if (_registry == null)
            return Task.FromResult<GDLspCompletionList?>(null);

        var coreHandler = _registry.GetService<IGDCompletionHandler>();
        if (coreHandler == null)
            return Task.FromResult<GDLspCompletionList?>(null);

        var handler = new GDLspCompletionHandler(coreHandler, _documentManager);
        return GDLspOperation.RunAsync(token => handler.HandleAsync(@params, token), _settings.OperationTimeoutMs, ct);
    }

    private Task<GDWorkspaceEdit?> HandleRenameAsync(GDRenameParams @params, CancellationToken ct)
    {
        _logger?.Debug($"[Rename] START uri={@params.TextDocument.Uri} newName={@params.NewName} L{@params.Position.Line}:{@params.Position.Character}");

        if (_registry == null)
            return Task.FromResult<GDWorkspaceEdit?>(null);

        var renameHandler = _registry.GetService<IGDRenameHandler>();
        var goToDefHandler = _registry.GetService<IGDGoToDefHandler>();
        if (renameHandler == null || goToDefHandler == null)
            return Task.FromResult<GDWorkspaceEdit?>(null);

        var handler = new GDLspRenameHandler(renameHandler, goToDefHandler);
        return handler.HandleAsync(@params, ct);
    }

    private Task<GDPrepareRenameResult?> HandlePrepareRenameAsync(GDPrepareRenameParams @params, CancellationToken ct)
    {
        _logger?.Debug($"[PrepareRename] START uri={@params.TextDocument.Uri} L{@params.Position.Line}:{@params.Position.Character}");

        if (_registry == null)
            return Task.FromResult<GDPrepareRenameResult?>(null);

        var renameHandler = _registry.GetService<IGDRenameHandler>();
        if (renameHandler == null)
            return Task.FromResult<GDPrepareRenameResult?>(null);

        var handler = new GDLspPrepareRenameHandler(renameHandler);
        return handler.HandleAsync(@params, ct);
    }

    private Task<GDDocumentHighlight[]?> HandleDocumentHighlightAsync(GDDocumentHighlightParams @params, CancellationToken ct)
    {
        _logger?.Debug($"[Highlight] START uri={@params.TextDocument.Uri} L{@params.Position.Line}:{@params.Position.Character}");

        if (_registry == null)
            return Task.FromResult<GDDocumentHighlight[]?>(null);

        var highlightHandler = _registry.GetService<IGDHighlightHandler>();
        var goToDefHandler = _registry.GetService<IGDGoToDefHandler>();
        if (highlightHandler == null || goToDefHandler == null)
            return Task.FromResult<GDDocumentHighlight[]?>(null);

        var handler = new GDLspDocumentHighlightHandler(highlightHandler, goToDefHandler);
        return handler.HandleAsync(@params, ct);
    }

    private Task<GDFoldingRange[]?> HandleFoldingRangeAsync(GDFoldingRangeParams @params, CancellationToken ct)
    {
        _logger?.Debug($"[FoldingRange] START uri={@params.TextDocument.Uri}");

        if (_registry == null)
            return Task.FromResult<GDFoldingRange[]?>(null);

        var coreHandler = _registry.GetService<IGDFoldingRangeHandler>();
        if (coreHandler == null)
            return Task.FromResult<GDFoldingRange[]?>(null);

        var handler = new GDLspFoldingRangeHandler(coreHandler);
        return handler.HandleAsync(@params, ct);
    }

    private Task<GDSelectionRange[]?> HandleSelectionRangeAsync(GDSelectionRangeParams @params, CancellationToken ct)
    {
        if (_registry == null)
            return Task.FromResult<GDSelectionRange[]?>(null);

        var coreHandler = _registry.GetService<IGDSelectionRangeHandler>();
        if (coreHandler == null)
            return Task.FromResult<GDSelectionRange[]?>(null);

        var handler = new GDLspSelectionRangeHandler(coreHandler);
        return handler.HandleAsync(@params, ct);
    }

    private Task<GDDocumentLink[]?> HandleDocumentLinkAsync(GDDocumentLinkParams @params, CancellationToken ct)
    {
        if (_registry == null)
            return Task.FromResult<GDDocumentLink[]?>(null);

        var coreHandler = _registry.GetService<IGDDocumentLinkHandler>();
        if (coreHandler == null)
            return Task.FromResult<GDDocumentLink[]?>(null);

        var handler = new GDLspDocumentLinkHandler(coreHandler);
        return handler.HandleAsync(@params, ct);
    }

    private Task<GDWorkspaceEdit?> HandleWillRenameFilesAsync(GDRenameFilesParams @params, CancellationToken ct)
    {
        if (_registry == null)
            return Task.FromResult<GDWorkspaceEdit?>(null);

        var coreHandler = _registry.GetService<IGDFileRenameHandler>();
        if (coreHandler == null)
            return Task.FromResult<GDWorkspaceEdit?>(null);

        var handler = new GDLspFileRenameHandler(coreHandler);
        return handler.HandleAsync(@params, ct);
    }

    private Task<GDLspTextEdit[]?> HandleFormattingAsync(GDDocumentFormattingParams @params, CancellationToken ct)
    {
        _logger?.Debug($"[Formatting] START uri={@params.TextDocument.Uri}");

        if (_registry == null)
            return Task.FromResult<GDLspTextEdit[]?>(null);

        var coreHandler = _registry.GetService<IGDFormatHandler>();
        if (coreHandler == null)
            return Task.FromResult<GDLspTextEdit[]?>(null);

        var handler = new GDFormattingHandler(coreHandler, _config);
        return handler.HandleAsync(@params, ct);
    }

    private Task<GDLspTextEdit[]?> HandleRangeFormattingAsync(GDDocumentRangeFormattingParams @params, CancellationToken ct)
    {
        if (_registry == null)
            return Task.FromResult<GDLspTextEdit[]?>(null);

        var coreHandler = _registry.GetService<IGDFormatHandler>();
        if (coreHandler == null)
            return Task.FromResult<GDLspTextEdit[]?>(null);

        var handler = new GDRangeFormattingHandler(coreHandler, _config);
        return handler.HandleAsync(@params, ct);
    }

    private Task<GDLspCodeAction[]?> HandleCodeActionAsync(GDCodeActionParams @params, CancellationToken ct)
    {
        _logger?.Debug($"[CodeAction] START uri={@params.TextDocument.Uri}");

        if (_registry == null)
            return Task.FromResult<GDLspCodeAction[]?>(null);

        var coreHandler = _registry.GetService<IGDCodeActionHandler>();
        if (coreHandler == null)
            return Task.FromResult<GDLspCodeAction[]?>(null);

        var handler = new GDLspCodeActionHandler(coreHandler);
        return handler.HandleAsync(@params, ct);
    }

    private Task<GDLspSignatureHelp?> HandleSignatureHelpAsync(GDSignatureHelpParams @params, CancellationToken ct)
    {
        _logger?.Debug($"[SignatureHelp] START uri={@params.TextDocument.Uri} L{@params.Position.Line}:{@params.Position.Character}");

        if (_registry == null)
            return Task.FromResult<GDLspSignatureHelp?>(null);

        var coreHandler = _registry.GetService<IGDSignatureHelpHandler>();
        if (coreHandler == null)
            return Task.FromResult<GDLspSignatureHelp?>(null);

        var handler = new GDLspSignatureHelpHandler(coreHandler);
        return handler.HandleAsync(@params, ct);
    }

    private async Task<GDLspInlayHint[]?> HandleInlayHintAsync(GDInlayHintParams @params, CancellationToken ct)
    {
        var filename = Path.GetFileName(GDDocumentManager.UriToPath(@params.TextDocument.Uri));
        _logger?.Debug($"[InlayHint] START {filename}");

        if (_registry == null)
            return null;

        var coreHandler = _registry.GetService<IGDInlayHintHandler>();
        if (coreHandler == null)
            return null;

        var sw = System.Diagnostics.Stopwatch.StartNew();
        var handler = new GDLspInlayHintHandler(coreHandler);
        var result = await handler.HandleAsync(@params, ct).ConfigureAwait(false);
        sw.Stop();

        _logger?.Debug($"[InlayHint] END {filename} {sw.ElapsedMilliseconds}ms");

        return result;
    }

    private async Task<GDLspCodeLens[]?> HandleCodeLensAsync(GDCodeLensParams @params, CancellationToken ct)
    {
        var filename = Path.GetFileName(GDDocumentManager.UriToPath(@params.TextDocument.Uri));
        _logger?.Debug($"[CodeLens] START {filename}");

        // Return null before analysis completes — CodeLens will refresh after analysis via workspace/codeLens/refresh
        if (_analysisComplete != null && !_analysisComplete.Task.IsCompleted)
        {
            _logger?.Debug($"[CodeLens] SKIP {filename} (analysis not complete)");
            return null;
        }

        if (_registry == null)
            return null;

        var coreHandler = _registry.GetService<IGDCodeLensHandler>();
        if (coreHandler == null)
            return null;

        var sw = System.Diagnostics.Stopwatch.StartNew();
        var handler = new GDLspCodeLensHandler(coreHandler);
        var result = await GDLspOperation.RunAsync(token => handler.HandleAsync(@params, token), _settings.OperationTimeoutMs, ct).ConfigureAwait(false);
        sw.Stop();

        _logger?.Debug($"[CodeLens] END {filename} {sw.ElapsedMilliseconds}ms count={result?.Length ?? 0}");

        return result;
    }

    private Task<GDLspLocation[]?> HandleCodeLensReferencesAsync(GDCodeLensReferencesParams @params, CancellationToken ct)
    {
        _logger?.Debug($"[CodeLensRefs] START symbol={@params.SymbolName}");

        if (_registry == null)
            return Task.FromResult<GDLspLocation[]?>(null);

        var codeLensHandler = _registry.GetService<IGDCodeLensHandler>();
        var findRefsHandler = _registry.GetService<IGDFindRefsHandler>();
        if (codeLensHandler == null || findRefsHandler == null)
            return Task.FromResult<GDLspLocation[]?>(null);

        var handler = new GDCodeLensReferencesHandler(codeLensHandler, findRefsHandler);
        return handler.HandleAsync(@params, ct);
    }

    private async Task<GDSemanticTokens?> HandleSemanticTokensFullAsync(GDSemanticTokensParams @params, CancellationToken ct)
    {
        var filename = Path.GetFileName(GDDocumentManager.UriToPath(@params.TextDocument.Uri));
        _logger?.Debug($"[SemanticTokens] START {filename}");

        if (_registry == null)
            return null;

        var tokensHandler = _registry.GetService<IGDSemanticTokensHandler>();
        if (tokensHandler == null)
            return null;

        var sw = System.Diagnostics.Stopwatch.StartNew();
        var handler = new GDLspSemanticTokensHandler(tokensHandler);
        var result = await GDLspOperation.RunAsync(token => handler.HandleAsync(@params, token), _settings.OperationTimeoutMs, ct).ConfigureAwait(false);
        sw.Stop();

        _logger?.Debug($"[SemanticTokens] END {filename} {sw.ElapsedMilliseconds}ms");

        return result;
    }

    private async Task<GDLspLocationLink[]?> HandleTypeDefinitionAsync(GDDefinitionParams @params, CancellationToken ct)
    {
        _logger?.Debug($"[TypeDefinition] START uri={@params.TextDocument.Uri} L{@params.Position.Line}:{@params.Position.Character}");

        if (_registry == null)
            return null;

        var coreHandler = _registry.GetService<IGDTypeDefinitionHandler>();
        if (coreHandler == null)
            return null;

        var handler = new GDTypeDefinitionLspHandler(coreHandler);
        var (links, infoMessage) = await handler.HandleAsync(@params, ct);

        if (infoMessage != null)
            _ = _transport?.SendNotificationAsync("window/showMessage", new GDShowMessageParams
            {
                Type = GDLspMessageType.Info,
                Message = infoMessage
            });

        return links;
    }

    private Task<GDLspLocation[]?> HandleImplementationAsync(GDDefinitionParams @params, CancellationToken ct)
    {
        _logger?.Debug($"[Implementation] START uri={@params.TextDocument.Uri} L{@params.Position.Line}:{@params.Position.Character}");

        if (_registry == null)
            return Task.FromResult<GDLspLocation[]?>(null);

        var coreHandler = _registry.GetService<IGDImplementationHandler>();
        if (coreHandler == null)
            return Task.FromResult<GDLspLocation[]?>(null);

        var handler = new GDImplementationLspHandler(coreHandler);
        return handler.HandleAsync(@params, ct);
    }

    private Task<GDLspCallHierarchyItem[]?> HandlePrepareCallHierarchyAsync(GDCallHierarchyPrepareParams @params, CancellationToken ct)
    {
        _logger?.Debug($"[CallHierarchy] PREPARE uri={@params.TextDocument.Uri} L{@params.Position.Line}:{@params.Position.Character}");

        if (_registry == null)
            return Task.FromResult<GDLspCallHierarchyItem[]?>(null);

        var coreHandler = _registry.GetService<IGDCallHierarchyHandler>();
        if (coreHandler == null)
            return Task.FromResult<GDLspCallHierarchyItem[]?>(null);

        var handler = new GDLspCallHierarchyHandler(coreHandler);
        return handler.HandlePrepareAsync(@params, ct);
    }

    private Task<GDLspCallHierarchyIncomingCall[]?> HandleIncomingCallsAsync(GDCallHierarchyIncomingCallsParams @params, CancellationToken ct)
    {
        _logger?.Debug("[CallHierarchy] INCOMING");

        if (_registry == null)
            return Task.FromResult<GDLspCallHierarchyIncomingCall[]?>(null);

        var coreHandler = _registry.GetService<IGDCallHierarchyHandler>();
        if (coreHandler == null)
            return Task.FromResult<GDLspCallHierarchyIncomingCall[]?>(null);

        var handler = new GDLspCallHierarchyHandler(coreHandler);
        return handler.HandleIncomingCallsAsync(@params, ct);
    }

    private Task<GDLspCallHierarchyOutgoingCall[]?> HandleOutgoingCallsAsync(GDCallHierarchyOutgoingCallsParams @params, CancellationToken ct)
    {
        _logger?.Debug("[CallHierarchy] OUTGOING");

        if (_registry == null)
            return Task.FromResult<GDLspCallHierarchyOutgoingCall[]?>(null);

        var coreHandler = _registry.GetService<IGDCallHierarchyHandler>();
        if (coreHandler == null)
            return Task.FromResult<GDLspCallHierarchyOutgoingCall[]?>(null);

        var handler = new GDLspCallHierarchyHandler(coreHandler);
        return handler.HandleOutgoingCallsAsync(@params, ct);
    }

    #endregion

    #region Trace and Notifications

    private async Task SendProgressAsync(string token, string kind, string? title = null, string? message = null, int? percentage = null)
    {
        if (_transport == null) return;
        try
        {
            var value = new GDWorkDoneProgressValue { Kind = kind };
            if (title != null) value.Title = title;
            if (message != null) value.Message = message;
            if (percentage.HasValue) value.Percentage = percentage.Value;

            await _transport.SendNotificationAsync("$/progress", new GDProgressParams
            {
                Token = token,
                Value = value
            }).ConfigureAwait(false);
        }
        catch
        {
            // Best-effort: client may not support progress
        }
    }

    private async Task ShowCriticalErrorAsync(string message)
    {
        if (_transport == null)
            return;

        try
        {
            await _transport.SendNotificationAsync("window/showMessage", new GDShowMessageParams
            {
                Type = GDLspMessageType.Error,
                Message = message
            }).ConfigureAwait(false);
        }
        catch
        {
            // Best-effort: if transport is broken, nothing we can do
        }
    }

    /// <inheritdoc />
    public Task TryShowErrorAsync(string message) => ShowCriticalErrorAsync(message);

    #endregion

    public async ValueTask DisposeAsync()
    {
        if (_disposed)
            return;

        _disposed = true;

        if (_project != null)
        {
            _project.SceneScriptsChanged -= OnSceneScriptsChanged;
        }

        if (_diagnosticPublisher != null)
        {
            await _diagnosticPublisher.DisposeAsync().ConfigureAwait(false);
        }

        if (_transport != null)
        {
            await _transport.DisposeAsync().ConfigureAwait(false);
        }

        _project?.Dispose();
    }
}

/// <summary>
/// Helper for loading GDScript projects.
/// </summary>
internal static class GDProjectLoader
{
    public static string? FindProjectRoot(string startPath)
    {
        var dir = System.IO.Directory.Exists(startPath)
            ? startPath
            : System.IO.Path.GetDirectoryName(startPath);

        while (!string.IsNullOrEmpty(dir))
        {
            var projectFile = System.IO.Path.Combine(dir, "project.godot");
            if (System.IO.File.Exists(projectFile))
                return dir;

            dir = System.IO.Path.GetDirectoryName(dir);
        }

        return null;
    }

    /// <summary>
    /// Loads project without analysis (fast for initialization).
    /// Call AnalyzeAllAsync() separately for background analysis.
    /// </summary>
    public static GDScriptProject LoadProjectWithoutAnalysis(string projectRoot)
    {
        var context = new GDDefaultProjectContext(projectRoot);
        var project = new GDScriptProject(context, new GDScriptProjectOptions
        {
            EnableSceneTypesProvider = true,
            EnableFileWatcher = true,
            EnableSceneChangeReanalysis = true,
            EnableCallSiteRegistry = true
        });

        project.LoadScripts();
        project.LoadScenes();
        // NOTE: AnalyzeAll() is NOT called here - do it asynchronously after initialized

        // Enable scene file watcher for real-time scene change detection
        project.SceneTypesProvider?.EnableFileWatcher();

        return project;
    }

    /// <summary>
    /// Loads project with full analysis (blocking, for CLI use).
    /// </summary>
    public static GDScriptProject LoadProject(string projectRoot)
    {
        var project = LoadProjectWithoutAnalysis(projectRoot);
        project.AnalyzeAll();
        return project;
    }
}
