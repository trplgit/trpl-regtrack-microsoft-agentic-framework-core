using System.Drawing;
using SixLabors.ImageSharp.PixelFormats;
using Image = SixLabors.ImageSharp.Image<SixLabors.ImageSharp.PixelFormats.Rgba32>;

namespace Insights.Presentation;

/// <summary>
/// [ADDED 2026-10-07] Pure, deterministic PNG pixel comparison - no Playwright, no LLM. Used by
/// InteractiveTileChecker to decide (a) whether an interaction visibly changed anything inside its
/// own tile at all, and (b) whether anything OUTSIDE that tile visibly changed. A small per-channel
/// tolerance absorbs anti-aliasing/font-rendering jitter between two otherwise-identical renders,
/// same class of noise LayoutCollisionChecker's own `area(...) &lt; 4` guard exists for.
/// </summary>
public static class PixelDiff
{
    private const int ChannelTolerance = 24;

    public static double RegionChangeRatio(byte[] beforePng, byte[] afterPng, Rectangle region)
    {
        using var before = SixLabors.ImageSharp.Image.Load<Rgba32>(beforePng);
        using var after = SixLabors.ImageSharp.Image.Load<Rgba32>(afterPng);
        return RegionChangeRatio(before, after, region);
    }

    private static double RegionChangeRatio(Image before, Image after, Rectangle region)
    {
        var width = Math.Min(before.Width, after.Width);
        var height = Math.Min(before.Height, after.Height);
        var clamped = Rectangle.Intersect(region, new Rectangle(0, 0, width, height));
        if (clamped.Width <= 0 || clamped.Height <= 0)
            return 0.0;

        var differing = 0L;
        var total = (long)clamped.Width * clamped.Height;
        for (var y = clamped.Top; y < clamped.Bottom; y++)
        {
            for (var x = clamped.Left; x < clamped.Right; x++)
            {
                var a = before[x, y];
                var b = after[x, y];
                if (Math.Abs(a.R - b.R) > ChannelTolerance || Math.Abs(a.G - b.G) > ChannelTolerance || Math.Abs(a.B - b.B) > ChannelTolerance)
                    differing++;
            }
        }
        return total == 0 ? 0.0 : (double)differing / total;
    }

    public static IReadOnlyList<Rectangle> FindChangedBlocksOutside(
        byte[] beforePng, byte[] afterPng, Rectangle excluded, int blockSize, double blockChangeThreshold)
    {
        using var before = SixLabors.ImageSharp.Image.Load<Rgba32>(beforePng);
        using var after = SixLabors.ImageSharp.Image.Load<Rgba32>(afterPng);
        var width = Math.Min(before.Width, after.Width);
        var height = Math.Min(before.Height, after.Height);

        var flaggedBlocks = new List<Rectangle>();
        for (var y = 0; y < height; y += blockSize)
        {
            for (var x = 0; x < width; x += blockSize)
            {
                var block = new Rectangle(x, y, Math.Min(blockSize, width - x), Math.Min(blockSize, height - y));
                if (block.IntersectsWith(excluded))
                    continue;
                if (RegionChangeRatio(before, after, block) >= blockChangeThreshold)
                    flaggedBlocks.Add(block);
            }
        }

        return MergeAdjacent(flaggedBlocks);
    }

    /// <summary>Greedy union of any two flagged blocks whose (slightly inflated) rectangles touch or overlap,
    /// repeated until no further merge happens - turns a grid of small flagged blocks into a handful of
    /// real regions worth cropping and sending to the vision model.</summary>
    private static List<Rectangle> MergeAdjacent(List<Rectangle> blocks)
    {
        var merged = new List<Rectangle>(blocks);
        var changed = true;
        while (changed)
        {
            changed = false;
            for (var i = 0; i < merged.Count && !changed; i++)
            {
                var inflatedI = Rectangle.Inflate(merged[i], 1, 1);
                for (var j = i + 1; j < merged.Count; j++)
                {
                    if (!inflatedI.IntersectsWith(merged[j]))
                        continue;
                    merged[i] = Rectangle.Union(merged[i], merged[j]);
                    merged.RemoveAt(j);
                    changed = true;
                    break;
                }
            }
        }
        return merged;
    }
}
