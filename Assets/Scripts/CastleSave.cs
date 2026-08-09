using System;
using System.Collections.Generic;
using System.IO;
using UnityEngine;

// Save/load for the castle: the wall graph and its riders serialized to
// JSON, geometry rebuilt on load. WHAT is saved follows the graph's own
// doctrine — stored facts only, never derived geometry: nodes, edges
// (curve, heights, openings), stairs (their baked runs), and slabs
// (outline, planes). Meshes, sections and treads are all re-derived on
// load by the same Rebuild paths a commit uses.
//
// Written-at-the-moment data (raised sills) is serialized VERBATIM and
// restored without re-running the moments that wrote it. Those answers
// are stored precisely because the world that produced them can have
// moved on; a load that re-derived them would quietly re-answer old
// questions with new facts.
//
// Identity is positional: a node or edge's id is its index in this file.
// Nothing needs identity that survives ACROSS saves yet — a save is a
// complete graph, so within-file indices are all the stability required.
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
    // 2: the building rebuild (Docs/BuildingRebuild.md) — rooms left the
    // schema, slab tiles joined it. Version-1 saves refuse cleanly; the
    // user's call was to delete them, not migrate.
    // 3: slabs became drawn polygons (2026-08-08) — a SlabData carries an
    // outline. Version-2 square tiles still load: they migrate as
    // 4-corner outlines derived from their center/size/yaw.
    const int Version = 3;

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

    // One earthwork operation (kind 0 pad / 1 bench / 2 restore) — older
    // saves carry only poly+level, whose missing fields default to kind 0,
    // which is exactly what those saves meant.
    [Serializable]
    class PadData
    {
        public int kind;
        public List<Vector3> poly = new List<Vector3>();
        public float level;
        public float width;
        public float ya, yb;
    }

    // A slab (foundation or between-storey floor) — pure stored facts,
    // rebuilt as the same polygon prism on load. The x/z/size/yaw fields
    // are the version-2 square-tile schema, read only to migrate: a v2
    // tile with no verts loads as the 4-corner outline it was.
    [Serializable]
    class SlabData
    {
        public List<Vector3> verts = new List<Vector3>();
        public float topY, bottomY;
        public bool isFoundation;
        public float x, z;
        public float size, yaw;
    }

    [Serializable]
    class SaveData
    {
        public int version;
        public string terrainName;
        public List<PadData> pads = new List<PadData>();
        public List<NodeData> nodes = new List<NodeData>();
        public List<EdgeData> edges = new List<EdgeData>();
        public List<StairData> stairs = new List<StairData>();
        public List<SlabData> slabs = new List<SlabData>();
    }

    // Project-root /Saves, next to Assets — visible, diffable saves.
    // Slots are files: the slot NAME is the file name, the save DATE is
    // the file's write time — nothing stored twice. The Saves panel in
    // BuildMenu is the front end; F5/F9 act on whichever slot was last
    // saved or loaded (CurrentSlot), so the hotkeys never guess.
    public static string SavesDir =>
        Path.GetFullPath(Path.Combine(Application.dataPath, "..", "Saves"));

    public static string CurrentSlot = "castle";

    public static string PathFor(string slot) => Path.Combine(SavesDir, slot + ".json");

    public static string DefaultPath => PathFor(CurrentSlot);

    // A slot name doubles as a file name, so it keeps only characters
    // every filesystem accepts. Empty after cleaning means "not a name".
    public static string SanitizeSlot(string raw)
    {
        if (string.IsNullOrEmpty(raw))
            return "";
        var sb = new System.Text.StringBuilder();
        foreach (char c in raw)
            if (char.IsLetterOrDigit(c) || c == ' ' || c == '-' || c == '_')
                sb.Append(c);
        return sb.ToString().Trim();
    }

    // Every slot on disk, newest first — the Saves panel's list.
    public static List<(string slot, DateTime time)> ListSlots()
    {
        var slots = new List<(string, DateTime)>();
        if (!Directory.Exists(SavesDir))
            return slots;
        foreach (string file in Directory.GetFiles(SavesDir, "*.json"))
            slots.Add((Path.GetFileNameWithoutExtension(file), File.GetLastWriteTime(file)));
        slots.Sort((a, b) => b.Item2.CompareTo(a.Item2));
        return slots;
    }

    // Removes a slot's file — the panel's trashcan is the only caller.
    // A deleted CurrentSlot stays current: F5 simply writes it anew,
    // and F9 against a missing file refuses like any bad load.
    public static void DeleteSlot(string slot)
    {
        string path = PathFor(slot);
        if (File.Exists(path))
            File.Delete(path);
    }

    public static bool Save(string path)
    {
        var data = new SaveData { version = Version };
        Terrain terrain = Terrain.activeTerrain;
        data.terrainName = terrain != null ? terrain.name : "";

        foreach (TerrainPads.Op op in TerrainPads.All)
            data.pads.Add(new PadData
            {
                kind = op.kind,
                poly = new List<Vector3>(op.poly),
                level = op.level,
                width = op.width,
                ya = op.ya,
                yb = op.yb,
            });

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

        foreach (SlabTile t in SlabTile.All)
        {
            if (t == null)
                continue;
            var sd = new SlabData
            {
                topY = t.topY,
                bottomY = t.bottomY,
                isFoundation = t.isFoundation,
            };
            sd.verts.AddRange(t.verts);
            data.slabs.Add(sd);
        }

        Directory.CreateDirectory(Path.GetDirectoryName(path));
        File.WriteAllText(path, JsonUtility.ToJson(data, true));
        BuildLog.Add($"Saved — {data.edges.Count} walls, {data.stairs.Count} stairs,"
            + $" {data.slabs.Count} slabs → {Path.GetFileName(path)}.");
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
        if (data == null || (data.version != Version && data.version != 2))
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

        // Earth before masonry: pristine ground back first, then the
        // saved pads reapply in order — every wall created below re-foots
        // on the padded ground exactly as it stood at save time. (Nothing
        // exists yet, so the live keep-out around existing walls is moot
        // here; footings re-foot regardless.)
        TerrainPads.ResetToPristine();
        foreach (PadData pd in data.pads)
            TerrainPads.Replay(new TerrainPads.Op
            {
                kind = pd.kind,
                poly = pd.poly,
                level = pd.level,
                width = pd.width,
                ya = pd.ya,
                yb = pd.yb,
            });

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
            stairs.Add(WallStair.Create(parent, spans, sd.bottomY, sd.topY, sd.runArc,
                Unpack(sd.baseFrom), sd.width, sd.baseWallHeight, sd.baseStep, material));
        }

        var outline = new List<Vector3>();
        foreach (SlabData sd in data.slabs)
        {
            outline.Clear();
            if (sd.verts != null && sd.verts.Count >= 3)
            {
                foreach (Vector3 v in sd.verts)
                    outline.Add(new Vector3(v.x, 0f, v.z));
            }
            else
            {
                // A version-2 square tile: its outline is its corners.
                float rad = sd.yaw * Mathf.Deg2Rad;
                Vector3 ax = new Vector3(Mathf.Cos(rad), 0f, -Mathf.Sin(rad))
                    * (sd.size * 0.5f);
                Vector3 az = new Vector3(Mathf.Sin(rad), 0f, Mathf.Cos(rad))
                    * (sd.size * 0.5f);
                Vector3 c = new Vector3(sd.x, 0f, sd.z);
                outline.Add(c - ax - az);
                outline.Add(c + ax - az);
                outline.Add(c + ax + az);
                outline.Add(c - ax + az);
            }
            SlabTile.Create(parent, material, outline, sd.topY, sd.bottomY,
                sd.isFoundation);
        }

        Physics.SyncTransforms();
        BuildLog.Add($"Loaded — {data.edges.Count} walls, {stairs.Count} stairs,"
            + $" {data.slabs.Count} slabs from {Path.GetFileName(path)}.");
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
        foreach (WallStair s in new List<WallStair>(WallStair.All))
            if (s != null)
                UnityEngine.Object.DestroyImmediate(s.gameObject);
        foreach (WallEdge e in new List<WallEdge>(WallEdge.All))
            if (e != null)
                UnityEngine.Object.DestroyImmediate(e.gameObject);
        foreach (WallNode n in new List<WallNode>(WallNode.All))
            if (n != null)
                UnityEngine.Object.DestroyImmediate(n.gameObject);
        foreach (SlabTile t in new List<SlabTile>(SlabTile.All))
            if (t != null)
                UnityEngine.Object.DestroyImmediate(t.gameObject);
    }
}
