using System.Collections.Generic;
using UnityEngine;

// Graph mutations: the ONLY way walls come to exist, split, or die.
// Nodes and edges are the stored model — placement gestures resolve into
// node reuse / creation / edge splits here, crossings are computed on the
// curves themselves (never physics), and every mutation leaves the
// invariant standing: edges meet only at nodes, and never overlap.
public static class WallGraph
{
    public struct EdgeParams
    {
        public float height;
        public float thickness;
        public float baseWallHeight;
        public float targetSectionLength;
        public float baseStep;
        public float fixedTopY;
        // Elevated ground (a room slab or wall top) the wall stands on;
        // -inf rides the terrain. This is the STOREY: node identity and
        // every planar query filter on it.
        public float baseY;
        // How far below baseY the masonry starts — buries a stacked wall
        // into the stepped top carrying it. Geometry only; 0 is always a
        // safe default (see WallEdge.skirt).
        public float skirt;
        public Material material;
        // Stamped onto every edge the gesture creates: the room tool's
        // walls can be designated a room after the fact, the wall tool's
        // cannot. See WallEdge.roomBuilt.
        public bool roomBuilt;
    }

    // Where a proposed curve crosses an existing edge. reuseNode is set
    // when the crossing lands close enough to an existing node to thread
    // through it instead of splitting the host.
    public struct Crossing
    {
        public WallEdge edge;
        public WallNode reuseNode;
        public float tSelf;
        public float tHost;
        public Vector3 point;
    }

    // One directed traversal of a ground edge: forward = nodeA toward
    // nodeB. Face walks and room boundaries speak in these.
    public struct DirEdge
    {
        public WallEdge edge;
        public bool forward;
    }

    // Crossings this close to a proposed curve's end belong to the end's
    // own node resolution, not to a split.
    const float EndClearance = 0.35f;
    // A crossing landing this close to an existing node reuses it.
    const float NodeReuse = 0.3f;
    // Two crossings closer than this together collapse into one (tangent
    // grazes, several hosts meeting at one point).
    const float CrossingMerge = 0.5f;
    // An end landing within this of an edge's centerline splits the host
    // there (the flank snap puts its point exactly on the centerline).
    const float CenterlineLand = 0.12f;
    const int CurveSamples = 48;
    // Two things stand on the same base when both ride the terrain, or
    // both sit on masonry/slab within this of each other. The planar
    // graph is per-storey: crossings, the redraw refusal, centerline
    // landings and node identity all apply WITHIN one base only, so a
    // wall built on a wall top passes over the graph below without
    // touching it.
    public const float BaseTolerance = 0.25f;

    public static bool SameBase(float a, float b)
    {
        if (float.IsNegativeInfinity(a) || float.IsNegativeInfinity(b))
            return float.IsNegativeInfinity(a) && float.IsNegativeInfinity(b);
        return Mathf.Abs(a - b) <= BaseTolerance;
    }

    // ------------------------------------------------------------------
    // Queries (also used by previews)
    // ------------------------------------------------------------------

    // All points where the proposed curve crosses existing ground edges,
    // sorted along the proposed run. Every one becomes a shared node at
    // commit — both walls split, four edges meet, the node draws the
    // joint.
    // Edges on another base are passed OVER, not crossed: an upper-storey
    // wall running above a courtyard wall shares nothing with it.
    public static List<Crossing> FindCrossings(Vector3 s, Vector3 c, Vector3 e, float baseY)
    {
        var crossings = new List<Crossing>();
        Vector3[] mine = SampleCurve(s, c, e);
        Bounds myBounds = CurveBounds(s, c, e, 0.5f);

        foreach (WallEdge host in WallEdge.All)
        {
            if (!SameBase(host.baseY, baseY))
                continue;
            if (!myBounds.Intersects(CurveBounds(host.A, host.control, host.B, 0.5f)))
                continue;

            Vector3[] theirs = SampleCurve(host.A, host.control, host.B);
            for (int i = 0; i < CurveSamples; i++)
            {
                for (int j = 0; j < CurveSamples; j++)
                {
                    if (!TrySegmentHit(mine[i], mine[i + 1], theirs[j], theirs[j + 1],
                        out float fi, out float fj))
                        continue;
                    Vector3 p = Vector3.Lerp(mine[i], mine[i + 1], fi);
                    if (Flat(p, s) < EndClearance || Flat(p, e) < EndClearance)
                        continue;
                    WallNode reuse = null;
                    if (Flat(p, host.A) < NodeReuse)
                        reuse = host.nodeA;
                    else if (Flat(p, host.B) < NodeReuse)
                        reuse = host.nodeB;
                    crossings.Add(new Crossing
                    {
                        edge = reuse == null ? host : null,
                        reuseNode = reuse,
                        tSelf = (i + fi) / CurveSamples,
                        tHost = (j + fj) / CurveSamples,
                        point = reuse != null ? reuse.point : p,
                    });
                }
            }
        }

        crossings.Sort((x, y) => x.tSelf.CompareTo(y.tSelf));
        for (int i = crossings.Count - 1; i > 0; i--)
        {
            if (Flat(crossings[i].point, crossings[i - 1].point) < CrossingMerge)
            {
                // Prefer keeping a node-reuse entry over a fresh split.
                if (crossings[i].reuseNode != null && crossings[i - 1].reuseNode == null)
                    crossings[i - 1] = crossings[i];
                crossings.RemoveAt(i);
            }
        }
        return crossings;
    }

    // The one refusal left: a near-exact redraw along an existing wall —
    // same line, either direction, sustained. Angled contact, shallow V's
    // off a shared node, flush parallel runs, and crossings are all legal
    // (crossings split); this only guards the degenerate coincident case
    // that would z-fight and hide a duplicate edge inside another.
    // Only against masonry on the SAME base — drawing a wall along the top
    // of another wall is the whole point of an upper storey, and reads as
    // a coincident redraw in XZ.
    public static bool OverlapsExisting(Vector3 s, Vector3 c, Vector3 e, float baseY)
    {
        const float MaxDist = 0.15f;
        const float MinParallel = 0.9986f; // cos 3°
        const float RefuseRun = 0.6f;

        Vector3[] mine = SampleCurve(s, c, e);
        Bounds myBounds = CurveBounds(s, c, e, MaxDist + 0.1f);

        foreach (WallEdge host in WallEdge.All)
        {
            if (!SameBase(host.baseY, baseY))
                continue;
            if (!myBounds.Intersects(CurveBounds(host.A, host.control, host.B, MaxDist + 0.1f)))
                continue;

            float run = 0f;
            for (int i = 0; i <= CurveSamples; i++)
            {
                float t = (float)i / CurveSamples;
                float tHost = host.NearestT(mine[i], out float distSq);
                bool close = distSq < MaxDist * MaxDist;
                if (close)
                {
                    Vector3 myTan = WallEdge.Tangent(s, c, e, t).normalized;
                    Vector3 hostTan = WallEdge.Tangent(host.A, host.control, host.B, tHost).normalized;
                    float dot = Mathf.Abs(myTan.x * hostTan.x + myTan.z * hostTan.z);
                    close = dot >= MinParallel;
                }
                if (!close)
                {
                    run = 0f;
                    continue;
                }
                if (i > 0)
                    run += Flat(mine[i], mine[i - 1]);
                if (run >= RefuseRun)
                    return true;
            }
        }
        return false;
    }

    // The smallest enclosed face containing `point`, traced fresh from
    // every edge in both directions and kept only if it comes back as an
    // interior face (positive area) the point falls inside. Smallest
    // wins, so a face nested inside another designates the room you are
    // standing in, not the one around it. Null when the point sits in no
    // enclosure at all.
    public static List<DirEdge> FaceAt(Vector3 point)
    {
        List<DirEdge> best = null;
        float bestArea = float.MaxValue;
        foreach (WallEdge e in WallEdge.All)
        {
            if (e == null)
                continue;
            for (int dir = 0; dir < 2; dir++)
            {
                List<DirEdge> face = TraceFace(e, dir == 0);
                if (face == null)
                    continue;
                float area = WallRoom.RingArea(face);
                if (area <= 0.5f || area >= bestArea)
                    continue;
                if (!WallRoom.RingContains(face, point))
                    continue;
                best = face;
                bestArea = area;
            }
        }
        return best;
    }

    // Walks the boundary of one planar face: travel the directed edge,
    // and at each node turn onto the member with the smallest clockwise
    // angle from the arrival direction (keep-left), until the walk
    // returns to where it started. Interior faces come back counter-
    // clockwise (positive outline area); the outer walk comes back
    // clockwise. Null on dead graphs or a walk that never closes.
    public static List<DirEdge> TraceFace(WallEdge startEdge, bool startForward)
    {
        var result = new List<DirEdge>();
        WallEdge e = startEdge;
        bool fwd = startForward;
        for (int step = 0; step < 512; step++)
        {
            result.Add(new DirEdge { edge = e, forward = fwd });
            WallNode n = fwd ? e.nodeB : e.nodeA;
            if (n == null)
                return null;
            bool arrivedAtStart = !fwd;
            Vector3 back = -e.EndOutwardDir(arrivedAtStart);
            float bIn = Mathf.Atan2(back.z, back.x) * Mathf.Rad2Deg;

            WallNode.Member bestMember = default;
            float bestDelta = float.MaxValue;
            bool found = false;
            foreach (WallNode.Member m in n.members)
            {
                if (m.edge == null)
                    continue;
                Vector3 inward = -m.edge.EndOutwardDir(m.atStart);
                if (inward.sqrMagnitude < 0.01f)
                    continue;
                float bm = Mathf.Atan2(inward.z, inward.x) * Mathf.Rad2Deg;
                float delta = Mathf.Repeat(bIn - bm, 360f);
                // Turning straight back down the arrival edge is the last
                // resort (a dead end's face wraps around the tip).
                if (m.edge == e && m.atStart == arrivedAtStart)
                    delta = 360f;
                if (delta < bestDelta)
                {
                    bestDelta = delta;
                    bestMember = m;
                    found = true;
                }
            }
            if (!found)
                return null;
            e = bestMember.edge;
            fwd = bestMember.atStart;
            if (e == startEdge && fwd == startForward)
                return result;
        }
        return null;
    }

    // Whether any of `from` reaches any of `to` through ground edges —
    // the room tool's enclosure test: a pending chain whose two ends
    // attach to connected masonry closes a loop.
    public static bool Connected(IEnumerable<WallNode> from, HashSet<WallNode> to)
    {
        var seen = new HashSet<WallNode>();
        var queue = new Queue<WallNode>();
        foreach (WallNode n in from)
            if (n != null && seen.Add(n))
                queue.Enqueue(n);
        while (queue.Count > 0)
        {
            WallNode n = queue.Dequeue();
            if (to.Contains(n))
                return true;
            foreach (WallNode.Member m in n.members)
            {
                if (m.edge == null)
                    continue;
                WallNode other = m.atStart ? m.edge.nodeB : m.edge.nodeA;
                if (other != null && seen.Add(other))
                    queue.Enqueue(other);
            }
        }
        return false;
    }

    // Fraction of the proposed run lying ALONG existing masonry (same
    // tolerances as the redraw refusal). The shape tools read this to
    // REUSE a host wall as a figure side (~1.0) instead of redrawing it,
    // and to refuse a side that only partially lines up.
    public static float CoincidentCoverage(Vector3 s, Vector3 c, Vector3 e, float baseY)
    {
        const float MaxDist = 0.15f;
        const float MinParallel = 0.9986f; // cos 3°

        Vector3[] mine = SampleCurve(s, c, e);
        Bounds myBounds = CurveBounds(s, c, e, MaxDist + 0.1f);
        var hosts = new List<WallEdge>();
        foreach (WallEdge h in WallEdge.All)
            if (SameBase(h.baseY, baseY)
                && myBounds.Intersects(CurveBounds(h.A, h.control, h.B, MaxDist + 0.1f)))
                hosts.Add(h);
        if (hosts.Count == 0)
            return 0f;

        int close = 0;
        for (int i = 0; i <= CurveSamples; i++)
        {
            Vector3 myTan = WallEdge.Tangent(s, c, e, (float)i / CurveSamples).normalized;
            foreach (WallEdge h in hosts)
            {
                float tH = h.NearestT(mine[i], out float distSq);
                if (distSq > MaxDist * MaxDist)
                    continue;
                Vector3 hTan = WallEdge.Tangent(h.A, h.control, h.B, tH).normalized;
                if (Mathf.Abs(myTan.x * hTan.x + myTan.z * hTan.z) < MinParallel)
                    continue;
                close++;
                break;
            }
        }
        return (float)close / (CurveSamples + 1);
    }

    // ------------------------------------------------------------------
    // Mutations
    // ------------------------------------------------------------------

    // Commits a placement gesture: resolve the end nodes (existing node /
    // split-at-landing / new), split every crossed host at the crossing
    // point, and chain sub-edges of the proposed curve through the nodes
    // in order. Returns the created edges (empty = the gesture was
    // refused).
    public static List<WallEdge> CommitEdge(Transform parent, Vector3 s, Vector3 c, Vector3 e,
        EdgeParams p)
    {
        var created = new List<WallEdge>();
        if (Flat(s, e) < 0.05f)
            return created;
        if (OverlapsExisting(s, c, e, p.baseY))
            return created;

        WallNode start = ResolveEndNode(parent, s, p);
        WallNode end = ResolveEndNode(parent, e, p);
        if (start == null || end == null || start == end)
            return created;

        var crossings = FindCrossings(s, c, e, p.baseY);

        // Split each crossed host once, at every crossing it carries.
        var chain = new List<(float t, WallNode node)> { (0f, start), (1f, end) };
        var byHost = new Dictionary<WallEdge, List<Crossing>>();
        foreach (Crossing x in crossings)
        {
            if (x.reuseNode != null)
            {
                chain.Add((x.tSelf, x.reuseNode));
                continue;
            }
            if (!byHost.TryGetValue(x.edge, out List<Crossing> list))
                byHost[x.edge] = list = new List<Crossing>();
            list.Add(x);
        }
        foreach (KeyValuePair<WallEdge, List<Crossing>> pair in byHost)
        {
            var ts = new List<float>();
            foreach (Crossing x in pair.Value)
                ts.Add(x.tHost);
            List<WallNode> nodes = SplitEdgeAtMany(parent, pair.Key, ts);
            for (int i = 0; i < pair.Value.Count; i++)
                if (nodes[i] != null)
                    chain.Add((pair.Value[i].tSelf, nodes[i]));
        }
        chain.Sort((x, y) => x.t.CompareTo(y.t));

        var touched = new HashSet<WallNode>();
        for (int i = 0; i + 1 < chain.Count; i++)
        {
            (float t0, WallNode n0) = chain[i];
            (float t1, WallNode n1) = chain[i + 1];
            if (n0 == n1 || t1 - t0 < 0.001f)
                continue;
            SubCurve(s, c, e, t0, t1, out _, out Vector3 subC, out _);
            WallEdge made = WallEdge.Create(parent, n0, n1, subC, p.height, p.thickness,
                p.baseWallHeight, p.material, p.targetSectionLength, p.baseStep, p.fixedTopY,
                p.baseY, p.skirt);
            made.roomBuilt = p.roomBuilt;
            created.Add(made);
            touched.Add(n0);
            touched.Add(n1);
        }
        foreach (WallNode n in touched)
            if (n != null)
                n.RebuildMesh();
        Physics.SyncTransforms();
        // A room that refused a roof may have just been completed or
        // levelled by this run.
        if (created.Count > 0)
            WallRoom.RetryRoofless();
        return created;
    }

    // Deletes a run of sections from an edge. The edge splits into
    // sub-edges over the kept ranges — hole boundaries get new flat-capped
    // 1-valent nodes, end nodes are reused where a range touches them,
    // and nodes left with no members dissolve. Masonry standing on this
    // edge is NOT followed: nothing stores what rests on what yet, so an
    // upper wall over a deleted span is left hanging (deliberate — the
    // support rules are a later design).
    public static void DeleteSections(Transform parent, WallEdge edge, int minIndex, int maxIndex)
    {
        var doomed = new HashSet<int>();
        for (int i = minIndex; i <= maxIndex; i++)
            doomed.Add(i);
        DeleteSections(parent, edge, doomed);
    }

    // The general form: any set of doomed indices, contiguous or not — a
    // marquee can catch scattered sections of one edge, and the edge can
    // only be surgered ONCE (the survivors are new edges with new
    // indices), so all of an edge's doomed sections go in a single call.
    public static void DeleteSections(Transform parent, WallEdge edge, ICollection<int> doomedIndices)
    {
        var ranges = new List<(float lo, float hi)>();
        foreach (WallEdgeSection section in edge.SectionsInOrder())
        {
            if (doomedIndices.Contains(section.index))
                continue;
            if (ranges.Count > 0 && Mathf.Abs(ranges[ranges.Count - 1].hi - section.tStart) < 0.001f)
                ranges[ranges.Count - 1] = (ranges[ranges.Count - 1].lo, section.tEnd);
            else
                ranges.Add((section.tStart, section.tEnd));
        }

        WallNode nodeA = edge.nodeA;
        WallNode nodeB = edge.nodeB;

        // Deleting masonry breaches any room this edge bounded —
        // the sub-edges cover less than the whole run, so the ring is
        // open no matter which sections went.
        WallRoom.NotifyBreach(edge);

        Vector3 s = edge.A, c = edge.control, e = edge.B;
        var touched = new HashSet<WallNode> { nodeA, nodeB };
        foreach ((float lo, float hi) in ranges)
        {
            WallNode n0 = lo < 0.001f
                ? nodeA
                : WallNode.Create(parent, WallEdge.Evaluate(s, c, e, lo), edge.baseY,
                    edge.thickness * 0.5f, edge.material);
            WallNode n1 = hi > 0.999f
                ? nodeB
                : WallNode.Create(parent, WallEdge.Evaluate(s, c, e, hi), edge.baseY,
                    edge.thickness * 0.5f, edge.material);
            if (n0 == n1)
                continue;
            SubCurve(s, c, e, lo, hi, out _, out Vector3 subC, out _);
            WallEdge sub = WallEdge.Create(parent, n0, n1, subC, edge.height, edge.thickness,
                edge.baseWallHeight, edge.material, edge.targetSectionLength, edge.baseStep,
                edge.fixedTopY, edge.baseY, edge.skirt);
            sub.roomBuilt = edge.roomBuilt;
            touched.Add(n0);
            touched.Add(n1);
        }
        Object.DestroyImmediate(edge.gameObject);
        foreach (WallNode n in touched)
            if (n != null)
                n.RebuildMesh();
        Physics.SyncTransforms();
    }

    // ------------------------------------------------------------------
    // Internals
    // ------------------------------------------------------------------

    // Splits one edge at several curve parameters in a single pass: nodes
    // at the split points, exact sub-curves between them, the original
    // destroyed. Returns one node per requested t (an end node where the t
    // lands on one). The end nodes never dissolve mid-split: sub-edges
    // attach before the original detaches.
    public static List<WallNode> SplitEdgeAtMany(Transform parent, WallEdge edge, List<float> ts)
    {
        Vector3 s = edge.A, c = edge.control, e = edge.B;
        var result = new List<WallNode>();
        var bounds = new List<(float t, WallNode node)> { (0f, edge.nodeA), (1f, edge.nodeB) };
        foreach (float t in ts)
        {
            Vector3 pt = WallEdge.Evaluate(s, c, e, t);
            if (Flat(pt, edge.A) < NodeReuse)
            {
                result.Add(edge.nodeA);
                continue;
            }
            if (Flat(pt, edge.B) < NodeReuse)
            {
                result.Add(edge.nodeB);
                continue;
            }
            WallNode node = WallNode.Create(parent, pt, edge.baseY, edge.thickness * 0.5f,
                edge.material);
            result.Add(node);
            bounds.Add((t, node));
        }
        if (bounds.Count == 2)
            return result;

        bounds.Sort((x, y) => x.t.CompareTo(y.t));
        var orderedSubs = new List<WallEdge>();
        var touched = new HashSet<WallNode>();
        for (int i = 0; i + 1 < bounds.Count; i++)
        {
            (float t0, WallNode n0) = bounds[i];
            (float t1, WallNode n1) = bounds[i + 1];
            if (n0 == n1 || t1 - t0 < 0.001f)
                continue;
            SubCurve(s, c, e, t0, t1, out _, out Vector3 subC, out _);
            WallEdge sub = WallEdge.Create(parent, n0, n1, subC, edge.height, edge.thickness,
                edge.baseWallHeight, edge.material, edge.targetSectionLength, edge.baseStep,
                edge.fixedTopY, edge.baseY, edge.skirt);
            sub.roomBuilt = edge.roomBuilt;
            orderedSubs.Add(sub);
            touched.Add(n0);
            touched.Add(n1);
        }
        // The sub-edges cover the whole old run, so any room ring through
        // this edge stays closed — it just re-points at the pieces.
        WallRoom.NotifySplit(edge, orderedSubs);
        Object.DestroyImmediate(edge.gameObject);
        foreach (WallNode n in touched)
            if (n != null)
                n.RebuildMesh();
        Physics.SyncTransforms();
        return result;
    }

    // The node a placement end lands on: an existing node under the
    // point, a split of whatever edge the point sits on (the flank snap
    // puts it exactly on the centerline), or a brand new free node.
    static WallNode ResolveEndNode(Transform parent, Vector3 point, EdgeParams p)
    {
        WallNode node = WallNode.FindAt(point, p.baseY);
        if (node != null)
            return node;

        foreach (WallEdge edge in new List<WallEdge>(WallEdge.All))
        {
            if (edge == null || !SameBase(edge.baseY, p.baseY))
                continue;
            float t = edge.NearestT(point, out float distSq);
            if (distSq > CenterlineLand * CenterlineLand)
                continue;
            List<WallNode> nodes = SplitEdgeAtMany(parent, edge, new List<float> { t });
            return nodes[0];
        }

        return WallNode.Create(parent, point, p.baseY, p.thickness * 0.5f, p.material);
    }

    // Exact Bézier segment extraction: the quadratic's polar form gives
    // the sub-curve's control point, so split walls follow the original
    // curve exactly.
    public static void SubCurve(Vector3 s, Vector3 c, Vector3 e, float a, float b,
        out Vector3 s2, out Vector3 c2, out Vector3 e2)
    {
        s2 = WallEdge.Evaluate(s, c, e, a);
        e2 = WallEdge.Evaluate(s, c, e, b);
        c2 = (1f - a) * (1f - b) * s + ((1f - a) * b + a * (1f - b)) * c + a * b * e;
    }

    static Vector3[] SampleCurve(Vector3 s, Vector3 c, Vector3 e)
    {
        var pts = new Vector3[CurveSamples + 1];
        for (int i = 0; i <= CurveSamples; i++)
        {
            Vector3 p = WallEdge.Evaluate(s, c, e, (float)i / CurveSamples);
            pts[i] = new Vector3(p.x, 0f, p.z);
        }
        return pts;
    }

    static Bounds CurveBounds(Vector3 s, Vector3 c, Vector3 e, float pad)
    {
        // The curve lies inside its control polygon's bounds.
        var bounds = new Bounds(new Vector3(s.x, 0f, s.z), Vector3.zero);
        bounds.Encapsulate(new Vector3(c.x, 0f, c.z));
        bounds.Encapsulate(new Vector3(e.x, 0f, e.z));
        bounds.Expand(pad * 2f);
        return bounds;
    }

    static bool TrySegmentHit(Vector3 a0, Vector3 a1, Vector3 b0, Vector3 b1,
        out float fa, out float fb)
    {
        fa = 0f;
        fb = 0f;
        float rx = a1.x - a0.x, rz = a1.z - a0.z;
        float sx = b1.x - b0.x, sz = b1.z - b0.z;
        float denom = rx * sz - rz * sx;
        if (Mathf.Abs(denom) < 1e-6f)
            return false;
        float qx = b0.x - a0.x, qz = b0.z - a0.z;
        fa = (qx * sz - qz * sx) / denom;
        fb = (qx * rz - qz * rx) / denom;
        return fa >= 0f && fa <= 1f && fb >= 0f && fb <= 1f;
    }

    static float Flat(Vector3 a, Vector3 b)
    {
        float dx = a.x - b.x, dz = a.z - b.z;
        return Mathf.Sqrt(dx * dx + dz * dz);
    }
}
