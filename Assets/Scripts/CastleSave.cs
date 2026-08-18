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
    // 4: floors carry stairwells (2026-08-09) — stamped holes, stored on
    // the slab. Versions 2–3 load unchanged: no wells means no wells.
    // 5: the economy's first slice (2026-08-09) — the treasury travels,
    // and each edge carries its WallKind. Versions 2–4 load unchanged:
    // kind defaults to Stone (what every old wall was), and a save with
    // no treasury grants the fresh-start inheritance.
    // 6: the income side (2026-08-10) — the survey's parcels, the
    // calendar's elapsed days and the household count travel. A pre-v6
    // save has no parcels, so the survey regenerates fresh after load
    // (LandParcels.EnsureGenerated), the clock restarts at Lady Day and
    // households take the starting dozen.
    // 7: castle territories (2026-08-12, Docs/Territories.md) — each
    // parcel carries its lordship as an index into the name-sorted
    // site list. A v6 plot migrates ONCE at load: nearest site wins,
    // stored thereafter.
    // 8: the BNG world frame (2026-08-15, Docs/WorldFrame.md) — world
    // coordinates became the British National Grid and world Y became
    // metres ODN. Nothing NEW travels; the version records which frame
    // the save's coordinates speak. A pre-v8 save loading into the
    // Marches (current terrain "Marches", saved terrain "Terrain")
    // migrates ONCE through WorldFrame's legacy chain: every stored XZ
    // through WorldFromOldWorld, every finite absolute Y lifted to ODN.
    // Spiš saves — same old terrain name, but loaded into the scene
    // whose BaseName still matches — never migrate.
    // 9: bridges (2026-08-16) — a rider like the stair: ends, planes,
    // width and STAMPED pier footings travel. Pre-v9 saves simply have
    // none.
    // 10: buttresses (2026-08-16) — edge data like a doorway, so each
    // edge carries a list of them (parameter, side, absolute top). Pre-v10
    // saves simply have none: the list deserializes empty and every wall
    // stands exactly as it did.
    // 11: corner buttresses (2026-08-16) — a clasping pier belongs to two
    // walls at once and so to neither, and lives on the NODE (bearing,
    // absolute top), where this graph has always kept joint geometry.
    // Pre-v11 saves have none.
    // 12: crenellations (2026-08-17) — a battlement is a SPAN on the edge
    // (two parameters), so each edge carries a list of them. Nothing about
    // the merlons travels: their number and spacing are derived from the
    // span, so a save states the stretch and the load lays it out again
    // identically. Pre-v12 saves have none.
    const int Version = 12;

    // Lifting by WorldFrame.LegacyLift keeps every structure's relation
    // to the ground at its site; the re-foot on the new LIDAR ground
    // absorbs the residue.
    const float LegacyLift = WorldFrame.LegacyLift;

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
        public List<CornerData> buttresses = new List<CornerData>();
        public List<NodeCrenelData> crenels = new List<NodeCrenelData>();
    }

    [Serializable]
    class CornerData
    {
        public float bearing, topY;
    }

    [Serializable]
    class OpeningData
    {
        public float t, width, head, sill;
    }

    [Serializable]
    class ButtressData
    {
        public float t, side, topY;
    }

    [Serializable]
    class CrenelData
    {
        public float t0, t1;
        // Zero means "the default" — which is also what a v12 file written
        // before these two existed deserializes to, so the omitted-field
        // trap is a safe answer here rather than a silent change.
        public float height, gap;
    }

    [Serializable]
    class NodeCrenelData
    {
        public float height;
    }

    [Serializable]
    class EdgeData
    {
        public int a, b;
        public Vector3 control;
        public float height, thickness, baseWallHeight, targetSectionLength, baseStep;
        public float fixedTopY, baseY, skirt;
        // WallKind as int — 0 (Stone) is both the enum default and what
        // every pre-v5 wall was, so missing fields mean what they meant.
        public int kind;
        public List<OpeningData> openings = new List<OpeningData>();
        public List<ButtressData> buttresses = new List<ButtressData>();
        public List<CrenelData> crenels = new List<CrenelData>();
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
    // A stairwell hole through a floor slab. Its own type because
    // JsonUtility can't serialize a list of lists directly. `stair` is
    // the owning stair's index in this save's stairs list (-1 = no owner;
    // the well then never heals on stair delete) — positional identity,
    // like everything else here.
    [Serializable]
    class WellData
    {
        public List<Vector3> verts = new List<Vector3>();
        public int stair = -1;
    }

    [Serializable]
    class SlabData
    {
        public List<Vector3> verts = new List<Vector3>();
        public List<WellData> wells = new List<WellData>();
        public float topY, bottomY;
        public bool isFoundation;
        public float x, z;
        public float size, yaw;
    }

    // A bridge: two stated ends with their deck-top planes, the width,
    // and the piers exactly as the commit stamped them — footings are
    // stored facts, never re-derived from whatever the ground is now.
    [Serializable]
    class PierData
    {
        public Vector3 pos;
        public float bottomY;
    }

    [Serializable]
    class BridgeData
    {
        public Vector3 start, end;
        public float startY, endY, width;
        public List<PierData> piers = new List<PierData>();
    }

    [Serializable]
    class PlotData
    {
        public List<Vector3> verts = new List<Vector3>();
        public int type, owner, gx, gz;
        public int territory;
    }

    [Serializable]
    class SaveData
    {
        public int version;
        public string terrainName;
        // The treasury in pence — a stored fact like any other, so the
        // delete tool's snapshot undo rolls money back with the masonry.
        public long treasury;
        // The calendar and the manor: days since Lady Day 1220, and the
        // one population number.
        public double clockDays;
        public int households;
        public List<PadData> pads = new List<PadData>();
        public List<NodeData> nodes = new List<NodeData>();
        public List<EdgeData> edges = new List<EdgeData>();
        public List<StairData> stairs = new List<StairData>();
        public List<SlabData> slabs = new List<SlabData>();
        public List<BridgeData> bridges = new List<BridgeData>();
        public List<PlotData> plots = new List<PlotData>();
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
        SaveData data = Capture();
        Directory.CreateDirectory(Path.GetDirectoryName(path));
        File.WriteAllText(path, JsonUtility.ToJson(data, true));
        BuildLog.Add($"Saved — {data.edges.Count} walls, {data.stairs.Count} stairs,"
            + $" {data.slabs.Count} slabs → {Path.GetFileName(path)}.");
        return true;
    }

    // Everything the world stores, as data — the save's body, shared with
    // the delete tool's snapshot undo so both go through one door.
    static SaveData Capture()
    {
        var data = new SaveData { version = Version };
        data.terrainName = Ground.Any ? Ground.BaseName : "";
        data.treasury = Estate.TreasuryPence;
        data.clockDays = GameClock.DaysElapsed;
        data.households = Estate.Households;
        foreach (LandPlot p in LandPlot.All)
        {
            if (p == null)
                continue;
            var pd = new PlotData
            {
                type = (int)p.type,
                owner = (int)p.owner,
                gx = p.gx,
                gz = p.gz,
                territory = p.territory,
            };
            pd.verts.AddRange(p.verts);
            data.plots.Add(pd);
        }

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
            var nd = new NodeData
            {
                x = n.point.x,
                z = n.point.z,
                baseY = Pack(n.baseY),
                radius = n.radius,
            };
            foreach (WallNode.Buttress b in n.buttresses)
                nd.buttresses.Add(new CornerData { bearing = b.bearing, topY = b.topY });
            foreach (WallNode.Crenel c in n.crenels)
                nd.crenels.Add(new NodeCrenelData { height = c.height });
            data.nodes.Add(nd);
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
                kind = (int)e.kind,
            };
            foreach (WallEdge.Opening o in e.openings)
                ed.openings.Add(new OpeningData
                {
                    t = o.t,
                    width = o.width,
                    head = o.head,
                    sill = Pack(o.sill),
                });
            foreach (WallEdge.Buttress b in e.buttresses)
                ed.buttresses.Add(new ButtressData
                {
                    t = b.t,
                    side = b.side,
                    topY = b.topY,
                });
            foreach (WallEdge.Crenel c in e.crenels)
                ed.crenels.Add(new CrenelData
                {
                    t0 = c.t0,
                    t1 = c.t1,
                    height = c.height,
                    gap = c.gap,
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
            foreach (SlabTile.Well w in t.wells)
            {
                var wd = new WellData();
                wd.verts.AddRange(w.verts);
                if (w.owner != null && stairIndex.TryGetValue(w.owner, out int si))
                    wd.stair = si;
                sd.wells.Add(wd);
            }
            data.slabs.Add(sd);
        }

        foreach (Bridge b in Bridge.All)
        {
            if (b == null)
                continue;
            var bd = new BridgeData
            {
                start = b.start,
                end = b.end,
                startY = b.startY,
                endY = b.endY,
                width = b.width,
            };
            foreach (Bridge.Pier p in b.piers)
                bd.piers.Add(new PierData { pos = p.pos, bottomY = p.bottomY });
            data.bridges.Add(bd);
        }

        return data;
    }

    // In-memory snapshot for one-step undo: the same data a save writes,
    // never touching disk, restored through the same Apply a load uses —
    // restore, never re-derive.
    public static string Snapshot()
    {
        return JsonUtility.ToJson(Capture());
    }

    public static bool RestoreSnapshot(string json, Transform parent, Material material,
        Material palisadeMaterial = null)
    {
        if (string.IsNullOrEmpty(json))
            return false;
        SaveData data = JsonUtility.FromJson<SaveData>(json);
        if (data == null)
            return false;
        Apply(data, parent, material, palisadeMaterial);
        return true;
    }

    public static bool Load(string path, Transform parent, Material material,
        Material palisadeMaterial = null)
    {
        if (!File.Exists(path))
        {
            BuildLog.Add($"No save found at {Path.GetFileName(path)}.");
            return false;
        }
        SaveData data = JsonUtility.FromJson<SaveData>(File.ReadAllText(path));
        if (data == null || data.version < 2 || data.version > Version)
        {
            BuildLog.Add("Save file unreadable or from a different version — not loaded.");
            return false;
        }

        // A pre-v8 Marches save speaks the old Mercator frame; loading
        // it into the BNG world transforms it ONCE, here, before Apply
        // ever sees it. The gate is the frame MISMATCH — old save, new
        // Marches — so Spiš saves (old name, old scene, frames agree)
        // pass untouched.
        if (data.version < 8 && Ground.Any && Ground.BaseName == "Marches"
            && data.terrainName == "Terrain")
        {
            MigrateToBngFrame(data);
            BuildLog.Add("Save migrated to the BNG world frame (pre-v8 Marches save).");
        }

        if (Ground.Any && !string.IsNullOrEmpty(data.terrainName)
            && data.terrainName != Ground.BaseName)
            BuildLog.Add($"Save was made on terrain '{data.terrainName}' — footings"
                + $" will re-foot on '{Ground.BaseName}'.");

        Apply(data, parent, material, palisadeMaterial);
        BuildLog.Add($"Loaded — {data.edges.Count} walls, {data.stairs.Count} stairs,"
            + $" {data.slabs.Count} slabs"
            + (data.bridges.Count > 0 ? $", {data.bridges.Count} bridges" : "")
            + $" from {Path.GetFileName(path)}.");
        return true;
    }

    // The one-time frame migration (v8): every stored XZ through the
    // legacy Mercator→BNG chain, every finite ABSOLUTE Y lifted to ODN.
    // Relative quantities (height, thickness, skirt, width, head, arc
    // lengths) and the −inf terrain sentinels stay; curve/span points
    // keep their y (the planar-point convention — verticals live in the
    // named Y fields). The terrain name is rewritten so the mismatch
    // warning stays quiet and a re-save is v8 in every respect.
    static void MigrateToBngFrame(SaveData data)
    {
        Vector2 XZ(float x, float z) =>
            WorldFrame.WorldFromOldWorld(new Vector2(x, z));
        Vector3 P(Vector3 v)
        {
            Vector2 p = XZ(v.x, v.z);
            return new Vector3(p.x, v.y, p.y);
        }
        float Lift(float packedY) => packedY <= NegInf * 0.5f
            ? packedY : packedY + LegacyLift;

        foreach (NodeData nd in data.nodes)
        {
            Vector2 p = XZ(nd.x, nd.z);
            nd.x = p.x;
            nd.z = p.y;
            nd.baseY = Lift(nd.baseY);
        }
        foreach (EdgeData ed in data.edges)
        {
            ed.control = P(ed.control);
            ed.fixedTopY = Lift(ed.fixedTopY);
            ed.baseY = Lift(ed.baseY);
            for (int i = 0; i < ed.openings.Count; i++)
                ed.openings[i].sill = Lift(ed.openings[i].sill);
        }
        foreach (StairData sd in data.stairs)
        {
            for (int i = 0; i < sd.spans.Count; i++)
            {
                SpanData sp = sd.spans[i];
                sp.s = P(sp.s);
                sp.c = P(sp.c);
                sp.e = P(sp.e);
            }
            sd.bottomY += LegacyLift;
            sd.topY += LegacyLift;
            sd.baseFrom = Lift(sd.baseFrom);
        }
        foreach (SlabData sd in data.slabs)
        {
            for (int i = 0; i < sd.verts.Count; i++)
                sd.verts[i] = P(sd.verts[i]);
            foreach (WellData wd in sd.wells)
                for (int i = 0; i < wd.verts.Count; i++)
                    wd.verts[i] = P(wd.verts[i]);
            sd.topY += LegacyLift;
            sd.bottomY += LegacyLift;
            if (sd.verts.Count < 3)
            {
                // A v2 square tile still stores center/size/yaw; Apply
                // derives its corners from these, so they migrate too.
                Vector2 c = XZ(sd.x, sd.z);
                sd.x = c.x;
                sd.z = c.y;
            }
        }
        foreach (PlotData pd in data.plots)
            for (int i = 0; i < pd.verts.Count; i++)
                pd.verts[i] = P(pd.verts[i]);
        foreach (PadData pd in data.pads)
        {
            for (int i = 0; i < pd.poly.Count; i++)
                pd.poly[i] = P(pd.poly[i]);
            pd.level += LegacyLift;
            pd.ya += LegacyLift;
            pd.yb += LegacyLift;
        }
        data.terrainName = "Marches";
        data.version = 8;
    }

    // The restore itself: tear the world down and rebuild it from data.
    // The load's body, shared with the snapshot undo.
    static void Apply(SaveData data, Transform parent, Material material,
        Material palisadeMaterial = null)
    {
        Clear();

        // The treasury is part of the world's state: a v5 save restores
        // its balance exactly (the snapshot undo depends on this); an
        // older save predates money and grants the fresh inheritance.
        Estate.TreasuryPence = data.version >= 5 ? data.treasury : Estate.StartingPence;
        // The calendar, the manor and the survey travel from v6. No
        // back-payments for skipped quarters — the saved run settled its
        // own. A pre-v6 save has no parcels; the caller's
        // EnsureGenerated pass will survey fresh ground.
        GameClock.SetElapsed(data.version >= 6 ? data.clockDays : 0);
        Estate.Households = data.version >= 6 ? data.households : 12;
        // A v6 plot predates territories: migrate ONCE by nearest site,
        // stored on the plot from here on (Docs/Territories.md).
        List<LandParcels.Site> sites =
            data.version < 7 ? LandParcels.Sites() : null;
        foreach (PlotData pd in data.plots)
        {
            if (pd.verts == null || pd.verts.Count < 3)
                continue;
            int territory = pd.territory;
            if (sites != null && sites.Count > 0)
            {
                Vector3 mid = Vector3.zero;
                foreach (Vector3 v in pd.verts)
                    mid += v;
                mid /= pd.verts.Count;
                territory = LandParcels.TerritoryOf(
                    new Vector2(mid.x, mid.z), sites);
            }
            LandPlot.Create(pd.verts, (LandType)pd.type,
                (LandOwner)pd.owner, pd.gx, pd.gz, territory);
        }
        // The woods follow the parcels wherever they come from —
        // deterministic, so a snapshot undo replants the same oaks.
        if (data.plots.Count > 0)
            TimberWoods.Plant();

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
        {
            WallNode node = WallNode.Create(parent, new Vector3(nd.x, 0f, nd.z),
                Unpack(nd.baseY), nd.radius, material);
            // Stated before any member arrives; the node's own RebuildMesh
            // (run once the edges are back) is what raises the pier.
            foreach (CornerData cd in nd.buttresses)
                node.buttresses.Add(new WallNode.Buttress
                {
                    bearing = cd.bearing,
                    topY = cd.topY,
                });
            // Stated before the members arrive, like the corner pier: the
            // RebuildMesh that follows every edge's creation raises both.
            foreach (NodeCrenelData ncd in nd.crenels)
                node.crenels.Add(new WallNode.Crenel { height = ncd.height });
            nodes.Add(node);
        }

        var edges = new List<WallEdge>();
        foreach (EdgeData ed in data.edges)
        {
            if (ed.a < 0 || ed.a >= nodes.Count || ed.b < 0 || ed.b >= nodes.Count)
            {
                edges.Add(null);
                continue;
            }
            WallKind kind = ed.kind == (int)WallKind.Palisade
                ? WallKind.Palisade : WallKind.Stone;
            Material edgeMaterial = kind == WallKind.Palisade && palisadeMaterial != null
                ? palisadeMaterial : material;
            WallEdge edge = WallEdge.Create(parent, nodes[ed.a], nodes[ed.b], ed.control,
                ed.height, ed.thickness, ed.baseWallHeight, edgeMaterial,
                ed.targetSectionLength, ed.baseStep, Unpack(ed.fixedTopY),
                Unpack(ed.baseY), ed.skirt, kind);
            foreach (OpeningData od in ed.openings)
                edge.openings.Add(new WallEdge.Opening
                {
                    t = od.t,
                    width = od.width,
                    head = od.head,
                    sill = Unpack(od.sill),
                });
            foreach (ButtressData bd in ed.buttresses)
                edge.buttresses.Add(new WallEdge.Buttress
                {
                    t = bd.t,
                    side = bd.side,
                    topY = bd.topY,
                });
            foreach (CrenelData cd in ed.crenels)
                edge.crenels.Add(new WallEdge.Crenel
                {
                    t0 = cd.t0,
                    t1 = cd.t1,
                    height = cd.height,
                    gap = cd.gap,
                });
            if (ed.openings.Count > 0 || ed.buttresses.Count > 0 || ed.crenels.Count > 0)
                edge.Rebuild();
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
            List<SlabTile.Well> wells = null;
            if (sd.wells != null && sd.wells.Count > 0)
            {
                wells = new List<SlabTile.Well>();
                foreach (WellData wd in sd.wells)
                    if (wd != null && wd.verts != null && wd.verts.Count >= 3)
                        wells.Add(new SlabTile.Well(
                            wd.stair >= 0 && wd.stair < stairs.Count
                                ? stairs[wd.stair] : null,
                            wd.verts));
            }
            SlabTile.Create(parent, material, outline, sd.topY, sd.bottomY,
                sd.isFoundation, wells);
        }

        foreach (BridgeData bd in data.bridges)
        {
            var piers = new List<Bridge.Pier>();
            foreach (PierData pd in bd.piers)
                piers.Add(new Bridge.Pier { pos = pd.pos, bottomY = pd.bottomY });
            Bridge.Create(parent, material, bd.start, bd.end,
                bd.startY, bd.endY, bd.width, piers);
        }

        Physics.SyncTransforms();
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
        foreach (Bridge b in new List<Bridge>(Bridge.All))
            if (b != null)
                UnityEngine.Object.DestroyImmediate(b.gameObject);
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
        foreach (LandPlot p in new List<LandPlot>(LandPlot.All))
            if (p != null)
                UnityEngine.Object.DestroyImmediate(p.gameObject);
    }
}
