using System.Drawing;
using Insights.Presentation;
using SkiaSharp;
using Xunit;

namespace Insights.UnitTests;

public class PixelDiffTests
{
    private static readonly SKColor White = new(255, 255, 255);
    private static readonly SKColor Red = new(255, 0, 0);

    private static byte[] MakePng(int width, int height, Action<SKBitmap>? paint = null)
    {
        using var bitmap = new SKBitmap(width, height);
        Fill(bitmap, White);
        paint?.Invoke(bitmap);
        using var image = SKImage.FromBitmap(bitmap);
        using var data = image.Encode(SKEncodedImageFormat.Png, 100);
        return data.ToArray();
    }

    private static void Fill(SKBitmap bitmap, SKColor color)
    {
        for (var y = 0; y < bitmap.Height; y++)
            for (var x = 0; x < bitmap.Width; x++)
                bitmap.SetPixel(x, y, color);
    }

    private static void PaintSquare(SKBitmap bitmap, int x0, int y0, int size, SKColor color)
    {
        for (var y = y0; y < Math.Min(y0 + size, bitmap.Height); y++)
            for (var x = x0; x < Math.Min(x0 + size, bitmap.Width); x++)
                bitmap.SetPixel(x, y, color);
    }

    [Fact]
    public void RegionChangeRatio_IdenticalImages_IsZero()
    {
        var png = MakePng(200, 200);
        var ratio = PixelDiff.RegionChangeRatio(png, png, new Rectangle(0, 0, 200, 200));
        Assert.Equal(0.0, ratio);
    }

    [Fact]
    public void RegionChangeRatio_FullyPaintedRegion_IsNearOne()
    {
        var before = MakePng(100, 100);
        var after = MakePng(100, 100, img => PaintSquare(img, 0, 0, 100, Red));
        var ratio = PixelDiff.RegionChangeRatio(before, after, new Rectangle(0, 0, 100, 100));
        Assert.True(ratio > 0.95, $"Expected near-total change, got {ratio}");
    }

    [Fact]
    public void RegionChangeRatio_ChangeOutsideTheRegion_IsNotCounted()
    {
        var before = MakePng(200, 200);
        var after = MakePng(200, 200, img => PaintSquare(img, 150, 150, 40, Red));
        var ratio = PixelDiff.RegionChangeRatio(before, after, new Rectangle(0, 0, 100, 100));
        Assert.Equal(0.0, ratio);
    }

    [Fact]
    public void RegionChangeRatio_RegionLargerThanImage_IsClampedNotThrown()
    {
        var png = MakePng(50, 50);
        var ratio = PixelDiff.RegionChangeRatio(png, png, new Rectangle(0, 0, 500, 500));
        Assert.Equal(0.0, ratio);
    }

    [Fact]
    public void FindChangedBlocksOutside_ChangeInsideExcludedRegion_IsNotFlagged()
    {
        var before = MakePng(200, 200);
        var after = MakePng(200, 200, img => PaintSquare(img, 10, 10, 30, Red));
        var flagged = PixelDiff.FindChangedBlocksOutside(before, after, new Rectangle(0, 0, 60, 60), blockSize: 20, blockChangeThreshold: 0.1);
        Assert.Empty(flagged);
    }

    [Fact]
    public void FindChangedBlocksOutside_ChangeOutsideExcludedRegion_IsFlaggedAndMerged()
    {
        var before = MakePng(200, 200);
        var after = MakePng(200, 200, img => PaintSquare(img, 150, 150, 40, Red));
        var flagged = PixelDiff.FindChangedBlocksOutside(before, after, new Rectangle(0, 0, 60, 60), blockSize: 20, blockChangeThreshold: 0.1);
        Assert.NotEmpty(flagged);
        Assert.Contains(flagged, r => r.IntersectsWith(new Rectangle(150, 150, 40, 40)));
    }
}
