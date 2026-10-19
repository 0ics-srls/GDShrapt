# GDShrapt.CLI.Core

CLI commands, service registry, and handlers for IDE-like functionality.

## Module System

Modular architecture for service registration and override.

### Registry

| Class | Purpose |
|-------|---------|
| `IGDServiceRegistry` | Interface for service registration and retrieval |
| `GDServiceRegistry` | Implementation with priority-based override support |
| `IGDModule` | Module interface (Priority, Configure) |

### Modules

| Module | Priority | Location | Provides |
|--------|----------|----------|----------|
| `GDBaseModule` | 0 | CLI.Core | 14 handlers (all core functionality) |
| `GDProModule` | 100 | Pro.CLI | ProRename (confidence), BatchAddTypes, BatchReorder |

### Usage

```csharp
var registry = new GDServiceRegistry();
registry.LoadModules(project, new GDBaseModule());  // Base
registry.LoadModules(project, new GDProModule(license));  // Pro overrides

var handler = registry.GetService<IGDRenameHandler>();  // Returns Pro if loaded
```

## Base Handlers (14)

| Handler | Interface | Purpose |
|---------|-----------|---------|
| `GDRenameHandler` | `IGDRenameHandler` | Symbol rename |
| `GDCompletionHandler` | `IGDCompletionHandler` | Code completion |
| `GDFindRefsHandler` | `IGDFindRefsHandler` | Find references |
| `GDDiagnosticsHandler` | `IGDDiagnosticsHandler` | Validation + linting |
| `GDSymbolsHandler` | `IGDSymbolsHandler` | Document symbols |
| `GDGoToDefHandler` | `IGDGoToDefHandler` | Go to definition |
| `GDFormatHandler` | `IGDFormatHandler` | Code formatting |
| `GDHoverHandler` | `IGDHoverHandler` | Hover info |
| `GDCodeActionHandler` | `IGDCodeActionHandler` | Quick fixes |
| `GDSignatureHelpHandler` | `IGDSignatureHelpHandler` | Signature help |
| `GDInlayHintHandler` | `IGDInlayHintHandler` | Inlay hints |
| `GDTypeFlowHandler` | `IGDTypeFlowHandler` | Type flow visualization |
| `GDHighlightHandler` | `IGDHighlightHandler` | Document highlight |
| `GDSemanticTokensHandler` | `IGDSemanticTokensHandler` | Semantic token classification with override/abstract method detection |

## Pro Handlers (3)

| Handler | Interface | Status |
|---------|-----------|--------|
| `GDProRenameHandler` | `IGDRenameHandler` | Complete |
| `GDProBatchAddTypesHandler` | `IGDProBatchAddTypesHandler` | Complete |
| `GDProBatchReorderHandler` | `IGDProBatchReorderHandler` | Complete |

## Handler Rules

**Rule 11:** Handlers access type info through `file.SemanticModel?.Method()`. SemanticModel is the single API entry point.

Example:
```csharp
// Correct - direct access to SemanticModel
var semanticModel = script.SemanticModel;
var symbol = semanticModel?.FindSymbol(name);
var type = semanticModel?.GetTypeForNode(node);

// All methods are on SemanticModel:
// - FindSymbol(), GetSymbolForNode()
// - GetReferencesTo()
// - GetTypeForNode(), GetTypeNodeForNode()
// - GetMethods(), GetVariables(), GetSignals(), etc.
```

## Exit Codes

**CRITICAL:** Always use `GDExitCode` constants, never magic numbers.

| Constant | Value | Meaning |
|----------|-------|---------|
| `GDExitCode.Success` | 0 | No issues |
| `GDExitCode.WarningsOrHints` | 1 | Warnings/hints found (when fail-on configured), or check-only "needs formatting" |
| `GDExitCode.Errors` | 2 | Errors found in codebase |
| `GDExitCode.Fatal` | 3 | Project not found, configuration error, or unrecoverable exception |

Use `GDExitCode.FromResults()` for analysis commands. Use `GDExitCode.Fatal` for infrastructure failures (project not found, handler not available).

## Experimental Features

Features under active development use `[Experimental]` prefix in their description:

```csharp
// Command:
var command = new Command("watch", "[Experimental] Watch for file changes...");

// Option:
var opt = new Option<bool>("--incremental", "[Experimental] Only analyze changed files...");
```

**Experimental features:** none in Base CLI (the `watch` command graduated to stable in 6.0.0-alpha.7).
Pro adds experimental options (e.g. `--incremental`) in `GDShrapt.Pro.CLI`.

## Potential References Messaging (Rename)

**CRITICAL:** Base CLI must NOT show Pro upsell messages. Potential (duck-typed) references are shown as informational, without advertising Pro:

```csharp
// Dry-run: show list with neutral label
"Potential references (5) [lower confidence, not applied]:"

// Apply mode: inform about exclusion
"5 duck-typed reference(s) found but not applied (lower confidence)."
```

Never use "Use GDShrapt Pro" or "[Pro only]" in Base CLI output.

## Position Conventions

Input positions to handlers are **1-based** line and column. The AST and SemanticModel use
**0-based** positions internally; handlers convert at the boundary (`inputLine - 1`, etc.).

**Output `Line` is 1-based for all DTOs. Output `Column` is NOT uniform** — it differs per DTO
today (a known, non-user-visible inconsistency: each LSP/Plugin consumer compensates):

| Output DTO | Line | Column |
|------------|------|--------|
| `GDCliReferenceLocation` (find-refs) | 1-based | **1-based** |
| `GDDefinitionLocation` (go-to-def / type-def / impl) | 1-based | **0-based** |
| Inlay hints / rename edits / code lens | 1-based | 1-based |
| Document symbols (`GDDocumentSymbol`) | 1-based (identifier; `EndLine` = last line of the declaration) | 1-based (identifier; `EndColumn` exclusive) |
| Diagnostics (`GDUnifiedDiagnostic`) & semantic tokens | 1-based line / 0-based col (AST-aligned; shared with the `# LINE:COL` marker system) | — |

This contract is pinned by `GDShrapt.CLI.Tests/Handlers/GDPositionContractTests.cs`. A planned
normalization to uniform 1-based columns is **deferred** — it is cross-cutting (LSP + Plugin +
tests) and cosmetic (outputs are correct today). Any such change must update that test and review
every consumer in the same commit.

See `Analysis/CLAUDE.md` for the AST/SemanticModel/LSP/Plugin position table.

## Document Symbols

`GDSymbolsHandler.GetSymbols` builds symbols from the script's own AST: class members and inner classes
(recursively, as `Children`) — methods, variables (incl. static/`@export`/`@onready`/properties with get/set),
constants, signals, enums (anonymous enum = empty name, LSP reports `enum`), inner classes. Locals, parameters
and inherited engine members are never included. `Type` is the declaration signature (`static func make() ->
Constructs`, `signal hit(damage: int, source: Node)`, `const MAX_HP: int`). Built-in type files keep the
model-based extraction.

## Known Limitations

1. **Service Priority** - Higher priority modules override lower, no merge
2. **Handler State** - Handlers are stateless, create new instances per request
3. **TypeFlow** - Only visualizes single method, no cross-method flow

## Key Files

```
Registry/
├── IGDServiceRegistry.cs
├── GDServiceRegistry.cs
├── IGDModule.cs
└── GDBaseModule.cs

Handlers/
├── IGD*Handler.cs (interfaces)
└── GD*Handler.cs (implementations)

TypeFlow/
└── GD*Node.cs (navigation models)

Commands/
└── GD*Command.cs
```
