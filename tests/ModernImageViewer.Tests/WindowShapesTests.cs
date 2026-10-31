using System.Linq;
using Xunit;

namespace ModernImageViewer.Tests
{
    public class WindowShapesTests
    {
        [Fact]
        public void RectangleHasNoOutline()
        {
            Assert.Null(WindowShapes.Outline(WindowShapes.Rectangle));
            Assert.Null(WindowShapes.Outline(null));
        }

        [Theory]
        [InlineData(WindowShapes.LShape)]
        [InlineData(WindowShapes.Oval)]
        [InlineData(WindowShapes.Triangle)]
        [InlineData(WindowShapes.Arch)]
        [InlineData(WindowShapes.Hexagon)]
        [InlineData(WindowShapes.Trapezoid)]
        [InlineData(WindowShapes.Rhombus)]
        [InlineData(WindowShapes.RightTriangle)]
        [InlineData(WindowShapes.Squircle)]
        [InlineData(WindowShapes.Blob)]
        public void OutlineStaysInsideTheWindow(string shape)
        {
            var points = WindowShapes.Outline(shape, flipH: true, flipV: true)!;
            Assert.True(points.Length >= 3);
            Assert.All(points, p =>
            {
                Assert.InRange(p.X, 0, 1);
                Assert.InRange(p.Y, 0, 1);
            });
        }

        [Fact]
        public void EveryShapeTakesARadiusAndAHeavyGutterInAnyWindow()
        {
            foreach (string shape in WindowShapes.All.Where(s => s != WindowShapes.Rectangle))
            {
                foreach (var (w, h) in new[] { (800.0, 500.0), (500.0, 800.0), (100.0, 80.0), (1400.0, 90.0) })
                {
                    var geometry = new CellGeometryDto { Shape = shape };
                    var outer = WindowShapes.Placed(geometry, 0, 0, w, h)!;
                    var inner = WindowShapes.Placed(geometry, 0, 0, w, h, radius: 30, inset: 40)!;
                    Assert.True(outer.Min(p => p.X) >= -1e-6 && outer.Max(p => p.X) <= w + 1e-6, shape);
                    Assert.True(outer.Min(p => p.Y) >= -1e-6 && outer.Max(p => p.Y) <= h + 1e-6, shape);
                    Assert.All(inner, p => Assert.True(double.IsFinite(p.X) && double.IsFinite(p.Y), shape));
                    // A window too small for the gutter gets less of it, but the shape never spills out of its window.
                    Assert.True(inner.Min(p => p.X) >= -1 && inner.Max(p => p.X) <= w + 1, $"{shape} {w}x{h} X");
                    Assert.True(inner.Min(p => p.Y) >= -1 && inner.Max(p => p.Y) <= h + 1, $"{shape} {w}x{h} Y");
                }
            }
        }

        [Fact]
        public void CurvedShapesReachEverySideOfTheWindow()
        {
            foreach (string shape in new[] { WindowShapes.Squircle, WindowShapes.Blob, WindowShapes.Arch })
            {
                var points = WindowShapes.Outline(shape)!;
                Assert.Equal(0, points.Min(p => p.X), 2);
                Assert.Equal(1, points.Max(p => p.X), 2);
                Assert.Equal(0, points.Min(p => p.Y), 2);
                Assert.Equal(1, points.Max(p => p.Y), 2);
            }
        }

        [Fact]
        public void MirrorMovesTheNotchOfTheL()
        {
            // The notch is top right. Mirrored, the corner at top right is filled.
            Assert.DoesNotContain((1.0, 0.0), WindowShapes.Outline(WindowShapes.LShape)!);
            Assert.Contains((1.0, 0.0), WindowShapes.Outline(WindowShapes.LShape, flipH: true)!);
        }

        [Fact]
        public void NotchMovesTheInnerCornerOfTheL()
        {
            Assert.Contains((0.3, 0.7), WindowShapes.Outline(WindowShapes.LShape, notchX: 0.3, notchY: 0.7)!);
        }

        [Fact]
        public void CornerRadiusCutsTheSharpCornersOfAShape()
        {
            var geometry = new CellGeometryDto { Shape = WindowShapes.LShape };
            Assert.Contains((0.0, 0.0), WindowShapes.Placed(geometry, 0, 0, 100, 100)!);
            var rounded = WindowShapes.Placed(geometry, 0, 0, 100, 100, 10)!;
            Assert.DoesNotContain((0.0, 0.0), rounded);
            Assert.Contains((10.0, 0.0), rounded);
            Assert.All(rounded, p =>
            {
                Assert.InRange(p.X, 0, 100);
                Assert.InRange(p.Y, 0, 100);
            });
        }

        [Fact]
        public void CoverBoxMatchesAPlainCellAndNeedsLessForAnOval()
        {
            var plain = WindowShapes.CoverBox(new CellGeometryDto(), 400, 300);
            Assert.Equal(400, plain.Width, 3);
            Assert.Equal(300, plain.Height, 3);

            var quarter = WindowShapes.CoverBox(new CellGeometryDto { ImageRotation = 90 }, 400, 300);
            Assert.Equal(300, quarter.Width, 3);
            Assert.Equal(400, quarter.Height, 3);

            var square = WindowShapes.CoverBox(new CellGeometryDto { ImageCounter = 45 }, 400, 400);
            var circle = WindowShapes.CoverBox(new CellGeometryDto { ImageCounter = 45, Shape = WindowShapes.Oval }, 400, 400);
            Assert.True(square.Width > 560);
            Assert.InRange(circle.Width, 395, 401);
        }

        [Fact]
        public void InsetMovesEveryEdgeOfAnLTheSameDistance()
        {
            var inner = WindowShapes.Placed(new CellGeometryDto { Shape = WindowShapes.LShape }, 0, 0, 100, 100, inset: 10)!;
            var want = new[] { (10.0, 10.0), (40.0, 10.0), (40.0, 60.0), (90.0, 60.0), (90.0, 90.0), (10.0, 90.0) };
            Assert.Equal(want.Length, inner.Length);
            for (int i = 0; i < want.Length; i++)
            {
                Assert.Equal(want[i].Item1, inner[i].X, 6);
                Assert.Equal(want[i].Item2, inner[i].Y, 6);
            }
        }

        [Fact]
        public void InsetIsTheSameOnAllThreeSidesOfATriangle()
        {
            var inner = WindowShapes.Placed(new CellGeometryDto { Shape = WindowShapes.Triangle }, 0, 0, 200, 100, inset: 10)!;
            var outer = WindowShapes.Placed(new CellGeometryDto { Shape = WindowShapes.Triangle }, 0, 0, 200, 100)!;
            for (int edge = 0; edge < 3; edge++)
            {
                var a = outer[edge];
                var b = outer[(edge + 1) % 3];
                double length = Math.Sqrt((b.X - a.X) * (b.X - a.X) + (b.Y - a.Y) * (b.Y - a.Y));
                double nearest = inner.Min(p => Math.Abs((b.X - a.X) * (a.Y - p.Y) - (a.X - p.X) * (b.Y - a.Y)) / length);
                Assert.Equal(10, nearest, 6);
            }
        }

        [Fact]
        public void ShadowFollowsTheOutlineAndNotItsRectangle()
        {
            var triangle = WindowShapes.Placed(new CellGeometryDto { Shape = WindowShapes.Triangle }, 0, 0, 200, 200, inset: 30)!;
            var alpha = FrameShadow.RenderAlpha(200, 200, triangle, 60);
            byte At(int x, int y) => alpha[y * 200 + x];
            Assert.Equal(0, At(100, 150));      // under the photo
            Assert.True(At(100, 180) > 0);      // just below the bottom edge
            Assert.Equal(0, At(10, 10));        // the far top corner of the rectangle
        }

        [Fact]
        public void NoTwoSwatchesLookAlike()
        {
            var lists = new[]
            {
                FramePalette.Colors.Select(c => c.Hex).ToArray(),
                FramePalette.MatColors.Select(c => c.Hex).ToArray(),
                CanvasLook.Colors.Select(c => c.Hex).ToArray()
            };
            foreach (var list in lists)
            {
                for (int i = 0; i < list.Length; i++)
                {
                    for (int j = i + 1; j < list.Length; j++)
                        Assert.True(FramePalette.Distance(list[i], list[j]) >= 60, list[i] + " is too close to " + list[j]);
                }
            }
        }

        [Fact]
        public void SnapPullsTheNearestEdgeOrCentreOntoALine()
        {
            Assert.Equal((100.0, (double?)100), FreeformSnap.Snap(104, 50, new[] { 100.0 }));
            Assert.Equal((75.0, (double?)100), FreeformSnap.Snap(72, 50, new[] { 100.0 }));
            Assert.Equal((50.0, (double?)100), FreeformSnap.Snap(53, 50, new[] { 100.0 }));
            Assert.Equal((200.0, (double?)null), FreeformSnap.Snap(200, 50, new[] { 100.0 }));
        }

        [Fact]
        public void TurningAnImageWrapsAroundACircle()
        {
            var geometry = new CellGeometryDto();
            geometry.TurnImage(-90);
            Assert.Equal(270, geometry.ImageRotation);
            geometry.TurnImage(90);
            Assert.Equal(0, geometry.ImageRotation);
        }

        [Fact]
        public void GeometryAndSurfaceSurviveTheLayoutFile()
        {
            var file = new MultiViewLayoutFile
            {
                Canvas = new CanvasLayoutDto { BackgroundColor = "#FFFFFFFF", Surface = CanvasLook.Vignette },
                Photos = { new PhotoSlotDto { Path = "a.jpg", Geometry = new CellGeometryDto { ImageRotation = 90, ImageFlipH = true } } },
                Freeform = new() { new FreeformCellDto { Path = "a.jpg", Geometry = new CellGeometryDto { Shape = WindowShapes.Triangle, WindowRotation = 15 } } }
            };

            var loaded = MultiViewLayoutFile.FromJson(file.ToJson())!;

            Assert.Equal(CanvasLook.Vignette, loaded.Canvas.Surface);
            Assert.Equal(90, loaded.Photos.Single().Geometry!.ImageRotation);
            Assert.True(loaded.Photos.Single().Geometry!.ImageFlipH);
            Assert.Equal(WindowShapes.Triangle, loaded.Freeform!.Single().Geometry!.Shape);
            Assert.Equal(15, loaded.Freeform!.Single().Geometry!.WindowRotation);
        }
    }
}
