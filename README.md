# Polysmith for Unity

Reduce a prefab's meshes in Polysmith without leaving Unity.

## Install

Package Manager → **+** → **Add package from git URL…**:

```
https://github.com/sebas77/polysmith-unity.git
```

You also need `Polysmith.exe`. The first run asks where it is, and Unity remembers it per machine.

## Use

1. In the Project window, right-click a prefab → **Optimise in Polysmith**. Its meshes open in Polysmith, placed as the
   prefab shows them.
2. Reduce them, then export with **Export OBJ…**, keeping the suggested `<part>_lod.obj` name and folder. Every export
   updates the prefab in Unity straight away, so you can look at it and export again.
3. Close Polysmith to finish.

Each reduced mesh is saved as `<prefab folder>/Reduced/<prefab>/<part>.asset` and labelled `PolysmithReduced`; the
prefab's MeshFilters point at it. The original meshes are never changed. Running it again overwrites the same assets.

## Limits

- Prefab assets only, not model files (FBX, OBJ) and not objects in a scene.
- Meshes come back with positions, normals, the first UV set and their submeshes. Vertex colours, other UV sets,
  tangents and blend shapes are not kept.
- Don't edit the prefab while Polysmith is open: each export saves it.
