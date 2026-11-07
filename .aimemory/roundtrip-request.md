# Unity round trip request (GLB, 2026-11-07)

- Current workflow is GLB only, using optional UnityGLTF >=2.24.0. The independent
  `Polysmith.Setup.Editor` assembly offers an explicitly confirmed OpenUPM install;
  the main Editor assembly has a Version Define/constraint and does not compile
  without UnityGLTF. No unresolved hard dependency prevents the installer loading.
- Requests export the original meshes/materials and sampled skeletal clips from a
  temporary instance. Returns save mesh/material assets only; existing rig,
  Animator, Avatar, controller and original clips remain. Bone weights are remapped
  by transform path, with the original mesh's bind poses, not surface projection.
- The OBJ/MTL reader/writer, tangent OBJ importer and nearest-surface skin transfer
  are removed. Returned GLB atlas maps persist as images; materials use UnityGLTF's
  shader. A fully hidden object receives an empty mesh, preserving its rig node.
- Verification: six round-trip tests and three registry tests passed in lodtest;
  package removal compiled with integration disabled, and the installer restored
  2.24.0 with integration enabled. A scratch dragon return applied 15 mesh assets,
  2610 triangles/3243 vertices, while controller/Avatar/clips/bones and source prefab
  bytes stayed unchanged. Shader observed: `UnityGLTF/PBRGraph`.
- The final original-UV material-reuse addition has an authored test but was not
  observed in the executed test list before the user stopped further testing.
- Limits: Polysmith drops blend shapes; a Humanoid fixture was not exercised.

## Historical OBJ implementation (superseded)

## Fixed 2026-10-24: request model was rotated and untextured

- `WriteRequest` placed meshes relative to the prefab root (`rootFromWorld * localToWorld`). A prefab whose
  root carries a rotation (the tripo model: 270° X) therefore reached Polysmith in mesh space (Z-up) and lay
  on its back. The placement is now `filter.transform.localToWorldMatrix` (prefab space), which matches the
  documented "placed as the prefab shows it". `ApplyResults` inverts the same placement, so reduced meshes
  stay in mesh-local space; verified exact (vertex/normal/UV error < 1e-6) with a rotated (33/47/21°) and
  non-uniformly scaled (0.5, 2, 0.75) prefab, root transform untouched by the repoint.
- The request MTL carried only `Kd`, so Polysmith showed "Textured (no texture)". It now copies the
  material's `_BaseMap`/`_MainTex` and `_BumpMap` image files beside the OBJ and writes `map_Kd` and `norm`.
  Polysmith's OBJ loader reads only those two kinds (not AO/metal-rough/emissive).
- `.jpeg` is copied as `.jpg`: Polysmith's loader picks the decoder by extension and tries `.jpg`, not `.jpeg`.
- Textures embedded in a model asset, or package textures with no file on disk, are skipped with a warning.
- Verified on lodtest's `Assets/Original (1818874 tris) (1).prefab`: request bounds became prefab/world bounds
  (y 0..0.979), the Polysmith screenshot is upright and textured and matches loading the FBX; compile 0
  errors, 0 warnings.

## Several round trips at once (Seb, 2026-10-26)

Seb: optimising another prefab while one Polysmith is open must not be refused,
"it's ok to open a new instance of polysmith". PolysmithWatcher keeps a LIST of
round trips (SessionState "Polysmith.RoundTrips", JSON: prefab, temp folder,
process id + start ticks, last applied stamp); Poll applies / finishes each.
Only the SAME prefab is refused while its round trip is open (both would write
its reduced assets). A round trip open under the old single-trip keys
("Polysmith.RoundTrip.*") when this code loads is not migrated: its Polysmith
keeps running but is no longer applied. Compiles against 6000.6.2f1; NOT run.
