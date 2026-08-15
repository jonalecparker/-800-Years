#!/usr/bin/env python3
"""Bake Assets/Terrain/MarchesFeatures.json from OpenStreetMap.

The offline half of the Marches dressing (Docs/WorldFrame.md): Unity
never fetches, so this script talks to Overpass, projects every
feature into the BNG world frame (world X = easting - 332000, world
Z = northing - 210000 -- WorldFrame.cs is the authority; the math
here is the same WGS84->OSGB36 Helmert + Airy transverse Mercator,
kept in sync BY HAND if WorldFrame's constants ever change), thins
the polylines with Douglas-Peucker, and writes the JSON schema
MarchesDressing/LandFeatures consume:

  {"roads":  [{"c": class, "w": width, "p": [x0,z0,x1,z1,...]}, ...],
   "waters": [...same...],
   "woods":  [...closed polygons...]}

Road classes: 0 through-road, 1 lane/track, 2 path (widths are the
consumer's roadWidths dial; w here is informational). Water widths
are what the carve digs from: the Monnow is a river (8m), the rest
run 2m. Woods are closed ways only (multipolygon relations are
counted and skipped -- graybox woods, not cartography).

The 2026-08-15 bake (this window) predates any committed version of
the original Mercator-frame script, which was session-scratch and is
gone; this one is the repeatable path.
"""

import json
import math
import os
import urllib.request

# The Marches window + 200m margin, as WGS84 (computed through
# WorldFrame.LatLonFromBng at the four corners; re-derive if the
# window ever moves).
BBOX = (51.78269, -2.99328, 51.93189, -2.75233)  # S, W, N, E

ORIGIN_E, ORIGIN_N = 332000.0, 210000.0
DP_TOLERANCE = 1.5  # metres; lanes keep their wiggle, files stay small

# Overpass returns WHOLE ways for anything touching the bbox — the
# Monnow arrives with 8km of downstream tail. Clip to the window plus
# a margin so nothing dresses thin air past the map edge.
CLIP = (-200.0, -200.0, 16200.0, 16200.0)  # minx, minz, maxx, maxz

OUT = os.path.join(os.path.dirname(__file__), "..",
                   "Assets", "Terrain", "MarchesFeatures.json")

ROAD_CLASS = {
    "trunk": 0, "primary": 0, "secondary": 0, "tertiary": 0,
    "unclassified": 0, "road": 0, "living_street": 1,
    "residential": 1, "service": 1, "track": 1,
    "path": 2, "footway": 2, "bridleway": 2, "cycleway": 2, "steps": 2,
}
ROAD_WIDTH = {0: 4.0, 1: 3.0, 2: 1.8}
WATER_WIDTH = {"river": 8.0, "canal": 5.0, "stream": 2.0,
               "brook": 2.0, "drain": 1.5, "ditch": 1.5}

QUERY = f"""
[out:json][timeout:180];
(
  way["highway"~"^({'|'.join(ROAD_CLASS)})$"]({BBOX[0]},{BBOX[1]},{BBOX[2]},{BBOX[3]});
  way["waterway"~"^({'|'.join(WATER_WIDTH)})$"]({BBOX[0]},{BBOX[1]},{BBOX[2]},{BBOX[3]});
  way["natural"="wood"]({BBOX[0]},{BBOX[1]},{BBOX[2]},{BBOX[3]});
  way["landuse"="forest"]({BBOX[0]},{BBOX[1]},{BBOX[2]},{BBOX[3]});
);
out geom;
"""


# ---- WGS84 -> BNG (WorldFrame.cs's math, ported) ----

def latlon_to_bng(lat, lon):
    lat, lon = math.radians(lat), math.radians(lon)
    # WGS84 -> ECEF (h = 0)
    a1, f1 = 6378137.0, 1.0 / 298.257223563
    e21 = 2 * f1 - f1 * f1
    sl, cl = math.sin(lat), math.cos(lat)
    nu = a1 / math.sqrt(1 - e21 * sl * sl)
    x = nu * cl * math.cos(lon)
    y = nu * cl * math.sin(lon)
    z = nu * (1 - e21) * sl
    # Helmert WGS84 -> OSGB36 (small-angle)
    sec = math.pi / (180.0 * 3600.0)
    tx, ty, tz = -446.448, 125.157, -542.060
    s = 20.4894e-6
    rx, ry, rz = -0.1502 * sec, -0.2470 * sec, -0.8421 * sec
    x2 = tx + (1 + s) * x - rz * y + ry * z
    y2 = ty + rz * x + (1 + s) * y - rx * z
    z2 = tz - ry * x + rx * y + (1 + s) * z
    # ECEF -> Airy 1830 lat/lon
    a, b = 6377563.396, 6356256.909
    e2 = (a * a - b * b) / (a * a)
    p = math.sqrt(x2 * x2 + y2 * y2)
    lat2 = math.atan2(z2, p * (1 - e2))
    for _ in range(5):
        sl2 = math.sin(lat2)
        nu2 = a / math.sqrt(1 - e2 * sl2 * sl2)
        lat2 = math.atan2(z2 + e2 * nu2 * sl2, p)
    lon2 = math.atan2(y2, x2)
    # Airy TM -> BNG (the OS formulas)
    F0 = 0.9996012717
    lat0, lon0 = math.radians(49.0), math.radians(-2.0)
    E0, N0 = 400000.0, -100000.0
    n = (a - b) / (a + b)
    s2, c2, t2 = math.sin(lat2), math.cos(lat2), math.tan(lat2)
    nu = a * F0 / math.sqrt(1 - e2 * s2 * s2)
    rho = a * F0 * (1 - e2) / (1 - e2 * s2 * s2) ** 1.5
    eta2 = nu / rho - 1
    dlat, slat = lat2 - lat0, lat2 + lat0
    M = b * F0 * (
        (1 + n + 1.25 * n * n + 1.25 * n ** 3) * dlat
        - (3 * n + 3 * n * n + 2.625 * n ** 3)
        * math.sin(dlat) * math.cos(slat)
        + (1.875 * n * n + 1.875 * n ** 3)
        * math.sin(2 * dlat) * math.cos(2 * slat)
        - (35.0 / 24.0) * n ** 3
        * math.sin(3 * dlat) * math.cos(3 * slat))
    I = M + N0
    II = nu / 2 * s2 * c2
    III = nu / 24 * s2 * c2 ** 3 * (5 - t2 * t2 + 9 * eta2)
    IIIA = nu / 720 * s2 * c2 ** 5 * (61 - 58 * t2 * t2 + t2 ** 4)
    IV = nu * c2
    V = nu / 6 * c2 ** 3 * (nu / rho - t2 * t2)
    VI = nu / 120 * c2 ** 5 * (5 - 18 * t2 * t2 + t2 ** 4
                               + 14 * eta2 - 58 * t2 * t2 * eta2)
    dlon = lon2 - lon0
    north = I + II * dlon ** 2 + III * dlon ** 4 + IIIA * dlon ** 6
    east = E0 + IV * dlon + V * dlon ** 3 + VI * dlon ** 5
    return east, north


def to_world(lat, lon):
    e, n = latlon_to_bng(lat, lon)
    return e - ORIGIN_E, n - ORIGIN_N


# ---- Douglas-Peucker ----

def dp_simplify(pts, tol, forced=None):
    """forced: indices that must survive (road junctions — OSM ways that
    join share the literal node, and CastleRoads stitches its graph by
    merging vertices within 2.5m, so simplifying a junction out of ONE
    of the two ways severs the graph there. The first bake did exactly
    that: 800 components, Skenfrith's nearest vertex in a 2-vertex
    orphan, 'castles share no road route'.)"""
    if len(pts) < 3:
        return pts
    keep = [False] * len(pts)
    keep[0] = keep[-1] = True
    anchors = [0, len(pts) - 1]
    if forced:
        for i in forced:
            keep[i] = True
            anchors.append(i)
    anchors = sorted(set(anchors))
    stack = [(anchors[k], anchors[k + 1]) for k in range(len(anchors) - 1)]
    while stack:
        i0, i1 = stack.pop()
        ax, az = pts[i0]
        bx, bz = pts[i1]
        dx, dz = bx - ax, bz - az
        seg2 = dx * dx + dz * dz
        worst, wd = -1, tol * tol
        for i in range(i0 + 1, i1):
            px, pz = pts[i][0] - ax, pts[i][1] - az
            if seg2 > 1e-12:
                t = max(0.0, min(1.0, (px * dx + pz * dz) / seg2))
                qx, qz = px - t * dx, pz - t * dz
            else:
                qx, qz = px, pz
            d2 = qx * qx + qz * qz
            if d2 > wd:
                worst, wd = i, d2
        if worst >= 0:
            keep[worst] = True
            stack.append((i0, worst))
            stack.append((worst, i1))
    return [p for p, k in zip(pts, keep) if k]


def clip_polyline(pts):
    """Split a polyline into the pieces inside CLIP, interpolating a
    point wherever a segment crosses the boundary."""
    minx, minz, maxx, maxz = CLIP

    def inside(p):
        return minx <= p[0] <= maxx and minz <= p[1] <= maxz

    def cross(a, b):
        # March from a toward b, clamping against each slab in turn.
        t0, t1 = 0.0, 1.0
        dx, dz = b[0] - a[0], b[1] - a[1]
        for delta, lo, hi, o in ((dx, minx, maxx, a[0]), (dz, minz, maxz, a[1])):
            if abs(delta) < 1e-12:
                if o < lo or o > hi:
                    return None
                continue
            ta, tb = (lo - o) / delta, (hi - o) / delta
            if ta > tb:
                ta, tb = tb, ta
            t0, t1 = max(t0, ta), min(t1, tb)
            if t0 > t1:
                return None
        return t0, t1

    pieces, run = [], []
    for i in range(len(pts)):
        p = pts[i]
        if inside(p):
            if not run and i > 0:
                seg = cross(pts[i - 1], p)
                if seg:
                    t0, _ = seg
                    a = pts[i - 1]
                    run.append((a[0] + (p[0] - a[0]) * t0,
                                a[1] + (p[1] - a[1]) * t0))
            run.append(p)
        else:
            if run:
                seg = cross(pts[i - 1], p)
                if seg:
                    _, t1 = seg
                    a = pts[i - 1]
                    run.append((a[0] + (p[0] - a[0]) * t1,
                                a[1] + (p[1] - a[1]) * t1))
                pieces.append(run)
                run = []
            elif i > 0:
                # Both ends outside; the segment may still pass through.
                seg = cross(pts[i - 1], p)
                if seg:
                    t0, t1 = seg
                    a = pts[i - 1]
                    pieces.append([
                        (a[0] + (p[0] - a[0]) * t0, a[1] + (p[1] - a[1]) * t0),
                        (a[0] + (p[0] - a[0]) * t1, a[1] + (p[1] - a[1]) * t1)])
    if run:
        pieces.append(run)
    return [r for r in pieces if len(r) >= 2]


def clip_polygon(pts):
    """Sutherland-Hodgman against the CLIP rect (convex)."""
    minx, minz, maxx, maxz = CLIP
    edges = (lambda p: p[0] >= minx, lambda p: p[0] <= maxx,
             lambda p: p[1] >= minz, lambda p: p[1] <= maxz)
    axes = ((0, minx), (0, maxx), (1, minz), (1, maxz))
    poly = list(pts)
    for keep, (ax, val) in zip(edges, axes):
        if not poly:
            return []
        out = []
        for i in range(len(poly)):
            a, b = poly[i - 1], poly[i]
            ka, kb = keep(a), keep(b)
            if ka != kb:
                t = (val - a[ax]) / (b[ax] - a[ax])
                out.append((a[0] + (b[0] - a[0]) * t,
                            a[1] + (b[1] - a[1]) * t))
            if kb:
                out.append(b)
        poly = out
    return poly


def flatten(pts):
    out = []
    for x, z in pts:
        out.append(round(x, 2))
        out.append(round(z, 2))
    return out


ENDPOINTS = (
    "https://overpass-api.de/api/interpreter",
    "https://overpass.kumi.systems/api/interpreter",
)


def fetch():
    import time
    last = None
    for attempt in range(4):
        url = ENDPOINTS[attempt % len(ENDPOINTS)]
        req = urllib.request.Request(
            url, data=("data=" + urllib.parse.quote(QUERY)).encode(),
            headers={"User-Agent": "800years-marches-bake"})
        print(f"Fetching {url}…")
        try:
            with urllib.request.urlopen(req, timeout=300) as resp:
                return json.load(resp)
        except Exception as e:  # busy mirrors 504 freely; try the next
            last = e
            print(f"  {e}; retrying…")
            time.sleep(20)
    raise last


def main():
    data = fetch()

    # Junction census over the road ways: an OSM node shared by two ways
    # arrives as the SAME lat/lon in both geometries, so exact-match
    # counting finds every junction. Those vertices are forced through
    # DP (roads only — waters/woods need no graph, and keeping their
    # output stable keeps the baked river refs aligned).
    node_count = {}
    for el in data.get("elements", []):
        if el.get("type") != "way" or "geometry" not in el:
            continue
        tags = el.get("tags", {})
        if ROAD_CLASS.get(tags.get("highway")) is None:
            continue
        for g in el["geometry"]:
            k = (g["lat"], g["lon"])
            node_count[k] = node_count.get(k, 0) + 1

    roads, waters, woods = [], [], []
    for el in data.get("elements", []):
        if el.get("type") != "way" or "geometry" not in el:
            continue
        tags = el.get("tags", {})
        raw = [(g["lat"], g["lon"]) for g in el["geometry"]]
        pts = [to_world(lat, lon) for lat, lon in raw]
        if "highway" in tags:
            c = ROAD_CLASS.get(tags["highway"])
            if c is None:
                continue
            forced = [i for i, ll in enumerate(raw) if node_count[ll] > 1]
            pts = dp_simplify(pts, DP_TOLERANCE, forced)
            if len(pts) < 2:
                continue
            for piece in clip_polyline(pts):
                roads.append({"c": c, "w": ROAD_WIDTH[c], "p": flatten(piece)})
        elif "waterway" in tags:
            w = WATER_WIDTH.get(tags["waterway"])
            if w is None:
                continue
            pts = dp_simplify(pts, DP_TOLERANCE)
            if len(pts) < 2:
                continue
            for piece in clip_polyline(pts):
                waters.append({"c": 0, "w": w, "p": flatten(piece)})
        elif tags.get("natural") == "wood" or tags.get("landuse") == "forest":
            pts = dp_simplify(pts, DP_TOLERANCE)
            if len(pts) < 2:
                continue
            # Woods are polygons: closed ways only.
            if pts[0] != pts[-1] and len(pts) < 4:
                continue
            poly = clip_polygon(pts)
            if len(poly) >= 3:
                woods.append({"c": 0, "w": 0.0, "p": flatten(poly)})

    out = os.path.abspath(OUT)
    with open(out, "w") as f:
        json.dump({"roads": roads, "waters": waters, "woods": woods},
                  f, separators=(",", ":"))
    print(f"Wrote {out}: {len(roads)} roads, {len(waters)} waterways,"
          f" {len(woods)} woods.")


if __name__ == "__main__":
    main()
