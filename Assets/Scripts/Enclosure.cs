using System.Collections.Generic;
using UnityEngine;

// Enclosure is a CHECKED fact, not a built one (Docs/BuildingRebuild.md):
// walls can close a face of the graph, and the check announces it — but
// no geometry is ever derived from it. Foundations, floors and roofs are
// the player's own authored objects; "roomness" becomes stored gameplay
// state when the economy needs it, not before.
public static class Enclosure
{
    const int EdgeSamples = 12;

    // Called when a room-tool chain closes its loop: trace the enclosed
    // face off the last committed edge (the chain's winding picks the
    // side, the complement is tried before giving up) and say what
    // closed. A chamber stands on a slab storey; a ring on bare terrain
    // is a yard.
    public static void AnnounceChainClose(WallEdge lastEdge, bool ccw)
    {
        List<WallGraph.DirEdge> ring = WallGraph.TraceFace(lastEdge, ccw);
        if (ring == null || RingArea(ring) <= 0f)
            ring = WallGraph.TraceFace(lastEdge, !ccw);
        if (ring == null || RingArea(ring) <= 0f)
        {
            BuildLog.Add("Walls committed — no enclosed face traced.");
            return;
        }

        bool onSlab = true;
        float storey = float.NegativeInfinity;
        foreach (WallGraph.DirEdge d in ring)
        {
            if (d.edge == null)
                continue;
            if (float.IsNegativeInfinity(d.edge.baseY))
            {
                onSlab = false;
                break;
            }
            storey = d.edge.baseY;
        }
        BuildLog.Add(onSlab
            ? $"Room enclosed — {ring.Count} walls on the {storey:0.##}m floor."
            : $"Enclosure closed — a yard. Buildings start from a Foundation.");
    }

    public static float RingArea(List<WallGraph.DirEdge> ring)
    {
        return SignedArea(SampleOutline(ring));
    }

    // Does a candidate ring's face contain this point? (FaceAt asks when
    // picking the smallest face around the cursor.)
    public static bool RingContains(List<WallGraph.DirEdge> ring, Vector3 point)
    {
        return PointInPolygon(new Vector3(point.x, 0f, point.z), SampleOutline(ring));
    }

    static bool PointInPolygon(Vector3 p, List<Vector3> poly)
    {
        bool inside = false;
        for (int i = 0, j = poly.Count - 1; i < poly.Count; j = i++)
        {
            Vector3 a = poly[i];
            Vector3 b = poly[j];
            if (a.z > p.z != b.z > p.z
                && p.x < (b.x - a.x) * (p.z - a.z) / (b.z - a.z) + a.x)
                inside = !inside;
        }
        return inside;
    }

    // Shoelace over XZ: positive = counter-clockwise in the ground plane.
    public static float SignedArea(List<Vector3> poly)
    {
        float sum = 0f;
        for (int i = 0; i < poly.Count; i++)
        {
            Vector3 a = poly[i];
            Vector3 b = poly[(i + 1) % poly.Count];
            sum += a.x * b.z - b.x * a.z;
        }
        return sum * 0.5f;
    }

    static List<Vector3> SampleOutline(List<WallGraph.DirEdge> ring)
    {
        var pts = new List<Vector3>();
        foreach (WallGraph.DirEdge d in ring)
        {
            if (d.edge == null)
                continue;
            Vector3 s = d.edge.A, c = d.edge.control, e = d.edge.B;
            for (int j = 0; j < EdgeSamples; j++)
            {
                float t = (float)j / EdgeSamples;
                if (!d.forward)
                    t = 1f - t;
                Vector3 p = WallEdge.Evaluate(s, c, e, t);
                pts.Add(new Vector3(p.x, 0f, p.z));
            }
        }
        return pts;
    }
}
