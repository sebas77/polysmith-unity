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
prefab's MeshFilters point at it. The original meshes are never changed. Running it again overwrites the same assets.

Material groups follow the prefab's materials, so Polysmith bakes one atlas per material. When an export carries atlas
maps (colour, normal, occlusion, metal-rough, emissive), each becomes a texture asset with exactly Polysmith's mip
levels, and each material gets a reduced copy (`Reduced/<prefab>/<material>.mat`) using them. Normal-mapped parts are
read through Unity's OBJ importer so their tangents are MikkTSpace, matching Polysmith's bake.

## Limits

- Prefab assets only, not model files (FBX, OBJ) and not objects in a scene.
- Meshes come back with positions, normals, the first UV set (the atlas UVs when there are maps), tangents when
  there is a normal map, and their submeshes. Vertex colours, other UV sets and blend shapes are not kept.
- Material properties follow the URP Lit and Built-in Standard shaders.
- Don't edit the prefab while Polysmith is open: each export saves it.
