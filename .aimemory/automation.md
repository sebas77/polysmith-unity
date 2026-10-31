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

## Independent LOD-set import (2026-10-31)

- `PolysmithLodSet.Build(manifestPath, prefabPath, sourceMaterials, overwrite)`
  consumes native manifest v1 and separate FBXs, not the old levels.bin.
  Assets-relative paths only; existing output needs explicit overwrite.
  The Assets/Polysmith/Create LOD prefab menu creates a unique output.
- LOD0 retains hierarchy/animation. Lower-level renderers reference separate
  meshes, attach to matching LOD0 nodes, and reuse its skeletal bones.
  Original-UV exports share exact-name source materials; absent/ambiguous names
  fail. Atlas exports keep their own compatible baked materials. Animated blend
  shapes are rejected rather than silently left unsynchronized.
- Verified in lodtest, Unity 6000.6.2f1: recompile zero errors/warnings; all 12
  authored validation/integration cases passed through CLI `script run` in an
  in-memory harness. Three native synthetic sets cover static atlas,
  original-UV material identity, and skeletal animation. Assertions verified
  independent compact imported meshes, transitions, shared bone references,
  and BakeMesh deformation at every level. Test assets were deleted by
  TearDown. Unity generated both new script .meta files.
- Package testables remain unchanged: NUnit discovery found zero tests, so the
  direct assertion run is not described as a completed NUnit runner execution.
