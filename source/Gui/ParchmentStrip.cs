using System;
using Cairo;
using Vintagestory.API.Client;

namespace AlmanacTcm.Gui;

/// <summary>
/// The parchment ancestry, shared by every TCM surface that claims to be a scrap of the
/// almanac's own page (the discovery banner, the quest-step toast). Extracted from
/// DiscoveryBannerRenderer 2026-08-08 when the second surface arrived: one palette, one
/// tearing technique, one paint pass, so the two can never drift into looking like two
/// different books.
///
/// The palette is SAMPLED from Illuminated's bookframe.png, not invented: the book's page
/// tones, its aged edge ambers, and manuscript rubric red for the flourishes. Ink matches
/// the deep leather shadows.
///
/// Everything here works on a TEXEL GRID — one array cell is one texel, painted one Cairo
/// pixel, and the caller blows the texture up with nearest-neighbour magnification
/// (LoadOrUpdateCairoTexture linearMag: false). Antialiasing stays off. Shapes are
/// deterministic on the seeded Random the caller passes in, so a strip does not reshape
/// itself frame to frame and two equal messages tear equally.
/// </summary>
public static class ParchmentStrip
{
    public static readonly double[][] PageTones =
    {
        new[] { 0.941, 0.878, 0.847 },  // #f0e0d8
        new[] { 0.910, 0.878, 0.816 },  // #e8e0d0
        new[] { 0.941, 0.910, 0.847 },  // #f0e8d8
    };
    public static readonly double[][] RimTones =
    {
        new[] { 0.910, 0.816, 0.722 },  // #e8d0b8
        new[] { 0.878, 0.784, 0.659 },  // #e0c8a8
        new[] { 0.910, 0.847, 0.753 },  // #e8d8c0
    };
    public static readonly double[] Rust = { 0.541, 0.294, 0.133 };     // #8a4b22, the accent culture
    public static readonly double[] RustDark = { 0.373, 0.180, 0.078 };
    public static readonly double[] Ink = { 0.227, 0.145, 0.090 };      // #3a2517, sepia ink

    /// <summary>Cell codes. Positive values index PageTones (1-based); the negatives are
    /// the fixed inks. -1..-3 are the rim tones, assigned by <see cref="AgeRim"/>.</summary>
    public const int Air = 0;
    public const int RustCell = -10;
    public const int RustDarkCell = -11;
    public const int InkCell = -20;

    /// <summary>A stable seed for a strip of a given size: equal strips tear equally, and the
    /// tear never re-rolls between frames.</summary>
    public static Random Seed(int wT, int hT) => new Random(7919 * wT + hT);

    /// <summary>Builds the torn strip as a texel field: air outside, mottled page tones
    /// inside. What makes it read hand-made rather than generated is all here — the three
    /// page tones speckled per-texel, ragged torn ends from a random walk, and a top and
    /// bottom edge that wobbles in runs the way a cut-then-worn page sits.</summary>
    public static int[,] BuildField(int wT, int hT, Random rng)
    {
        int[,] cell = new int[wT, hT];

        int topEdge = 1, botEdge = hT - 2;
        int[] top = new int[wT], bot = new int[wT];
        for (int x = 0; x < wT;)
        {
            int run = 3 + rng.Next(6);
            int off = rng.Next(3) == 0 ? 1 : 0;
            for (int i = 0; i < run && x < wT; i++, x++) top[x] = topEdge + off;
        }
        for (int x = 0; x < wT;)
        {
            int run = 3 + rng.Next(6);
            int off = rng.Next(3) == 0 ? 1 : 0;
            for (int i = 0; i < run && x < wT; i++, x++) bot[x] = botEdge - off;
        }
        // Torn ends: a drifting random walk, deeper bites near top and bottom.
        int[] left = new int[hT], right = new int[hT];
        int lw = 1 + rng.Next(2), rw = 1 + rng.Next(2);
        for (int y = 0; y < hT; y++)
        {
            lw = Math.Clamp(lw + rng.Next(3) - 1, 0, 4);
            rw = Math.Clamp(rw + rng.Next(3) - 1, 0, 4);
            left[y] = lw;
            right[y] = wT - 1 - rw;
        }

        for (int x = 0; x < wT; x++)
        {
            for (int y = 0; y < hT; y++)
            {
                if (y < top[x] || y > bot[x] || x < left[y] || x > right[y]) continue;
                int roll = rng.Next(100);
                cell[x, y] = roll < 45 ? 1 : roll < 75 ? 2 : 3;
            }
        }

        return cell;
    }

    /// <summary>Aged rim: any parchment texel touching air takes an amber edge tone, exactly
    /// how the bookframe's own pages darken at their margins. Run this straight after
    /// <see cref="BuildField"/> and before any ink is stamped, or the ink counts as parchment.</summary>
    public static void AgeRim(int[,] cell, Random rng)
    {
        int wT = cell.GetLength(0);
        int hT = cell.GetLength(1);
        for (int x = 0; x < wT; x++)
        {
            for (int y = 0; y < hT; y++)
            {
                if (cell[x, y] <= 0) continue;
                bool edge = x == 0 || x == wT - 1 || y == 0 || y == hT - 1
                    || cell[x - 1, y] == 0 || cell[x + 1, y] == 0
                    || cell[x, y - 1] == 0 || cell[x, y + 1] == 0;
                if (edge) cell[x, y] = -1 - rng.Next(RimTones.Length); // -1..-3: rim tone
            }
        }
    }

    /// <summary>Paints a field one Cairo pixel per texel. The caller scales the result up.</summary>
    public static LoadedTexture Paint(ICoreClientAPI capi, int[,] cell, double opacity)
    {
        int wT = cell.GetLength(0);
        int hT = cell.GetLength(1);

        var surface = new ImageSurface(Format.Argb32, wT, hT);
        var ctx = new Context(surface);
        ctx.Antialias = Antialias.None;

        for (int x = 0; x < wT; x++)
        {
            for (int y = 0; y < hT; y++)
            {
                int v = cell[x, y];
                if (v == Air) continue;
                double[] c = v switch
                {
                    > 0 => PageTones[v - 1],
                    RustCell => Rust,
                    RustDarkCell => RustDark,
                    InkCell => Ink,
                    _ => RimTones[-v - 1],
                };
                ctx.SetSourceRGBA(c[0], c[1], c[2], opacity);
                ctx.Rectangle(x, y, 1, 1);
                ctx.Fill();
            }
        }

        var texture = new LoadedTexture(capi);
        capi.Gui.LoadOrUpdateCairoTexture(surface, false, ref texture);
        ctx.Dispose();
        surface.Dispose();
        return texture;
    }
}
