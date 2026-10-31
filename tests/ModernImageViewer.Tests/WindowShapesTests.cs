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
