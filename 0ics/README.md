# 0ics fork of GDShrapt

Fork of [elamaunt/GDShrapt](https://github.com/elamaunt/GDShrapt) maintained by 0ics for
[LSAI](https://github.com/0ics-srls/Zerox.Lsai.Public) (semantic code navigation for AI assistants).
Apache-2.0, like upstream.

## Branches

| Branch | Role |
|--------|------|
| `main` | Mirror of upstream `main`. **Never push it.** |
| `main-0ics` | Default branch: upstream + our patches. All work happens here. |
| `release-0ics` | Release trigger only: `git push origin main-0ics:release-0ics`. |

## CI/CD policy

No CI on normal pushes or pull requests. The only workflow is `.github/workflows/release-0ics.yml`
(push to `release-0ics` or manual dispatch): build, tests (CI filter), framework-dependent net10 publish of
`GDShrapt.LSP`, GitHub release `lsai-gdshrapt-v6.0.0-alpha.7.N` with `gdshrapt-lsp-6.0.0-alpha.7.N.zip` + `.sha256`.
`N` comes from `0ics/VERSION`; bump it before every release (an existing release is never overwritten).
`GDForkPolicyTests` guards this policy.

Local green gate (Docker, from the LSAI repo): `bash tasks/50-godot/fork-gate.sh ["<test filter>"]`.

## Syncing upstream

```bash
git fetch upstream            # upstream = https://github.com/elamaunt/GDShrapt.git
git checkout main && git merge --ff-only upstream/main   # local mirror only, not pushed
git checkout main-0ics && git merge main                  # resolve conflicts, run the gate
```

## Our patches (measured in the LSAI POC, task 50-godot)

| Id | Fix |
|----|-----|
| net10 | All net8.0 projects retargeted to net10.0. |
| D8a/D7b | stdio framing is byte-exact (Content-Length = UTF-8 bytes); outgoing-request continuations never run on the reader loop. |
| D1/D2 | Semantic model rebuilt on demand after reload; no stale `_fileModels` entries. |
| D3 | Hierarchical document symbols from the AST, 1-based positions, full declaration ranges, signatures. |
| D6 | Disk changes via the server's own watchers + didOpen of unknown scripts; scene connections refreshed. |
| D7a | Linear call-site inference (call-name index, typed methods skipped). |
| D4a/D4d | Rename keeps typed cross-file callers; `as` casts are type usages; `(x as T)` / `T\|null` receivers resolve to `T`. |
| D4b/D4c | `.tscn` connections are references for signal and callback; instanced sub-scene typing; rename of `signal=`/`method=`. |
| D5 | References and rename for autoload singleton names (project.godot key + uses). |
| D8d-f | Opt-in trace (`GDSHRAPT_LSP_TRACE`), OS-aware path comparison, no cross-assignment for same-named scripts. |
