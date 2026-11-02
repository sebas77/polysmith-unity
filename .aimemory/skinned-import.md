# Single-level imports and animation (2026-11-02)

`PolysmithLodSet.Build` now accepts one to four levels.

- One level: a plain animated prefab, no LODGroup. `EnsureAnimation` finds
  (or adds) the Animator, loads the model's clips and saves
  `<prefab>.controller` with one state per clip; glTFast imports clips but no
  controller, so a GLB would otherwise stay in its bind pose.
- Levels may be `.glb` or `.fbx`; manifest triangle/vertex counts may be 0 when
  unknown, in which case the import does not compare counts.
- Two to four levels behave as before (LODGroup, LOD0 verbatim, lower levels
  bound to LOD0 bones).
- The static OBJ round trip is unchanged and still cannot carry skin.
