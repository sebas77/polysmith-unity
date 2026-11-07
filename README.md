# Polysmith for Unity

Reduce a prefab's meshes in Polysmith without leaving Unity.

## Install

Package Manager → **+** → **Add package from git URL…**:

```
https://github.com/sebas77/polysmith-unity.git
```

You also need `Polysmith.exe`, a build that accepts `--unity-roundtrip` (it adds the "Replace prefab mesh" button).
The first run asks where it is, and Unity remembers it per machine.

## Use

1. In the Project window, right-click a prefab → **Optimise in Polysmith**. Its meshes open in Polysmith, placed as the
   prefab shows them.
2. Reduce them, then press **Replace prefab mesh**, which writes every part. **Export OBJ…** also works for one part
   at a time; keep the suggested `<part>_lod.obj` name and folder. Every export updates the prefab in Unity straight
   away, so you can look at it and export again.
3. Close Polysmith to finish.

Several prefabs can be open at once, each in its own Polysmith; a prefab that already has one open is refused
until that Polysmith is closed.

Each reduced mesh is saved as `<prefab folder>/Reduced/<prefab>/<part>.asset` and labelled `PolysmithReduced`; the
prefab's MeshFilters point at it. The original meshes are never changed. Running it again overwrites the same assets,
and always starts from the originals: each reduced mesh remembers its original mesh and materials (in its import
settings), and a prefab that already shows reduced meshes sends those originals to Polysmith.

Material groups follow the prefab's materials, so Polysmith bakes one atlas per material. When an export carries atlas
maps (colour, normal, occlusion, metal-rough, emissive), each is saved as a PNG in `Reduced/<prefab>/` (occlusion and
metal-rough repacked to Unity's channels) and imported with the settings its kind needs; Unity builds its mips. Each
material gets a reduced copy (`Reduced/<prefab>/<material>.mat`) using them. Normal-mapped parts are read through
Unity's OBJ importer so their tangents are MikkTSpace, matching Polysmith's bake.

Billboards built in Polysmith come back inside their part: each `billboard_<n>` group becomes one more submesh after
the mesh's own, with one more material slot. Its material (`Reduced/<prefab>/<part>_billboard_<n>.mat`) is a copy of
the part's first material with the billboard image (`<part>_lod_billboard<n>.png`) as its colour, cut out at alpha 0.5
and every other map cleared; the image's mips keep their coverage at that value. A later export without billboards
removes those slots again.

## AI / Unity CLI

Use Unity CLI with the running Editor's Unity Pipeline bridge. No Unity MCP
package or custom adapter is required. Run `unity status --format json`, then
`unity list --project-path "C:\absolute\UnityProject" --format json` to discover
commands. Always target that exact project.

Save a C# snippet outside `Assets/` and evaluate it through the discovered command:

```powershell
unity command eval --project-path "C:\absolute\UnityProject" --format json --file "C:\absolute\handoff.cs"
```

Read-only discovery:

```csharp
return Polysmith.PolysmithAutomation.FindPrefabs("Tree");
```

After inspecting the returned paths, launch the exact prefabs:

```csharp
return Polysmith.PolysmithAutomation.OpenPrefabs(
    new[] { "Assets/Trees/Oak.prefab" }, @"C:\absolute\Polysmith.exe");
```

- `OpenPrefabs` selects and opens those exact prefabs, one automation-enabled
  Polysmith per prefab. Omit the executable to use the menu's saved path.
- The returned process ids identify their sessions in Polysmith MCP. Opening never
  exports or replaces prefab meshes. The agent leaves its optimized result visible
  for feedback; export/apply happens **only on request** through Polysmith MCP.

The public `PolysmithAutomation` API reuses the normal round trip.
Scene objects, FBX/model prefabs and package assets are rejected. Duplicate paths
are deduplicated; already-open prefabs and partial launch errors are reported.
See Polysmith's `tools/README.md` for its
stdio server setup, tool workflow and verification commands.

## Independent LOD sets

Polysmith's explicitly confirmed `polysmith_export_levels` operation writes
`LOD0.fbx` through `LOD3.fbx` (two to four levels) and `manifest.json`. Each level
has its own compact indexed mesh; there is no shared-vertex-buffer requirement.
This is separate from the existing single-LOD OBJ prefab round trip above.

1. Copy the exported set into a folder under `Assets/` and let Unity import it.
2. Select `manifest.json`, then **Assets → Polysmith → Create LOD prefab**.
3. For **original-UV** exports, also select the original Unity prefab/model:
   its materials are reused by name across levels. Missing or ambiguous material
   names are rejected, never guessed. Atlas-mode levels retain their own imported
   baked materials, since their UV layouts need not match.

The menu creates a new prefab; it never replaces the source. Editor scripts can
call `PolysmithLodSet.Build(manifestPath, prefabPath, sourceMaterials, overwrite)`;
paths must be Assets-relative, and replacing an existing output requires explicit
`overwrite: true`. The manifest supplies transition heights (defaults 0.5, 0.2,
0.05, 0.01 for the available levels).

Static and skeletally animated meshes are supported. LOD0 keeps the imported
hierarchy and animation clips; lower-level renderers attach to the matching LOD0
nodes and bind to its bones, rather than duplicating the rig. Assign/play the
imported animation on LOD0's Animator as with the individual FBX. Animated blend
shapes are currently rejected by this importer rather than left unsynchronized.
Generate/readable mesh settings and animation import settings remain ordinary
Unity model import settings.

Integration tests use synthetic outputs from `PolysmithLodSetSmoke.py`; set
`POLYSMITH_LOD_SET_TEST_DIR` to its output directory when running
`PolysmithLodSetTests` in the `Polysmith.Editor.Tests` EditMode assembly.

## Limits

- Prefab assets only, not model files (FBX, OBJ) and not objects in a scene.
- Meshes come back with positions, normals, the first UV set (the atlas UVs when there are maps), tangents when
  there is a normal map, and their submeshes. Vertex colours, other UV sets and blend shapes are not kept.
- Material properties follow the URP Lit and Built-in Standard shaders.
- Billboard cards are drawn from both sides with URP Lit, from the front only with Built-in Standard (it has no
  two-sided setting). With another shader they are not cut out (a warning says so).
- Don't edit the prefab while Polysmith is open: each export saves it.
