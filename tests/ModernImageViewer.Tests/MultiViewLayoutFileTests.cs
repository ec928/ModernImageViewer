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
                    new() { Slot = 0, Path = @"C:\Photos\PhotoA.jpg", Name = "PhotoA.jpg", Zoom = 1.5, PanX = 10.0, PanY = -5.0, Hidden = true },
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
            Assert.True(loaded.Photos[0].Hidden);
            Assert.False(loaded.Photos[1].Hidden);

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
        public void SerializeAndDeserialize_PreservesFreeformCells()
        {
            var file = new MultiViewLayoutFile
            {
                Freeform = new List<FreeformCellDto>
                {
                    new()
                    {
                        SourceSlot = 0,
                        Path = @"C:\Photos\Floating1.jpg",
                        Name = "Floating1.jpg",
                        X = 120.0,
                        Y = 80.0,
                        Width = 450.0,
                        Height = 320.0,
                        ZIndex = 5,
                        Zoom = 1.25,
                        PanX = 15.0,
                        PanY = -10.0,
                        Hidden = true,
                        Border = new CellBorderDto { Thickness = 1, StrokeStyle = "InnerMuted" }
                    }
                }
            };

            string json = file.ToJson();
            Assert.Contains(@"""freeform"": [", json);
            Assert.Contains(@"""sourceSlot"": 0", json);
            Assert.Contains(@"""Floating1.jpg""", json);

            var loaded = MultiViewLayoutFile.FromJson(json);
            Assert.NotNull(loaded?.Freeform);
            Assert.Single(loaded.Freeform);
            var f0 = loaded.Freeform[0];
            Assert.Equal(0, f0.SourceSlot);
            Assert.Equal(@"C:\Photos\Floating1.jpg", f0.Path);
            Assert.Equal(120.0, f0.X);
            Assert.Equal(80.0, f0.Y);
            Assert.Equal(450.0, f0.Width);
            Assert.Equal(320.0, f0.Height);
            Assert.Equal(5, f0.ZIndex);
            Assert.Equal(1.25, f0.Zoom);
            Assert.True(f0.Hidden);
            Assert.NotNull(f0.Border);
            Assert.Equal("InnerMuted", f0.Border.StrokeStyle);
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

        [Fact]
        public void SameLook_IgnoresTheShadowFlagButNotTheFrame()
        {
            var frame = new CellBorderDto { Thickness = 2, Padding = 8, ShadowIntensity = 40, Shadow = true };
            var same = frame.Clone();
            same.Shadow = !frame.Shadow;
            Assert.True(frame.SameLook(same));

            same.Thickness = 9;
            Assert.False(frame.SameLook(same));
            Assert.True(FrameLooks.Flush().SameLook(FrameLooks.Flush()));
        }

        [Fact]
        public void Mosaic_FitsOnlyATreeThatPlacesEachPhotoOnce()
        {
            var good = MosaicNodeDto.FromMosaicLayout(MosaicLayout.FromCounts(new[] { 2, 2 }, rows: true));
            Assert.True(good.FitsPhotos(4));
            Assert.False(good.FitsPhotos(5));
            Assert.False(good.FitsPhotos(3));

            var duplicate = new MosaicNodeDto
            {
                Weights = new List<double> { 1, 1 },
                Children = new List<MosaicNodeDto> { new() { Leaf = 0 }, new() { Leaf = 0 } }
            };
            Assert.False(duplicate.FitsPhotos(2));

            var missingWeights = new MosaicNodeDto
            {
                Children = new List<MosaicNodeDto> { new() { Leaf = 0 }, new() { Leaf = 1 } }
            };
            Assert.False(missingWeights.FitsPhotos(2));
        }

        [Fact]
        public void FrameColor_MutedMatAndGradientFollowTheSwatch()
        {
            var gold = FrameColor.MutedStroke("#FFFFC83B");
            Assert.Equal(255, gold.R);
            Assert.Equal(200, gold.G);
            Assert.Equal(59, gold.B);
            Assert.Equal(140, gold.A);

            var fromClear = FrameColor.MutedStroke("#00000000");
            Assert.Equal(255, fromClear.R);
            Assert.Equal(140, fromClear.A);

            var frost = FrameColor.MatFill("Acrylic", "#FFF3EFE6");
            Assert.Equal(48, frost.A);
            Assert.Equal(243, frost.R);
            Assert.Equal(239, frost.G);
            Assert.Equal(230, frost.B);

            var solid = FrameColor.MatFill("Solid", "#FFF8F8F8");
            Assert.Equal(255, solid.A);
            Assert.Equal(248, solid.R);

            Assert.Equal(0, FrameColor.MatFill("None", "#FFFFFFFF").A);

            var faded = FrameColor.GradientEnd("#00000000", new FrameColor.Rgba(255, 0, 120, 212));
            Assert.Equal(102, faded.A);
            Assert.Equal(0, faded.R);
            Assert.Equal(120, faded.G);
            Assert.Equal(212, faded.B);

            var goldEnd = FrameColor.GradientEnd("#FFFFC83B", new FrameColor.Rgba(255, 255, 255, 255));
            Assert.Equal(255, goldEnd.A);
            Assert.Equal(255, goldEnd.R);
            Assert.Equal(200, goldEnd.G);
            Assert.Equal(59, goldEnd.B);

            Assert.Equal("FFFFC83B", FrameColor.Canon("#FFC83B", "#00000000"));
            Assert.Equal(0, FrameColor.Parse("").A);
            Assert.Equal(255, FrameColor.Parse("nope").A);
        }

        [Fact]
        public void FrameShadow_NinetyOnCreamIsLowAndFades()
        {
            const int width = 640;
            const int height = 400;
            const double pad = 48;
            double cardW = width - (pad * 2);
            double cardH = height - (pad * 2);
            int cardTop = (int)pad;
            int cardBottom = (int)(pad + cardH);
            int cardLeft = (int)pad;
            int midX = width / 2;
            int midY = height / 2;

            var none = FrameShadow.RenderAlpha(width, height, pad, pad, cardW, cardH, 0, 0);
            Assert.Equal(0, none[midY * width + midX]);

            var watch = System.Diagnostics.Stopwatch.StartNew();
            var alpha = FrameShadow.RenderAlpha(width, height, pad, pad, cardW, cardH, 0, 90);
            watch.Stop();

            int At(int x, int y) => alpha[(y * width) + x];
            int bottomNear = At(midX, cardBottom + 2);
            int bottomMid = At(midX, cardBottom + 10);
            int bottomFar = At(midX, cardBottom + 22);
            int bottomGone = At(midX, cardBottom + 36);
            int topNear = At(midX, cardTop - 4);
            int sideNear = At(cardLeft - 4, midY);
            int outer = At(8, 8);

            var mild = FrameShadow.RenderAlpha(width, height, pad, pad, cardW, cardH, 0, 30);
            int mildBottom = mild[((cardBottom + 2) * width) + midX];

            WriteShadowPreview(width, height, pad, cardW, cardH, alpha, watch.ElapsedMilliseconds, bottomNear, bottomMid, bottomFar, bottomGone, topNear, sideNear, outer);

            Assert.True(bottomNear > topNear + 15, "bottom " + bottomNear + " top " + topNear);
            Assert.True(bottomNear > sideNear, "bottom " + bottomNear + " side " + sideNear);
            Assert.InRange(bottomNear, 40, 190);
            Assert.True(bottomNear > bottomMid && bottomMid > bottomFar && bottomFar > bottomGone);
            Assert.True(bottomNear - bottomMid < bottomNear * 0.55);
            Assert.True(bottomFar > 12, "far " + bottomFar);
            Assert.True(bottomGone < 14, "gone " + bottomGone);
            Assert.True(outer < 8, "outer " + outer);
            Assert.True(mildBottom < bottomNear);
        }

        private static void WriteShadowPreview(int width, int height, double pad, double cardW, double cardH, byte[] alpha, long elapsedMs, int bottomNear, int bottomMid, int bottomFar, int bottomGone, int topNear, int sideNear, int outer)
        {
            const byte matR = 0xF3, matG = 0xEF, matB = 0xE6;
            const byte cardR = 0x1A, cardG = 0x6E, cardB = 0xA8;
            var bgr = new byte[width * height * 3];
            for (int y = 0; y < height; y++)
            {
                for (int x = 0; x < width; x++)
                {
                    int a = alpha[(y * width) + x];
                    byte r = (byte)(matR * (255 - a) / 255);
                    byte g = (byte)(matG * (255 - a) / 255);
                    byte b = (byte)(matB * (255 - a) / 255);
                    double px = x + 0.5;
                    double py = y + 0.5;
                    if (px >= pad && py >= pad && px < pad + cardW && py < pad + cardH)
                    {
                        r = cardR;
                        g = cardG;
                        b = cardB;
                    }
                    int i = ((y * width) + x) * 3;
                    bgr[i] = b;
                    bgr[i + 1] = g;
                    bgr[i + 2] = r;
                }
            }

            string path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "frame-shadow-90.bmp");
            int row = ((width * 3) + 3) & ~3;
            int pixelBytes = row * height;
            var file = new byte[54 + pixelBytes];
            file[0] = (byte)'B';
            file[1] = (byte)'M';
            int fileSize = file.Length;
            file[2] = (byte)fileSize;
            file[3] = (byte)(fileSize >> 8);
            file[4] = (byte)(fileSize >> 16);
            file[5] = (byte)(fileSize >> 24);
            file[10] = 54;
            file[14] = 40;
            file[18] = (byte)width;
            file[19] = (byte)(width >> 8);
            file[22] = (byte)height;
            file[23] = (byte)(height >> 8);
            file[26] = 1;
            file[28] = 24;
            for (int y = 0; y < height; y++)
            {
                int src = (height - 1 - y) * width * 3;
                int dst = 54 + (y * row);
                System.Buffer.BlockCopy(bgr, src, file, dst, width * 3);
            }
            System.IO.File.WriteAllBytes(path, file);
            System.IO.File.WriteAllText(path + ".txt",
                "ms=" + elapsedMs
                + " bottom2=" + bottomNear
                + " bottom10=" + bottomMid
                + " bottom22=" + bottomFar
                + " bottom36=" + bottomGone
                + " top4=" + topNear
                + " side4=" + sideNear
                + " outer=" + outer);
        }
    }
}
