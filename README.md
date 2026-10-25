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

## Limits

- Prefab assets only, not model files (FBX, OBJ) and not objects in a scene.
- Meshes come back with positions, normals, the first UV set (the atlas UVs when there are maps), tangents when
  there is a normal map, and their submeshes. Vertex colours, other UV sets and blend shapes are not kept.
- Material properties follow the URP Lit and Built-in Standard shaders.
- Billboard cards are drawn from both sides with URP Lit, from the front only with Built-in Standard (it has no
  two-sided setting). With another shader they are not cut out (a warning says so).
- Don't edit the prefab while Polysmith is open: each export saves it.
