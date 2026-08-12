using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering.HighDefinition;

// Two-tier river water (Docs/Rivers.md): cheap translucent ribbons
// everywhere, real HDRP water surfaces on the chunks near the camera —
// each upgraded chunk hides its ribbon renderer and gets a WaterSurface
// whose custom geometry is the SAME chunk mesh, so the water occupies
// exactly the footprint the ribbon did. Walking away swaps back.
//
// Ticks every frame BY DESIGN with no stand-down — it's a view system
// like tree LOD: it reads no input, and walk mode is where it matters
// most. Requires water support in the HDRP asset (a one-time settings
// flip); without it the surfaces render nothing and the ribbons still
// carry the look, so the failure mode is cosmetic, never broken.
public class RiverWaterLOD : MonoBehaviour
{
    public float upgradeRadius = 260f;
    // Each active surface is its own GPU simulation — keep the pool small.
    public int maxActive = 4;
    public float checkInterval = 0.5f;
    [Header("Far lift")]
    // The terrain's distance LOD carries ~pixelError (2px) of SCREEN-space
    // height error, which is metres of world error at a kilometre — more
    // than any fixed freeboard, so far ribbons kept getting z-killed in
    // dashes (the user's second screenshot). The counter is scaled the
    // same way the error is: lift far chunks by a few metres that are
    // sub-pixel AT THAT DISTANCE. A VIEW correction owned by this view
    // system — never baked into the meshes, zeroed near the camera and on
    // an HDRP upgrade.
    public float liftStart = 250f;
    public float liftPerMeter = 0.004f;
    public float maxLift = 8f;

    [Header("Waves")]
    // HDRP's river defaults are SEA-sized (largeWindSpeed 30) — big
    // rolling swells that read worse the smaller the river (the user's
    // eye, first water test). These dial the motion to brook scale, and
    // Upgrade() scales them by the chunk's widest river (RiverChunk
    // .maxWidth) so the Monnow keeps visible flow while a 2m stream
    // barely stirs.
    public float swellWindSpeed = 3f;
    public float swellChaos = 0.6f;
    public float rippleWindSpeed = 4.5f;
    public float rippleChaos = 0.85f;
    // A gentle default current gives the ripples a consistent drift —
    // "flow" without per-reach direction data (deferred with foam).
    public float currentSpeed = 0.45f;
    public float timeMultiplier = 0.8f;

    // Custom-mesh water keeps the mesh's own vertex heights — HDRP sets
    // waterToWorld to IDENTITY for custom meshes (WaterSurface.Simulation
    // .cs, read before trusting this), so a sloping chunk renders as
    // sloping water and banks are never clipped. This guard is therefore
    // COSMETIC: the ripple simulation is a horizontal model, and a reach
    // falling more than this across one chunk is a torrent that would
    // shimmer like a lake tipped on its side — it keeps its ribbon.
    // Reads the RiverChunk numbers the bake stored.
    public float maxChunkFall = 8f;

    float nextCheck;
    RiverChunk[] chunks;
    readonly Dictionary<RiverChunk, GameObject> active = new();

    void Update()
    {
        if (Time.unscaledTime < nextCheck)
            return;
        nextCheck = Time.unscaledTime + checkInterval;
        // Re-dressing rebuilds the chunk set under us — refind when any die.
        if (chunks == null || chunks.Length == 0 || AnyDead())
            chunks = FindObjectsByType<RiverChunk>();
        Camera cam = Camera.main;
        if (cam == null)
            return;
        Vector3 eye = cam.transform.position;

        var near = new List<(float d, RiverChunk c)>();
        foreach (RiverChunk c in chunks)
        {
            if (c == null)
                continue;
            var rend = c.GetComponent<MeshRenderer>();
            if (rend == null)
                continue;
            float d = Mathf.Sqrt(rend.bounds.SqrDistance(eye));
            ApplyLift(c, active.ContainsKey(c) ? 0f
                : Mathf.Min(maxLift, Mathf.Max(0f, (d - liftStart) * liftPerMeter)));
            if (d <= upgradeRadius && c.maxWaterY - c.minWaterY <= maxChunkFall)
                near.Add((d, c));
        }
        near.Sort((a, b) => a.d.CompareTo(b.d));

        var chosen = new HashSet<RiverChunk>();
        for (int i = 0; i < near.Count && chosen.Count < maxActive; i++)
            chosen.Add(near[i].c);

        var drop = new List<RiverChunk>();
        foreach (KeyValuePair<RiverChunk, GameObject> kv in active)
            if (kv.Key == null || !chosen.Contains(kv.Key))
                drop.Add(kv.Key);
        foreach (RiverChunk c in drop)
            Downgrade(c);
        foreach (RiverChunk c in chosen)
            if (!active.ContainsKey(c))
                Upgrade(c);
    }

    bool AnyDead()
    {
        foreach (RiverChunk c in chunks)
            if (c == null)
                return true;
        return false;
    }

    static void ApplyLift(RiverChunk c, float lift)
    {
        Transform tr = c.transform;
        Vector3 p = tr.position;
        if (Mathf.Abs(p.y - lift) > 0.01f)
            tr.position = new Vector3(p.x, lift, p.z);
    }

    void Upgrade(RiverChunk c)
    {
        // The HDRP surface uses the mesh at its true height — no lift.
        ApplyLift(c, 0f);
        var rend = c.GetComponent<MeshRenderer>();
        var go = new GameObject("RiverWater " + c.name);
        go.transform.SetParent(transform, false);
        // The mesh's world-space vertices ARE the water surface (see
        // maxChunkFall); the transform only anchors decal/current mapping,
        // so seat it at the chunk's center and mean level.
        Vector3 center = rend.bounds.center;
        go.transform.position = new Vector3(
            center.x, (c.minWaterY + c.maxWaterY) * 0.5f, center.z);
        var ws = go.AddComponent<WaterSurface>();
        ws.surfaceType = WaterSurfaceType.River;
        ws.geometryType = WaterGeometryType.Custom;
        ws.meshRenderers = new List<MeshRenderer> { rend };
        // Brook-scale motion (see the Waves header). maxWidth is 0 on
        // chunks from an older bake — the clamp floor covers them.
        float scale = Mathf.Clamp(c.maxWidth / 8f, 0.3f, 1f);
        ws.timeMultiplier = timeMultiplier;
        ws.largeWindSpeed = swellWindSpeed * scale;
        ws.largeChaos = swellChaos;
        ws.largeBand0Multiplier = scale;
        ws.ripplesWindSpeed = rippleWindSpeed * Mathf.Lerp(0.6f, 1f, scale);
        ws.ripplesChaos = rippleChaos;
        ws.largeCurrentSpeedValue = currentSpeed * scale;
        ws.ripplesCurrentSpeedValue = currentSpeed;
        // Hide the cheap translucent draw — HDRP draws the water on this
        // mesh EXPLICITLY (cmd.DrawMesh in the water system), so the
        // renderer's enabled state doesn't affect the water itself.
        rend.enabled = false;
        active[c] = go;
    }

    void Downgrade(RiverChunk c)
    {
        if (active.TryGetValue(c, out GameObject go) && go != null)
            Destroy(go);
        active.Remove(c);
        if (c != null)
        {
            var rend = c.GetComponent<MeshRenderer>();
            if (rend != null)
                rend.enabled = true;
        }
    }

    void OnDisable()
    {
        foreach (KeyValuePair<RiverChunk, GameObject> kv in active)
        {
            if (kv.Value != null)
                Destroy(kv.Value);
            if (kv.Key != null)
            {
                var rend = kv.Key.GetComponent<MeshRenderer>();
                if (rend != null)
                    rend.enabled = true;
            }
        }
        active.Clear();
        if (chunks != null)
            foreach (RiverChunk c in chunks)
                if (c != null)
                    ApplyLift(c, 0f);
    }
}
