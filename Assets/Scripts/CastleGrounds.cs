using System.Collections.Generic;
using UnityEngine;

// The castle's grounds are shaped by the TERRAIN, not by the survey
// grid (the user's call, 2026-08-12: "the castle area should be
// centered around the castle site and its limits should be determined
// by the terrain around it"). From the site, radial rays march outward
// and stop where the land falls away or climbs — the defensible
// platform's edge — clamped between a min and max radius. The result
// is a free polygon: the castle parcel, the boundary parcel corners
// snap to, and the line the castle roads are trimmed at (the trim
// point is the gate).
//
// Deterministic and edit-mode safe: the Dress bake trims its painted
// routes with the same outlines the runtime survey uses, so paint,
// parcels and tree keep-outs agree by construction.
public static class CastleGrounds
{
    const int Rays = 36;
    const float Step = 4f;

    // LandSurvey.Instance only registers in play mode (OnEnable); the
    // Dress bake runs in edit mode and must read the SAME dials or the
    // painted trim and the surveyed boundary would disagree.
    static LandSurvey Survey => LandSurvey.Instance != null
        ? LandSurvey.Instance
        : Object.FindAnyObjectByType<LandSurvey>(FindObjectsInactive.Include);

    static float MinRadius
    {
        get { LandSurvey s = Survey; return s != null ? Mathf.Max(20f, s.castleGroundsMin) : 60f; }
    }
    static float MaxRadius
    {
        get { LandSurvey s = Survey; return s != null ? Mathf.Max(MinRadius, s.castleGroundsMax) : 140f; }
    }
    static float Relief
    {
        get { LandSurvey s = Survey; return s != null ? Mathf.Max(1f, s.castleGroundsRelief) : 6f; }
    }

    // How far past its own outline a castle still claims a road end as
    // its gate — the nearest road vertex to a site can sit outside the
    // grounds (CastleRoads.CastleSnapRange is 400m).
    public const float GateReach = 600f;

    public static List<Vector3> Outline(Vector2 site)
    {
        float siteY = Ground.HeightAt(site.x, site.y);
        float relief = Relief;
        float min = MinRadius, max = MaxRadius;
        var radii = new float[Rays];
        for (int i = 0; i < Rays; i++)
        {
            float ang = i * Mathf.PI * 2f / Rays;
            var dir = new Vector2(Mathf.Cos(ang), Mathf.Sin(ang));
            float r = min;
            while (r + Step <= max)
            {
                Vector2 p = site + dir * (r + Step);
                if (Mathf.Abs(Ground.HeightAt(p.x, p.y) - siteY) > relief)
                    break;
                r += Step;
            }
            radii[i] = r;
        }
        // Two smoothing passes so the boundary reads as a ward wall
        // line, not a star of ray spikes. Averaging can't leave the
        // [min, max] band the raw radii already occupy.
        for (int pass = 0; pass < 2; pass++)
        {
            var s = new float[Rays];
            for (int i = 0; i < Rays; i++)
                s[i] = (radii[(i + Rays - 1) % Rays] + radii[i] * 2f
                    + radii[(i + 1) % Rays]) * 0.25f;
            radii = s;
        }
        var outline = new List<Vector3>(Rays);
        for (int i = 0; i < Rays; i++)
        {
            float ang = i * Mathf.PI * 2f / Rays;
            outline.Add(new Vector3(site.x + Mathf.Cos(ang) * radii[i], 0f,
                site.y + Mathf.Sin(ang) * radii[i]));
        }
        return outline;
    }

    public static List<List<Vector3>> Outlines(List<Vector2> sites)
    {
        var all = new List<List<Vector3>>(sites.Count);
        foreach (Vector2 s in sites)
            all.Add(Outline(s));
        return all;
    }
}
