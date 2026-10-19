# GDShrapt.LSP

Language Server Protocol 3.17 implementation.

## Architecture

LSP handlers are **thin wrappers** over CLI.Core handlers (Rule 8).
They convert LSP protocol (0-based positions) to CLI.Core (1-based positions).

## Handler Mapping

| LSP Handler | CLI.Core Handler | LSP Method |
|-------------|------------------|------------|
| `GDDefinitionHandler` | `IGDGoToDefHandler` | textDocument/definition |
| `GDReferencesHandler` | `IGDFindRefsHandler` + `IGDGoToDefHandler` | textDocument/references |
| `GDDocumentSymbolHandler` | `IGDSymbolsHandler` | textDocument/documentSymbol |
| `GDLspRenameHandler` | `IGDRenameHandler` + `IGDGoToDefHandler` | textDocument/rename |
| `GDFormattingHandler` | `IGDFormatHandler` | textDocument/formatting |
| `GDLspCompletionHandler` | `IGDCompletionHandler` | textDocument/completion |
| `GDLspHoverHandler` | `IGDHoverHandler` | textDocument/hover |
| `GDLspCodeActionHandler` | `IGDCodeActionHandler` | textDocument/codeAction |
| `GDLspSignatureHelpHandler` | `IGDSignatureHelpHandler` | textDocument/signatureHelp |
| `GDLspInlayHintHandler` | `IGDInlayHintHandler` | textDocument/inlayHint |
| `GDLspDocumentHighlightHandler` | `IGDHighlightHandler` + `IGDGoToDefHandler` | textDocument/documentHighlight |
| `GDLspSemanticTokensHandler` | `IGDSemanticTokensHandler` | textDocument/semanticTokens/full |
| `GDLspPrepareRenameHandler` | `IGDRenameHandler` | textDocument/prepareRename |
| `GDTypeDefinitionLspHandler` | `IGDTypeDefinitionHandler` | textDocument/typeDefinition |
| `GDImplementationLspHandler` | `IGDImplementationHandler` | textDocument/implementation |
| `GDLspCallHierarchyHandler` | `IGDCallHierarchyHandler` | textDocument/prepareCallHierarchy, callHierarchy/incomingCalls, callHierarchy/outgoingCalls |
| `GDLspFoldingRangeHandler` | `IGDFoldingRangeHandler` | textDocument/foldingRange |
| `GDLspCodeLensHandler` | `IGDCodeLensHandler` | textDocument/codeLens |
| `GDCodeLensReferencesHandler` | `IGDCodeLensHandler` + `IGDFindRefsHandler` | gdshrapt/codeLensReferences |
| `GDWorkspaceSymbolHandler` | (project AST scan) | workspace/symbol |
| `GDLspSelectionRangeHandler` | `IGDSelectionRangeHandler` | textDocument/selectionRange |
| `GDLspDocumentLinkHandler` | `IGDDocumentLinkHandler` | textDocument/documentLink |
| `GDRangeFormattingHandler` | `IGDFormatHandler` | textDocument/rangeFormatting |
| `GDLspFileRenameHandler` | `IGDFileRenameHandler` | workspace/willRenameFiles |
| `GDDiagnosticPublisher` | (uses GDScriptProject) | publishDiagnostics |

## Position Conversion

```csharp
// LSP → CLI.Core
int cliLine = lspPosition.Line + 1;      // 0-based → 1-based
int cliColumn = lspPosition.Character + 1;

// CLI.Core → LSP
int lspLine = cliLine - 1;               // 1-based → 0-based
int lspCharacter = cliColumn - 1;
```

## Capabilities

**Document sync:** Incremental (`GDDocumentManager.ApplyChanges` splices `{range, text}` edits).
**Completion triggers:** `.`, `:`, `(`, `$`, `/`
**Signature help triggers:** `(`, `,`

**Also advertised:** `selectionRangeProvider`, `documentLinkProvider`, `documentRangeFormattingProvider`,
`foldingRangeProvider`, `callHierarchyProvider`, `typeDefinitionProvider`, `implementationProvider`,
`workspaceSymbolProvider`, and `workspace.fileOperations.willRename` (res:// reference rewrite on rename).

**Semantic token legend:**
- 10 token types: `variable`, `parameter`, `property`, `function`, `class`, `enum`, `enumMember`, `event`, `decorator`, `type`
- 6 modifiers: `declaration` (bit 0), `readonly` (bit 1), `static` (bit 2), `modification` (bit 3), `abstract` (bit 4), `defaultLibrary` (bit 5)
- `abstract` modifier: methods with `@abstract` annotation → italic in most themes
- `defaultLibrary` modifier: methods overriding base class methods (built-in Godot virtuals or project scripts) → subtle accent

## Transports

- **stdio framing is byte-exact**: `GDStdioJsonRpcTransport(serializer, Stream, Stream)` (used by `Program.cs`
  via `Console.OpenStandardInput/Output`) reads ASCII headers and exactly `Content-Length` **UTF-8 bytes**
  of body, so raw non-ASCII JSON from clients cannot desynchronise the stream. Header blocks without a
  valid `Content-Length` are skipped. The `TextReader/TextWriter` constructor is legacy (tests only).
- Outgoing-request completions use `RunContinuationsAsynchronously`: a handler resumed by a client
  response never runs on the reader loop, so the reader keeps dispatching while it works.
- `Completion` completes when the read loop ends (end of input or stop).

## Disk Changes

- No `workspace/didChangeWatchedFiles` handler: clients such as LSAI deliver created/modified scripts as
  `didOpen` / full-text `didChange`; deletes, never-opened edits and `.tscn` changes come from the server's own
  watchers (`EnableFileWatcher`, `SceneTypesProvider.EnableFileWatcher()`).
- `GDBaseModule` (deferred/LSP mode) builds the project model with `subscribeToChanges: true`, so watcher events
  invalidate cached models; scripts created by the watcher get the lazy model factory (`ScriptCreated`).
- `didOpen` of a `.gd` under the project root that the project does not know yet adds it (`AddScript`).
- Scene `SceneChanged` refreshes that scene's entries in `SignalConnectionRegistry` (scene references).

## Important Notes

- **LSP = Strict mode only** — Pro module is NOT loaded in LSP (by design)
- No heuristics in WorkspaceEdit (Rule 3)
- All rename edits are Strict confidence only

## Known Limitations

1. **Strict Mode Only** - LSP does not load Pro module, no heuristic edits
2. **Single-file edits** - the exception is `workspace/willRenameFiles`, which rewrites res://
   `preload`/`extends` references across files (exact res:// paths only; relative paths not rewritten)
3. **Range formatting** - reformats the whole document (the safe formatter is context-dependent), not a
   sub-range
4. **Position conversion** - EOF/EOL/multibyte edge cases are unit-covered (`GDDocumentSyncTests`,
   `GDSelectionRangeHandlerTests`)

## Key Files

```
Handlers/
├── GDDefinitionHandler.cs
├── GDReferencesHandler.cs
├── GDDocumentSymbolHandler.cs
├── GDLspRenameHandler.cs
├── GDFormattingHandler.cs
├── GDLspCompletionHandler.cs
├── GDLspHoverHandler.cs
├── GDLspCodeActionHandler.cs
├── GDLspSignatureHelpHandler.cs
├── GDLspInlayHintHandler.cs
├── GDLspDocumentHighlightHandler.cs
└── GDLspSemanticTokensHandler.cs

Core/
├── GDLanguageServer.cs
├── GDDocumentManager.cs
└── GDDiagnosticPublisher.cs

Transport/
├── GDStdioJsonRpcTransport.cs
└── GDSocketJsonRpcTransport.cs
```
