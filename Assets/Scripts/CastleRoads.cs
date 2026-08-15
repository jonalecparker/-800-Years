using System.Collections.Generic;
using UnityEngine;

// The 1220 road filter (the user's call, 2026-08-12): the OSM network
// keeps its GEOMETRY — rural Monmouthshire lanes follow their medieval
// predecessors — but not its DENSITY, which is modern (every farm
// access lane and footpath). The roads of 1220 worth painting are the
// ones that CONNECT the castles. This stitches the road polylines into
// a graph (vertices merged within a tolerance; OSM junctions share
// nodes, so the merge mostly heals float noise) and keeps the pairwise
// shortest routes between castle sites. The Dress bake (splats,
// hedgerows, tree keep-outs) and the runtime timber planter's keep-out
// both call this, so painted roads and the gaps in the woods agree by
// construction.
public static class CastleRoads
{
    const float MergeTolerance = 2.5f;
    // A castle stands beside its road, not on it — how far the route
    // search looks for the nearest road vertex to a site.
    const float CastleSnapRange = 400f;

    // Input: flattened [x0,z0,x1,z1,...] polylines. Output: one merged
    // polyline per castle pair that found a route (unreachable pairs
    // log and drop). Deterministic — same roads, same castles, same
    // routes, so bake-time paint and runtime keep-outs cannot disagree.
    // With `grounds` (one castle-grounds polygon per castle,
    // CastleGrounds.Outlines) each route is TRIMMED where it first
    // leaves a polygon: the road ends at the castle's boundary — the
    // trim point IS the gate — instead of running through the middle
    // of the bailey. Only the ENDS trim; a route that merely passes
    // near a third castle mid-run is left alone.
    public static List<float[]> Routes(List<float[]> roads, List<Vector2> castles,
        List<List<Vector3>> grounds = null)
    {
        var keyToId = new Dictionary<(int, int), int>();
        var pos = new List<Vector2>();
        var adj = new List<List<(int to, float len)>>();

        int IdFor(float x, float z)
        {
            var key = (Mathf.RoundToInt(x / MergeTolerance),
                Mathf.RoundToInt(z / MergeTolerance));
            if (keyToId.TryGetValue(key, out int id))
                return id;
            id = pos.Count;
            keyToId[key] = id;
            pos.Add(new Vector2(x, z));
            adj.Add(new List<(int, float)>());
            return id;
        }

        foreach (float[] p in roads)
            for (int i = 0; i + 3 < p.Length; i += 2)
            {
                int a = IdFor(p[i], p[i + 1]);
                int b = IdFor(p[i + 2], p[i + 3]);
                if (a == b)
                    continue;
                float len = Vector2.Distance(pos[a], pos[b]);
                adj[a].Add((b, len));
                adj[b].Add((a, len));
            }

        // Castles snap to the MAIN network, not the nearest vertex:
        // OSM keeps orphan fragments (a driveway mapped without a
        // shared node — Skenfrith's closest vertex is a 2-vertex stub),
        // and a castle snapped to one can route nowhere. The largest
        // connected component IS the road network the routes ride.
        var comp = new int[pos.Count];
        for (int v = 0; v < pos.Count; v++)
            comp[v] = -1;
        int compCount = 0, mainComp = -1, mainSize = 0;
        var flood = new Stack<int>();
        for (int s = 0; s < pos.Count; s++)
        {
            if (comp[s] >= 0)
                continue;
            int size = 0;
            comp[s] = compCount;
            flood.Push(s);
            while (flood.Count > 0)
            {
                int v = flood.Pop();
                size++;
                foreach ((int to, float _) in adj[v])
                    if (comp[to] < 0)
                    {
                        comp[to] = compCount;
                        flood.Push(to);
                    }
            }
            if (size > mainSize)
            {
                mainSize = size;
                mainComp = compCount;
            }
            compCount++;
        }

        var castleNode = new int[castles.Count];
        for (int c = 0; c < castles.Count; c++)
        {
            int best = -1;
            float bestSq = CastleSnapRange * CastleSnapRange;
            for (int v = 0; v < pos.Count; v++)
            {
                if (comp[v] != mainComp)
                    continue;
                float sq = (pos[v] - castles[c]).sqrMagnitude;
                if (sq < bestSq)
                {
                    bestSq = sq;
                    best = v;
                }
            }
            castleNode[c] = best;
            if (best < 0)
                Debug.LogWarning($"CastleRoads: no road within {CastleSnapRange}m of castle {c}.");
        }

        var routes = new List<float[]>();
        for (int i = 0; i < castles.Count; i++)
        {
            if (castleNode[i] < 0)
                continue;
            // One Dijkstra from castle i serves every pair (i, j>i).
            var dist = new float[pos.Count];
            var prev = new int[pos.Count];
            for (int v = 0; v < pos.Count; v++)
            {
                dist[v] = float.MaxValue;
                prev[v] = -1;
            }
            dist[castleNode[i]] = 0f;
            var open = new SortedSet<(float d, int v)>
                { (0f, castleNode[i]) };
            while (open.Count > 0)
            {
                (float d, int v) = open.Min;
                open.Remove(open.Min);
                if (d > dist[v])
                    continue;
                foreach ((int to, float len) in adj[v])
                {
                    float nd = d + len;
                    if (nd < dist[to])
                    {
                        open.Remove((dist[to], to));
                        dist[to] = nd;
                        prev[to] = v;
                        open.Add((nd, to));
                    }
                }
            }
            for (int j = i + 1; j < castles.Count; j++)
            {
                if (castleNode[j] < 0)
                    continue;
                if (dist[castleNode[j]] >= float.MaxValue)
                {
                    Debug.LogWarning($"CastleRoads: castles {i} and {j} share no road route.");
                    continue;
                }
                var chain = new List<int>();
                for (int v = castleNode[j]; v >= 0; v = prev[v])
                    chain.Add(v);
                var pts = new List<Vector2>(chain.Count);
                foreach (int v in chain)
                    pts.Add(pos[v]);
                // chain walked back from j, so pts[0] is castle j's end
                // and pts[last] is castle i's.
                if (grounds != null && j < grounds.Count && i < grounds.Count)
                {
                    TrimIntoGrounds(pts, grounds[j]);
                    pts.Reverse();
                    TrimIntoGrounds(pts, grounds[i]);
                    pts.Reverse();
                }
                if (pts.Count < 2)
                    continue;
                var flat = new float[pts.Count * 2];
                for (int k = 0; k < pts.Count; k++)
                {
                    flat[k * 2] = pts[k].x;
                    flat[k * 2 + 1] = pts[k].y;
                }
                routes.Add(flat);
            }
        }
        return routes;
    }

    // The road ends at the gate: everything from this end up to the
    // LAST point inside the destination's own grounds is cut, and the
    // boundary crossing goes back as the new end. Scanning for the
    // LAST inside point (not the first outside one) matters — the
    // nearest road vertex to a castle can sit BEYOND the grounds, so
    // the route clips through the polygon and overshoots; a naive
    // leading-run trim sees an already-outside end and leaves the clip
    // (caught live at Grosmont, both approaches). Bisection against
    // SlabTile.Contains: simpler and more robust than edge-by-edge
    // segment intersection, and a meter of slack is invisible at road
    // scale. A route that merely passes near a THIRD castle mid-run is
    // deliberately left alone.
    static void TrimIntoGrounds(List<Vector2> pts, List<Vector3> poly)
    {
        int last = -1;
        for (int k = 0; k < pts.Count; k++)
            if (SlabTile.Contains(poly, new Vector3(pts[k].x, 0f, pts[k].y)))
                last = k;
        if (last < 0)
            return;
        if (last + 1 >= pts.Count)
        {
            pts.Clear();
            return;
        }
        Vector2 inside = pts[last], outside = pts[last + 1];
        for (int it = 0; it < 16; it++)
        {
            Vector2 mid = (inside + outside) * 0.5f;
            if (SlabTile.Contains(poly, new Vector3(mid.x, 0f, mid.y)))
                inside = mid;
            else
                outside = mid;
        }
        pts.RemoveRange(0, last + 1);
        pts.Insert(0, outside);
    }
}
