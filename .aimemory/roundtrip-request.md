# Unity round trip request (OBJ + MTL)

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
