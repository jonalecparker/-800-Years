using UnityEngine;
#if UNITY_EDITOR
using UnityEditor;
#endif

// Bakes the countryside into a Unity Terrain: rolling fields from layered
// noise, with a designed central hill whose flattened top gives the castle
// a buildable site. Deterministic for a given seed — tweak the numbers and
// re-run Generate (component context menu) to iterate on the landscape.
// The heightmap is procedural but the Terrain it writes into is a normal
// scene terrain: hand-sculpting afterwards, or swapping in a real-world
// DEM heightmap later, both work without touching any other system.
[RequireComponent(typeof(Terrain), typeof(TerrainCollider))]
public class TerrainGenerator : MonoBehaviour
{
    [Header("Dimensions")]
    public float terrainSize = 800f;
    public float terrainMaxHeight = 80f;
    public int heightmapResolution = 1025;

    [Header("Countryside")]
    public float baseElevation = 14f;
    // Total relief of the open fields and the size of their features —
    // long, gentle wavelengths read as rural Europe rather than badlands.
    public float rollingAmplitude = 12f;
    public float rollingWavelength = 150f;
    public float detailAmplitude = 0.5f;
    public float detailWavelength = 16f;
    public int seed = 800;

    [Header("Central Hill")]
    public float hillHeight = 40f;
    public float plateauRadius = 45f;
    public float hillBaseRadius = 150f;
    // Wobbles the hill's radius by angle so its footprint is organic
    // rather than a perfect circle.
    [Range(0f, 0.5f)] public float hillEdgeNoise = 0.15f;
    // How strongly the field noise is damped on the hill top, keeping the
    // plateau gently uneven but buildable.
    [Range(0f, 1f)] public float plateauCalm = 0.85f;

    [Header("Surface")]
    public Texture2D grassAlbedo;
    public Texture2D grassNormal;
    public Texture2D dirtAlbedo;
    public Texture2D dirtNormal;
    public float textureTileSize = 10f;
    // Dirt shows through the grass as slopes steepen, so worn ground reads
    // exactly where terrain gets too steep to farm.
    public float dirtSlopeStart = 24f;
    public float dirtSlopeFull = 38f;
    public int alphamapResolution = 512;

    const string AssetFolder = "Assets/Terrain";

    [ContextMenu("Generate")]
    public void Generate()
    {
        Terrain terrain = GetComponent<Terrain>();
        TerrainCollider terrainCollider = GetComponent<TerrainCollider>();

        TerrainData data = terrain.terrainData;
        if (data == null)
        {
            data = new TerrainData();
#if UNITY_EDITOR
            EnsureAssetFolder();
            AssetDatabase.CreateAsset(data, AssetFolder + "/CountrysideTerrain.asset");
#endif
            terrain.terrainData = data;
        }
        terrainCollider.terrainData = data;

        data.heightmapResolution = heightmapResolution;
        data.size = new Vector3(terrainSize, terrainMaxHeight, terrainSize);

        float half = terrainSize * 0.5f;
        float[,] heights = new float[heightmapResolution, heightmapResolution];
        for (int iz = 0; iz < heightmapResolution; iz++)
        {
            for (int ix = 0; ix < heightmapResolution; ix++)
            {
                float wx = (float)ix / (heightmapResolution - 1) * terrainSize - half;
                float wz = (float)iz / (heightmapResolution - 1) * terrainSize - half;
                heights[iz, ix] = Mathf.Clamp01(ComputeHeight(wx, wz) / terrainMaxHeight);
            }
        }
        data.SetHeights(0, 0, heights);

        ApplySurface(data);
        ApplyMaterial(terrain);

        // Terrain pivot is its corner; keep the hill centered on the world
        // origin, where the build systems and camera already live.
        transform.position = new Vector3(-half, 0f, -half);
        terrain.Flush();

#if UNITY_EDITOR
        EditorUtility.SetDirty(terrain);
        EditorUtility.SetDirty(data);
        AssetDatabase.SaveAssets();
#endif
    }

    float ComputeHeight(float x, float z)
    {
        float rolling = Fbm(x, z, rollingWavelength, 3, seed) * rollingAmplitude;
        float detail = Fbm(x, z, detailWavelength, 2, seed + 71) * detailAmplitude;

        // Radial hill profile: flat inside the plateau, smooth shoulder out
        // to the base radius, with the radius itself wobbled by angle.
        float r = Mathf.Sqrt(x * x + z * z);
        float invR = r > 0.001f ? 1f / r : 0f;
        float wobble = Mathf.PerlinNoise(
            seed * 0.731f + 37.2f + x * invR * 1.7f,
            seed * 0.517f + 11.9f + z * invR * 1.7f);
        float radiusScale = 1f + (wobble - 0.5f) * 2f * hillEdgeNoise;
        float shapedR = r / Mathf.Max(0.5f, radiusScale);

        float hillT = 1f - Mathf.SmoothStep(0f, 1f, Mathf.InverseLerp(plateauRadius, hillBaseRadius, shapedR));
        float calm = 1f - plateauCalm * hillT;

        return baseElevation + rolling * calm + detail + hillHeight * hillT;
    }

    // Standard octave-stacked Perlin in [-1, 1], offset well away from the
    // origin to dodge PerlinNoise's mirror artifacts around zero.
    float Fbm(float x, float z, float wavelength, int octaves, int noiseSeed)
    {
        float sum = 0f, amplitude = 1f, norm = 0f;
        float frequency = 1f / Mathf.Max(0.001f, wavelength);
        for (int i = 0; i < octaves; i++)
        {
            float n = Mathf.PerlinNoise(
                (x + 10000f) * frequency + noiseSeed * 0.7211f,
                (z + 10000f) * frequency + noiseSeed * 0.3391f);
            sum += (n - 0.5f) * 2f * amplitude;
            norm += amplitude;
            amplitude *= 0.5f;
            frequency *= 2f;
        }
        return sum / norm;
    }

    void ApplySurface(TerrainData data)
    {
        TerrainLayer grass = LoadOrCreateLayer("GrassLayer");
        grass.diffuseTexture = grassAlbedo != null ? grassAlbedo : Texture2D.whiteTexture;
        grass.normalMapTexture = grassNormal;
        grass.tileSize = Vector2.one * textureTileSize;
        if (grassAlbedo == null)
            grass.diffuseRemapMax = new Color(0.33f, 0.42f, 0.22f);

        TerrainLayer dirt = LoadOrCreateLayer("DirtLayer");
        dirt.diffuseTexture = dirtAlbedo != null ? dirtAlbedo : Texture2D.whiteTexture;
        dirt.normalMapTexture = dirtNormal;
        dirt.tileSize = Vector2.one * textureTileSize;
        if (dirtAlbedo == null)
            dirt.diffuseRemapMax = new Color(0.42f, 0.33f, 0.24f);

        data.terrainLayers = new[] { grass, dirt };

        int res = alphamapResolution;
        data.alphamapResolution = res;
        float[,,] alphas = new float[res, res, 2];
        for (int iz = 0; iz < res; iz++)
        {
            for (int ix = 0; ix < res; ix++)
            {
                float steepness = data.GetSteepness((float)ix / (res - 1), (float)iz / (res - 1));
                float dirtWeight = Mathf.InverseLerp(dirtSlopeStart, dirtSlopeFull, steepness);
                alphas[iz, ix, 0] = 1f - dirtWeight;
                alphas[iz, ix, 1] = dirtWeight;
            }
        }
        data.SetAlphamaps(0, 0, alphas);
    }

    TerrainLayer LoadOrCreateLayer(string name)
    {
#if UNITY_EDITOR
        string path = AssetFolder + "/" + name + ".terrainlayer";
        TerrainLayer layer = AssetDatabase.LoadAssetAtPath<TerrainLayer>(path);
        if (layer == null)
        {
            EnsureAssetFolder();
            layer = new TerrainLayer();
            AssetDatabase.CreateAsset(layer, path);
        }
        return layer;
#else
        return new TerrainLayer();
#endif
    }

    void ApplyMaterial(Terrain terrain)
    {
        Shader shader = Shader.Find("HDRP/TerrainLit");
        if (shader == null)
            return;
#if UNITY_EDITOR
        string path = AssetFolder + "/CountrysideTerrainMat.mat";
        Material mat = AssetDatabase.LoadAssetAtPath<Material>(path);
        if (mat == null)
        {
            EnsureAssetFolder();
            mat = new Material(shader);
            AssetDatabase.CreateAsset(mat, path);
        }
        terrain.materialTemplate = mat;
#else
        terrain.materialTemplate = new Material(shader);
#endif
    }

#if UNITY_EDITOR
    void EnsureAssetFolder()
    {
        if (!AssetDatabase.IsValidFolder(AssetFolder))
            AssetDatabase.CreateFolder("Assets", "Terrain");
    }
#endif
}
