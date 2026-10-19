# Refactoring — Refactoring Services

Refactoring services for GDScript code transformations.

## Architecture

```
Refactoring/
├── Services/           - 20 refactoring services
├── Context/            - Refactoring context (selection, cursor)
├── Results/            - Result types (single-file only in Base)
├── GDCrossFileReferenceFinder.cs  - Cross-file reference search
└── GDNodePathReferenceFinder.cs   - Scene node path reference search
```

## Plan vs Execute Model

**Base (Free):** Returns plans only, single-file scope
**Pro (Paid):** Adds execution, batch operations

```csharp
// Base
GDAddTypeAnnotationsResult PlanFile(file, options);  // ✓ Available

// Pro only
GDBatchAddTypeAnnotationsResult PlanProject(options);  // Pro
GDProResult Execute(plan);                              // Pro
```

## Services (20)

| Service | Scope | Confidence |
|---------|-------|------------|
| `GDRenameService` | Cross-file | Strict/Potential |
| `GDFindReferencesService` | Cross-file | Strict/Potential |
| `GDGoToDefinitionService` | Single-file | Strict |
| `GDAddTypeAnnotationsService` | Single-file | Common base from union |
| `GDAddTypeAnnotationService` | Single-file | - |
| `GDReorderMembersService` | Single-file | - |
| `GDExtractMethodService` | Single-file | - |
| `GDExtractConstantService` | Single-file | - |
| `GDExtractVariableService` | Single-file | - |
| `GDGenerateGetterSetterService` | Single-file | - |
| `GDGenerateOnreadyService` | Single-file | - |
| `GDInvertConditionService` | Single-file | - |
| `GDConvertForToWhileService` | Single-file | - |
| `GDSurroundWithService` | Single-file | - |
| `GDRemoveCommentsService` | Single-file | - |
| `GDSnippetService` | Single-file | - |
| `GDFormatCodeService` | Single-file | - |
| `GDCallHierarchyService` | Cross-file | - |
| `GDImplementationService` | Cross-file | - |
| `GDTypeDefinitionService` | Single-file | - |

(`GDSymbolReferenceCollector` is the shared reference-collection utility used by the cross-file services.)

## Confidence Modes

| Mode | Description | Base | Pro |
|------|-------------|------|-----|
| `Strict` | Explicit type annotation | ✓ | ✓ |
| `Potential` | Duck-typed, method in TypesMap | - | ✓ |
| `NameMatch` | Heuristic, name-based | - | ✓ |

## Key Classes

### GDRenameService

Cross-file rename with conflict detection.

**Features:**
- Symbol resolution via SemanticModel
- Reference collection with confidence
- Conflict detection (shadowing, redefinition)

**Union/shared reference promotion.** A `Confidence == Union` reference (a member access whose
receiver's data-flow type is a union `A|B`) is a *shared* reference across all member declarations.
It is promoted to a **strict** (auto-applied) edit only when **every** type in its
`SharedTypes` set is renamed in the same operation — i.e. all possible resolutions of the receiver
produce the identical edit, so the rewrite is risk-free. This naturally happens only when the rename
covers all members (e.g. the bridge-connected path, where `declaringTypeName` is null and every
hierarchy's declaration is in scope). Otherwise the union reference stays in `PotentialEdits` (shown,
never applied), so renaming `A.foo` never silently rewrites `x.foo()` where `x` could be `B`.

Coverage is computed by `BuildRenamedDeclaringTypes` (declaring types of the `Declaration`/`Override`
refs in this operation) and checked per shared type via `IsTypeCompatible` (direct or inherited).
`RemoveStrictDuplicatesFromPotential` guarantees a promoted position is not also reported as potential.

The behavior is controlled by `GDRenameService.PromoteFullyCoveredUnionReferences` (default `true`),
surfaced on `GDRenameHandler` and via the Base CLI `rename --no-union-strict` option. When disabled,
union/shared references are always kept potential.

**Union references are union-first.** `GDCrossFileReferenceFinder` (Path A and the member-access Path B)
consults the receiver's raw data-flow union (`DetermineFlowBasedConfidence` reads `CurrentType.Types`)
**before** the collapsed single-type short-circuit, so branch-merge/ternary/multi-assign and call-site
parameter unions surface as `Union` references with `SharedTypes`. Path B resolves the member-op from a
`GDCallExpression.CallerExpression` so the call-node index entry doesn't pre-empt the union entry.

**Union-driven bridge for rename promotion.** A `Union` reference's `SharedTypes` is a data-flow proof
that a call site is shared across hierarchies, so `FindBridgeMethodsWithDuckRef` treats `Union` (alongside
duck `Potential`/`NameMatch`) as a bridge connection. `PlanRename(oldName, …, filterFilePath)` checks
`CollectAllReferences(...).IsBridgeConnected` even when the filter file declares the symbol, and defers to
the bridge-merge path when connected — so renaming covers every shared-type declaration and the shared
call is promoted to a strict edit (fully-covered). The call-site param union is treated as a real union
(promotable); a receiver with no provable union (no call sites) stays Potential.

**Unrelated hierarchies (0ics D4a).** `BuildUnrelatedFilesSet` marks as unrelated only files that DECLARE a
same-named member in an unrelated hierarchy. Files that only REFERENCE the name are never marked: the caller's
own class says nothing about the receiver's type (`coin.gd extends Area2D` calling `p.try_jump()` on a typed
`Player` is a strict reference); each reference's confidence already reflects the receiver type.

**Casts.** `x as T` is a type usage (`GDTypeUsageKind.Cast`, next to `TypeCheck` for `is`), so class
references/rename include casts. A member access on `(x as T)` has receiver `T`, and a `T|null` receiver
(e.g. `var q := x as T`) is treated as `T` — in `GDSemanticReferenceCollector` and in the cross-file finder.

**Scene connections (0ics D4b/D4c).** `.tscn` `[connection signal="X" from=... to=... method="Y"]` lines are
references for both roles: the callback method (existing `SignalConnectionRegistry` step) and the signal
(`GDSymbolReferenceCollector.CollectSceneSignalNameReferences`, at `GDSignalConnectionInfo.SignalColumn`). Node
types come from one resolver, `GDProjectSemanticModel.ResolveSceneNodeTypeName(scene, node)`, which types an
instanced sub-scene node by its root script. `GDRenameService.CollectTscnEdits` edits `method="..."` (typed by the
TO node) and `signal="..."` (typed by the FROM node); Strict when compatible, Potential when the type is unknown.

**Autoload singletons (0ics D5).** A name declared only in `project.godot` `[autoload] name="*res://..."` has
references: the project.godot key (`Declaration`, located by `GDProjectGodotLocator`, script wrapped like `.tscn`
hits) plus every identifier with that name not resolved by the script's model to a local/member/parameter
(`GDSymbolReferenceCollector.TryCollectAutoloadReferences`). Entries without `*` are not global names.
`GDRenameService.PlanRename(name, …)` takes this path first (unless the filter script declares the name) and
edits the project.godot key and the uses.

### GDCrossFileReferenceFinder

Finds references across project files.

**Algorithm:**
1. Find symbol definition
2. Scan all project files
3. Classify references by confidence
4. Return categorized results

### GDExtractMethodService

Extracts selected statements into new method.

**Requirements:**
- Contiguous statement selection
- Dependency analysis for parameters
- Return value inference

## Result Types

**Base (single-file):**
- `GDAddTypeAnnotationsResult`
- `GDFileReorderPlan`
- `GDRefactoringResult`
- `GDRenameResult`

**Pro (batch):**
- `GDBatchAddTypeAnnotationsResult` (Pro only)
- `GDBatchReorderMembersResult` (Pro only)

## Union → Common Base Type Resolution

`GDAddTypeAnnotationsService` resolves union types to common base types when all union members share a hierarchy:
- `Sprite2D | AnimatedSprite2D` → `Node2D` (real annotation at `Medium` confidence)
- Uses `GDUnionTypeHelper.FindCommonBaseType()` via `IGDRuntimeProvider` inheritance chain
- Skips `Object`, `Variant`, `RefCounted` as too generic
- Applies to both variable and return type annotations

## Known Limitations

1. **Extract Method** - Requires contiguous statements, no partial extraction
2. **Cross-file Batch** - Only available in Pro
3. **Confidence Potential/NameMatch** - Execution requires Pro license
4. **Rename Conflicts** - Reports but doesn't auto-resolve shadowing
5. **Extract Variable** - Single expression only, no multi-statement

## Two reference-finding paths (consolidation candidate)

There are currently two reference-finding implementations and two rename entry points:

| Consumer | References | Rename |
|----------|-----------|--------|
| CLI / LSP (`GDFindRefsHandler`, `GDRenameHandler`) | `GDSymbolReferenceCollector.CollectReferences` | `GDRenameService.PlanRename(oldName, …)` → same collector |
| Plugin (`FindReferencesCommand`, gutters, `RenameIdentifierCommand`) | `GDFindReferencesService` | `GDRenameService.PlanRenameAtCursor`/`PlanRenameInScope` → `GDFindReferencesService` |

CLI and LSP are guaranteed identical because they share the collector (pinned by
`GDShrapt.LSP.Tests/Handlers/GDCliLspParityTests.cs`). The Plugin path is separate and could
diverge for local scopes (`GDFindReferencesService` only delegates to the unified collector for
cross-file scopes). Unifying the Plugin onto the CLI/LSP path is a future Plugin-parity task.

## Files

| File | Purpose |
|------|---------|
| `Services/GDRenameService.cs` | Cross-file rename |
| `Services/GDFindReferencesService.cs` | Reference search |
| `Services/GDAddTypeAnnotationsService.cs` | Batch type annotations |
| `GDCrossFileReferenceFinder.cs` | Cross-file reference finder |
| `Results/GDAddTypeAnnotationsResult.cs` | Single-file result |
| `Context/GDRefactoringContext.cs` | Refactoring context |
