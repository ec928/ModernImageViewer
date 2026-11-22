using System;
using Xunit;

namespace ModernImageViewer.Tests
{
    public class SwayPortraitTests
    {
        private const int W = 80;
        private const int H = 80;

        private static byte[] MakeCenterMask(int w = W, int h = H, int x0 = 28, int x1 = 52, int y0 = 16, int y1 = 72)
        {
            var alpha = new byte[w * h];
            for (int y = y0; y < y1; y++)
                for (int x = x0; x < x1; x++)
                    alpha[y * w + x] = 255;
            return alpha;
        }

        [Fact]
        public void ComputePortraitPivot_anchors_to_subject_away_from_background_median()
        {
            // Small subject (~22% area) at depth ~0.78 (byte 200) against background at ~0.16 (byte 40).
            // Global median sits in background (~0.16), whereas portrait pivot should anchor near subject.
            var grey = new byte[W * H];
            Array.Fill(grey, (byte)40);
            var mask = MakeCenterMask();
            for (int i = 0; i < grey.Length; i++)
                if (mask[i] == 255) grey[i] = 200;

            var stats = SwayPortrait.ComputeSubjectStats(grey, W, H, mask, W);
            Assert.True(stats.HasUsableSubject);
            Assert.InRange(stats.Area, 0.15f, 0.30f);
            Assert.InRange(stats.SubjectMedianDepth, 0.75f, 0.82f);
            Assert.InRange(stats.BgMedianDepth, 0.14f, 0.18f);

            float globalMiddle = 40f / 255f;
            float origPivot = SwayPortrait.ComputePortraitPivot(globalMiddle, stats, backdropActive: false);
            float bgPivot = SwayPortrait.ComputePortraitPivot(globalMiddle, stats, backdropActive: true);

            Assert.True(origPivot > 0.60f, $"original mode pivot should stay close to subject (got {origPivot})");
            Assert.InRange(bgPivot, stats.SubjectMedianDepth - 0.02f, stats.SubjectMedianDepth + 0.02f);
        }

        [Fact]
        public void ShapeSubjectDepth_compresses_internal_spread_without_touching_background()
        {
            // Subject occupies columns 28..51; left of 28 is background depth 30.
            // Inside subject, depth ranges from 120 (ears/shoulder) to 245 (nose).
            var crisp = new byte[W * H * 4];
            var mask = MakeCenterMask();
            for (int y = 0; y < H; y++)
            {
                for (int x = 0; x < W; x++)
                {
                    byte d = mask[y * W + x] == 0
                        ? (byte)30
                        : (x < 40 ? (byte)120 : (byte)245);
                    int o = (y * W + x) * 4;
                    crisp[o] = d;
                    crisp[o + 1] = d;
                    crisp[o + 2] = d;
                    crisp[o + 3] = 255;
                }
            }

            byte[] snapshot = (byte[])crisp.Clone();
            float subjMedian = 182f / 255f;
            byte[] shaped = SwayPortrait.ShapeSubjectDepth(crisp, W, H, mask, W, subjMedian);

            // Source array must remain untouched.
            Assert.Equal(snapshot, crisp);

            // Background pixel stays exactly 30.
            Assert.Equal(30, shaped[(40 * W + 10) * 4]);

            // Internal subject spread is compressed (120 raised toward 182, 245 lowered toward 182),
            // while nose remains in front of ear/shoulder.
            byte earDepth = shaped[(40 * W + 32) * 4];
            byte noseDepth = shaped[(40 * W + 46) * 4];
            Assert.True(earDepth > 135, $"expected ear depth compressed upward toward median, got {earDepth}");
            Assert.True(noseDepth < 230, $"expected nose depth compressed downward toward median, got {noseDepth}");
            Assert.True(noseDepth > earDepth, "front-to-back ordering inside subject must remain intact");
        }

        [Fact]
        public void BuildDecontaminatedCutout_cleans_bright_wall_bleed_from_hair_rim()
        {
            // Photo: dark brown hair/subject (R=40, G=28, B=22) on pure white wall (255, 255, 255).
            // Mask: columns >= 40 opaque (255), column 39 soft edge (102 -> alpha 0.40), <= 38 empty (0).
            var photo = new byte[W * H * 4];
            var mask = new byte[W * H];
            for (int y = 0; y < H; y++)
            {
                for (int x = 0; x < W; x++)
                {
                    int idx = y * W + x;
                    int o = idx * 4;
                    if (x >= 40)
                    {
                        mask[idx] = 255;
                        photo[o] = 22; photo[o + 1] = 28; photo[o + 2] = 40; photo[o + 3] = 255;
                    }
                    else if (x == 39)
                    {
                        mask[idx] = 102; // 0.40 alpha contaminated with white wall: 0.4*hair + 0.6*255
                        photo[o] = 162; photo[o + 1] = 164; photo[o + 2] = 169; photo[o + 3] = 255;
                    }
                    else
                    {
                        mask[idx] = 0;
                        photo[o] = 255; photo[o + 1] = 255; photo[o + 2] = 255; photo[o + 3] = 255;
                    }
                }
            }

            byte[] cutout = SwayPortrait.BuildDecontaminatedCutout(photo, W, H, mask, W);

            // Empty background pixel is fully transparent zero.
            int bgIdx = (40 * W + 10) * 4;
            Assert.Equal(0, cutout[bgIdx]);
            Assert.Equal(0, cutout[bgIdx + 1]);
            Assert.Equal(0, cutout[bgIdx + 2]);
            Assert.Equal(0, cutout[bgIdx + 3]);

            // Opaque core keeps exact RGB and full alpha.
            int coreIdx = (40 * W + 50) * 4;
            Assert.Equal(22, cutout[coreIdx]);
            Assert.Equal(28, cutout[coreIdx + 1]);
            Assert.Equal(40, cutout[coreIdx + 2]);
            Assert.Equal(255, cutout[coreIdx + 3]);

            // Rim pixel (col 39, alpha ~0.40) must have unmixed white halo removed so premultiplied RGB
            // reflects hair color rather than 0.40 * 169 = 68.
            int rimIdx = (40 * W + 39) * 4;
            Assert.InRange(cutout[rimIdx + 3], 95, 110);
            Assert.True(cutout[rimIdx + 2] < 32, $"expected decontaminated premultiplied red < 32, got {cutout[rimIdx + 2]}");
        }

        [Fact]
        public void BuildSubjectDilatedDepth_stays_smooth_across_silhouette_boundary()
        {
            // Depth map has sharp cliff: 20 outside, 180 inside; mask cuts at column 40.
            var grey = new byte[W * H];
            var mask = new byte[W * H];
            for (int y = 0; y < H; y++)
            {
                for (int x = 0; x < W; x++)
                {
                    int idx = y * W + x;
                    bool inside = x >= 40 && x < 70 && y >= 10 && y < 70;
                    mask[idx] = inside ? (byte)255 : (byte)0;
                    grey[idx] = inside ? (byte)180 : (byte)20;
                }
            }

            byte[] dilated = SwayPortrait.BuildSubjectDilatedDepth(grey, W, H, mask, W, 180f / 255f);

            // Pixels just outside the mask (columns 34..39) must carry subject depth smoothly without dropping to 20.
            byte insideDepth = dilated[(40 * W + 45) * 4];
            byte edgeOutDepth = dilated[(40 * W + 37) * 4];
            Assert.True(Math.Abs(insideDepth - edgeOutDepth) <= 6, $"expected continuous dilated depth across edge, got in={insideDepth}, out={edgeOutDepth}");
            Assert.True(edgeOutDepth > 150, $"expected dilated depth outside rim near subject depth, got {edgeOutDepth}");
        }

        [Fact]
        public void SampleBackgroundPalette_ignores_subject_and_samples_backdrop_top_bottom()
        {
            // Top half background is cool sky blue (R=40, G=120, B=220).
            // Bottom half background is dark forest green (R=20, G=80, B=30).
            // Center subject is bright magenta (R=255, G=0, B=255) and must be ignored.
            var photo = new byte[W * H * 4];
            var mask = MakeCenterMask();
            for (int y = 0; y < H; y++)
            {
                for (int x = 0; x < W; x++)
                {
                    int idx = y * W + x;
                    int o = idx * 4;
                    if (mask[idx] == 255)
                    {
                        photo[o] = 255; photo[o + 1] = 0; photo[o + 2] = 255; photo[o + 3] = 255;
                    }
                    else if (y < H / 2)
                    {
                        photo[o] = 220; photo[o + 1] = 120; photo[o + 2] = 40; photo[o + 3] = 255;
                    }
                    else
                    {
                        photo[o] = 30; photo[o + 1] = 80; photo[o + 2] = 20; photo[o + 3] = 255;
                    }
                }
            }

            var pal = SwayPortrait.SampleBackgroundPalette(photo, W, H, mask, W);
            Assert.True(pal.TopB > pal.TopR * 2f, $"expected cool blue top background, got R={pal.TopR}, B={pal.TopB}");
            Assert.True(pal.BotG > pal.BotR * 2f, $"expected green bottom background, got R={pal.BotR}, G={pal.BotG}");
            // Accent must NOT come from magenta subject (where R and B are both near 1 and G near 0).
            Assert.False(pal.AccR > 0.9f && pal.AccB > 0.9f && pal.AccG < 0.1f, "accent leaked from foreground subject");
        }

        [Fact]
        public void SolidifySubjectMask_fills_interior_face_and_arm_dips_without_filling_background_holes()
        {
            const int S = 160;
            var mask = new byte[S * S];
            var grey = new byte[S * S];
            for (int y = 0; y < S; y++)
            {
                for (int x = 0; x < S; x++)
                {
                    int i = y * S + x;
                    bool inBox = x >= 30 && x < 130 && y >= 20 && y < 140;
                    if (!inBox)
                    {
                        mask[i] = 0;
                        grey[i] = 30;
                    }
                    else if (x == 30 || x == 31)
                    {
                        // Outer 2-px soft hair/edge transition
                        mask[i] = (byte)(x == 30 ? 80 : 160);
                        grey[i] = 190;
                    }
                    else
                    {
                        mask[i] = 255;
                        grey[i] = 190;
                    }
                }
            }

            // 1) Interior cheek/sleeve confidence dip (alpha=140, foreground depth=190)
            for (int y = 60; y < 76; y++)
                for (int x = 60; x < 76; x++)
                    mask[y * S + x] = 140;

            // 2) Enclosed foreground pocket dipping even lower (alpha=20, foreground depth=190)
            for (int y = 85; y < 95; y++)
                for (int x = 60; x < 70; x++)
                    mask[y * S + x] = 20;

            // 3) True enclosed background gap (between arm & torso: alpha=0, background depth=30)
            for (int y = 60; y < 76; y++)
                for (int x = 100; x < 112; x++)
                {
                    mask[y * S + x] = 0;
                    grey[y * S + x] = 30;
                }

            var stats = SwayPortrait.ComputeSubjectStats(grey, S, S, mask, S);
            byte[] solid = SwayPortrait.SolidifySubjectMask(mask, S, grey, S, S, stats);

            Assert.Equal(255, solid[68 * S + 68]);   // Cheek dip solidified
            Assert.Equal(255, solid[90 * S + 65]);   // Enclosed foreground pocket solidified
            Assert.Equal(80, solid[80 * S + 30]);    // Outer soft edge preserved
            Assert.Equal(0, solid[68 * S + 106]);    // True background hole stays 0
        }

        [Fact]
        public void BuildDecontaminatedCutout_never_overwrites_interior_pixels_away_from_silhouette()
        {
            // Subject spans columns 20..79; interior pixel at col 50 has alpha=178 (~0.70) and distinctive green RGB=(30,210,40),
            // surrounded by bright white RGB=(245,245,245). Decontamination must NOT smear white over col 50!
            // Meanwhile outer rim at col 19 (adjacent to col 18 alpha=0 white background) MUST be decontaminated toward subject.
            var photo = new byte[W * H * 4];
            var mask = new byte[W * H];
            for (int y = 0; y < H; y++)
            {
                for (int x = 0; x < W; x++)
                {
                    int idx = y * W + x;
                    int o = idx * 4;
                    if (x >= 20)
                    {
                        mask[idx] = 255;
                        photo[o] = 245; photo[o + 1] = 245; photo[o + 2] = 245; photo[o + 3] = 255;
                    }
                    else if (x == 19)
                    {
                        mask[idx] = 102; // 0.40 alpha on white/bright-subject vs dark edge test below
                        photo[o] = 10; photo[o + 1] = 10; photo[o + 2] = 10; photo[o + 3] = 255;
                    }
                    else
                    {
                        mask[idx] = 0;
                        photo[o] = 0; photo[o + 1] = 0; photo[o + 2] = 0; photo[o + 3] = 255;
                    }
                }
            }

            // Set interior dip at (50, 40): alpha=178 (0.70), original straight color B=30, G=210, R=40
            mask[40 * W + 50] = 178;
            int dipO = (40 * W + 50) * 4;
            photo[dipO] = 30; photo[dipO + 1] = 210; photo[dipO + 2] = 40; photo[dipO + 3] = 255;

            byte[] cutout = SwayPortrait.BuildDecontaminatedCutout(photo, W, H, mask, W);

            // Interior dip at (50, 40) keeps exact original RGB scaled by alpha=178/255 (~0.698): R~28, G~147, B~21.
            // If BFS had overwritten it with white (245), R and B would be ~171!
            Assert.True(cutout[dipO + 2] < 40, $"interior pixel red should preserve original ~28, got {cutout[dipO + 2]}");
            Assert.True(cutout[dipO + 1] > 130, $"interior pixel green should preserve original ~147, got {cutout[dipO + 1]}");

            // Outer rim at (19, 40) next to alpha=0 should be decontaminated upward toward interior 245 (premult > 60).
            int rimO = (40 * W + 19) * 4;
            Assert.True(cutout[rimO + 2] > 60, $"outer rim pixel should still decontaminate toward interior 245, got {cutout[rimO + 2]}");
        }

        [Fact]
        public void SolidifySubjectMask_leaves_zero_ring_around_enclosed_cheek_dip()
        {
            const int S = 160;
            var mask = new byte[S * S];
            var grey = new byte[S * S];
            for (int y = 0; y < S; y++)
            {
                for (int x = 0; x < S; x++)
                {
                    int i = y * S + x;
                    bool inBox = x >= 25 && x < 135 && y >= 20 && y < 140;
                    mask[i] = inBox ? (byte)255 : (byte)0;
                    grey[i] = inBox ? (byte)195 : (byte)25;
                }
            }

            // Concentric cheek dip well inside the face:
            // outer ring (r=6..8): alpha=150; inner ring (r=4..5): alpha=60; core (r<4): alpha=20. All at foreground depth 195.
            int cx = 80, cy = 80;
            for (int y = cy - 10; y <= cy + 10; y++)
            {
                for (int x = cx - 10; x <= cx + 10; x++)
                {
                    int d2 = (x - cx) * (x - cx) + (y - cy) * (y - cy);
                    if (d2 <= 16) mask[y * S + x] = 20;
                    else if (d2 <= 36) mask[y * S + x] = 60;
                    else if (d2 <= 64) mask[y * S + x] = 150;
                }
            }

            var stats = SwayPortrait.ComputeSubjectStats(grey, S, S, mask, S);
            byte[] solid = SwayPortrait.SolidifySubjectMask(mask, S, grey, S, S, stats);

            for (int y = cy - 10; y <= cy + 10; y++)
                for (int x = cx - 10; x <= cx + 10; x++)
                    Assert.Equal(255, solid[y * S + x]);
        }

        [Fact]
        public void BuildSymmetricCleanPlate_fills_symmetrically_without_foreground_bleed()
        {
            // Left half background = blue (220, 40, 20), right half background = green (30, 210, 20).
            // Center subject = bright red circle (10, 20, 240).
            var photo = new byte[W * H * 4];
            var mask = new byte[W * H];
            for (int y = 0; y < H; y++)
            {
                for (int x = 0; x < W; x++)
                {
                    int idx = y * W + x;
                    int o = idx * 4;
                    float dx = x - W * 0.5f, dy = y - H * 0.5f;
                    bool inCircle = dx * dx + dy * dy <= 14f * 14f;
                    if (inCircle)
                    {
                        mask[idx] = 255;
                        photo[o] = 10; photo[o + 1] = 20; photo[o + 2] = 240; photo[o + 3] = 255;
                    }
                    else if (x < W / 2)
                    {
                        photo[o] = 220; photo[o + 1] = 40; photo[o + 2] = 20; photo[o + 3] = 255;
                    }
                    else
                    {
                        photo[o] = 30; photo[o + 1] = 210; photo[o + 2] = 20; photo[o + 3] = 255;
                    }
                }
            }

            byte[] plate = SwayPortrait.BuildSymmetricCleanPlate(photo, W, H, mask, W);

            // Left interior of circle (x=34, y=40) fills symmetrically toward blue with zero red bleed.
            int leftHoleO = (40 * W + 34) * 4;
            Assert.True(plate[leftHoleO] > plate[leftHoleO + 1] + 40, $"expected blue dominance on left side of hole, got BGR=({plate[leftHoleO]},{plate[leftHoleO + 1]},{plate[leftHoleO + 2]})");
            Assert.True(plate[leftHoleO + 2] < 60, $"expected zero foreground red bleed on left side, got R={plate[leftHoleO + 2]}");

            // Right interior of circle (x=46, y=40) fills symmetrically toward green with zero red bleed.
            int rightHoleO = (40 * W + 46) * 4;
            Assert.True(plate[rightHoleO + 1] > plate[rightHoleO] + 40, $"expected green dominance on right side of hole, got BGR=({plate[rightHoleO]},{plate[rightHoleO + 1]},{plate[rightHoleO + 2]})");
            Assert.True(plate[rightHoleO + 2] < 60, $"expected zero foreground red bleed on right side, got R={plate[rightHoleO + 2]}");

            // Unoccluded far background stays pixel-identical.
            int farLeftO = (5 * W + 5) * 4;
            int farRightO = (5 * W + (W - 6)) * 4;
            Assert.Equal((byte)220, plate[farLeftO]);
            Assert.Equal((byte)40, plate[farLeftO + 1]);
            Assert.Equal((byte)20, plate[farLeftO + 2]);
            Assert.Equal((byte)30, plate[farRightO]);
            Assert.Equal((byte)210, plate[farRightO + 1]);
            Assert.Equal((byte)20, plate[farRightO + 2]);
        }

        [Fact]
        public void SolidifySubjectMask_solidifies_deeper_limb_dip_while_preserving_background_hole()
        {
            const int S = 160;
            var mask = new byte[S * S];
            var grey = new byte[S * S];
            for (int y = 0; y < S; y++)
            {
                for (int x = 0; x < S; x++)
                {
                    int i = y * S + x;
                    bool inSubject = x >= 25 && x < 135 && y >= 20 && y < 140;
                    mask[i] = inSubject ? (byte)255 : (byte)0;
                    grey[i] = inSubject ? (byte)191 : (byte)38; // ~0.75 subject median, ~0.15 bg median
                }
            }

            // Deeper limb region (~0.48 = 122) containing an interior low-confidence dip (mask=20).
            for (int y = 95; y < 125; y++)
            {
                for (int x = 35; x < 65; x++)
                {
                    grey[y * S + x] = 122;
                    if (x >= 46 && x <= 54 && y >= 106 && y <= 114)
                        mask[y * S + x] = 20;
                }
            }

            // Enclosed true background hole at bg depth (~0.15 = 38).
            for (int y = 50; y < 70; y++)
            {
                for (int x = 95; x < 115; x++)
                {
                    mask[y * S + x] = 0;
                    grey[y * S + x] = 38;
                }
            }

            var stats = SwayPortrait.ComputeSubjectStats(grey, S, S, mask, S);
            Assert.InRange(stats.SubjectMedianDepth, 0.72f, 0.78f);
            Assert.InRange(stats.BgMedianDepth, 0.13f, 0.17f);

            byte[] solid = SwayPortrait.SolidifySubjectMask(mask, S, grey, S, S, stats);
            Assert.Equal(255, solid[110 * S + 50]); // Deeper limb dip solidified
            Assert.Equal(0, solid[60 * S + 105]);    // True background hole remains transparent
        }

        [Fact]
        public void HolisticAlpha_releases_continuous_water_plane_when_expansion_exceeds_65_percent()
        {
            // Layout (200x100):
            // - Top 33 rows (33%): flat sky (depth = 0.0, subject = 0)
            // - Left 120 cols of bottom 67 rows (~40.2%): cliff/town subject (depth = 0.55, subject = 1)
            // - Right 80 cols of bottom 67 rows (~26.8%): textured water plane rising toward camera (depth = 0.12..0.50, subject = 0)
            // Holistic depth expansion swallows the water plane to unionArea ~ 67%, so HolisticAlpha must report
            // unionArea > 0.65 (preserving ParallaxJudge) while returning the clean subject mask so water stays mobile.
            const int cw = 200, ch = 100;
            int n = cw * ch;
            var light = new float[n];
            var depth = new float[n];
            var grey = new byte[n];
            var subj = new float[n];

            for (int y = 0; y < ch; y++)
            {
                for (int x = 0; x < cw; x++)
                {
                    int i = y * cw + x;
                    if (y < 33)
                    {
                        light[i] = 0.85f;
                        depth[i] = 0f;
                        grey[i] = 0;
                        subj[i] = 0f;
                    }
                    else if (x < 120)
                    {
                        light[i] = 0.45f;
                        depth[i] = 0.55f;
                        grey[i] = 140;
                        subj[i] = 1f;
                    }
                    else
                    {
                        // Textured ocean waves rising from 0.12 at horizon to 0.50 at bottom
                        float t = (y - 33) / 66f;
                        float d = 0.12f + 0.38f * t;
                        light[i] = ((y / 2) & 1) == 0 ? 0.25f : 0.75f;
                        depth[i] = d;
                        grey[i] = (byte)Math.Round(d * 255f);
                        subj[i] = 0f;
                    }
                }
            }

            float[] alpha = ParallaxLayers.HolisticAlpha(light, depth, grey, subj, cw, ch, out float unionArea, out _);
            Assert.True(unionArea > 0.65f && unionArea <= 0.68f, $"expected unionArea in (0.65, 0.68], got {unionArea:F3}");
            Assert.Equal(1f, alpha[60 * cw + 50]);   // Cliff/town remains stationary foreground
            Assert.Equal(0f, alpha[85 * cw + 170]);  // Near water on bottom-right stays mobile background
        }

        [Fact]
        public void HolisticAlpha_preserves_connected_architecture_when_union_is_moderate()
        {
            // Small subject (~12% area) next to connected foreground architecture (~36% extra -> union ~ 48% <= 65%).
            const int cw = 200, ch = 100;
            int n = cw * ch;
            var light = new float[n];
            var depth = new float[n];
            var grey = new byte[n];
            var subj = new float[n];

            for (int y = 0; y < ch; y++)
            {
                for (int x = 0; x < cw; x++)
                {
                    int i = y * cw + x;
                    if (y < 52)
                    {
                        light[i] = 0.85f;
                        depth[i] = 0f;
                        grey[i] = 0;
                    }
                    else
                    {
                        light[i] = ((x + y) & 1) == 0 ? 0.25f : 0.60f;
                        depth[i] = 0.65f;
                        grey[i] = 166;
                        if (x >= 75 && x < 125) subj[i] = 1f;
                    }
                }
            }

            float[] alpha = ParallaxLayers.HolisticAlpha(light, depth, grey, subj, cw, ch, out float unionArea, out _);
            Assert.InRange(unionArea, 0.40f, 0.55f);
            Assert.True(alpha[75 * cw + 30] > 0.5f, $"expected connected foreground architecture to stay locked, got {alpha[75 * cw + 30]:F2}");
        }

        [Fact]
        public void ShapeDepthGrey_before_SwayDepth_Crisp_never_creates_rim_depth_spike()
        {
            // Two foreground objects at different depths (like near blossom=230 and mid blossom=100 -> subject median ~125)
            // against dark background (depth=15). Shaping grey BEFORE SwayDepth.Crisp ensures the expanded crisp rim
            // inherits the exact shaped interior depth with zero spike wall around the near blossom.
            const int S = 120;
            var grey = new byte[S * S];
            var mask = new byte[S * S];
            var photo = new byte[S * S * 4];
            Array.Fill(grey, (byte)15);
            for (int y = 0; y < S; y++)
            {
                for (int x = 0; x < S; x++)
                {
                    int i = y * S + x;
                    int o = i * 4;
                    bool farObj = x >= 10 && x < 50 && y >= 20 && y < 100;
                    bool nearObj = x >= 70 && x < 100 && y >= 35 && y < 85;
                    if (farObj)
                    {
                        mask[i] = 255;
                        grey[i] = 100;
                        photo[o] = 180; photo[o + 1] = 160; photo[o + 2] = 220; photo[o + 3] = 255;
                    }
                    else if (nearObj)
                    {
                        mask[i] = 255;
                        grey[i] = 230;
                        photo[o] = 210; photo[o + 1] = 180; photo[o + 2] = 250; photo[o + 3] = 255;
                    }
                    else
                    {
                        photo[o] = 25; photo[o + 1] = 25; photo[o + 2] = 25; photo[o + 3] = 255;
                    }
                }
            }

            var stats = SwayPortrait.ComputeSubjectStats(grey, S, S, mask, S);
            byte[] shapedGrey = SwayPortrait.ShapeDepthGrey(grey, S, S, mask, S, stats.SubjectMedianDepth);
            byte[] crisp = SwayDepth.Crisp(shapedGrey, S, S, mask, S, S, S, photo, S, S);

            byte centerD = crisp[(60 * S + 85) * 4];
            // No pixel across the near object or its crisp rim (cols 64..106) may spike above the interior depth!
            for (int x = 64; x <= 106; x++)
            {
                byte d = crisp[(60 * S + x) * 4];
                Assert.True(d <= centerD + 3, $"rim spike detected at col {x}: d={d} vs interior={centerD}");
            }
        }

        [Fact]
        public void SolidifySubjectMask_preserves_enclosed_background_hole_in_shallow_depth_scene()
        {
            const int S = 160;
            var mask = new byte[S * S];
            var grey = new byte[S * S];
            for (int y = 0; y < S; y++)
            {
                for (int x = 0; x < S; x++)
                {
                    int i = y * S + x;
                    bool inSubject = x >= 25 && x < 135 && y >= 20 && y < 140;
                    mask[i] = inSubject ? (byte)255 : (byte)0;
                    grey[i] = inSubject ? (byte)140 : (byte)115; // Shallow scene: subj ~0.55, bg ~0.45
                }
            }

            // Enclosed true-background hole (e.g. handle/arm gap) at bg depth (115).
            for (int y = 65; y < 95; y++)
                for (int x = 65; x < 95; x++)
                {
                    mask[y * S + x] = 0;
                    grey[y * S + x] = 115;
                }

            var stats = SwayPortrait.ComputeSubjectStats(grey, S, S, mask, S);
            byte[] solid = SwayPortrait.SolidifySubjectMask(mask, S, grey, S, S, stats);

            Assert.Equal(0, solid[80 * S + 80]);
        }
    }
}
