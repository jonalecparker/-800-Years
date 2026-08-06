using System;
using System.Collections.Generic;
using System.IO;
using UnityEngine;

// Save/load for the castle: the wall graph and its riders serialized to
// JSON, geometry rebuilt on load. WHAT is saved follows the graph's own
// doctrine — stored facts only, never derived geometry: nodes, edges
// (curve, heights, openings), stairs (their baked runs), and rooms (ring,
// slab planes, outlines, wells). Meshes, sections and treads are all
// re-derived on load by the same Rebuild paths a commit uses.
//
// Written-at-the-moment data (a room's frozen roofY and roofNote, raised
// sills, stair wells) is serialized VERBATIM and restored without
// re-running the moments that wrote it — WallRoom.Restore, not
// WallRoom.Create. Those answers are stored precisely because the world
// that produced them can have moved on; a load that re-derived them would
// quietly re-answer old questions with new facts.
//
// Identity is positional: a node or edge's id is its index in this file,
// and rooms/wells reference edges/stairs by that index. Nothing needs
// identity that survives ACROSS saves yet — a save is a complete graph,
// so within-file indices are all the stability required.
//
// A save is tied to its terrain: footings, slab bottoms and stair wedges
// sample the ground on rebuild. The terrain's name is stored and a
// mismatch is reported, but the load still proceeds — walls keep their
// curves and tops, and their footings re-foot on whatever ground is
// there.
//
// Load is NOT a graph mutation in the doctrine's sense: it never splits,
// never resolves crossings, and calls no room hooks — the save was taken
// from a legal graph, and restoring it wholesale re-creates exactly that
// graph. WallEdge.Create (not CommitEdge) is the right door in.
public static class CastleSave
{
    const int Version = 1;

    // JSON has no -Infinity, and JsonUtility's handling of one is not
    // worth trusting across Unity versions — the graph's "-inf means
    // terrain" convention travels as this sentinel instead.
    const float NegInf = -1e30f;
    static float Pack(float v) => float.IsNegativeInfinity(v) ? NegInf : v;
    static float Unpack(float v) => v <= NegInf * 0.5f ? float.NegativeInfinity : v;

    [Serializable]
    class NodeData
    {
        public float x, z;
        public float baseY;
        public float radius;
    }

    [Serializable]
    class OpeningData
    {
        public float t, width, head, sill;
    }

    [Serializable]
    class EdgeData
    {
        public int a, b;
        public Vector3 control;
        public float height, thickness, baseWallHeight, targetSectionLength, baseStep;
        public float fixedTopY, baseY, skirt;
        public bool roomBuilt;
        public List<OpeningData> openings = new List<OpeningData>();
    }

    [Serializable]
    class SpanData
    {
        public Vector3 s, c, e;
        public float arcStart, arcEnd;
    }

    [Serializable]
    class StairData
    {
        public List<SpanData> spans = new List<SpanData>();
        public float bottomY, topY, runArc, baseFrom, width;
        public float baseWallHeight, baseStep;
    }

    [Serializable]
    class RingRef
    {
        public int edge;
        public bool forward;
    }

    [Serializable]
    class WellData
    {
        public int stair;
        public List<Vector3> poly = new List<Vector3>();
    }

    [Serializable]
    class RoomData
    {
        public List<RingRef> boundary = new List<RingRef>();
        public float floorY;
        public bool hasFloorSlab;
        public bool hasRoof;
        public float roofY;
        public string roofNote;
        public List<Vector3> outline = new List<Vector3>();
        public List<Vector3> deckOutline = new List<Vector3>();
        public List<WellData> wells = new List<WellData>();
    }

    [Serializable]
    class SaveData
    {
        public int version;
        public string terrainName;
        public List<NodeData> nodes = new List<NodeData>();
        public List<EdgeData> edges = new List<EdgeData>();
        public List<StairData> stairs = new List<StairData>();
        public List<RoomData> rooms = new List<RoomData>();
    }

    // Project-root /Saves, next to Assets — visible, diffable test saves.
    // Real save slots move to persistentDataPath when the game has a
    // front end to pick them from.
    public static string DefaultPath =>
        Path.GetFullPath(Path.Combine(Application.dataPath, "..", "Saves", "castle.json"));

    public static bool Save(string path)
    {
        var data = new SaveData { version = Version };
        Terrain terrain = Terrain.activeTerrain;
        data.terrainName = terrain != null ? terrain.name : "";

        var nodeIndex = new Dictionary<WallNode, int>();
        foreach (WallNode n in WallNode.All)
        {
            if (n == null)
                continue;
            nodeIndex[n] = data.nodes.Count;
            data.nodes.Add(new NodeData
            {
                x = n.point.x,
                z = n.point.z,
                baseY = Pack(n.baseY),
                radius = n.radius,
            });
        }

        var edgeIndex = new Dictionary<WallEdge, int>();
        foreach (WallEdge e in WallEdge.All)
        {
            if (e == null || e.nodeA == null || e.nodeB == null
                || !nodeIndex.ContainsKey(e.nodeA) || !nodeIndex.ContainsKey(e.nodeB))
                continue;
            var ed = new EdgeData
            {
                a = nodeIndex[e.nodeA],
                b = nodeIndex[e.nodeB],
                control = e.control,
                height = e.height,
                thickness = e.thickness,
                baseWallHeight = e.baseWallHeight,
                targetSectionLength = e.targetSectionLength,
                baseStep = e.baseStep,
                fixedTopY = Pack(e.fixedTopY),
                baseY = Pack(e.baseY),
                skirt = e.skirt,
                roomBuilt = e.roomBuilt,
            };
            foreach (WallEdge.Opening o in e.openings)
                ed.openings.Add(new OpeningData
                {
                    t = o.t,
                    width = o.width,
                    head = o.head,
                    sill = Pack(o.sill),
                });
            edgeIndex[e] = data.edges.Count;
            data.edges.Add(ed);
        }

        var stairIndex = new Dictionary<WallStair, int>();
        foreach (WallStair s in WallStair.All)
        {
            if (s == null)
                continue;
            var sd = new StairData
            {
                bottomY = s.bottomY,
                topY = s.topY,
                runArc = s.runArc,
                baseFrom = Pack(s.baseFrom),
                width = s.width,
                baseWallHeight = s.baseWallHeight,
                baseStep = s.baseStep,
            };
            foreach (WallStair.Span sp in s.spans)
                sd.spans.Add(new SpanData
                {
                    s = sp.s,
                    c = sp.c,
                    e = sp.e,
                    arcStart = sp.arcStart,
                    arcEnd = sp.arcEnd,
                });
            stairIndex[s] = data.stairs.Count;
            data.stairs.Add(sd);
        }

        foreach (WallRoom r in WallRoom.All)
        {
            if (r == null || r.broken || r.Outline == null)
                continue;
            var rd = new RoomData
            {
                floorY = r.floorY,
                hasFloorSlab = r.HasFloorSlab,
                hasRoof = r.HasRoof,
                roofY = r.HasRoof ? r.roofY : 0f,
                roofNote = r.roofNote,
            };
            bool ringIntact = true;
            foreach (WallGraph.DirEdge d in r.boundary)
            {
                if (d.edge == null || !edgeIndex.TryGetValue(d.edge, out int ei))
                {
                    ringIntact = false;
                    break;
                }
                rd.boundary.Add(new RingRef { edge = ei, forward = d.forward });
            }
            if (!ringIntact)
                continue;
            rd.outline.AddRange(r.Outline);
            if (r.DeckOutline != null)
                rd.deckOutline.AddRange(r.DeckOutline);
            foreach (WallRoom.Well w in r.wells)
                if (w.owner != null && w.poly != null && stairIndex.TryGetValue(w.owner, out int si))
                    rd.wells.Add(new WellData { stair = si, poly = new List<Vector3>(w.poly) });
            data.rooms.Add(rd);
        }

        Directory.CreateDirectory(Path.GetDirectoryName(path));
        File.WriteAllText(path, JsonUtility.ToJson(data, true));
        BuildLog.Add($"Saved — {data.edges.Count} walls, {data.rooms.Count} rooms,"
            + $" {data.stairs.Count} stairs → {Path.GetFileName(path)}.");
        return true;
    }

    public static bool Load(string path, Transform parent, Material material)
    {
        if (!File.Exists(path))
        {
            BuildLog.Add($"No save found at {Path.GetFileName(path)}.");
            return false;
        }
        SaveData data = JsonUtility.FromJson<SaveData>(File.ReadAllText(path));
        if (data == null || data.version != Version)
        {
            BuildLog.Add("Save file unreadable or from a different version — not loaded.");
            return false;
        }

        Terrain terrain = Terrain.activeTerrain;
        if (terrain != null && !string.IsNullOrEmpty(data.terrainName)
            && data.terrainName != terrain.name)
            BuildLog.Add($"Save was made on terrain '{data.terrainName}' — footings"
                + $" will re-foot on '{terrain.name}'.");

        Clear();

        var nodes = new List<WallNode>();
        foreach (NodeData nd in data.nodes)
            nodes.Add(WallNode.Create(parent, new Vector3(nd.x, 0f, nd.z),
                Unpack(nd.baseY), nd.radius, material));

        var edges = new List<WallEdge>();
        foreach (EdgeData ed in data.edges)
        {
            if (ed.a < 0 || ed.a >= nodes.Count || ed.b < 0 || ed.b >= nodes.Count)
            {
                edges.Add(null);
                continue;
            }
            WallEdge edge = WallEdge.Create(parent, nodes[ed.a], nodes[ed.b], ed.control,
                ed.height, ed.thickness, ed.baseWallHeight, material,
                ed.targetSectionLength, ed.baseStep, Unpack(ed.fixedTopY),
                Unpack(ed.baseY), ed.skirt);
            edge.roomBuilt = ed.roomBuilt;
            if (ed.openings.Count > 0)
            {
                foreach (OpeningData od in ed.openings)
                    edge.openings.Add(new WallEdge.Opening
                    {
                        t = od.t,
                        width = od.width,
                        head = od.head,
                        sill = Unpack(od.sill),
                    });
                edge.Rebuild();
            }
            edges.Add(edge);
        }

        foreach (WallNode n in nodes)
            if (n != null)
                n.RebuildMesh();

        var stairs = new List<WallStair>();
        foreach (StairData sd in data.stairs)
        {
            var spans = new List<WallStair.Span>();
            foreach (SpanData sp in sd.spans)
                spans.Add(new WallStair.Span
                {
                    s = sp.s,
                    c = sp.c,
                    e = sp.e,
                    arcStart = sp.arcStart,
                    arcEnd = sp.arcEnd,
                });
            // No NotifyStairBuilt: the wells this stair earned are stored
            // on the rooms below and restored with them.
            stairs.Add(WallStair.Create(parent, spans, sd.bottomY, sd.topY, sd.runArc,
                Unpack(sd.baseFrom), sd.width, sd.baseWallHeight, sd.baseStep, material));
        }

        int roomCount = 0;
        foreach (RoomData rd in data.rooms)
        {
            var ring = new List<WallGraph.DirEdge>();
            bool ringIntact = true;
            foreach (RingRef rr in rd.boundary)
            {
                if (rr.edge < 0 || rr.edge >= edges.Count || edges[rr.edge] == null)
                {
                    ringIntact = false;
                    break;
                }
                ring.Add(new WallGraph.DirEdge { edge = edges[rr.edge], forward = rr.forward });
            }
            if (!ringIntact || rd.outline.Count < 3)
                continue;
            var wells = new List<WallRoom.Well>();
            foreach (WellData wd in rd.wells)
                if (wd.stair >= 0 && wd.stair < stairs.Count && stairs[wd.stair] != null)
                    wells.Add(new WallRoom.Well
                    {
                        owner = stairs[wd.stair],
                        poly = new List<Vector3>(wd.poly),
                    });
            WallRoom.Restore(parent, ring, material, ring[0].edge.baseStep,
                ring[0].edge.baseWallHeight, rd.floorY, rd.hasFloorSlab,
                rd.hasRoof, rd.roofY, rd.roofNote, rd.outline, rd.deckOutline, wells);
            roomCount++;
        }

        Physics.SyncTransforms();
        BuildLog.Add($"Loaded — {data.edges.Count} walls, {roomCount} rooms,"
            + $" {stairs.Count} stairs from {Path.GetFileName(path)}.");
        return true;
    }

    // Everything the graph owns, removed before the restore —
    // DestroyImmediate, so the registries are empty before the first
    // restored node is created (deferred Destroy leaves corpses in All
    // for the rest of the frame). Rooms go first and silently (destroying
    // the object directly skips Break's breach log — this is a load, not
    // a breach), then stairs, then edges, whose OnDisable dissolves their
    // own nodes; the final node sweep catches only free-standing litter.
    static void Clear()
    {
        foreach (WallRoom r in new List<WallRoom>(WallRoom.All))
            if (r != null)
                UnityEngine.Object.DestroyImmediate(r.gameObject);
        foreach (WallStair s in new List<WallStair>(WallStair.All))
            if (s != null)
                UnityEngine.Object.DestroyImmediate(s.gameObject);
        foreach (WallEdge e in new List<WallEdge>(WallEdge.All))
            if (e != null)
                UnityEngine.Object.DestroyImmediate(e.gameObject);
        foreach (WallNode n in new List<WallNode>(WallNode.All))
            if (n != null)
                UnityEngine.Object.DestroyImmediate(n.gameObject);
    }
}
