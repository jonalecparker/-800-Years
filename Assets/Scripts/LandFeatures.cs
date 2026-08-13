using System.Collections.Generic;
using UnityEngine;

// One door to the Marches feature polylines for RUNTIME land systems:
// the castle routes (the same CastleRoads set the Dress bake painted —
// deterministic, so paint, parcel boundaries and tree keep-outs cannot
// disagree) and the waterways. Parsed once per session from the
// MarchesDressing component's features asset; a scene without one
// (Spiš) has no features and the consumers degrade to nothing.
public static class LandFeatures
{
    [System.Serializable] class Fea { public int c; public float w; public float[] p; }
    [System.Serializable] class FeaSet { public Fea[] roads; public Fea[] waters; public Fea[] woods; }

    static bool parsed;
    static readonly List<float[]> roadLines = new List<float[]>();
    static readonly List<(float[] p, float w)> waterLines
        = new List<(float[], float)>();

    // The roads of 1220 — castle routes when the bake filters (its
    // castleRoadsOnly flag is the authority), the full network when it
    // doesn't.
    public static List<float[]> RoadLines()
    {
        Parse();
        return roadLines;
    }

    public static List<(float[] p, float w)> WaterLines()
    {
        Parse();
        return waterLines;
    }

    static void Parse()
    {
        if (parsed)
            return;
        parsed = true;
        var dressing = Object.FindAnyObjectByType<MarchesDressing>(
            FindObjectsInactive.Include);
        if (dressing == null || dressing.features == null)
            return;
        var set = JsonUtility.FromJson<FeaSet>(dressing.features.text);
        if (set.waters != null)
            foreach (Fea w in set.waters)
                waterLines.Add((w.p, Mathf.Max(1f, w.w)));
        if (set.roads == null)
            return;
        var polylines = new List<float[]>(set.roads.Length);
        foreach (Fea r in set.roads)
            polylines.Add(r.p);
        if (!dressing.castleRoadsOnly)
        {
            roadLines.AddRange(polylines);
            return;
        }
        var castles = new List<Vector2>();
        foreach (LandParcels.Site s in LandParcels.Sites())
            castles.Add(s.pos);
        // Routes trim at each castle's grounds boundary — the road ends
        // at the gate, and the survey's village walk starts there.
        if (castles.Count >= 2)
            roadLines.AddRange(CastleRoads.Routes(polylines, castles,
                CastleGrounds.Outlines(castles)));
    }
}
