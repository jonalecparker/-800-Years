using System.Collections.Generic;
using UnityEngine;

// The castle-site LIDAR bake (Docs/CastleLidar.md): patches real NRW
// 2m DTM ground into the terrain tiles around each castle site. An
// edit-mode step like CarveRivers — run AFTER Split (re-running Split
// is the un-patch), writes the tile heightmaps in place. The ASC
// source files live in TerrainSources/lidar (outside Assets; the
// brief records the repeatable download query).
//
// Honesty notes:
// - The tiles hold ~7.3m samples, so the 2m data is DOWNSAMPLED for
//   now — the earthworks' shape arrives, not 2m mesh density. A
//   castle-tile resolution bump is phase 2 (LOD seam work).
// - The patch blends to the BASE over featherBand, after fitting a
//   constant vertical offset over that ring — the world's datum wins,
//   the LIDAR provides shape.
// - Waterways keep their carved beds (the water ribbons and refs were
//   baked from them): the patch weight fades to zero near water.
public class CastleLidar : MonoBehaviour
{
    [Tooltip("Folder of NRW 2m DTM zips (ASC tiles, BNG, mm units), relative to the project root.")]
    public string lidarFolder = "TerrainSources/lidar";
    [Tooltip("Full-strength LIDAR inside patchRadius - featherBand of each site; blended to base out to patchRadius.")]
    public float patchRadius = 600f;
    public float featherBand = 150f;
    [Tooltip("Patch weight is zero within (half width + this) of a waterway centerline and full a further 25m out — the carved beds stay.")]
    public float waterMargin = 10f;

    [ContextMenu("Apply Lidar Patches")]
    public void ApplyLidarPatches()
    {
        if (Application.isPlaying)
        {
            Debug.LogWarning("CastleLidar is an edit-mode bake — stop play first.");
            return;
        }
        var sites = new List<Vector2>();
        var names = new List<string>();
        foreach (CastleSite s in FindObjectsByType<CastleSite>(
            FindObjectsInactive.Include, FindObjectsSortMode.None))
        {
            sites.Add(s.PositionXZ);
            names.Add(s.SiteName);
        }
        if (sites.Count == 0)
        {
            Debug.LogWarning("CastleLidar: no CastleSite markers in the scene.");
            return;
        }

        string folder = System.IO.Path.Combine(
            System.IO.Path.GetDirectoryName(Application.dataPath), lidarFolder);
        var bngSites = new List<Vector2>();
        foreach (Vector2 s in sites)
            bngSites.Add(Geo.WorldToBng(s));
        var asc = new AscSet(folder, bngSites, patchRadius + 100f);

        List<(float[] p, float w)> waters = LandFeatures.WaterLines();

        // Active tiles only — the deactivated source terrain is the
        // re-bake authority and must keep its unpatched heights.
        Terrain[] tiles = Terrain.activeTerrains;
        if (tiles.Length == 0)
        {
            Debug.LogWarning("CastleLidar: no active terrain tiles.");
            return;
        }

        // Pass 1: the datum offset per site — mean (base − lidar) over
        // the feather ring, so the blend meets the base on average and
        // a vertical datum disagreement becomes a smooth ramp, not a
        // cliff at the patch edge.
        var offSum = new double[sites.Count];
        var offCount = new int[sites.Count];
        foreach (Terrain t in tiles)
            ForEachSample(t, sites, (ix, iz, world, si, d, baseY, data, heights) =>
            {
                if (d < patchRadius - featherBand || d > patchRadius)
                    return false;
                float lidar = asc.Sample(world, out bool ok);
                if (!ok)
                    return false;
                offSum[si] += baseY - lidar;
                offCount[si]++;
                return false;
            });
        var offset = new float[sites.Count];
        for (int i = 0; i < sites.Count; i++)
            offset[i] = offCount[i] > 0
                ? (float)(offSum[i] / offCount[i]) : 0f;

        // Pass 2: apply.
        int patched = 0, tilesTouched = 0;
        foreach (Terrain t in tiles)
        {
            bool touched = false;
            TerrainData data = null;
            float[,] heights = null;
            ForEachSample(t, sites, (ix, iz, world, si, d, baseY, dta, hts) =>
            {
                float w = d <= patchRadius - featherBand ? 1f
                    : 1f - (d - (patchRadius - featherBand)) / featherBand;
                w *= WaterFactor(waters, world);
                if (w <= 0f)
                    return false;
                float lidar = asc.Sample(world, out bool ok);
                if (!ok)
                    return false;
                float newY = Mathf.Lerp(baseY, lidar + offset[si], w);
                Vector3 pos = t.transform.position;
                hts[iz, ix] = Mathf.Clamp01(
                    (newY - pos.y) / dta.size.y);
                data = dta;
                heights = hts;
                touched = true;
                patched++;
                return true;
            });
            if (!touched)
                continue;
            tilesTouched++;
            data.SetHeights(0, 0, heights);
            // Terrain trees store a NORMALIZED height — reseat the
            // ones standing on reshaped ground or they float/sink.
            TreeInstance[] trees = data.treeInstances;
            for (int i = 0; i < trees.Length; i++)
                trees[i].position.y = data.GetInterpolatedHeight(
                    trees[i].position.x, trees[i].position.z) / data.size.y;
            data.SetTreeInstances(trees, false);
#if UNITY_EDITOR
            UnityEditor.EditorUtility.SetDirty(data);
#endif
        }
        for (int i = 0; i < sites.Count; i++)
            Debug.Log($"CastleLidar: {names[i]} datum offset"
                + $" {offset[i]:0.00}m over {offCount[i]} ring samples.");
        Debug.Log($"CastleLidar: patched {patched} samples across"
            + $" {tilesTouched} tiles from {asc.TilesLoaded} ASC tiles."
            + " Re-run TerrainTiler.Split to undo.");
    }

    // Walks every heightmap sample of a tile that lies within
    // patchRadius of some site; the callback returns true if it wrote.
    delegate bool SampleFn(int ix, int iz, Vector2 world, int site,
        float d, float baseY, TerrainData data, float[,] heights);

    void ForEachSample(Terrain t, List<Vector2> sites, SampleFn fn)
    {
        TerrainData data = t.terrainData;
        Vector3 pos = t.transform.position;
        Vector3 size = data.size;
        // Skip tiles that no patch circle touches.
        var tileRect = new Rect(pos.x, pos.z, size.x, size.z);
        bool near = false;
        foreach (Vector2 s in sites)
            if (RectDistance(tileRect, s) <= patchRadius)
                near = true;
        if (!near)
            return;
        int res = data.heightmapResolution;
        float[,] heights = data.GetHeights(0, 0, res, res);
        for (int iz = 0; iz < res; iz++)
            for (int ix = 0; ix < res; ix++)
            {
                var world = new Vector2(
                    pos.x + size.x * ix / (res - 1),
                    pos.z + size.z * iz / (res - 1));
                int best = -1;
                float bestD = float.MaxValue;
                for (int si = 0; si < sites.Count; si++)
                {
                    float d = (sites[si] - world).magnitude;
                    if (d < bestD)
                    {
                        bestD = d;
                        best = si;
                    }
                }
                if (bestD > patchRadius)
                    continue;
                float baseY = pos.y + heights[iz, ix] * size.y;
                fn(ix, iz, world, best, bestD, baseY, data, heights);
            }
    }

    static float RectDistance(Rect r, Vector2 p)
    {
        float dx = Mathf.Max(r.xMin - p.x, 0f, p.x - r.xMax);
        float dy = Mathf.Max(r.yMin - p.y, 0f, p.y - r.yMax);
        return Mathf.Sqrt(dx * dx + dy * dy);
    }

    float WaterFactor(List<(float[] p, float w)> waters, Vector2 c)
    {
        float factor = 1f;
        foreach ((float[] p, float w) in waters)
        {
            float keep = w * 0.5f + waterMargin;
            for (int i = 0; i + 3 < p.Length; i += 2)
            {
                var a = new Vector2(p[i], p[i + 1]);
                // Cheap reject: both ends far outside the ramp.
                if (Mathf.Abs(a.x - c.x) > keep + 27f
                    && Mathf.Abs(p[i + 2] - c.x) > keep + 27f)
                    continue;
                var ab = new Vector2(p[i + 2], p[i + 3]) - a;
                float t = Mathf.Clamp01(Vector2.Dot(c - a, ab)
                    / Mathf.Max(0.0001f, ab.sqrMagnitude));
                float d = (a + ab * t - c).magnitude;
                if (d >= keep + 25f)
                    continue;
                factor = Mathf.Min(factor,
                    Mathf.Clamp01((d - keep) / 25f));
                if (factor <= 0f)
                    return 0f;
            }
        }
        return factor;
    }

    // ---- The ASC tile set ----
    // NRW archive DTM: 2×2km Esri ASCII grids in the zips, EPSG:27700,
    // heights in MILLIMETRES. Indexed by 2km cell, loaded on demand.
    class AscSet
    {
        class Tile
        {
            public float xll, yll, cell;
            public int ncols, nrows;
            public float[] h; // meters; NaN = NODATA
        }

        readonly Dictionary<(int, int), Tile> tiles
            = new Dictionary<(int, int), Tile>();
        public int TilesLoaded => tiles.Count;

        // Loose .asc files, extracted from the NRW zips (Mono's
        // ZipArchive refuses the archive's compression — extract with
        // PowerShell's Expand-Archive; the zips stay as the source of
        // record). Only tiles a patch can reach are read past their
        // headers — ~100 tiles of 1M text values each would take
        // minutes to parse for ground nothing uses.
        public AscSet(string folder, List<Vector2> bngSites, float reach)
        {
            if (!System.IO.Directory.Exists(folder))
            {
                Debug.LogWarning("CastleLidar: no lidar folder at " + folder);
                return;
            }
            foreach (string path in System.IO.Directory.GetFiles(
                folder, "*.asc", System.IO.SearchOption.AllDirectories))
            {
                using var reader = new System.IO.StreamReader(path);
                Tile t = ParseHeader(reader, out float nodata);
                if (t == null)
                    continue;
                var r = new Rect(t.xll, t.yll,
                    t.ncols * t.cell, t.nrows * t.cell);
                bool wanted = false;
                foreach (Vector2 s in bngSites)
                    if (RectDistance(r, s) <= reach)
                        wanted = true;
                if (!wanted)
                    continue;
                ParseData(reader, t, nodata);
                tiles[(Mathf.FloorToInt(t.xll / 2000f),
                    Mathf.FloorToInt(t.yll / 2000f))] = t;
            }
        }

        static Tile ParseHeader(System.IO.StreamReader r, out float nodata)
        {
            var t = new Tile();
            nodata = -9999f;
            for (int i = 0; i < 6; i++)
            {
                string[] kv = r.ReadLine().Split(
                    (char[])null, System.StringSplitOptions.RemoveEmptyEntries);
                switch (kv[0].ToLowerInvariant())
                {
                    case "ncols": t.ncols = int.Parse(kv[1]); break;
                    case "nrows": t.nrows = int.Parse(kv[1]); break;
                    case "xllcorner": t.xll = float.Parse(kv[1]); break;
                    case "yllcorner": t.yll = float.Parse(kv[1]); break;
                    case "cellsize": t.cell = float.Parse(kv[1]); break;
                    case "nodata_value": nodata = float.Parse(kv[1]); break;
                }
            }
            return t.ncols > 0 && t.nrows > 0 ? t : null;
        }

        static void ParseData(System.IO.StreamReader r, Tile t, float nodata)
        {
            t.h = new float[t.ncols * t.nrows];
            int n = 0;
            string line;
            while (n < t.h.Length && (line = r.ReadLine()) != null)
            {
                string[] vals = line.Split((char[])null,
                    System.StringSplitOptions.RemoveEmptyEntries);
                foreach (string v in vals)
                {
                    if (n >= t.h.Length)
                        break;
                    float mm = float.Parse(v);
                    // mm → m; NODATA → NaN.
                    t.h[n++] = mm == nodata ? float.NaN : mm / 1000f;
                }
            }
        }

        // Bilinear at a WORLD point (converted to BNG here) — clamped
        // within its 2km tile; a NaN corner invalidates the sample.
        public float Sample(Vector2 world, out bool ok)
        {
            ok = false;
            Vector2 bng = Geo.WorldToBng(world);
            var key = (Mathf.FloorToInt(bng.x / 2000f),
                Mathf.FloorToInt(bng.y / 2000f));
            if (!tiles.TryGetValue(key, out Tile t))
                return 0f;
            // Cell-center registration; row 0 is the NORTH edge.
            float fx = (bng.x - t.xll) / t.cell - 0.5f;
            float fy = (t.yll + t.nrows * t.cell - bng.y) / t.cell - 0.5f;
            int x0 = Mathf.Clamp(Mathf.FloorToInt(fx), 0, t.ncols - 2);
            int y0 = Mathf.Clamp(Mathf.FloorToInt(fy), 0, t.nrows - 2);
            float tx = Mathf.Clamp01(fx - x0);
            float ty = Mathf.Clamp01(fy - y0);
            float h00 = t.h[y0 * t.ncols + x0];
            float h10 = t.h[y0 * t.ncols + x0 + 1];
            float h01 = t.h[(y0 + 1) * t.ncols + x0];
            float h11 = t.h[(y0 + 1) * t.ncols + x0 + 1];
            if (float.IsNaN(h00) || float.IsNaN(h10)
                || float.IsNaN(h01) || float.IsNaN(h11))
                return 0f;
            ok = true;
            return Mathf.Lerp(Mathf.Lerp(h00, h10, tx),
                Mathf.Lerp(h01, h11, tx), ty);
        }
    }

    // ---- Georeferencing ----
    // World XZ → WGS84 via the verified linear Web Mercator map
    // (Docs/Territories.md: White Castle marker at world
    // (−4640.3, −6428.2) is 51.8459°N −2.9021°E, scale cos(51.89°)),
    // then WGS84 → OSGB36 by the standard small-angle Helmert, then
    // the Airy 1830 transverse Mercator (the OS projection). ~5m
    // absolute without the OSTN15 grid — under a heightmap sample.
    static class Geo
    {
        const double R = 6378137.0;
        const double AnchorLat = 51.8459, AnchorLon = -2.9021;
        const double AnchorX = -4640.3, AnchorZ = -6428.2;
        static readonly double K = System.Math.Cos(51.89 * Deg);
        const double Deg = System.Math.PI / 180.0;

        public static Vector2 WorldToBng(Vector2 world)
        {
            // world → mercator meters → WGS84
            double mx0 = R * AnchorLon * Deg - AnchorX / K;
            double my0 = R * System.Math.Log(System.Math.Tan(
                System.Math.PI / 4.0 + AnchorLat * Deg / 2.0))
                - AnchorZ / K;
            double mx = mx0 + world.x / K;
            double my = my0 + world.y / K;
            double lon = mx / R;
            double lat = 2.0 * System.Math.Atan(System.Math.Exp(my / R))
                - System.Math.PI / 2.0;

            // WGS84 → ECEF (h = 0)
            double a1 = 6378137.0, f1 = 1.0 / 298.257223563;
            double e21 = 2.0 * f1 - f1 * f1;
            double sinLat = System.Math.Sin(lat),
                cosLat = System.Math.Cos(lat);
            double nu1 = a1 / System.Math.Sqrt(1.0 - e21 * sinLat * sinLat);
            double x = nu1 * cosLat * System.Math.Cos(lon);
            double y = nu1 * cosLat * System.Math.Sin(lon);
            double z = nu1 * (1.0 - e21) * sinLat;

            // Helmert WGS84 → OSGB36 (small-angle)
            const double tx = -446.448, ty = 125.157, tz = -542.060;
            const double s = 20.4894e-6;
            const double sec = System.Math.PI / (180.0 * 3600.0);
            const double rx = -0.1502 * sec, ry = -0.2470 * sec,
                rz = -0.8421 * sec;
            double x2 = tx + (1.0 + s) * x - rz * y + ry * z;
            double y2 = ty + rz * x + (1.0 + s) * y - rx * z;
            double z2 = tz - ry * x + rx * y + (1.0 + s) * z;

            // ECEF → Airy 1830 lat/lon
            double a = 6377563.396, b = 6356256.909;
            double e2 = (a * a - b * b) / (a * a);
            double p = System.Math.Sqrt(x2 * x2 + y2 * y2);
            double lat2 = System.Math.Atan2(z2, p * (1.0 - e2));
            for (int i = 0; i < 5; i++)
            {
                double sl = System.Math.Sin(lat2);
                double nu2 = a / System.Math.Sqrt(1.0 - e2 * sl * sl);
                lat2 = System.Math.Atan2(z2 + e2 * nu2 * sl, p);
            }
            double lon2 = System.Math.Atan2(y2, x2);

            // Airy TM → BNG (the OS formulas)
            const double F0 = 0.9996012717;
            double lat0 = 49.0 * Deg, lon0 = -2.0 * Deg;
            const double E0 = 400000.0, N0 = -100000.0;
            double n = (a - b) / (a + b);
            double sin2 = System.Math.Sin(lat2),
                cos2 = System.Math.Cos(lat2),
                tan2 = System.Math.Tan(lat2);
            double nu = a * F0 / System.Math.Sqrt(1.0 - e2 * sin2 * sin2);
            double rho = a * F0 * (1.0 - e2)
                / System.Math.Pow(1.0 - e2 * sin2 * sin2, 1.5);
            double eta2 = nu / rho - 1.0;
            double dLat = lat2 - lat0, sLat = lat2 + lat0;
            double M = b * F0 * (
                (1.0 + n + 1.25 * n * n + 1.25 * n * n * n) * dLat
                - (3.0 * n + 3.0 * n * n + 2.625 * n * n * n)
                    * System.Math.Sin(dLat) * System.Math.Cos(sLat)
                + (1.875 * n * n + 1.875 * n * n * n)
                    * System.Math.Sin(2.0 * dLat) * System.Math.Cos(2.0 * sLat)
                - (35.0 / 24.0) * n * n * n
                    * System.Math.Sin(3.0 * dLat) * System.Math.Cos(3.0 * sLat));
            double I = M + N0;
            double II = nu / 2.0 * sin2 * cos2;
            double III = nu / 24.0 * sin2 * cos2 * cos2 * cos2
                * (5.0 - tan2 * tan2 + 9.0 * eta2);
            double IIIA = nu / 720.0 * sin2
                * System.Math.Pow(cos2, 5.0)
                * (61.0 - 58.0 * tan2 * tan2
                    + System.Math.Pow(tan2, 4.0));
            double IV = nu * cos2;
            double V = nu / 6.0 * cos2 * cos2 * cos2
                * (nu / rho - tan2 * tan2);
            double VI = nu / 120.0 * System.Math.Pow(cos2, 5.0)
                * (5.0 - 18.0 * tan2 * tan2 + System.Math.Pow(tan2, 4.0)
                    + 14.0 * eta2 - 58.0 * tan2 * tan2 * eta2);
            double dLon = lon2 - lon0;
            double north = I + II * dLon * dLon
                + III * System.Math.Pow(dLon, 4.0)
                + IIIA * System.Math.Pow(dLon, 6.0);
            double east = E0 + IV * dLon + V * dLon * dLon * dLon
                + VI * System.Math.Pow(dLon, 5.0);
            return new Vector2((float)east, (float)north);
        }
    }
}
