# Polysmith for Unity

Reduce a prefab's meshes in Polysmith without leaving Unity.

## Install

Package Manager → **+** → **Add package from git URL…**:

```
https://github.com/sebas77/polysmith-unity.git
```

You also need `Polysmith.exe`, a build that accepts `--unity-roundtrip` (it adds the "Replace prefab mesh" button).
The first run asks where it is, and Unity remembers it per machine.

On installation, Polysmith offers to install **UnityGLTF 2.24.0**. Accepting adds the
OpenUPM scoped registry (`https://package.openupm.com`, scope `org.khronos.unitygltf`)
to this project and installs the package. Existing registries and dependencies are preserved;
conflicting registry scopes are reported, not overwritten. Declining leaves the optimisation
assembly disabled. Retry from **Tools → Polysmith → Install GLB support**.
UnityGLTF is deliberately not a hard manifest dependency: the independent setup assembly
must compile before the project has registered OpenUPM. An asmdef Version Define enables
the GLB assembly only when UnityGLTF 2.24.0 or newer is installed.

## Use

1. In the Project window, right-click a prefab → **Optimise in Polysmith**. Its meshes open in Polysmith, placed as the
   prefab shows them.
2. Reduce them, then press **Replace prefab mesh**, which writes `<prefab>_lod.glb` next to the request.
   Every export updates the prefab in Unity straight away, so you can look at it and export again.
3. Close Polysmith to finish.

Several prefabs can be open at once, each in its own Polysmith; a prefab that already has one open is refused
until that Polysmith is closed.

Each reduced mesh is saved as `<prefab folder>/Reduced/<prefab>/<part>.asset` and labelled `PolysmithReduced`; the
prefab's MeshFilters and SkinnedMeshRenderers point at it. The original meshes are never changed. Running it again
overwrites the same assets, and always starts from the originals: each reduced mesh remembers its original mesh and
materials (in its import settings), and a prefab that already shows reduced meshes sends those originals to Polysmith.

Requests are **GLB only**, with or without animation. UnityGLTF exports the original meshes,
skin weights, skeleton and sampled animation clips from a temporary copy. Humanoid clips are
baked to bone-transform tracks for Polysmith's preview, not baked into deformed vertices.
Returned meshes retain Polysmith's skin weights, remapped by transform path to the prefab's
existing bones, with the original bind poses. **Only meshes and materials are replaced**;
the hierarchy, Animator, Avatar, controller and original clips are untouched.
The old OBJ/MTL parser, writer and nearest-surface skin transfer are removed.

When Polysmith merged materials, a part's result names the merged material (after the first one merged, possibly
another part's): the part then has one submesh per material it uses, and every part merged into one material shares one
reduced material.

Embedded atlas maps are saved as PNGs in `Reduced/<prefab>/`, with UnityGLTF materials
saved beside the meshes. They use glTF's channel packing (including RGB normals), not
Unity Standard's metal/smoothness packing. Unity builds their mips and calculates tangents.
Billboard primitives/materials are imported from the same GLB; no sidecar OBJ or MTL is used.
Original-UV results without baked maps reuse the remembered source materials by slot name.

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
This is separate from the single-LOD GLB prefab round trip above.

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
- Returned materials use UnityGLTF's shaders. Custom shader properties outside glTF are not transferred.
- Polysmith's GLB writer currently omits blend shapes and original-UV source textures.
- Ambiguous/missing transform paths and bones outside the original renderer are rejected.
- A fully hidden object keeps its renderer/bones but receives an empty mesh, so its original geometry does not reappear.
- Don't edit the prefab while Polysmith is open: each export saves it.
