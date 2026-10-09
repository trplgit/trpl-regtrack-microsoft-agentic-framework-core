using System.Drawing;
using SkiaSharp;

namespace Insights.Presentation;

/// <summary>
/// Pixel comparison for the tile-QA checker. Two PNGs are decoded ONCE per public call and
/// compared over raw pixel spans - [CHANGED 2026-10-09] the previous per-pixel
/// <c>SKBitmap.GetPixel</c> loop over a 1280 x 4000 screenshot, repeated per 40 x 40 block, was a
/// measurable share of the hour-long checker passes seen live. Semantics are unchanged: a pixel
/// "differs" when any colour channel differs by more than <see cref="ChannelTolerance"/>.
/// </summary>
public static class PixelDiff
{
    private const int ChannelTolerance = 24;

    public static double RegionChangeRatio(byte[] beforePng, byte[] afterPng, Rectangle region)
    {
        using var before = SKBitmap.Decode(beforePng);
        using var after = SKBitmap.Decode(afterPng);
        return RegionChangeRatio(before, after, region);
    }

    private static double RegionChangeRatio(SKBitmap before, SKBitmap after, Rectangle region)
    {
        var width = Math.Min(before.Width, after.Width);
        var height = Math.Min(before.Height, after.Height);
        var clamped = Rectangle.Intersect(region, new Rectangle(0, 0, width, height));
        if (clamped.Width <= 0 || clamped.Height <= 0)
            return 0.0;

        var total = (long)clamped.Width * clamped.Height;
        var differing = before.BytesPerPixel == 4 && after.BytesPerPixel == 4 && before.ColorType == after.ColorType
            ? CountDifferingSpan(before, after, clamped)
            : CountDifferingSlow(before, after, clamped);
        return total == 0 ? 0.0 : (double)differing / total;
    }

    private static long CountDifferingSpan(SKBitmap before, SKBitmap after, Rectangle r)
    {
        var differing = 0L;
        var b = before.GetPixelSpan();
        var a = after.GetPixelSpan();
        var rowB = before.RowBytes;
        var rowA = after.RowBytes;
        for (var y = r.Top; y < r.Bottom; y++)
        {
            var offB = y * rowB + r.Left * 4;
            var offA = y * rowA + r.Left * 4;
            for (var x = 0; x < r.Width; x++, offB += 4, offA += 4)
            {
                // Channel order (RGBA vs BGRA) is identical for both decodes, so comparing the
                // first three bytes position-wise is a colour comparison either way; byte 4 is alpha.
                if (Math.Abs(b[offB] - a[offA]) > ChannelTolerance
                    || Math.Abs(b[offB + 1] - a[offA + 1]) > ChannelTolerance
                    || Math.Abs(b[offB + 2] - a[offA + 2]) > ChannelTolerance)
                    differing++;
            }
        }
        return differing;
    }

    private static long CountDifferingSlow(SKBitmap before, SKBitmap after, Rectangle r)
    {
        var differing = 0L;
        for (var y = r.Top; y < r.Bottom; y++)
        {
            for (var x = r.Left; x < r.Right; x++)
            {
                var a = before.GetPixel(x, y);
                var b = after.GetPixel(x, y);
                if (Math.Abs(a.Red - b.Red) > ChannelTolerance || Math.Abs(a.Green - b.Green) > ChannelTolerance || Math.Abs(a.Blue - b.Blue) > ChannelTolerance)
                    differing++;
            }
        }
        return differing;
    }

    public static IReadOnlyList<Rectangle> FindChangedBlocksOutside(
        byte[] beforePng, byte[] afterPng, Rectangle excluded, int blockSize, double blockChangeThreshold)
    {
        using var before = SKBitmap.Decode(beforePng);
        using var after = SKBitmap.Decode(afterPng);
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
