using UnityEngine;

// The world frame (Docs/WorldFrame.md): world coordinates ARE the
// British National Grid — world X = easting − OriginEasting, world
// Z = northing − OriginNorthing, world Y = metres ODN. One even-km
// origin constant pair, and NO code converts between real and world
// coordinates except through here — that law is what makes origin
// rebasing (Britain-scale streaming) a one-constant change later.
//
// The legacy chain (linear Web Mercator anchored at 51.89°N — the
// pre-2026-08-15 frame) lives here too, ONLY for migration: old saves
// and the old terrarium DEM speak it. Nothing new may call the
// legacy methods; when the migration era closes they delete.
//
// Math provenance: WGS84↔OSGB36 by the standard small-angle Helmert
// (~5m absolute without OSTN15 — under a base heightmap sample);
// BNG↔OSGB36 lat/lon by the OS's own transverse Mercator formulas
// (Airy 1830). Forward TM verified 2026-08-14 against all three
// castles' documented grid refs (CastleLidar); the inverse is the
// OS companion algorithm, round-trip-tested at bake time.
public static class WorldFrame
{
    // The Marches window: E 332000–348000, N 210000–226000 (16×16km,
    // 8×8 2km tiles) — chosen 2026-08-15 so the NRW 2m LIDAR on disk
    // covers all but 3 far-corner cells. Even-km by law.
    public const double OriginEasting = 332000.0;
    public const double OriginNorthing = 210000.0;

    // The tile lattice (Docs/WorldFrame.md): 2km tiles on even-km BNG
    // lines. Heights are metres ODN on a shared vertical span — every
    // tile sits at y = 0 with the same height range, so world Y IS
    // elevation ODN (16-bit heightmaps make the quantum ~11mm).
    public const float TileSize = 2000f;
    public const float HeightRange = 700f;

    const double Deg = System.Math.PI / 180.0;

    // ---- BNG ↔ world (the everyday pair — pure offset) ----

    public static Vector2 WorldFromBng(double easting, double northing)
        => new Vector2((float)(easting - OriginEasting),
                       (float)(northing - OriginNorthing));

    public static void BngFromWorld(Vector2 world,
        out double easting, out double northing)
    {
        easting = OriginEasting + world.x;
        northing = OriginNorthing + world.y;
    }

    // ---- Grid-ref tile names ----
    // The 2km tile whose SW corner is (340000, 224000) is "SO4024" —
    // the OS two-letter 100km square plus the corner's km within it.
    // Tile identity for Britain-scale streaming: unique, permanent,
    // human-readable ("Marches [SO4024]"; Ground.BaseName strips at
    // " [" so saves keep matching the scene by base name).
    public static string GridRefName(double easting, double northing)
    {
        int e100 = (int)System.Math.Floor(easting / 100000.0);
        int n100 = (int)System.Math.Floor(northing / 100000.0);
        int l1 = (19 - n100) - (19 - n100) % 5 + (e100 + 10) / 5;
        int l2 = (19 - n100) * 5 % 25 + e100 % 5;
        const string Letters = "ABCDEFGHJKLMNOPQRSTUVWXYZ"; // no I
        int ekm = (int)System.Math.Floor(easting / 1000.0) - e100 * 100;
        int nkm = (int)System.Math.Floor(northing / 1000.0) - n100 * 100;
        return $"{Letters[l1]}{Letters[l2]}{ekm:00}{nkm:00}";
    }

    // ---- WGS84 lat/lon ↔ BNG (degrees at the boundary) ----
    // For bakes that ingest lat/lon sources (the OSM feature bake).

    public static void BngFromLatLon(double lat, double lon,
        out double easting, out double northing)
    {
        LatLonToEcef(lat * Deg, lon * Deg, Wgs84A, Wgs84E2,
            out double x, out double y, out double z);
        HelmertWgs84ToOsgb36(ref x, ref y, ref z);
        EcefToLatLon(x, y, z, AiryA, AiryE2,
            out double lat2, out double lon2);
        TmForward(lat2, lon2, out easting, out northing);
    }

    public static void LatLonFromBng(double easting, double northing,
        out double lat, out double lon)
    {
        TmInverse(easting, northing, out double lat2, out double lon2);
        LatLonToEcef(lat2, lon2, AiryA, AiryE2,
            out double x, out double y, out double z);
        HelmertOsgb36ToWgs84(ref x, ref y, ref z);
        EcefToLatLon(x, y, z, Wgs84A, Wgs84E2,
            out double latR, out double lonR);
        lat = latR / Deg;
        lon = lonR / Deg;
    }

    // ---- The LEGACY frame (migration only — see header) ----
    // The pre-BNG world: Web Mercator metres scaled by cos(51.89°),
    // anchored so White Castle's marker sat at (−4640.3, −6428.2).

    // ODN minus the old Marches world's Y at Grosmont, where the
    // player's masonry lives (the 08-14 LIDAR bake fitted it over 27k
    // ring samples; the other two sites came out within ±0.55m). Every
    // finite absolute Y in a legacy save or scene object lifts by this.
    public const float LegacyLift = 23.85f;

    const double MercR = 6378137.0;
    const double AnchorLat = 51.8459, AnchorLon = -2.9021;
    const double AnchorX = -4640.3, AnchorZ = -6428.2;
    static readonly double MercK = System.Math.Cos(51.89 * Deg);
    static readonly double MercX0 =
        MercR * AnchorLon * Deg - AnchorX / MercK;
    static readonly double MercY0 =
        MercR * System.Math.Log(System.Math.Tan(
            System.Math.PI / 4.0 + AnchorLat * Deg / 2.0))
        - AnchorZ / MercK;

    public static void LatLonFromOldWorld(Vector2 oldWorld,
        out double lat, out double lon)
    {
        double mx = MercX0 + oldWorld.x / MercK;
        double my = MercY0 + oldWorld.y / MercK;
        lon = mx / MercR / Deg;
        lat = (2.0 * System.Math.Atan(System.Math.Exp(my / MercR))
            - System.Math.PI / 2.0) / Deg;
    }

    public static Vector2 OldWorldFromLatLon(double lat, double lon)
    {
        double mx = MercR * lon * Deg;
        double my = MercR * System.Math.Log(System.Math.Tan(
            System.Math.PI / 4.0 + lat * Deg / 2.0));
        return new Vector2((float)((mx - MercX0) * MercK),
                           (float)((my - MercY0) * MercK));
    }

    // The save-migration map: every stored XZ of a legacy save goes
    // through this once (CastleSave's version gate decides).
    public static Vector2 WorldFromOldWorld(Vector2 oldWorld)
    {
        LatLonFromOldWorld(oldWorld, out double lat, out double lon);
        BngFromLatLon(lat, lon, out double e, out double n);
        return WorldFromBng(e, n);
    }

    // The DEM gap-fill map: where the LIDAR has no ground, the bake
    // samples the legacy terrarium DEM at the old-frame point.
    public static Vector2 OldWorldFromWorld(Vector2 world)
    {
        BngFromWorld(world, out double e, out double n);
        LatLonFromBng(e, n, out double lat, out double lon);
        return OldWorldFromLatLon(lat, lon);
    }

    // ---- Ellipsoids ----

    const double Wgs84A = 6378137.0;
    const double Wgs84F = 1.0 / 298.257223563;
    const double Wgs84E2 = 2.0 * Wgs84F - Wgs84F * Wgs84F;
    const double AiryA = 6377563.396, AiryB = 6356256.909;
    const double AiryE2 = (AiryA * AiryA - AiryB * AiryB)
        / (AiryA * AiryA);

    static void LatLonToEcef(double lat, double lon, double a, double e2,
        out double x, out double y, out double z)
    {
        double sinLat = System.Math.Sin(lat),
            cosLat = System.Math.Cos(lat);
        double nu = a / System.Math.Sqrt(1.0 - e2 * sinLat * sinLat);
        x = nu * cosLat * System.Math.Cos(lon);
        y = nu * cosLat * System.Math.Sin(lon);
        z = nu * (1.0 - e2) * sinLat;
    }

    static void EcefToLatLon(double x, double y, double z,
        double a, double e2, out double lat, out double lon)
    {
        double p = System.Math.Sqrt(x * x + y * y);
        lat = System.Math.Atan2(z, p * (1.0 - e2));
        for (int i = 0; i < 5; i++)
        {
            double sl = System.Math.Sin(lat);
            double nu = a / System.Math.Sqrt(1.0 - e2 * sl * sl);
            lat = System.Math.Atan2(z + e2 * nu * sl, p);
        }
        lon = System.Math.Atan2(y, x);
    }

    // Small-angle Helmert, WGS84 → OSGB36; the inverse negates every
    // parameter (exact enough at these magnitudes — sub-mm).
    const double HTx = -446.448, HTy = 125.157, HTz = -542.060;
    const double HS = 20.4894e-6;
    const double Sec = System.Math.PI / (180.0 * 3600.0);
    const double HRx = -0.1502 * Sec, HRy = -0.2470 * Sec,
        HRz = -0.8421 * Sec;

    static void HelmertWgs84ToOsgb36(ref double x, ref double y, ref double z)
    {
        double x2 = HTx + (1.0 + HS) * x - HRz * y + HRy * z;
        double y2 = HTy + HRz * x + (1.0 + HS) * y - HRx * z;
        double z2 = HTz - HRy * x + HRx * y + (1.0 + HS) * z;
        x = x2; y = y2; z = z2;
    }

    static void HelmertOsgb36ToWgs84(ref double x, ref double y, ref double z)
    {
        double x2 = -HTx + (1.0 - HS) * x + HRz * y - HRy * z;
        double y2 = -HTy - HRz * x + (1.0 - HS) * y + HRx * z;
        double z2 = -HTz + HRy * x - HRx * y + (1.0 - HS) * z;
        x = x2; y = y2; z = z2;
    }

    // ---- The OS transverse Mercator (Airy 1830) ----

    const double TmF0 = 0.9996012717;
    static readonly double TmLat0 = 49.0 * Deg;
    static readonly double TmLon0 = -2.0 * Deg;
    const double TmE0 = 400000.0, TmN0 = -100000.0;

    static double MeridianArc(double lat)
    {
        double n = (AiryA - AiryB) / (AiryA + AiryB);
        double dLat = lat - TmLat0, sLat = lat + TmLat0;
        return AiryB * TmF0 * (
            (1.0 + n + 1.25 * n * n + 1.25 * n * n * n) * dLat
            - (3.0 * n + 3.0 * n * n + 2.625 * n * n * n)
                * System.Math.Sin(dLat) * System.Math.Cos(sLat)
            + (1.875 * n * n + 1.875 * n * n * n)
                * System.Math.Sin(2.0 * dLat) * System.Math.Cos(2.0 * sLat)
            - (35.0 / 24.0) * n * n * n
                * System.Math.Sin(3.0 * dLat) * System.Math.Cos(3.0 * sLat));
    }

    static void TmForward(double lat, double lon,
        out double east, out double north)
    {
        double sin = System.Math.Sin(lat), cos = System.Math.Cos(lat),
            tan = System.Math.Tan(lat);
        double nu = AiryA * TmF0
            / System.Math.Sqrt(1.0 - AiryE2 * sin * sin);
        double rho = AiryA * TmF0 * (1.0 - AiryE2)
            / System.Math.Pow(1.0 - AiryE2 * sin * sin, 1.5);
        double eta2 = nu / rho - 1.0;
        double I = MeridianArc(lat) + TmN0;
        double II = nu / 2.0 * sin * cos;
        double III = nu / 24.0 * sin * System.Math.Pow(cos, 3.0)
            * (5.0 - tan * tan + 9.0 * eta2);
        double IIIA = nu / 720.0 * sin * System.Math.Pow(cos, 5.0)
            * (61.0 - 58.0 * tan * tan + System.Math.Pow(tan, 4.0));
        double IV = nu * cos;
        double V = nu / 6.0 * System.Math.Pow(cos, 3.0)
            * (nu / rho - tan * tan);
        double VI = nu / 120.0 * System.Math.Pow(cos, 5.0)
            * (5.0 - 18.0 * tan * tan + System.Math.Pow(tan, 4.0)
                + 14.0 * eta2 - 58.0 * tan * tan * eta2);
        double dLon = lon - TmLon0;
        north = I + II * dLon * dLon
            + III * System.Math.Pow(dLon, 4.0)
            + IIIA * System.Math.Pow(dLon, 6.0);
        east = TmE0 + IV * dLon + V * dLon * dLon * dLon
            + VI * System.Math.Pow(dLon, 5.0);
    }

    static void TmInverse(double east, double north,
        out double lat, out double lon)
    {
        // Iterate the footpoint latitude until the meridian arc
        // matches the northing to a hair (OS: 0.01mm).
        double latF = (north - TmN0) / (AiryA * TmF0) + TmLat0;
        for (int i = 0; i < 20; i++)
        {
            double dM = north - TmN0 - MeridianArc(latF);
            if (System.Math.Abs(dM) < 1e-8)
                break;
            latF += dM / (AiryA * TmF0);
        }
        double sin = System.Math.Sin(latF),
            tan = System.Math.Tan(latF),
            sec = 1.0 / System.Math.Cos(latF);
        double nu = AiryA * TmF0
            / System.Math.Sqrt(1.0 - AiryE2 * sin * sin);
        double rho = AiryA * TmF0 * (1.0 - AiryE2)
            / System.Math.Pow(1.0 - AiryE2 * sin * sin, 1.5);
        double eta2 = nu / rho - 1.0;
        double VII = tan / (2.0 * rho * nu);
        double VIII = tan / (24.0 * rho * nu * nu * nu)
            * (5.0 + 3.0 * tan * tan + eta2
                - 9.0 * tan * tan * eta2);
        double IX = tan / (720.0 * rho * System.Math.Pow(nu, 5.0))
            * (61.0 + 90.0 * tan * tan
                + 45.0 * System.Math.Pow(tan, 4.0));
        double X = sec / nu;
        double XI = sec / (6.0 * nu * nu * nu)
            * (nu / rho + 2.0 * tan * tan);
        double XII = sec / (120.0 * System.Math.Pow(nu, 5.0))
            * (5.0 + 28.0 * tan * tan
                + 24.0 * System.Math.Pow(tan, 4.0));
        double XIIA = sec / (5040.0 * System.Math.Pow(nu, 7.0))
            * (61.0 + 662.0 * tan * tan
                + 1320.0 * System.Math.Pow(tan, 4.0)
                + 720.0 * System.Math.Pow(tan, 6.0));
        double dE = east - TmE0;
        lat = latF - VII * dE * dE
            + VIII * System.Math.Pow(dE, 4.0)
            - IX * System.Math.Pow(dE, 6.0);
        lon = TmLon0 + X * dE - XI * dE * dE * dE
            + XII * System.Math.Pow(dE, 5.0)
            - XIIA * System.Math.Pow(dE, 7.0);
    }
}
