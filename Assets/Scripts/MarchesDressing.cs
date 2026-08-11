using System;
using System.Collections.Generic;
using UnityEngine;
#if UNITY_EDITOR
using UnityEditor;
#endif

// Dresses the Marches terrain with real land cover baked from OpenStreetMap
// (Docs/MarchesRealism.md is the brief): roads as draped dirt ribbons, the
// Monnow and its streams as draped water ribbons, real wood polygons filled
// with terrain trees, bushes as undergrowth and hedging the lanes. Everything
// here is scene DRESSING derived from the features asset — no stored gameplay
// facts, nothing joins CastleSave, and the dressing root stays out of
// splineParent (the cutaway has no business slicing a road, the LandPlot
// precedent). ContextMenu bake like TerrainGenerator: re-run Dress to
// iterate, Clear Dressing to remove everything it made. Ribbons carry no
// colliders (the walker must never trip on a road); oak trunks DO collide.
[RequireComponent(typeof(Terrain))]
public class MarchesDressing : MonoBehaviour
{
    // World-XZ polylines/polygons preprocessed offline from Overpass with the
    // verified Marches georeferencing — Unity never talks to the network.
    public TextAsset features;

    [Header("Ribbons")]
    // 1220 filter: every road class renders as the same dirt; class sets
    // width only (0 through-road, 1 lane/track, 2 path).
    public float[] roadWidths = { 4f, 3f, 1.8f };
    // Draped surfaces sit proud of the terrain so the RENDERED ground can't
    // swallow them: the terrain draws a simplified LOD mesh whose height can
    // deviate from the true heightmap by pixelError's worth of screen space
    // (a 0.12m lift vanished entirely at pixelError 5 — verified with a probe
    // quad that rendered fine while the draped ribbon didn't). Dress() also
    // tightens heightmapPixelError to 2; the lift covers what remains.
    public float roadLift = 0.30f;
    public float waterLift = 0.18f;
    public float sampleStep = 5f;

    [Header("Trees")]
    public float treeSpacing = 9f;
    public float bushSpacing = 14f;
    public float roadsideBushStep = 16f;
    public int maxTreeInstances = 300000;

    const string DressFolder = "Assets/Terrain/Dressing";
    const string RootName = "Marches Dressing";
    // Chunk flush threshold — comfortably under the 65k 16-bit index limit.
    const int MaxChunkVerts = 60000;
    // Spatial-hash cell for "is a road here" tests during scattering; a tree
    // in the same or neighboring cell as a road sample is skipped, so a
    // track through a wood stays walkable.
    const float RoadCell = 6f;

    [Serializable] class Fea { public int c; public float w; public float[] p; }
    [Serializable] class FeaSet { public Fea[] roads; public Fea[] waters; public Fea[] woods; }

    [ContextMenu("Dress")]
    public void Dress()
    {
        if (features == null)
        {
            Debug.LogError("MarchesDressing: no features asset assigned.");
            return;
        }
        ClearDressing();
        Terrain terrain = GetComponent<Terrain>();
        // See roadLift: the ribbons only stay visible if the rendered terrain
        // stays close to the heightmap the drape sampled.
        terrain.heightmapPixelError = 2f;
        FeaSet set = JsonUtility.FromJson<FeaSet>(features.text);

        Transform root = new GameObject(RootName).transform;

        var roadCells = new HashSet<(int, int)>();
        foreach (Fea r in set.roads)
            MarkRoadCells(roadCells, r.p, Mathf.CeilToInt(
                roadWidths[Mathf.Clamp(r.c, 0, roadWidths.Length - 1)] * 0.5f / RoadCell) + 1);

        Material dirt = LoadOrCreateMaterial("DirtRoadMat",
            new Color(0.36f, 0.29f, 0.20f), 0.15f);
        Material water = LoadOrCreateMaterial("WaterMat",
            new Color(0.14f, 0.22f, 0.26f), 0.92f);

        int roadCount = BuildRibbons(terrain, root, "Roads", dirt, roadLift,
            set.roads, r => roadWidths[Mathf.Clamp(r.c, 0, roadWidths.Length - 1)]);
        int waterCount = BuildRibbons(terrain, root, "Waters", water, waterLift,
            set.waters, w => w.w);

        int trees = ScatterTrees(terrain, set, roadCells);

        Debug.Log($"Marches dressed: {roadCount} road ribbons, {waterCount} water ribbons, {trees} trees/bushes.");
#if UNITY_EDITOR
        EditorUtility.SetDirty(terrain.terrainData);
        AssetDatabase.SaveAssets();
        UnityEditor.SceneManagement.EditorSceneManager.MarkSceneDirty(gameObject.scene);
#endif
    }

    [ContextMenu("Clear Dressing")]
    public void ClearDressing()
    {
        GameObject root = GameObject.Find(RootName);
        if (root != null)
        {
            if (Application.isPlaying) Destroy(root);
            else DestroyImmediate(root);
        }
        Terrain terrain = GetComponent<Terrain>();
        TerrainData data = terrain.terrainData;
        if (data.treeInstanceCount > 0 || data.treePrototypes.Length > 0)
        {
            data.SetTreeInstances(new TreeInstance[0], false);
            data.treePrototypes = new TreePrototype[0];
        }
    }

    // ---- Ribbons -----------------------------------------------------------

    int BuildRibbons(Terrain terrain, Transform root, string groupName,
        Material mat, float lift, Fea[] feas, Func<Fea, float> widthOf)
    {
        if (feas == null)
            return 0;
        var group = new GameObject(groupName).transform;
        group.SetParent(root, false);
        var verts = new List<Vector3>();
        var tris = new List<int>();
        int chunk = 0, built = 0;

        foreach (Fea f in feas)
        {
            List<Vector3> line = Resample(Unflatten(f.p), sampleStep);
            if (line.Count < 2)
                continue;
            float hw = widthOf(f) * 0.5f;
            int start = verts.Count;
            for (int i = 0; i < line.Count; i++)
            {
                Vector3 dir = (line[Mathf.Min(i + 1, line.Count - 1)]
                    - line[Mathf.Max(i - 1, 0)]);
                dir.y = 0f;
                if (dir.sqrMagnitude < 1e-6f)
                    dir = Vector3.forward;
                dir.Normalize();
                var perp = new Vector3(-dir.z, 0f, dir.x) * hw;
                verts.Add(Drape(terrain, line[i] - perp, lift));
                verts.Add(Drape(terrain, line[i] + perp, lift));
            }
            for (int i = 0; i < line.Count - 1; i++)
            {
                // Winding order matters and is easy to get mirrored (the
                // LandPlot lesson: a downward face is unhoverable AND
                // invisible from above): with -perp added before +perp,
                // THIS order faces up — verified by reading the baked
                // normals back (a mirrored order shipped once and the
                // whole road network was backface-culled from above).
                int a = start + i * 2;
                tris.AddRange(new[] { a, a + 1, a + 2, a + 1, a + 3, a + 2 });
            }
            built++;
            if (verts.Count > MaxChunkVerts)
                Flush(group, mat, groupName, ref chunk, verts, tris);
        }
        Flush(group, mat, groupName, ref chunk, verts, tris);
        return built;
    }

    void Flush(Transform group, Material mat, string groupName, ref int chunk,
        List<Vector3> verts, List<int> tris)
    {
        if (verts.Count == 0)
            return;
        var mesh = new Mesh
        {
            name = groupName + "Chunk" + chunk,
            indexFormat = UnityEngine.Rendering.IndexFormat.UInt32,
        };
        mesh.SetVertices(verts);
        mesh.SetTriangles(tris, 0);
        mesh.RecalculateNormals();
        mesh.RecalculateBounds();
        SaveMeshAsset(mesh);
        var go = new GameObject(mesh.name);
        go.transform.SetParent(group, false);
        go.AddComponent<MeshFilter>().sharedMesh = mesh;
        var rend = go.AddComponent<MeshRenderer>();
        rend.sharedMaterial = mat;
        rend.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
        chunk++;
        verts.Clear();
        tris.Clear();
    }

    static List<Vector3> Unflatten(float[] p)
    {
        var pts = new List<Vector3>(p.Length / 2);
        for (int i = 0; i + 1 < p.Length; i += 2)
            pts.Add(new Vector3(p[i], 0f, p[i + 1]));
        return pts;
    }

    // Every original vertex survives (a winding lane keeps its shape); long
    // segments are subdivided so the ribbon follows the ground between them.
    static List<Vector3> Resample(List<Vector3> pts, float step)
    {
        var outPts = new List<Vector3>();
        for (int i = 0; i < pts.Count - 1; i++)
        {
            Vector3 a = pts[i], b = pts[i + 1];
            int n = Mathf.Max(1, Mathf.CeilToInt((b - a).magnitude / step));
            for (int k = 0; k < n; k++)
                outPts.Add(Vector3.Lerp(a, b, (float)k / n));
        }
        if (pts.Count > 0)
            outPts.Add(pts[pts.Count - 1]);
        return outPts;
    }

    static Vector3 Drape(Terrain terrain, Vector3 p, float lift)
    {
        float y = terrain.SampleHeight(p) + terrain.transform.position.y + lift;
        return new Vector3(p.x, y, p.z);
    }

    // ---- Trees -------------------------------------------------------------

    int ScatterTrees(Terrain terrain, FeaSet set, HashSet<(int, int)> roadCells)
    {
        GameObject oak = EnsureTreePrefab(true);
        GameObject bush = EnsureTreePrefab(false);
        if (oak == null || bush == null)
            return 0;
        TerrainData data = terrain.terrainData;
        data.treePrototypes = new[]
        {
            new TreePrototype { prefab = oak },
            new TreePrototype { prefab = bush },
        };

        Vector3 origin = terrain.transform.position;
        Vector3 size = data.size;
        var trees = new List<TreeInstance>();

        void Add(float x, float z, int proto, float s)
        {
            float nx = (x - origin.x) / size.x;
            float nz = (z - origin.z) / size.z;
            if (nx < 0f || nx > 1f || nz < 0f || nz > 1f)
                return;
            trees.Add(new TreeInstance
            {
                position = new Vector3(nx, 0f, nz),
                widthScale = s,
                heightScale = s,
                rotation = Hash01(x, z, 5) * Mathf.PI * 2f,
                color = Color.white,
                lightmapColor = Color.white,
                prototypeIndex = proto,
            });
        }

        bool NearRoad(float x, float z)
        {
            int cx = Mathf.FloorToInt(x / RoadCell);
            int cz = Mathf.FloorToInt(z / RoadCell);
            for (int dx = -1; dx <= 1; dx++)
                for (int dz = -1; dz <= 1; dz++)
                    if (roadCells.Contains((cx + dx, cz + dz)))
                        return true;
            return false;
        }

        if (set.woods != null)
            foreach (Fea wood in set.woods)
            {
                List<Vector3> poly = Unflatten(wood.p);
                float minX = float.MaxValue, maxX = float.MinValue;
                float minZ = float.MaxValue, maxZ = float.MinValue;
                foreach (Vector3 v in poly)
                {
                    minX = Mathf.Min(minX, v.x); maxX = Mathf.Max(maxX, v.x);
                    minZ = Mathf.Min(minZ, v.z); maxZ = Mathf.Max(maxZ, v.z);
                }
                ScatterInPoly(poly, minX, maxX, minZ, maxZ, treeSpacing, 11,
                    (x, z, h) =>
                    {
                        if (!NearRoad(x, z))
                            Add(x, z, 0, 0.75f + h * 0.6f);
                    });
                ScatterInPoly(poly, minX, maxX, minZ, maxZ, bushSpacing, 23,
                    (x, z, h) =>
                    {
                        if (h < 0.5f && !NearRoad(x, z))
                            Add(x, z, 1, 0.8f + h);
                    });
            }

        // Hedging: bushes strung loosely along lanes and through-roads —
        // cheap, and it reads as the Marches' hedgerow lattice.
        if (set.roads != null)
            foreach (Fea r in set.roads)
            {
                if (r.c > 1)
                    continue;
                float off = roadWidths[r.c] * 0.5f + 1.5f;
                List<Vector3> line = Resample(Unflatten(r.p), roadsideBushStep);
                for (int i = 1; i < line.Count - 1; i++)
                {
                    float h = Hash01(line[i].x, line[i].z, 31);
                    if (h > 0.45f)
                        continue;
                    Vector3 dir = line[i + 1] - line[i - 1];
                    dir.y = 0f;
                    if (dir.sqrMagnitude < 1e-6f)
                        continue;
                    dir.Normalize();
                    var perp = new Vector3(-dir.z, 0f, dir.x)
                        * (h < 0.225f ? off : -off);
                    Vector3 p = line[i] + perp;
                    Add(p.x, p.z, 1, 0.7f + h);
                }
            }

        if (trees.Count > maxTreeInstances)
        {
            int before = trees.Count;
            var thinned = new List<TreeInstance>(maxTreeInstances);
            float keep = (float)maxTreeInstances / trees.Count;
            for (int i = 0; i < trees.Count; i++)
                if (Hash01(i, 0, 41) < keep)
                    thinned.Add(trees[i]);
            trees = thinned;
            Debug.LogWarning($"Marches dressing: thinned {before} tree instances to {trees.Count} (cap {maxTreeInstances}).");
        }
        data.SetTreeInstances(trees.ToArray(), true);
        // Mesh trees don't billboard (the Nature-shader console warning is
        // that fact, not a bug) — distance culling is the whole perf story,
        // and THREE dials govern it. treeBillboardDistance defaults to 50m
        // and treeMaximumFullLODCount to 50: past either limit Unity swaps
        // to billboards, which a plain mesh prefab doesn't have, so trees
        // just vanished ~50m out (the user's first hand-test caught it).
        terrain.treeDistance = 2500f;
        terrain.treeBillboardDistance = 2500f;
        terrain.treeCrossFadeLength = 30f;
        terrain.treeMaximumFullLODCount = 200000;
        return trees.Count;
    }

    void ScatterInPoly(List<Vector3> poly, float minX, float maxX,
        float minZ, float maxZ, float spacing, int salt,
        Action<float, float, float> emit)
    {
        int i0 = Mathf.FloorToInt(minX / spacing), i1 = Mathf.CeilToInt(maxX / spacing);
        int j0 = Mathf.FloorToInt(minZ / spacing), j1 = Mathf.CeilToInt(maxZ / spacing);
        for (int i = i0; i <= i1; i++)
            for (int j = j0; j <= j1; j++)
            {
                float x = i * spacing + (Hash01(i, j, salt) - 0.5f) * spacing * 0.9f;
                float z = j * spacing + (Hash01(i, j, salt + 1) - 0.5f) * spacing * 0.9f;
                if (SlabTile.Contains(poly, new Vector3(x, 0f, z)))
                    emit(x, z, Hash01(i, j, salt + 2));
            }
    }

    void MarkRoadCells(HashSet<(int, int)> cells, float[] p, int radius)
    {
        List<Vector3> line = Resample(Unflatten(p), RoadCell * 0.8f);
        foreach (Vector3 v in line)
        {
            int cx = Mathf.FloorToInt(v.x / RoadCell);
            int cz = Mathf.FloorToInt(v.z / RoadCell);
            for (int dx = -radius + 1; dx < radius; dx++)
                for (int dz = -radius + 1; dz < radius; dz++)
                    cells.Add((cx + dx, cz + dz));
        }
    }

    // Deterministic scatter noise — the LandParcels convention: Random would
    // tie the bake to whatever else rolled dice that frame.
    static float Hash01(float a, float b, int salt)
    {
        uint h = (uint)(Mathf.RoundToInt(a * 8f) * 73856093
            ^ Mathf.RoundToInt(b * 8f) * 19349663 ^ salt * 83492791);
        h ^= h >> 13; h *= 0x5bd1e995; h ^= h >> 15;
        return (h & 0xffffff) / (float)0x1000000;
    }

    // ---- Assets ------------------------------------------------------------

    // Graybox oak/bush built from primitive meshes, combined to one mesh with
    // two submeshes, saved as a prefab asset — terrain trees need a persisted
    // prefab, and the oak's trunk gets a collider (TerrainCollider bakes tree
    // colliders from the prototype, so the walker can't ghost through it).
    GameObject EnsureTreePrefab(bool oak)
    {
#if UNITY_EDITOR
        string path = DressFolder + (oak ? "/GrayboxOak.prefab" : "/GrayboxBush.prefab");
        var existing = AssetDatabase.LoadAssetAtPath<GameObject>(path);
        if (existing != null)
            return existing;
        EnsureFolder();

        // Hand-built low-poly geometry, NOT primitives: the primitive sphere
        // is ~760 tris, and a quarter-million instances of that is a couple
        // hundred million triangles. An icosahedron blob + prism trunk is
        // ~60 tris per oak.
        var combines = new List<CombineInstance>();
        if (oak)
            combines.Add(new CombineInstance
            {
                mesh = TrunkMesh(0.3f, 3.4f, 6),
                transform = Matrix4x4.identity,
            });
        combines.Add(new CombineInstance
        {
            mesh = oak
                ? CanopyMesh(new Vector3(0f, 4.6f, 0f), new Vector3(2.6f, 2.1f, 2.6f), 3)
                : CanopyMesh(new Vector3(0f, 0.55f, 0f), new Vector3(0.85f, 0.6f, 0.85f), 9),
            transform = Matrix4x4.identity,
        });
        var mesh = new Mesh { name = oak ? "GrayboxOakMesh" : "GrayboxBushMesh" };
        mesh.CombineMeshes(combines.ToArray(), false, true);
        mesh.RecalculateBounds();
        AssetDatabase.CreateAsset(mesh, DressFolder + "/" + mesh.name + ".asset");

        Material bark = LoadOrCreateMaterial("BarkMat", new Color(0.32f, 0.24f, 0.17f), 0.2f);
        Material leaf = LoadOrCreateMaterial(oak ? "LeafMat" : "BushLeafMat",
            oak ? new Color(0.22f, 0.36f, 0.16f) : new Color(0.26f, 0.38f, 0.20f), 0.25f);

        var go = new GameObject(oak ? "GrayboxOak" : "GrayboxBush");
        go.AddComponent<MeshFilter>().sharedMesh = mesh;
        var rend = go.AddComponent<MeshRenderer>();
        rend.sharedMaterials = oak ? new[] { bark, leaf } : new[] { leaf };
        if (oak)
        {
            var cap = go.AddComponent<CapsuleCollider>();
            cap.center = new Vector3(0f, 1.5f, 0f);
            cap.height = 3f;
            cap.radius = 0.35f;
        }
        GameObject prefab = PrefabUtility.SaveAsPrefabAsset(go, path);
        DestroyImmediate(go);
        return prefab;
#else
        return null;
#endif
    }

#if UNITY_EDITOR
    // Icosahedron stretched to the given radii, vertices jittered by hash so
    // no two prototypes read identical. Shared verts + RecalculateNormals =
    // a soft-shaded blob, which is what distant foliage wants.
    static Mesh CanopyMesh(Vector3 center, Vector3 radii, int salt)
    {
        float t = (1f + Mathf.Sqrt(5f)) / 2f;
        Vector3[] ico =
        {
            new(-1, t, 0), new(1, t, 0), new(-1, -t, 0), new(1, -t, 0),
            new(0, -1, t), new(0, 1, t), new(0, -1, -t), new(0, 1, -t),
            new(t, 0, -1), new(t, 0, 1), new(-t, 0, -1), new(-t, 0, 1),
        };
        int[] tris =
        {
            0,11,5, 0,5,1, 0,1,7, 0,7,10, 0,10,11,
            1,5,9, 5,11,4, 11,10,2, 10,7,6, 7,1,8,
            3,9,4, 3,4,2, 3,2,6, 3,6,8, 3,8,9,
            4,9,5, 2,4,11, 6,2,10, 8,6,7, 9,8,1,
        };
        var verts = new Vector3[12];
        for (int i = 0; i < 12; i++)
        {
            float jitter = 0.85f + Hash01(i, salt, 7) * 0.3f;
            verts[i] = center + Vector3.Scale(ico[i].normalized, radii) * jitter;
        }
        var m = new Mesh { vertices = verts, triangles = tris };
        m.RecalculateNormals();
        m.RecalculateBounds();
        return m;
    }

    // Open-ended tapered prism — the canopy hides the top, the ground the
    // bottom.
    static Mesh TrunkMesh(float radius, float height, int sides)
    {
        var verts = new List<Vector3>();
        var tris = new List<int>();
        for (int i = 0; i < sides; i++)
        {
            float a0 = i * 2f * Mathf.PI / sides;
            float a1 = (i + 1) * 2f * Mathf.PI / sides;
            int b = verts.Count;
            verts.Add(new Vector3(Mathf.Cos(a0) * radius, 0f, Mathf.Sin(a0) * radius));
            verts.Add(new Vector3(Mathf.Cos(a1) * radius, 0f, Mathf.Sin(a1) * radius));
            verts.Add(new Vector3(Mathf.Cos(a0) * radius * 0.7f, height, Mathf.Sin(a0) * radius * 0.7f));
            verts.Add(new Vector3(Mathf.Cos(a1) * radius * 0.7f, height, Mathf.Sin(a1) * radius * 0.7f));
            tris.AddRange(new[] { b, b + 2, b + 1, b + 1, b + 2, b + 3 });
        }
        var m = new Mesh();
        m.SetVertices(verts);
        m.SetTriangles(tris, 0);
        m.RecalculateNormals();
        m.RecalculateBounds();
        return m;
    }
#endif

    Material LoadOrCreateMaterial(string name, Color color, float smoothness)
    {
#if UNITY_EDITOR
        string path = DressFolder + "/" + name + ".mat";
        Material mat = AssetDatabase.LoadAssetAtPath<Material>(path);
        if (mat == null)
        {
            EnsureFolder();
            mat = new Material(Shader.Find("HDRP/Lit")) { name = name };
            AssetDatabase.CreateAsset(mat, path);
        }
#else
        Material mat = new Material(Shader.Find("HDRP/Lit")) { name = name };
#endif
        mat.SetColor("_BaseColor", color);
        mat.SetFloat("_Smoothness", smoothness);
        // A quarter-million tree instances live or die by instancing.
        mat.enableInstancing = true;
        return mat;
    }

    void SaveMeshAsset(Mesh mesh)
    {
#if UNITY_EDITOR
        EnsureFolder();
        string path = DressFolder + "/" + mesh.name + ".asset";
        AssetDatabase.DeleteAsset(path);
        AssetDatabase.CreateAsset(mesh, path);
#endif
    }

#if UNITY_EDITOR
    static void EnsureFolder()
    {
        if (!AssetDatabase.IsValidFolder(DressFolder))
            AssetDatabase.CreateFolder("Assets/Terrain", "Dressing");
    }
#endif
}
