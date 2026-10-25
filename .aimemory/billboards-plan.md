# Billboards in the Unity round trip (planned 2026-10-24, implemented 2026-10-25)

Seb: "keep the plan" - parked to go back to the Polysmith split speed. Open
decisions A-C below were not answered yet.

## Facts
- Polysmith's OBJ export already writes billboards: per object a `billboard_<n>`
  group (one per material part) of planes with face normals and atlas UVs, MTL entry
  with `map_Kd` + `map_d` -> `<object>_billboard<n>.png` (colour + alpha).
- Polysmith's Unity mode turns billboards off (BillboardsHud.cpp Build disabled,
  HostState.cpp profile restore skipped).
- The plugin refuses unknown groups (ObjMeshFile.Read, TangentObjImport.ToMeshSpace).

## Plan
1. Polysmith: Unity mode allows billboards (Build, profile restore). DONE
   2026-10-25 (polysmith bb84188). Since then a node gets one `billboard_<n>`
   group per billboard (one per Build; planes never span two file nodes), not
   one per material part: same shape for the plugin.
2. Plugin: `billboard_<n>` groups -> extra submeshes after the mesh's own (both readers).
3. Materials `Reduced/<prefab>/<object>_billboard<n>.mat` on the part's first material's
   shader, other maps cleared. URP Lit: base map, white, alpha clip 0.5, render face
   Both. Built-in Standard: Cutout 0.5 (single-sided).
4. Textures: sRGB, alpha is transparency, mips preserve coverage at the cutoff, clamp.
5. Repoint: one material slot per submesh; extra slots added, and trimmed when a later
   export has none (Unity redraws the last submesh with surplus slots).
6. README section + limits.
7. (decision A) Every round trip starts from the ORIGINAL meshes: the plugin
   remembers the original of each reduced mesh and writes that into the request.

## Open decisions
- A. DECIDED 2026-10-25 (Seb): a round trip ALWAYS starts from the original
  meshes: the plugin remembers the original of every reduced mesh and sends
  that, never a reduced one (cards included or not).
- B. DECIDED 2026-10-25 (Seb): Built-in Standard cards are front-face only (Cutout
  0.5); URP Lit renders both faces. No custom shader.
- C. DECIDED 2026-10-25 (Seb): Seb tests the round trip himself (lodtest); the code
  is checked as far as possible without Unity.

## Implemented (2026-10-25)

- ObjMeshFile.GroupsOf: the part's groups + the file's `billboard_<n>` groups (file order);
  both readers take that list, so billboards are extra submeshes; other unknown groups
  still refuse the file.
- ReducedMaterials.GetBillboard: `<part>_billboard_<n>.mat` (Polysmith names every
  part's groups billboard_0..., so the material name carries the part), copy of the
  part's first material, colour = billboard PNG, other maps cleared, SetCutout:
  _Cutoff 0.5, opaque blend, _ALPHATEST_ON, AlphaTest queue; URP _AlphaClip 1 +
  _Cull Off; Built-in _Mode 1; other shaders: warning.
- AtlasTextures: MapKind.Billboard = sRGB, alphaIsTransparency, mipMapsPreserveCoverage
  at ReducedMaterials.Cutoff.
- Repoint: one slot per result submesh; surplus slots holding a ReducedLabel material
  (an earlier export's billboards) are removed.
- Origins (decision A): RememberOrigin writes {original mesh, original material per
  slot} as "guid:localId" JSON into the reduced mesh asset's importer userData;
  OriginOf resolves it in Objects (request + apply) and Repoint. Slots without maps go
  back to the original material.
- Verified: compiles against Unity 6000.6.2f1 module assemblies (dotnet build of the
  Editor sources, scratch csproj), 0 errors / 0 warnings. NOT run in Unity: Seb tests
  in lodtest (decision C).
