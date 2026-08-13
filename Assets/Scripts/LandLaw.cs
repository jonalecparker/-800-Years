using System.Collections.Generic;
using UnityEngine;

// The castle-grounds law (Docs/Territories.md, the user's call
// 2026-08-12): the player builds masonry on their own CASTLE parcels
// and nowhere else — village land is the villagers' (future procedural
// houses keep out of castle land the same way, by the stored type).
// Wired into the wall family's live blocked flags and SlabPolyRefusal,
// so the red ghost and the commit gate are one test. The law STANDS
// DOWN when the player owns no castle parcel at all — old saves
// predate castle land, and a law that refuses everywhere is a bug,
// not a law. Stairs and doorways ride existing lawful walls and are
// deliberately not checked; the Ground tool stays free (paths cross
// the countryside).
public static class LandLaw
{
    public static bool Active()
    {
        foreach (LandPlot p in LandPlot.All)
            if (p != null && p.type == LandType.Castle
                && p.owner == LandOwner.Player)
                return true;
        return false;
    }

    static bool OutsideCastle(Vector3 point)
    {
        foreach (LandPlot p in LandPlot.All)
            if (p != null && p.type == LandType.Castle
                && p.owner == LandOwner.Player
                && SlabTile.Contains(p.verts, new Vector3(point.x, 0f, point.z)))
                return false;
        return true;
    }

    // A wall span leaves the castle grounds: sampled along the curve —
    // endpoints alone would miss a span that crosses out and back over
    // a concave boundary.
    public static bool SpanOutside(Vector3 s, Vector3 c, Vector3 e)
    {
        if (!Active())
            return false;
        for (int i = 0; i <= 4; i++)
        {
            float t = i / 4f;
            float u = 1f - t;
            Vector3 p = u * u * s + 2f * u * t * c + t * t * e;
            if (OutsideCastle(p))
                return true;
        }
        return false;
    }

    // A slab outline leaves the castle grounds: verts plus centroid.
    public static bool PolyOutside(List<Vector3> poly)
    {
        if (!Active())
            return false;
        Vector3 mid = Vector3.zero;
        foreach (Vector3 v in poly)
        {
            if (OutsideCastle(v))
                return true;
            mid += v;
        }
        return poly.Count > 0 && OutsideCastle(mid / poly.Count);
    }
}
