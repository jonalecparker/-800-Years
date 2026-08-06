using System.Collections.Generic;
using UnityEngine;

// A designated room: one enclosed face of the wall graph, STORED — the
// boundary is a directed ring of ground edges captured at designation,
// never re-inferred. The room owns its floor slab (flat, snapped up to
// the base step above the highest ground it covers) and, once the
// boundary offers a level crown to rest on, a flat stone roof slab.
// Borrowed walls may stand taller than that crown; the room's own may
// not. Splitting a boundary wall (a new tee or crossing) just
// re-points the ring at the sub-edges — the slabs never rebuild.
// Deleting boundary masonry breaches the room: the roof goes, the floor
// stays, and the record marks itself broken. The roof's elevation is
// frozen at creation, so courses stacked on the walls afterwards rise
// past the roofline as parapets.
public class WallRoom : MonoBehaviour
{
    public static readonly List<WallRoom> All = new List<WallRoom>();

    public readonly List<WallGraph.DirEdge> boundary = new List<WallGraph.DirEdge>();

    // A hole in the roof deck, and the stair that needs it. STORED, like
    // the ring: cut at exactly two moments (a stair commits, or a roof is
    // finally built over stairs that already stand) and never re-derived
    // after. Same doctrine as everything else here — a slab that
    // recomputed its own holes would be inferring structure, and would go
    // looking for it on every split and breach.
    public struct Well
    {
        public WallStair owner;
        public List<Vector3> poly;
    }
    public readonly List<Well> wells = new List<Well>();

    public float floorY;
    public float roofY = float.NegativeInfinity;
    // Set by TryBuildRoof: empty when the whole crown was one level, else
    // a phrase naming the borrowed walls that stand above the deck.
    public string roofNote = "";
    public bool broken;

    public bool HasRoof => roofObj != null;
    // Whether the room owns a floor slab (an elevated room doesn't — the
    // deck its walls stand on already is one). Settled at designation;
    // the save records the answer rather than re-asking.
    public bool HasFloorSlab => floorObj != null;
    // Read access for the save: both outlines are written-at-designation
    // data (boundary splits never change them), so they serialize
    // verbatim instead of being re-sampled from the ring on load.
    public List<Vector3> Outline => outline;
    public List<Vector3> DeckOutline => deckOutline;

    Material material;
    float baseStep = 0.5f;
    float baseWallHeight = 3.5f;
    // The face outline sampled once at designation (flattened, CCW). Both
    // slabs are cut from this polygon; boundary splits don't change it.
    List<Vector3> outline;
    // The roof's own footprint, kept so a well can be cut into it later
    // without re-deriving the plane or the inset.
    List<Vector3> deckOutline;
    GameObject floorObj;
    GameObject roofObj;
    Mesh floorMesh;
    Mesh roofMesh;

    const float FloorSkirt = 0.5f;
    const float RoofThickness = 0.3f;
    const float LevelEps = 0.05f;
    const int EdgeSamples = 8;
    const float InteriorSampleStep = 2f;
    // How much CLEAR head the well guarantees under the deck's UNDERSIDE
    // — deliberately far more than a body needs (WalkMode stands 1.8m),
    // and deliberately not computed.
    //
    // Sizing this to the minimum was a mistake twice over. It was first
    // measured to roofY instead of the soffit, so it promised 2m and
    // delivered 2m minus the deck; and then, measured correctly, it was
    // still tight enough that the difference between a stair and a ramp
    // — you stand on a tread, not on the line between the ends — put a
    // skull back into the masonry. A stairwell that is bigger than it
    // has to be costs a little deck nobody stands on. One that is a
    // few centimetres too small costs a head, every single climb.
    // So: be generous, and stop doing arithmetic on the player's skull.
    const float Headroom = 3f;
    // A stair butts flush against the wall's inner face, which is exactly
    // where the deck's edge sits — so the well's natural footprint touches
    // the deck boundary instead of sitting inside it, and a keyhole bridge
    // needs a hole that is strictly interior. The well is drawn a lip
    // narrower than the treads to guarantee that. The cost is a lip of
    // deck lying over the outer few centimetres of the landing, which is
    // coplanar with it; keep this small enough to read as a joint line.
    const float WellLip = 0.05f;
    // And the hole opens this much further down the run again, because a
    // climber is a body and not a point: standing at the edge of the well
    // you are up on the tread under your leading foot with the back of
    // your skull still under the deck. Generous for the same reason
    // Headroom is — a metre of extra slot is invisible, a tight one is a
    // headache.
    const float WellLead = 1f;
    const int WellSamplesPerSpan = 6;

    void OnEnable() { All.Add(this); }
    void OnDisable() { All.Remove(this); }

    void OnDestroy()
    {
        if (floorMesh != null)
            Destroy(floorMesh);
        if (roofMesh != null)
            Destroy(roofMesh);
    }

    // Designates a traced face as a room: floor immediately, roof if the
    // boundary tops are level. Returns null for degenerate rings.
    public static WallRoom Create(Transform parent, List<WallGraph.DirEdge> ring,
        Material material, float baseStep, float baseWallHeight)
    {
        List<Vector3> outline = SampleOutline(ring);
        if (outline.Count < 3 || Mathf.Abs(SignedArea(outline)) < 0.5f)
        {
            BuildLog.Add("Room not created: enclosure too small.");
            return null;
        }
        if (SignedArea(outline) < 0f)
            outline.Reverse();

        GameObject obj = new GameObject("WallRoom");
        obj.transform.SetParent(parent, false);
        WallRoom room = obj.AddComponent<WallRoom>();
        room.material = material;
        room.baseStep = baseStep;
        room.baseWallHeight = baseWallHeight;
        room.boundary.AddRange(ring);
        room.outline = outline;
        room.BuildFloor();
        room.RaiseSills();
        string roofFail = room.TryBuildRoof();
        BuildLog.Add(roofFail == null
            ? $"Room designated — {ring.Count} walls, roofed{room.roofNote}."
            : $"Room designated — {ring.Count} walls, floor only: {roofFail}.");
        return room;
    }

    // Rebuilds a SAVED room exactly as recorded — the restore counterpart
    // of Create, which asks the world all its questions (floor level,
    // roof crown, sill raising, stair wells) at designation and writes
    // the answers down. A load must not ask again: floorY, roofY,
    // roofNote, both outlines and the wells all arrive stored, and only
    // the slab MESHES are re-derived from them. The one thing re-sampled
    // is the floor slab's buried bottom, which chases the terrain the way
    // every footing does. No BuildLog entry — restoring is not an event
    // in the build, and the roofless case must not re-refuse either.
    public static WallRoom Restore(Transform parent, List<WallGraph.DirEdge> ring,
        Material material, float baseStep, float baseWallHeight,
        float floorY, bool hasFloorSlab, bool hasRoof, float roofY, string roofNote,
        List<Vector3> outline, List<Vector3> deckOutline, List<Well> wells)
    {
        GameObject obj = new GameObject("WallRoom");
        obj.transform.SetParent(parent, false);
        WallRoom room = obj.AddComponent<WallRoom>();
        room.material = material;
        room.baseStep = baseStep;
        room.baseWallHeight = baseWallHeight;
        room.boundary.AddRange(ring);
        room.outline = outline;
        room.floorY = floorY;
        if (hasFloorSlab)
        {
            float minGround = float.MaxValue;
            foreach (Vector3 p in room.GroundSamplePoints())
                minGround = Mathf.Min(minGround, GroundAt(p));
            float bottom = (baseStep > 0f ? Mathf.Floor(minGround / baseStep) * baseStep
                : minGround) - FloorSkirt;
            room.floorObj = room.BuildSlab("RoomFloor", outline, bottom, floorY,
                out room.floorMesh);
        }
        if (hasRoof && deckOutline != null && deckOutline.Count >= 3)
        {
            room.roofY = roofY;
            room.roofNote = roofNote ?? "";
            room.deckOutline = deckOutline;
            if (wells != null)
                room.wells.AddRange(wells);
            room.BuildRoofSlab();
        }
        return room;
    }

    // The enclosed area a candidate ring would designate — the placement
    // tool uses the sign to tell an interior face from the outer walk.
    public static float RingArea(List<WallGraph.DirEdge> ring)
    {
        return SignedArea(SampleOutline(ring));
    }

    // Does a candidate ring's face contain this point? (Designating an
    // existing enclosure asks the graph for the face under the cursor.)
    public static bool RingContains(List<WallGraph.DirEdge> ring, Vector3 point)
    {
        return PointInPolygon(new Vector3(point.x, 0f, point.z), SampleOutline(ring));
    }

    // The floor footprint a room on this face would cover: the ring
    // sampled and pushed in to the walls' inner faces. What to SHOW when
    // offering the face — the ring itself runs down the middle of the
    // masonry, where a guide line is buried inside the wall.
    public static List<Vector3> FaceFootprint(List<WallGraph.DirEdge> ring, float inset)
    {
        List<Vector3> outline = SampleOutline(ring);
        if (outline.Count < 3)
            return outline;
        if (SignedArea(outline) < 0f)
            outline.Reverse();
        return InsetOutline(outline, inset);
    }

    // Is the point inside THIS room's designated face — the test that
    // keeps a second designation off a face that already has one.
    public bool ContainsPoint(Vector3 point)
    {
        return !broken && outline != null && outline.Count >= 3
            && PointInPolygon(new Vector3(point.x, 0f, point.z), outline);
    }

    // ------------------------------------------------------------------
    // Graph notifications
    // ------------------------------------------------------------------

    // A boundary edge was split with full coverage (a tee or crossing
    // landed on it): the ring re-points at the sub-edges, in ring order.
    // Geometry is untouched — the face is the same face.
    public static void NotifySplit(WallEdge old, List<WallEdge> subsAscendingT)
    {
        foreach (WallRoom room in All)
        {
            if (room.broken)
                continue;
            for (int i = 0; i < room.boundary.Count; i++)
            {
                if (room.boundary[i].edge != old)
                    continue;
                bool fwd = room.boundary[i].forward;
                room.boundary.RemoveAt(i);
                for (int k = 0; k < subsAscendingT.Count; k++)
                {
                    WallEdge sub = subsAscendingT[fwd ? k : subsAscendingT.Count - 1 - k];
                    room.boundary.Insert(i + k, new WallGraph.DirEdge { edge = sub, forward = fwd });
                }
                break;
            }
        }
    }

    // Boundary masonry was deleted (or an edge died without a split
    // remap): the enclosure is breached and the room dissolves — floor,
    // roof, and record together.
    public static void NotifyBreach(WallEdge edge)
    {
        foreach (WallRoom room in new List<WallRoom>(All))
            if (!room.broken && room.ContainsEdge(edge))
                room.Break();
    }

    // Masonry landed somewhere: a room that refused a roof may have just
    // become roofable — retry every roofless one. Roofed rooms keep their
    // frozen roofY, so later walls rise past it as a parapet.
    public static void RetryRoofless()
    {
        foreach (WallRoom room in All)
            if (!room.broken && room.roofObj == null && room.TryBuildRoof() == null)
                BuildLog.Add(room.roofNote.Length == 0
                    ? "Roof added — walls now level."
                    : $"Roof added{room.roofNote}.");
    }

    // A breached room doesn't linger as a half-object: the same rule as
    // the roof applies to the floor, and the whole record dissolves.
    // (OnDestroy releases the slab meshes.)
    public void Break()
    {
        if (broken)
            return;
        broken = true;
        boundary.Clear();
        BuildLog.Add("Room breached — floor and roof removed.");
        Destroy(gameObject);
    }

    bool ContainsEdge(WallEdge edge)
    {
        foreach (WallGraph.DirEdge d in boundary)
            if (d.edge == edge)
                return true;
        return false;
    }

    // The highest room floor this wall bounds, or -Infinity if it bounds
    // none. A doorway through it has to open from that level: the floor is
    // ONE flat slab at the room's highest interior ground (see BuildFloor),
    // so on a slope it stands above the ground outside the downhill wall,
    // and a doorway cut from that ground opens into the side of the slab.
    // Highest, not lowest, because a wall between two rooms must clear
    // both — a doorway into a floor is worse than a step down onto one.
    public static float FloorServing(WallEdge edge)
    {
        float best = float.NegativeInfinity;
        foreach (WallRoom room in All)
            if (!room.broken && room.ContainsEdge(edge))
                best = Mathf.Max(best, room.floorY);
        return best;
    }

    // A doorway cut BEFORE this room was designated has its sill on the
    // ground outside, which this floor would then stand across. Raising it
    // here is the same rule the wells follow: the fact is written at both
    // of the two moments it can become true, and never re-derived after.
    //
    // Deliberately one-way. Deleting the room does not drop the sills back
    // down — nothing in the graph un-does itself, and a threshold left
    // standing is visible and removable (fill the doorway in and re-cut
    // it), where a sill silently sinking into a slab would not be.
    void RaiseSills()
    {
        foreach (WallGraph.DirEdge d in boundary)
        {
            WallEdge e = d.edge;
            if (e == null || e.openings.Count == 0)
                continue;
            bool changed = false;
            for (int i = 0; i < e.openings.Count; i++)
            {
                WallEdge.Opening o = e.openings[i];
                if (o.sill >= floorY - 0.01f)
                    continue;
                o.sill = floorY;
                e.openings[i] = o;
                changed = true;
            }
            if (!changed)
                continue;
            e.Rebuild();
            // Whether the doorway survived the lift. On a steep enough
            // slope the floor stands above this wall entirely and the
            // opening is walled up — worth saying, since the player
            // watches a door they cut disappear.
            bool survived = false;
            foreach (WallEdgeSection s in e.SectionsInOrder())
                if (s.openingIndex >= 0 && s.bottomY > floorY + 0.01f)
                    survived = true;
            BuildLog.Add(survived
                ? $"Doorway sill raised to the new floor at {floorY:0.0}m."
                : $"A doorway was walled up — this floor sits at {floorY:0.0}m,"
                    + " above that stretch of wall entirely.");
        }
    }

    // ------------------------------------------------------------------
    // Slabs
    // ------------------------------------------------------------------

    // Floor top: the base step at or above the highest ground the room
    // covers, so terrain never pokes through the slab; the skirt drops
    // below the lowest ground so dips near the walls stay closed. A room
    // whose walls stand on a deck (an upper storey) gets NO floor of its
    // own — the slab it stands on already is one, and a coplanar copy
    // would only z-fight it.
    void BuildFloor()
    {
        float maxGround = float.MinValue;
        float minGround = float.MaxValue;
        foreach (Vector3 p in GroundSamplePoints())
        {
            float g = GroundAt(p);
            maxGround = Mathf.Max(maxGround, g);
            minGround = Mathf.Min(minGround, g);
        }

        float wallBase = float.MinValue;
        foreach (WallGraph.DirEdge d in boundary)
        {
            if (d.edge == null)
                continue;
            foreach (WallEdgeSection s in d.edge.SectionsInOrder())
                // A lintel's bottom is the doorway's head, metres above
                // the ground the wall stands on. Counted here it reads as
                // "these walls start high up", and the room concludes it
                // is an upper storey and gives itself no floor.
                if (s.openingIndex < 0)
                    wallBase = Mathf.Max(wallBase, s.bottomY);
        }

        floorY = baseStep > 0f ? Mathf.Ceil(maxGround / baseStep) * baseStep : maxGround;
        if (wallBase > floorY + 0.25f)
        {
            // Elevated room: its walls stand well above the terrain, on a
            // slab that serves as its floor.
            floorY = wallBase;
            return;
        }
        float bottom = (baseStep > 0f ? Mathf.Floor(minGround / baseStep) * baseStep : minGround)
            - FloorSkirt;
        floorObj = BuildSlab("RoomFloor", outline, bottom, floorY, out floorMesh);
    }

    // The roof rests on the boundary's DOMINANT crown — the elevation the
    // greatest length of wall tops out at, over each section's masonry
    // stack (base wall plus any courses). Ties go to the lower level.
    // Anything BELOW that plane refuses: the deck would hang over a gap.
    // Masonry standing ABOVE it is allowed only where it belongs to a
    // structure that continues past this room — a borrowed keep or
    // curtain flank, which the deck simply butts into at its inner face
    // while the wall carries on upward. A span of the room's OWN ring
    // left off-level has nothing beyond it to justify the difference, so
    // it still refuses and says where: raggedness is a mistake worth
    // reporting, a lean-to against a taller wall is not.
    // Dominance is what keeps the two apart when both walls are borrowed
    // structure — three tall flanks and one short new wall roof at the
    // tall crown (and so refuse until the short one is levelled), not at
    // whatever happened to be lowest.
    // Levelling by stacking courses retries this automatically.
    // The deck sits FLUSH: its top at the plane, its footprint inset to
    // the walls' inner faces, so the wall-top rim stays walkable beside
    // it and a course stacked later rises as a parapet around it.
    // Returns null on success (or an existing roof), else the reason.
    public string TryBuildRoof()
    {
        if (broken || roofObj != null)
            return null;

        int n = boundary.Count;
        var ring = new HashSet<WallEdge>();
        foreach (WallGraph.DirEdge d in boundary)
        {
            if (d.edge == null)
                return "a boundary wall is missing";
            ring.Add(d.edge);
        }

        // Each span's own crown range, plus how much wall it is worth when
        // the levels vote on the plane.
        var lo = new float[n];
        var hi = new float[n];
        var loPos = new Vector3[n];
        var hiPos = new Vector3[n];
        var len = new float[n];
        for (int i = 0; i < n; i++)
        {
            WallEdge e = boundary[i].edge;
            List<WallEdgeSection> sections = e.SectionsInOrder();
            if (sections.Count == 0)
                return "a boundary wall has no masonry";
            lo[i] = float.MaxValue;
            hi[i] = float.MinValue;
            len[i] = Vector3.Distance(e.A, e.B);
            foreach (WallEdgeSection s in sections)
            {
                float top = s.topY;
                if (top < lo[i])
                {
                    lo[i] = top;
                    loPos[i] = s.transform.position;
                }
                if (top > hi[i])
                {
                    hi[i] = top;
                    hiPos[i] = s.transform.position;
                }
            }
        }

        // The plane: the level carrying the most boundary length, lowest
        // level winning a tie. A span votes with its low crown — the
        // elevation a deck could actually rest on there.
        float plane = float.MaxValue;
        float bestLength = -1f;
        for (int i = 0; i < n; i++)
        {
            float total = 0f;
            for (int k = 0; k < n; k++)
                if (Mathf.Abs(lo[k] - lo[i]) <= LevelEps)
                    total += len[k];
            if (total > bestLength + 0.001f
                || (total > bestLength - 0.001f && lo[i] < plane))
            {
                bestLength = total;
                plane = lo[i];
            }
        }

        // A wall that stops short of the plane leaves the deck hanging —
        // no structure beyond the room can excuse that.
        for (int i = 0; i < n; i++)
            if (plane - lo[i] > LevelEps)
                return $"wall tops uneven by {plane - lo[i]:0.0}m — lowest on the "
                    + $"{CompassFrom(loPos[i])} side (level with courses or lock-top)";

        // Spans carrying masonry above the plane, grouped into contiguous
        // runs and judged as one: a single node leaving the ring anchors
        // the whole run to the structure it belongs to. (Per-span would
        // condemn the middle of a three-span borrowed flank, whose inner
        // nodes touch nothing but the run itself.)
        var above = new bool[n];
        for (int i = 0; i < n; i++)
            above[i] = hi[i] - plane > LevelEps;
        var borrowed = new bool[n];
        for (int i = 0; i < n; i++)
        {
            if (!above[i] || borrowed[i])
                continue;
            int start = i;
            while (above[(start + n - 1) % n] && (start + n - 1) % n != i)
                start = (start + n - 1) % n;
            var run = new List<int>();
            for (int k = start; above[k]; k = (k + 1) % n)
            {
                run.Add(k);
                if (run.Count == n || (k + 1) % n == start)
                    break;
            }
            bool anchored = false;
            foreach (int k in run)
                if (LeavesRing(boundary[k].edge.nodeA, ring)
                    || LeavesRing(boundary[k].edge.nodeB, ring))
                {
                    anchored = true;
                    break;
                }
            foreach (int k in run)
                borrowed[k] = anchored;
        }

        float highest = plane;
        int tallest = -1;
        for (int i = 0; i < n; i++)
        {
            if (above[i] && !borrowed[i])
                return $"the {CompassFrom(hiPos[i])} wall stands {hi[i] - plane:0.0}m above"
                    + " the rest (level with courses or lock-top)";
            if (above[i] && hi[i] > highest)
            {
                highest = hi[i];
                tallest = i;
            }
        }

        float inset = 0.5f;
        foreach (WallGraph.DirEdge d in boundary)
            if (d.edge != null)
            {
                inset = d.edge.thickness * 0.5f;
                break;
            }
        List<Vector3> deck = InsetOutline(outline, inset);
        if (deck.Count < 3 || SignedArea(deck) < 0.5f)
            return "room too narrow for the roof slab";

        roofY = plane;
        roofNote = tallest < 0 ? ""
            : $" at {plane:0.0}m — the {CompassFrom(hiPos[tallest])} wall stands"
                + $" {highest - plane:0.0}m higher";
        deckOutline = deck;
        // Stairs that already stand get their wells now. A roof arriving
        // over a finished stair and a stair arriving under a finished roof
        // are the same fact reached from opposite directions; both have to
        // work, so both ends ask.
        wells.Clear();
        int opened = 0;
        foreach (WallStair stair in WallStair.All)
            if (AddWell(stair))
                opened++;
        if (opened > 0)
            roofNote += $" — {opened} stair well{(opened == 1 ? "" : "s")}";
        BuildRoofSlab();
        return null;
    }

    // ------------------------------------------------------------------
    // Stair wells
    // ------------------------------------------------------------------

    // A stair was committed: every roofed room its climb breaks through
    // opens a well for it.
    public static void NotifyStairBuilt(WallStair stair)
    {
        foreach (WallRoom room in All)
        {
            if (room.broken || room.roofObj == null)
                continue;
            if (!room.AddWell(stair))
                continue;
            room.BuildRoofSlab();
            BuildLog.Add("Stair well opened in the roof above.");
        }
    }

    // A stair was deleted: its wells close and the deck heals.
    public static void NotifyStairGone(WallStair stair)
    {
        foreach (WallRoom room in All)
        {
            if (room.broken || room.roofObj == null)
                continue;
            if (room.wells.RemoveAll(w => w.owner == stair) > 0)
                room.BuildRoofSlab();
        }
    }

    // Records the hole this stair needs in this deck, if it needs one.
    bool AddWell(WallStair stair)
    {
        if (stair == null || deckOutline == null)
            return false;
        foreach (Well w in wells)
            if (w.owner == stair)
                return false;
        List<Vector3> poly = WellFootprint(stair);
        if (poly == null)
            return false;
        // Only a hole that is actually over this room's deck. A stair up
        // the OUTSIDE of the same wall traces the same masonry and would
        // otherwise punch a hole in a roof it never touches.
        bool overlaps = false;
        foreach (Vector3 p in poly)
            if (PointInPolygon(p, deckOutline))
            {
                overlaps = true;
                break;
            }
        if (!overlaps)
            return false;
        wells.Add(new Well { owner = stair, poly = poly });
        return true;
    }

    // The plan footprint the climb needs open: from the tread that first
    // comes within Headroom of the deck, through the landing at the top.
    // The landing matters as much as the climb — it finishes flush AT the
    // roof plane, so left uncut it would be coplanar with the deck it is
    // supposed to arrive on.
    List<Vector3> WellFootprint(WallStair stair)
    {
        if (stair.spans.Count == 0)
            return null;
        float rise = stair.topY - stair.bottomY;
        if (rise < 0.05f)
            return null;
        // The elevation a climber's feet are at when their head first
        // needs the hole — Headroom of clear air under the deck's soffit,
        // which is RoofThickness below the deck's top.
        float openAt = roofY - RoofThickness - Headroom;
        // Standing on this deck, or never reaching it: no hole either way.
        if (stair.bottomY >= roofY - 0.01f || stair.topY <= openAt + 0.01f)
            return null;

        float runArc = stair.runArc > 0.01f ? stair.runArc : stair.TotalArc;

        // Where along the run that elevation falls — counted in TREADS,
        // not along the straight line between the stair's ends. A stair
        // is not a ramp: the step you actually stand on is up to a whole
        // riser above that line, and taking the line for the climb is
        // what let a well be cut a tread and a half too late.
        int steps = WallStair.StepsFor(rise);
        float riser = rise / steps;
        float going = runArc / steps;
        // The last tread whose TOP still leaves a full Headroom under the
        // soffit. Everything above it needs the hole.
        int lastClear = Mathf.FloorToInt((openAt - stair.bottomY) / riser);
        float from = Mathf.Clamp(lastClear * going - WellLead, 0f, runArc);

        return StripFootprint(stair, from, stair.TotalArc,
            stair.width * 0.5f - WellLip);
    }

    // The stair's own curve, walked between two arc positions and given a
    // width: the run's plan shape, corners and all, rather than a box
    // around it. A stair that turns a corner gets a hole that turns too.
    static List<Vector3> StripFootprint(WallStair stair, float from, float to, float half)
    {
        var left = new List<Vector3>();
        var right = new List<Vector3>();
        foreach (WallStair.Span sp in stair.spans)
        {
            float lo = Mathf.Max(from, sp.arcStart);
            float hi = Mathf.Min(to, sp.arcEnd);
            if (hi - lo < 0.01f)
                continue;
            float[] table = WallEdge.BuildArcTable(sp.s, sp.c, sp.e);
            for (int i = 0; i <= WellSamplesPerSpan; i++)
            {
                float arc = Mathf.Lerp(lo, hi, (float)i / WellSamplesPerSpan) - sp.arcStart;
                float t = WallEdge.TAtArc(table, arc);
                Vector3 p = WallEdge.Evaluate(sp.s, sp.c, sp.e, t);
                Vector3 d = WallEdge.Tangent(sp.s, sp.c, sp.e, t);
                d.y = 0f;
                if (d.sqrMagnitude < 1e-8f)
                    continue;
                d.Normalize();
                Vector3 side = new Vector3(-d.z, 0f, d.x) * half;
                left.Add(new Vector3(p.x + side.x, 0f, p.z + side.z));
                right.Add(new Vector3(p.x - side.x, 0f, p.z - side.z));
            }
        }
        if (left.Count < 2)
            return null;
        right.Reverse();
        var poly = new List<Vector3>(left);
        poly.AddRange(right);
        poly = Dedupe(poly);
        if (poly.Count < 3 || Mathf.Abs(SignedArea(poly)) < 0.05f)
            return null;
        if (SignedArea(poly) < 0f)
            poly.Reverse();
        return poly;
    }

    // Span joints repeat a point, and ear clipping treats a repeat as a
    // zero-area ear it can never remove.
    static List<Vector3> Dedupe(List<Vector3> poly)
    {
        var result = new List<Vector3>(poly.Count);
        foreach (Vector3 p in poly)
            if (result.Count == 0 || (result[result.Count - 1] - p).sqrMagnitude > 4e-4f)
                result.Add(p);
        while (result.Count > 1 && (result[0] - result[result.Count - 1]).sqrMagnitude <= 4e-4f)
            result.RemoveAt(result.Count - 1);
        return result;
    }

    void BuildRoofSlab()
    {
        // Immediate, not deferred: a well is cut in the middle of a commit
        // and the old deck's collider would otherwise stay in the ray's
        // way for the rest of the frame — the same reason WallStair
        // rebuilds its treads immediately.
        if (roofObj != null)
            DestroyImmediate(roofObj);
        if (roofMesh != null)
            DestroyImmediate(roofMesh);
        var holes = new List<List<Vector3>>();
        foreach (Well w in wells)
            if (w.owner != null && w.poly != null)
                holes.Add(w.poly);
        roofObj = BuildSlab("RoomRoof", deckOutline, holes, roofY - RoofThickness,
            roofY, out roofMesh);
    }

    // Does this node carry masonry the room's ring doesn't own — i.e.
    // does the wall continue past the room here? That's what tells a
    // borrowed flank from one of the room's own walls left off-level.
    static bool LeavesRing(WallNode node, HashSet<WallEdge> ring)
    {
        if (node == null)
            return false;
        foreach (WallNode.Member m in node.members)
            if (!ring.Contains(m.edge))
                return true;
        return false;
    }

    // The outline pushed inward by `inset` (mitered per vertex) — the
    // roof deck's footprint, meeting the walls at their inner faces.
    static List<Vector3> InsetOutline(List<Vector3> poly, float inset)
    {
        int n = poly.Count;
        var result = new List<Vector3>(n);
        for (int i = 0; i < n; i++)
        {
            Vector3 prev = poly[(i + n - 1) % n];
            Vector3 cur = poly[i];
            Vector3 next = poly[(i + 1) % n];
            Vector3 d1 = cur - prev;
            Vector3 d2 = next - cur;
            d1.y = 0f;
            d2.y = 0f;
            if (d1.sqrMagnitude < 1e-8f || d2.sqrMagnitude < 1e-8f)
                continue;
            d1.Normalize();
            d2.Normalize();
            // Left of travel is inward on a CCW outline; the miter keeps
            // corner offsets true, clamped so near-spikes can't explode.
            Vector3 n1 = new Vector3(-d1.z, 0f, d1.x);
            Vector3 n2 = new Vector3(-d2.z, 0f, d2.x);
            Vector3 m = n1 + n2;
            if (m.sqrMagnitude < 1e-6f)
                continue;
            m.Normalize();
            result.Add(cur + m * (inset / Mathf.Max(0.25f, Vector3.Dot(m, n1))));
        }
        return result;
    }

    // Which side of the room a point sits on, as a compass octant from
    // the outline's centroid — so a failed roof can name its culprit.
    string CompassFrom(Vector3 p)
    {
        Vector3 center = Vector3.zero;
        foreach (Vector3 v in outline)
            center += v;
        if (outline.Count > 0)
            center /= outline.Count;
        Vector3 d = p - center;
        if (new Vector2(d.x, d.z).sqrMagnitude < 0.01f)
            return "center";
        float bearing = Mathf.Atan2(d.z, d.x) * Mathf.Rad2Deg;
        string[] octants = { "east", "northeast", "north", "northwest",
            "west", "southwest", "south", "southeast" };
        return octants[Mathf.RoundToInt(Mathf.Repeat(bearing, 360f) / 45f) % 8];
    }


    GameObject BuildSlab(string name, List<Vector3> poly, float y0, float y1, out Mesh mesh)
    {
        return BuildSlab(name, poly, null, y0, y1, out mesh);
    }

    GameObject BuildSlab(string name, List<Vector3> poly, List<List<Vector3>> holes,
        float y0, float y1, out Mesh mesh)
    {
        mesh = BuildSlabMesh(poly, holes, y0, y1, baseWallHeight);
        mesh.name = name;
        GameObject obj = new GameObject(name);
        obj.transform.SetParent(transform, false);
        obj.AddComponent<MeshFilter>().sharedMesh = mesh;
        obj.AddComponent<MeshRenderer>().sharedMaterial = material;
        // Cursor raycasts only (targeting reads the surface point) —
        // never occupancy.
        obj.AddComponent<MeshCollider>().sharedMesh = mesh;
        return obj;
    }

    // Outline points plus an interior grid, so a terrain bump in the
    // middle of a large room still lifts the floor above it.
    IEnumerable<Vector3> GroundSamplePoints()
    {
        Vector3 min = outline[0];
        Vector3 max = outline[0];
        foreach (Vector3 p in outline)
        {
            min = Vector3.Min(min, p);
            max = Vector3.Max(max, p);
            yield return p;
        }
        for (float x = min.x; x <= max.x; x += InteriorSampleStep)
            for (float z = min.z; z <= max.z; z += InteriorSampleStep)
            {
                Vector3 p = new Vector3(x, 0f, z);
                if (PointInPolygon(p, outline))
                    yield return p;
            }
    }

    static float GroundAt(Vector3 p)
    {
        Terrain terrain = Terrain.activeTerrain;
        if (terrain == null)
            return 0f;
        return terrain.transform.position.y + terrain.SampleHeight(p);
    }

    // ------------------------------------------------------------------
    // Geometry helpers
    // ------------------------------------------------------------------

    // The ring's world outline: each directed edge sampled along its own
    // curve, start point included, end point left to the next edge (the
    // shared node), so joints contribute exactly one point.
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

    // The slab: triangulated caps (top faces up, bottom down), an outward
    // side band, and an inward band around every hole. Cap UVs are
    // world-planar and side V is world-height anchored, matching the
    // walls' masonry scale.
    //
    // Holes are cut by BRIDGING them into the outline — the keyhole trick
    // — so the caps stay one simple polygon and the existing ear clipper
    // needs no notion of holes at all. A hole's rim band is the same code
    // as the outer band walked backwards, which turns it to face into the
    // well.
    static Mesh BuildSlabMesh(List<Vector3> poly, List<List<Vector3>> holes,
        float y0, float y1, float baseWallHeight)
    {
        var vertices = new List<Vector3>();
        var uvs = new List<Vector2>();
        var triangles = new List<int>();

        List<Vector3> cap = poly;
        if (holes != null)
            foreach (List<Vector3> hole in holes)
                if (hole != null && hole.Count >= 3)
                    cap = BridgeHole(cap, hole);
        List<int> capTris = Triangulate(cap);

        for (int side = 0; side < 2; side++)
        {
            bool isTop = side == 0;
            float y = isTop ? y1 : y0;
            int baseIndex = vertices.Count;
            foreach (Vector3 p in cap)
            {
                vertices.Add(new Vector3(p.x, y, p.z));
                uvs.Add(new Vector2(p.x / baseWallHeight, p.z / baseWallHeight));
            }
            for (int i = 0; i < capTris.Count; i += 3)
            {
                if (isTop)
                {
                    triangles.Add(baseIndex + capTris[i]);
                    triangles.Add(baseIndex + capTris[i + 2]);
                    triangles.Add(baseIndex + capTris[i + 1]);
                }
                else
                {
                    triangles.Add(baseIndex + capTris[i]);
                    triangles.Add(baseIndex + capTris[i + 1]);
                    triangles.Add(baseIndex + capTris[i + 2]);
                }
            }
        }

        AddBand(vertices, uvs, triangles, poly, y0, y1, baseWallHeight);
        if (holes != null)
            foreach (List<Vector3> hole in holes)
            {
                if (hole == null || hole.Count < 3)
                    continue;
                var inward = new List<Vector3>(hole);
                inward.Reverse();
                AddBand(vertices, uvs, triangles, inward, y0, y1, baseWallHeight);
            }

        Mesh mesh = new Mesh { name = "RoomSlab" };
        mesh.SetVertices(vertices);
        mesh.SetUVs(0, uvs);
        mesh.SetTriangles(triangles, 0);
        mesh.RecalculateNormals();
        mesh.RecalculateBounds();
        return mesh;
    }

    static void AddBand(List<Vector3> vertices, List<Vector2> uvs, List<int> triangles,
        List<Vector3> ring, float y0, float y1, float baseWallHeight)
    {
        float u = 0f;
        for (int i = 0; i < ring.Count; i++)
        {
            Vector3 a = ring[i];
            Vector3 b = ring[(i + 1) % ring.Count];
            float len = Vector3.Distance(a, b);
            int q = vertices.Count;
            vertices.Add(new Vector3(a.x, y0, a.z));
            uvs.Add(new Vector2(u, y0 / baseWallHeight));
            vertices.Add(new Vector3(a.x, y1, a.z));
            uvs.Add(new Vector2(u, y1 / baseWallHeight));
            vertices.Add(new Vector3(b.x, y0, b.z));
            uvs.Add(new Vector2(u + len, y0 / baseWallHeight));
            vertices.Add(new Vector3(b.x, y1, b.z));
            uvs.Add(new Vector2(u + len, y1 / baseWallHeight));
            triangles.Add(q); triangles.Add(q + 1); triangles.Add(q + 2);
            triangles.Add(q + 1); triangles.Add(q + 3); triangles.Add(q + 2);
            u += len;
        }
    }

    // Splices a hole into the outline through the shortest bridge that
    // crosses nothing. The hole is walked BACKWARDS so it winds against
    // the outline, which is what makes the seam close and the interior
    // read as removed; the bridge vertices appear twice, giving two
    // zero-width edges the ear clipper walks straight past.
    static List<Vector3> BridgeHole(List<Vector3> outer, List<Vector3> hole)
    {
        int n = outer.Count, m = hole.Count;
        if (n < 3 || m < 3)
            return outer;
        int bi = -1, bj = -1;
        float best = float.MaxValue;
        for (int i = 0; i < n; i++)
            for (int j = 0; j < m; j++)
            {
                float d = (outer[i] - hole[j]).sqrMagnitude;
                if (d >= best || !BridgeClear(outer, hole, i, j))
                    continue;
                best = d;
                bi = i;
                bj = j;
            }
        // No clear bridge means the hole isn't cleanly inside the deck —
        // leave the slab whole rather than tear it open along a bad seam.
        if (bi < 0)
            return outer;

        var merged = new List<Vector3>(n + m + 2);
        for (int i = 0; i <= bi; i++)
            merged.Add(outer[i]);
        for (int k = 0; k <= m; k++)
            merged.Add(hole[((bj - k) % m + m) % m]);
        merged.Add(outer[bi]);
        for (int i = bi + 1; i < n; i++)
            merged.Add(outer[i]);
        return merged;
    }

    static bool BridgeClear(List<Vector3> outer, List<Vector3> hole, int i, int j)
    {
        Vector3 a = outer[i], b = hole[j];
        int n = outer.Count, m = hole.Count;
        for (int k = 0; k < n; k++)
            if (k != i && (k + 1) % n != i
                && SegmentsCross(a, b, outer[k], outer[(k + 1) % n]))
                return false;
        for (int k = 0; k < m; k++)
            if (k != j && (k + 1) % m != j
                && SegmentsCross(a, b, hole[k], hole[(k + 1) % m]))
                return false;
        return true;
    }

    // Proper crossing only: segments that merely touch at an endpoint are
    // not in the way, or no bridge would ever be clear.
    static bool SegmentsCross(Vector3 p1, Vector3 p2, Vector3 p3, Vector3 p4)
    {
        float d1 = Side(p3, p4, p1), d2 = Side(p3, p4, p2);
        float d3 = Side(p1, p2, p3), d4 = Side(p1, p2, p4);
        return ((d1 > 1e-6f && d2 < -1e-6f) || (d1 < -1e-6f && d2 > 1e-6f))
            && ((d3 > 1e-6f && d4 < -1e-6f) || (d3 < -1e-6f && d4 > 1e-6f));
    }

    static float Side(Vector3 a, Vector3 b, Vector3 p)
    {
        return (b.x - a.x) * (p.z - a.z) - (b.z - a.z) * (p.x - a.x);
    }

    // Ear clipping over a CCW polygon. Collinear runs (the outline is
    // dense with them along straight walls) are skipped as ears; if the
    // remainder degenerates to a collinear ring, fan it and stop rather
    // than loop.
    static List<int> Triangulate(List<Vector3> poly)
    {
        var tris = new List<int>();
        var idx = new List<int>();
        for (int i = 0; i < poly.Count; i++)
            idx.Add(i);

        int guard = poly.Count * poly.Count + 16;
        while (idx.Count > 3 && guard-- > 0)
        {
            bool clipped = false;
            for (int i = 0; i < idx.Count; i++)
            {
                int i0 = idx[(i + idx.Count - 1) % idx.Count];
                int i1 = idx[i];
                int i2 = idx[(i + 1) % idx.Count];
                Vector3 a = poly[i0], b = poly[i1], c = poly[i2];
                float cross = (b.x - a.x) * (c.z - a.z) - (b.z - a.z) * (c.x - a.x);
                if (cross <= 1e-5f)
                    continue;
                bool contains = false;
                foreach (int j in idx)
                {
                    if (j == i0 || j == i1 || j == i2)
                        continue;
                    // A bridged hole repeats its two seam vertices, and a
                    // repeat sits exactly ON the ear it duplicates — count
                    // it and no ear near the seam is ever clippable, which
                    // drops the whole cap to the fan fallback and fills
                    // the well back in.
                    Vector3 q = poly[j];
                    if (Coincident(q, a) || Coincident(q, b) || Coincident(q, c))
                        continue;
                    if (PointInTriangle(q, a, b, c))
                    {
                        contains = true;
                        break;
                    }
                }
                if (contains)
                    continue;
                tris.Add(i0); tris.Add(i1); tris.Add(i2);
                idx.RemoveAt(i);
                clipped = true;
                break;
            }
            if (!clipped)
            {
                for (int i = 1; i + 1 < idx.Count; i++)
                {
                    tris.Add(idx[0]); tris.Add(idx[i]); tris.Add(idx[i + 1]);
                }
                return tris;
            }
        }
        if (idx.Count == 3)
        {
            tris.Add(idx[0]); tris.Add(idx[1]); tris.Add(idx[2]);
        }
        return tris;
    }

    static bool Coincident(Vector3 a, Vector3 b)
    {
        return (a - b).sqrMagnitude < 1e-6f;
    }

    static bool PointInTriangle(Vector3 p, Vector3 a, Vector3 b, Vector3 c)
    {
        float s1 = (b.x - a.x) * (p.z - a.z) - (b.z - a.z) * (p.x - a.x);
        float s2 = (c.x - b.x) * (p.z - b.z) - (c.z - b.z) * (p.x - b.x);
        float s3 = (a.x - c.x) * (p.z - c.z) - (a.z - c.z) * (p.x - c.x);
        return s1 >= -1e-6f && s2 >= -1e-6f && s3 >= -1e-6f;
    }
}
