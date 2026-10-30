using System;
using System.Collections.Generic;
using Xunit;

namespace ModernImageViewer.Tests
{
    public class MultiViewLayoutFileTests
    {
        [Fact]
        public void SerializeAndDeserialize_PreservesMosaicTreeAndWeights()
        {
            // Build a non-trivial layout: a vertical cut of 2 rows, where row 0 has a horizontal cut of 2 cols
            var left = MosaicLayout.Cell(0);
            var right = MosaicLayout.Cell(1);
            var row0 = MosaicLayout.Cut(true, left, right);
            row0.Weights[0] = 0.4;
            row0.Weights[1] = 0.6;

            var bottom = MosaicLayout.Cell(2);
            var root = MosaicLayout.Cut(false, row0, bottom);
            root.Weights[0] = 0.7;
            root.Weights[1] = 0.3;

            var file = new MultiViewLayoutFile
            {
                SchemaVersion = 1,
                Canvas = new CanvasLayoutDto
                {
                    Aspect = "16:9",
                    BackgroundColor = "#FF121212"
                },
                State = new LayoutStateDto
                {
                    Lines = 2,
                    LineCounts = new List<int> { 2, 1 },
                    Flipped = false,
                    HeroActive = true,
                    HeroKind = "Banner",
                    HeroPosition = 0,
                    IsFillMode = true
                },
                Mosaic = MosaicNodeDto.FromMosaicLayout(root),
                Photos = new List<PhotoSlotDto>
                {
                    new() { Slot = 0, Path = @"C:\Photos\PhotoA.jpg", Name = "PhotoA.jpg", Zoom = 1.5, PanX = 10.0, PanY = -5.0 },
                    new() { Slot = 1, Path = @"C:\Photos\PhotoB.jpg", Name = "PhotoB.jpg" },
                    new() { Slot = 2, Path = @"C:\Photos\PhotoC.jpg", Name = "PhotoC.jpg" }
                }
            };

            string json = file.ToJson();
            Assert.Contains(@"""$schemaVersion"": 1", json);
            Assert.Contains(@"""aspect"": ""16:9""", json);
            Assert.Contains(@"PhotoA.jpg", json);
            Assert.Contains(@"""zoom"": 1.5", json);

            var loaded = MultiViewLayoutFile.FromJson(json);
            Assert.NotNull(loaded);
            Assert.Equal(1, loaded.SchemaVersion);
            Assert.Equal("16:9", loaded.Canvas.Aspect);
            Assert.Equal("#FF121212", loaded.Canvas.BackgroundColor);
            Assert.Equal(2, loaded.State.Lines);
            Assert.Equal(new List<int> { 2, 1 }, loaded.State.LineCounts);
            Assert.True(loaded.State.HeroActive);
            Assert.Equal("Banner", loaded.State.HeroKind);
            Assert.Equal(3, loaded.Photos.Count);
            Assert.Equal(@"C:\Photos\PhotoB.jpg", loaded.Photos[1].Path);
            Assert.Equal(1.5, loaded.Photos[0].Zoom);
            Assert.Equal(10.0, loaded.Photos[0].PanX);
            Assert.Equal(-5.0, loaded.Photos[0].PanY);

            // Verify MosaicLayout deserialization
            Assert.NotNull(loaded.Mosaic);
            var restoredRoot = loaded.Mosaic.ToMosaicLayout();
            Assert.False(restoredRoot.Vertical);
            Assert.Equal(2, restoredRoot.Children.Count);
            Assert.Equal(0.7, restoredRoot.Weights[0], precision: 4);
            Assert.Equal(0.3, restoredRoot.Weights[1], precision: 4);

            var restoredRow0 = restoredRoot.Children[0];
            Assert.True(restoredRow0.Vertical);
            Assert.Equal(2, restoredRow0.Children.Count);
            Assert.Equal(0, restoredRow0.Children[0].Leaf);
            Assert.Equal(1, restoredRow0.Children[1].Leaf);
            Assert.Equal(0.4, restoredRow0.Weights[0], precision: 4);
            Assert.Equal(0.6, restoredRow0.Weights[1], precision: 4);

            var restoredBottom = restoredRoot.Children[1];
            Assert.Equal(2, restoredBottom.Leaf);
        }

        [Fact]
        public void Deserialization_HandlesUnknownPropertiesAndDefaultsGracefully()
        {
            string futureJson = """
            {
              "$schemaVersion": 2,
              "futureUnknownField": "someValue",
              "canvas": {
                "aspect": "21:9"
              },
              "photos": [
                { "slot": 0, "path": "D:\\test.jpg", "extraField": 123 }
              ]
            }
            """;

            var loaded = MultiViewLayoutFile.FromJson(futureJson);
            Assert.NotNull(loaded);
            Assert.Equal(2, loaded.SchemaVersion);
            Assert.Equal("21:9", loaded.Canvas.Aspect);
            Assert.Single(loaded.Photos);
            Assert.Equal(@"D:\test.jpg", loaded.Photos[0].Path);
        }

        [Fact]
        public void SerializeAndDeserialize_PreservesCellBordersAndFraming()
        {
            var file = new MultiViewLayoutFile
            {
                SchemaVersion = 1,
                Photos = new List<PhotoSlotDto>
                {
                    new()
                    {
                        Slot = 0,
                        Path = @"C:\Photos\PhotoA.jpg",
                        Border = new CellBorderDto
                        {
                            Thickness = 4.0,
                            Color = "#FFFFFFFF",
                            Padding = 8.0,
                            CornerRadius = 12.0,
                            ShadowIntensity = 45.0,
                            StrokeStyle = "InnerMuted",
                            RimGlow = true,
                            GutterColor = "#FFF8F8F8",
                            GutterMaterial = "Solid"
                        }
                    },
                    new()
                    {
                        Slot = 1,
                        Path = @"C:\Photos\PhotoB.jpg",
                        Border = new CellBorderDto
                        {
                            Thickness = 1.0,
                            Color = "#FFFFC83B",
                            Padding = 16.0,
                            CornerRadius = 6.0,
                            ShadowIntensity = 20.0,
                            StrokeStyle = "Gradient"
                        }
                    }
                }
            };

            string json = file.ToJson();
            Assert.Contains(@"""thickness"": 4", json);
            Assert.Contains(@"""color"": ""#FFFFFFFF""", json);
            Assert.Contains(@"""padding"": 8", json);
            Assert.Contains(@"""cornerRadius"": 12", json);

            var loaded = MultiViewLayoutFile.FromJson(json);
            Assert.NotNull(loaded);
            Assert.Equal(2, loaded.Photos.Count);

            var b0 = loaded.Photos[0].Border;
            Assert.NotNull(b0);
            Assert.Equal(4.0, b0.Thickness);
            Assert.Equal("#FFFFFFFF", b0.Color);
            Assert.Equal(8.0, b0.Padding);
            Assert.Equal(12.0, b0.CornerRadius);
            Assert.Equal(45.0, b0.ShadowIntensity);
            Assert.Equal("InnerMuted", b0.StrokeStyle);
            Assert.True(b0.RimGlow);
            Assert.Equal(8.0, b0.Gutter);
            Assert.Equal("#FFF8F8F8", b0.GutterColor);
            Assert.Equal("Solid", b0.GutterMaterial);

            var b1 = loaded.Photos[1].Border;
            Assert.NotNull(b1);
            Assert.Equal(1.0, b1.Thickness);
            Assert.Equal("#FFFFC83B", b1.Color);
            Assert.Equal(16.0, b1.Padding);
            Assert.Equal(6.0, b1.CornerRadius);
            Assert.Equal(20.0, b1.ShadowIntensity);
            Assert.Equal("Gradient", b1.StrokeStyle);

            // Test clone
            var clone = b1.Clone();
            Assert.Equal(b1.Thickness, clone.Thickness);
            Assert.Equal(b1.Color, clone.Color);
            Assert.Equal(b1.Padding, clone.Padding);
            Assert.Equal(b1.CornerRadius, clone.CornerRadius);
            Assert.Equal(b1.ShadowIntensity, clone.ShadowIntensity);
            Assert.Equal(b1.StrokeStyle, clone.StrokeStyle);
        }
    }
}
