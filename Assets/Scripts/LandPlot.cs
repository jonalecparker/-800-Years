using System.Collections.Generic;
using UnityEngine;

public enum LandType { Farm = 0, Pasture = 1, Timber = 2, Living = 3 }
public enum LandOwner { Player = 0, Bandits = 1, Neighbor = 2 }

// A parcel of the countryside: a stored polygon with a nature, an owner
// and a place in the survey grid. Parcels are generated ONCE from the
// terrain (LandParcels) and are facts thereafter — reshaping the ground
// under one never re-classifies it, and every field here rides
// CastleSave (v6). The mesh is a translucent overlay draped on the
// ground, shown only while the Survey tool is in hand; its MeshCollider
// exists for that tool's cursor alone and is enabled only while shown —
// a survey map must never catch a mason's ray, and the walker must
// never trip on it. NOT under splineParent: the cutaway has no business
// slicing a map.
public class LandPlot : MonoBehaviour
{
    public static readonly List<LandPlot> All = new List<LandPlot>();

    // The outline, world XZ (y carries nothing), CCW.
    public readonly List<Vector3> verts = new List<Vector3>();
    public LandType type;
    public LandOwner owner;
    // Survey-grid coords; adjacency (one step apart) is what the
    // frontier rule reads. Stored, not re-derived from geometry.
    public int gx, gz;

    // The overlay floats this far above the sampled ground so it reads
    // through grass without hovering.
    const float Drape = 0.3f;
    // Triangles subdivide until edges are under this, so the sheet
    // follows the hill instead of knifing through it.
    const float MaxEdge = 8f;

    static Material overlayMat;
    static Transform root;

    MeshFilter filter;
    MeshRenderer rend;
    MeshCollider col;
    Mesh ownedMesh;
    bool highlighted;

    public float AreaM2 => Mathf.Abs(SlabTile.SignedArea(verts));

    public static LandPlot Create(List<Vector3> outline, LandType type,
        LandOwner owner, int gx, int gz)
    {
        var go = new GameObject("Plot " + gx + "," + gz);
        go.transform.SetParent(Root(), false);
        LandPlot plot = go.AddComponent<LandPlot>();
        foreach (Vector3 v in outline)
            plot.verts.Add(new Vector3(v.x, 0f, v.z));
        if (SlabTile.SignedArea(plot.verts) < 0f)
            plot.verts.Reverse();
        plot.type = type;
        plot.owner = owner;
        plot.gx = gx;
        plot.gz = gz;
        plot.filter = go.AddComponent<MeshFilter>();
        plot.rend = go.AddComponent<MeshRenderer>();
        plot.rend.sharedMaterial = OverlayMaterial();
        plot.rend.shadowCastingMode =
            UnityEngine.Rendering.ShadowCastingMode.Off;
        plot.col = go.AddComponent<MeshCollider>();
        plot.SetShown(false);
        return plot;
    }

    void OnEnable() { All.Add(this); }
    void OnDisable()
    {
        All.Remove(this);
        if (ownedMesh != null)
            Destroy(ownedMesh);
    }

    static Transform Root()
    {
        if (root == null)
            root = new GameObject("Land Survey").transform;
        return root;
    }

    // HDRP transparent unlit, built once in code — the overlay is a map,
    // not a lit surface. ValidateMaterial applies the keyword soup the
    // surface-type float alone doesn't.
    static Material OverlayMaterial()
    {
        if (overlayMat != null)
            return overlayMat;
        overlayMat = new Material(Shader.Find("HDRP/Unlit"))
        { name = "SurveyOverlay" };
        overlayMat.SetFloat("_SurfaceType", 1f); // transparent
        overlayMat.SetFloat("_BlendMode", 0f);   // alpha
        overlayMat.SetFloat("_ZWrite", 0f);
        UnityEngine.Rendering.HighDefinition.HDMaterial
            .ValidateMaterial(overlayMat);
        return overlayMat;
    }

    // The whole survey shows and hides together with the tool. Showing
    // re-drapes every plot over the CURRENT ground — the outline is the
    // stored fact, the drape is presentation, so a re-benched hillside
    // gets an honest map without any stored data changing.
    public static void ShowAll(bool shown)
    {
        foreach (LandPlot p in All)
            if (p != null)
                p.SetShown(shown);
    }

    public void SetShown(bool shown)
    {
        if (shown)
        {
            RebuildMesh();
            Retint();
        }
        rend.enabled = shown;
        col.enabled = shown;
    }

    // Type gives the hue, ownership the presence: your land sits solid,
    // a neighbor's faint, bandit land smoulders toward red.
    public void Retint()
    {
        Color c = type switch
        {
            LandType.Farm => new Color(0.86f, 0.72f, 0.28f),
            LandType.Pasture => new Color(0.42f, 0.72f, 0.32f),
            LandType.Timber => new Color(0.16f, 0.42f, 0.24f),
            _ => new Color(0.80f, 0.55f, 0.35f),
        };
        float a = owner == LandOwner.Player ? 0.5f : 0.18f;
        if (owner == LandOwner.Bandits)
        {
            c = Color.Lerp(c, new Color(0.75f, 0.15f, 0.1f), 0.55f);
            a = 0.3f;
        }
        if (highlighted)
            a = Mathf.Min(1f, a + 0.25f);
        var mpb = new MaterialPropertyBlock();
        mpb.SetColor("_UnlitColor", new Color(c.r, c.g, c.b, a));
        rend.SetPropertyBlock(mpb);
    }

    public void SetHighlight(bool on)
    {
        if (highlighted == on)
            return;
        highlighted = on;
        Retint();
    }

    void RebuildMesh()
    {
        var tris = new List<int>();
        if (!SlabTile.Triangulate(verts, tris))
            return;
        // Triangulate's winding on CCW verts faces DOWN (BuildPrism
        // flips it when emitting a slab's top; verified here with a
        // downward ray that sailed through). An upward face is not
        // cosmetic: raycasts ignore backfaces, so the map would be
        // unhoverable as well as invisible from above.
        for (int i = 0; i < tris.Count; i += 3)
            (tris[i + 1], tris[i + 2]) = (tris[i + 2], tris[i + 1]);
        var pts = new List<Vector3>(verts);
        Subdivide(pts, tris);
        bool ground = Ground.Any;
        var final = new Vector3[pts.Count];
        for (int i = 0; i < pts.Count; i++)
        {
            float y = ground ? Ground.HeightAt(pts[i]) : 0f;
            final[i] = new Vector3(pts[i].x, y + Drape, pts[i].z);
        }
        if (ownedMesh == null)
            ownedMesh = new Mesh { name = "PlotOverlay" };
        ownedMesh.Clear();
        ownedMesh.vertices = final;
        ownedMesh.triangles = tris.ToArray();
        ownedMesh.RecalculateNormals();
        ownedMesh.RecalculateBounds();
        filter.sharedMesh = ownedMesh;
        // A disabled MeshCollider skips its re-bake silently (the
        // SlabTile lesson) — assign while enabled, then let SetShown
        // decide visibility.
        col.enabled = true;
        col.sharedMesh = null;
        col.sharedMesh = ownedMesh;
    }

    // Midpoint subdivision until every edge fits the terrain's scale.
    // Splitting the longest edge of any oversized triangle and its
    // neighbor along that edge would be proper; splitting all three is
    // simpler and the overlay is a map, not masonry.
    static void Subdivide(List<Vector3> pts, List<int> tris)
    {
        for (int round = 0; round < 4; round++)
        {
            bool anyBig = false;
            var next = new List<int>(tris.Count * 4);
            var midCache = new Dictionary<(int, int), int>();
            for (int i = 0; i < tris.Count; i += 3)
            {
                int a = tris[i], b = tris[i + 1], c = tris[i + 2];
                float longest = Mathf.Max(
                    (pts[a] - pts[b]).magnitude,
                    Mathf.Max((pts[b] - pts[c]).magnitude,
                        (pts[c] - pts[a]).magnitude));
                if (longest <= MaxEdge)
                {
                    next.Add(a); next.Add(b); next.Add(c);
                    continue;
                }
                anyBig = true;
                int ab = Mid(pts, midCache, a, b);
                int bc = Mid(pts, midCache, b, c);
                int ca = Mid(pts, midCache, c, a);
                next.AddRange(new[] { a, ab, ca, ab, b, bc, ca, bc, c, ab, bc, ca });
            }
            tris.Clear();
            tris.AddRange(next);
            if (!anyBig)
                break;
        }
    }

    static int Mid(List<Vector3> pts, Dictionary<(int, int), int> cache,
        int a, int b)
    {
        var key = a < b ? (a, b) : (b, a);
        if (cache.TryGetValue(key, out int idx))
            return idx;
        pts.Add((pts[a] + pts[b]) * 0.5f);
        cache[key] = pts.Count - 1;
        return pts.Count - 1;
    }
}
