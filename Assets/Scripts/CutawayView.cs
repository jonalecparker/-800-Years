using UnityEngine;
using UnityEngine.InputSystem;

// A cutaway: a horizontal plane through the world. Masonry that crosses it
// is SLICED — you see the part below and nothing above — and masonry
// entirely above it is hidden and un-collidable.
//
// Slicing, not hiding, is the whole point. An all-or-nothing rule makes
// every object vanish at whatever height its own bottom sits at, so a room
// loses its roof, then its walls, then its corner posts, in three separate
// steps that have nothing to do with each other; and the one view you
// actually want — walls cut to knee height so you can see and work inside
// the room — doesn't exist at any level. One plane cuts everything at the
// same elevation because it IS the same elevation.
//
// The slice is a transform scale, not a mesh rebuild: every mesh here is a
// vertical prism (wall sections, node wedges, stair treads, room slabs all
// sweep a flat top and a flat bottom), so scaling Y about the object's own
// bottom moves the top face down and leaves everything else exact. That
// keeps view state out of geometry — no WallEdge, WallNode or WallStair
// code knows this exists, and nothing rebuilds because the cut moved.
//
// The collider rides along for free (it scales with the transform), which
// is what makes building inside a cut room possible at all: the near wall
// and the roof are the first things the cursor ray hits, and pointing at
// something you can't see is worse than not being able to build there.
// What you can see is what you can hit.
//
// Deliberately NOT in GridPlacementSystem.ModifierHints: that panel
// reports what the tool IN HAND reads, and this is a view control like the
// camera. Its own readout carries its keys instead.
//
// Static, and ticked from GridPlacementSystem.Update, so it needs no
// component in the scene.
public static class CutawayView
{
    // +Infinity = off, everything visible.
    public static float Level { get; private set; } = float.PositiveInfinity;
    public static bool Active => !float.IsPositiveInfinity(Level);

    const float Step = 0.5f;
    // Nothing is ever cut below this much above the lowest masonry —
    // slicing the world down to nothing reads as "my castle vanished".
    const float MinSlab = 0.5f;
    // A slice thinner than this isn't masonry any more, it's a smear.
    const float MinSlice = 0.02f;

    public static void Tick(Keyboard keyboard, Transform root)
    {
        if (keyboard != null && root != null)
            HandleKeys(keyboard, root);
        Apply(root);
    }

    // Is this world point ON the cut plane rather than on real masonry?
    //
    // A sliced wall's exposed top has an upward normal exactly like a real
    // wall top, so the build-surface resolvers would offer to stack a
    // storey on it — at the wall's REAL top, metres above the surface the
    // player is looking at. The cut is a view; what it exposes is a
    // cross-section, not a build surface.
    public static bool IsCutSurface(Vector3 point)
    {
        return Active && Mathf.Abs(point.y - Level) < 0.06f;
    }

    static void HandleKeys(Keyboard keyboard, Transform root)
    {
        bool down = keyboard.pageDownKey.wasPressedThisFrame;
        bool up = keyboard.pageUpKey.wasPressedThisFrame;
        if (keyboard.homeKey.wasPressedThisFrame)
        {
            Level = float.PositiveInfinity;
            return;
        }
        if (!down && !up)
            return;

        if (!WorldSpan(root, out float lowest, out float highest))
            return;

        // First press starts just under the tallest thing standing, which
        // is the press that means "show me under the roof".
        if (!Active)
        {
            Level = Mathf.Floor((highest - 0.01f) / Step) * Step;
            if (down)
                return;
        }

        Level = Mathf.Clamp(Level + (up ? Step : -Step), lowest + MinSlab,
            Mathf.Ceil(highest / Step) * Step + Step);
        // Raised clear of everything, the cut turns itself off rather than
        // sitting one step above the roofline pretending to do something.
        if (Level > highest + 0.001f)
            Level = float.PositiveInfinity;
    }

    // The unclipped vertical extent of everything committed. Measured
    // through Span, never through Renderer.bounds, because a renderer this
    // has already sliced reports the SLICE — read that back and the cut
    // would think it had reached the top of the world one step above
    // wherever it currently sits, and switch itself off.
    static bool WorldSpan(Transform root, out float lowest, out float highest)
    {
        lowest = float.PositiveInfinity;
        highest = float.NegativeInfinity;
        foreach (Renderer r in root.GetComponentsInChildren<Renderer>(true))
        {
            if (r == null || !TrySpan(r, out _, out float bottom, out float top))
                continue;
            lowest = Mathf.Min(lowest, bottom);
            highest = Mathf.Max(highest, top);
        }
        return highest > lowest;
    }

    // Where this renderer's masonry starts and ends in world Y with the
    // slice undone, plus the Y its transform sits at when whole.
    //
    // Rotations in this world are yaw-only and slicing only ever touches
    // scale.y, so a local vertex maps to world Y by adding the pivot — no
    // matrix needed, and the answer stays true while the object is sliced.
    static bool TrySpan(Renderer r, out float pivotY, out float bottom, out float top)
    {
        pivotY = 0f;
        bottom = 0f;
        top = 0f;
        MeshFilter filter = r.GetComponent<MeshFilter>();
        Mesh mesh = filter != null ? filter.sharedMesh : null;
        if (mesh == null)
            return false;

        CutawayPivot pivot = r.GetComponent<CutawayPivot>();
        if (pivot == null)
        {
            pivot = r.gameObject.AddComponent<CutawayPivot>();
            pivot.authoredY = r.transform.position.y;
        }
        pivotY = pivot.authoredY;
        bottom = pivotY + mesh.bounds.min.y;
        top = pivotY + mesh.bounds.max.y;
        return true;
    }

    // One array allocation a frame over the committed subtree — a few
    // hundred renderers at this scale. Revisit alongside FaceAt if edge
    // counts ever reach the hundreds.
    static void Apply(Transform root)
    {
        if (root == null)
            return;
        float cut = Level;
        foreach (Renderer r in root.GetComponentsInChildren<Renderer>(true))
        {
            if (r == null || !TrySpan(r, out float pivotY, out float bottom, out float top))
                continue;

            float height = top - bottom;
            // Whole: no cut at all, or the cut passes over its head.
            if (float.IsPositiveInfinity(cut) || cut >= top - 0.001f)
            {
                Restore(r, pivotY);
                Show(r, true);
                continue;
            }
            float slice = cut - bottom;
            // A flat thing — a deck — has nothing to slice: it is simply
            // above the cut or below it.
            //
            // Either way the transform is stood back up BEFORE hiding: the
            // slice is re-derived from the live transform every frame, so
            // leaving one collapsed would poison the next frame's
            // arithmetic.
            if (height <= MinSlice || slice < MinSlice)
            {
                Restore(r, pivotY);
                Show(r, height <= MinSlice && slice > 0f);
                continue;
            }
            Slice(r, pivotY, bottom, slice / height);
            Show(r, true);
        }
    }

    // Scales Y about the object's own bottom: the masonry stays planted
    // where it was built and only its top comes down.
    static void Slice(Renderer r, float pivotY, float bottom, float k)
    {
        Transform t = r.transform;
        float wantY = bottom * (1f - k) + k * pivotY;
        Vector3 scale = t.localScale;
        if (!Mathf.Approximately(scale.y, k))
        {
            scale.y = k;
            t.localScale = scale;
        }
        Vector3 pos = t.position;
        if (!Mathf.Approximately(pos.y, wantY))
        {
            pos.y = wantY;
            t.position = pos;
        }
    }

    static void Restore(Renderer r, float pivotY)
    {
        Transform t = r.transform;
        Vector3 scale = t.localScale;
        if (!Mathf.Approximately(scale.y, 1f))
        {
            scale.y = 1f;
            t.localScale = scale;
        }
        Vector3 pos = t.position;
        if (!Mathf.Approximately(pos.y, pivotY))
        {
            pos.y = pivotY;
            t.position = pos;
        }
    }

    static void Show(Renderer r, bool visible)
    {
        if (r.enabled != visible)
            r.enabled = visible;
        Collider col = r.GetComponent<Collider>();
        if (col != null && col.enabled != visible)
            col.enabled = visible;
    }

    public static string Readout()
    {
        return Active
            ? $"Cutaway {Level:0.0}m — PgUp/PgDn, Home to clear"
            : null;
    }
}
