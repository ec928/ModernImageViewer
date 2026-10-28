using Xunit;

namespace ModernImageViewer.Tests;

public class GridCanvasMathTests
{
    [Theory]
    [InlineData(1080, 16.0 / 9.0, 1920)]
    [InlineData(1080, 21.0 / 9.0, 2520)]
    [InlineData(1080, 4.0 / 3.0, 1440)]
    [InlineData(1080, 1.0, 1080)]
    [InlineData(1080, 3.0 / 4.0, 810)]
    [InlineData(1200, 16.0 / 9.0, 2133)]
    [InlineData(4320, 21.0 / 9.0, 10080)]
    public void Height_sets_the_width_from_the_canvas_shape(int height, double ratio, int expectedWidth)
    {
        Assert.True(GridCanvasMath.TryOutputSize(height, ratio, 1, out int width, out int outputHeight));
        Assert.Equal(expectedWidth, width);
        Assert.Equal(height, outputHeight);
    }

    [Fact]
    public void A_height_past_the_edge_cap_is_refused()
    {
        Assert.False(GridCanvasMath.TryOutputSize(16385, 1, 1, out _, out _));
        Assert.False(GridCanvasMath.TryOutputSize(8000, 21.0 / 9.0, 1, out _, out _));
    }

    [Fact]
    public void A_window_within_two_pixels_keeps_its_preset()
    {
        Assert.Equal("16:9", GridCanvasMath.MatchAspect(1920, 1080));
        Assert.Equal("16:9", GridCanvasMath.MatchAspect(1922, 1080));
        Assert.Equal("Custom", GridCanvasMath.MatchAspect(1923, 1080));
        Assert.Equal("3:4", GridCanvasMath.MatchAspect(810, 1080));
    }

    [Fact]
    public void Fit_shrinks_a_wide_canvas_and_leaves_a_smaller_one()
    {
        var shrunk = GridCanvasMath.Fit(4000, 2000, 3000, 3000);
        Assert.Equal((3000, 1500), shrunk);
        var same = GridCanvasMath.Fit(1280, 720, 1920, 1080);
        Assert.Equal((1280, 720), same);
    }

    [Fact]
    public void Fill_crop_keeps_the_center_of_a_wide_photo()
    {
        var placed = GridCanvasMath.Place(100, 100, 200, 100, 1, 0, 0);
        Assert.Equal(-50, placed.Left, 3);
        Assert.Equal(200, placed.Width, 3);
        double u0 = (0 - placed.Left) / placed.Width;
        double u1 = (100 - placed.Left) / placed.Width;
        Assert.Equal(0.25, u0, 3);
        Assert.Equal(0.75, u1, 3);
    }

    [Fact]
    public void A_centered_zoom_keeps_the_middle_of_the_photo()
    {
        var placed = GridCanvasMath.Place(200, 100, 200, 100, 2, 0, 0);
        double u0 = (0 - placed.Left) / placed.Width;
        double u1 = (200 - placed.Left) / placed.Width;
        double v0 = (0 - placed.Top) / placed.Height;
        double v1 = (100 - placed.Top) / placed.Height;
        Assert.Equal(0.25, u0, 3);
        Assert.Equal(0.75, u1, 3);
        Assert.Equal(0.25, v0, 3);
        Assert.Equal(0.75, v1, 3);
    }

    [Fact]
    public void Decode_shrinks_to_the_cell_and_does_not_enlarge_past_the_file()
    {
        Assert.Equal((1920, 1080), GridCanvasMath.DecodeSize(7680, 4320, 1920, 1080));
        Assert.Equal((7680, 4320), GridCanvasMath.DecodeSize(7680, 4320, 19200, 10800));
        var capped = GridCanvasMath.DecodeSize(20000, 10000, 20000, 10000);
        Assert.Equal(16384, Math.Max(capped.Width, capped.Height));
    }
}
