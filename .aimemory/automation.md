# Prefab automation handoff (2026-10-31)

- `PolysmithAutomation.FindPrefabs(filter)` returns sorted editable `.prefab`
  assets under Assets/. `OpenPrefabs(paths, executable)` validates all paths,
  deduplicates, selects them and opens one existing round trip per prefab.
  Per-prefab launch errors are explicit; no automatic retry of successes.
- Reuses `PolysmithMenu.OpenPrefab` and `PolysmithWatcher`: existing duplicate
  ownership, source meshes and live export import are unchanged. Automation
  adds `--automation --unity-roundtrip`, uses the request folder for logs,
  and never opens a file picker. Missing executable is reported to the caller.
- Opening/optimizing never exports or changes prefab meshes. Polysmith MCP's
  explicitly confirmed Unity export writes the existing `_lod.obj` outputs;
  the watcher then applies them as before. Scene objects/model/package prefabs
  are out of scope by user decision.
- Optional `Polysmith.UnityMcp.Editor` assembly registers `polysmith_prefabs`
  with Coplay Unity MCP 10+ (`find`, `open`). Base `Polysmith.Editor` has no MCP
  dependency. Unity generated the added .meta files during import.
- Evidence: local package compiled in lodtest (Unity CLI recompile: 0 errors,
  0 warnings). API discovery/invalid-path checks passed. Two temporary prefab
  assets opened in separate app processes; repeated path was deduplicated and
  already-open prefab refused. Explicit synthetic test exports imported
  203-triangle meshes with PolysmithReduced label under each Reduced/ folder.
  Test assets removed through AssetDatabase afterward; no user prefab changed.
- Coplay custom-tool registration not tested (that package is not installed in
  the connected test Editor). NUnit test assembly authored but not enabled by
  package testables; API checks ran through Unity CLI eval_file instead.
