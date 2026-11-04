using System;
using System.Collections.Generic;
using System.Linq;
using Xunit;

namespace ModernImageViewer.Tests
{
    /// <summary>Motion as a range, movement curves as odds, a bent path, a turned view, and scenes of two shots.</summary>
    public class CameraStyleTests
    {
        [Fact]
        public void EveryCurve_StartsAtRest_EndsThere_AndNeverGoesBack()
        {
            foreach (var curve in CameraCurves.All)
            {
                Assert.Equal(0, CameraCurves.Ease(curve, 0), 3);
                Assert.Equal(1, CameraCurves.Ease(curve, 1), 3);
                double last = -1;
                for (int i = 0; i <= 100; i++)
                {
                    double v = CameraCurves.Ease(curve, i / 100.0);
                    Assert.True(v >= last - 1e-9, $"{curve} went back at {i}%");
                    last = v;
                }
            }
            // Steady is the same speed throughout; Settle is already a third of the way at a tenth of the time.
            Assert.Equal(0.5, CameraCurves.Ease(CameraCurve.Steady, 0.5), 6);
            Assert.True(CameraCurves.Ease(CameraCurve.Settle, 0.1) > 0.25);
            Assert.True(CameraCurves.Ease(CameraCurve.Snap, 0.15) < 0.05 && CameraCurves.Ease(CameraCurve.Snap, 0.45) > 0.9);
            Assert.Equal(CameraCurve.Smooth, CameraCurves.Parse(null));
            Assert.Equal(CameraCurve.Gentle, CameraCurves.Parse("Gentle"));
        }

        [Fact]
        public void Curves_FollowTheirOdds_AndSnapIsForZoomsOnly()
        {
            var random = new Random(1);
            var onlySnap = new double[] { 0, 0, 0, 0, 0, 1 };
            Assert.All(Enumerable.Range(0, 50), _ => Assert.Equal(CameraCurve.Snap, CameraCurves.Pick(onlySnap, zoom: true, random)));
            // On a drift nothing is left, so it is Smooth.
            Assert.All(Enumerable.Range(0, 50), _ => Assert.Equal(CameraCurve.Smooth, CameraCurves.Pick(onlySnap, zoom: false, random)));

            var odds = new SurpriseOptions().CurveOdds;
            var counts = Enumerable.Range(0, 20000).Select(_ => CameraCurves.Pick(odds, zoom: true, random)).GroupBy(c => c).ToDictionary(g => g.Key, g => g.Count() / 20000.0);
            var shares = CameraCurves.Shares(odds).Zooms;
            foreach (var c in CameraCurves.All)
                Assert.InRange(counts.GetValueOrDefault(c), shares[c] - 0.015, shares[c] + 0.015);
            Assert.True(shares[CameraCurve.Gentle] > 0.5, "Gentle is the most common by default");
            Assert.Equal(0, shares[CameraCurve.Snap]);
        }

        [Fact]
        public void APlainStyle_IsTheMoveEverySceneHadBefore()
        {
            var subjects = new List<(double, double)> { (0.4, 0.4) };
            for (int seed = 0; seed < 40; seed++)
            {
                var move = CameraMove.Plan(subjects, 0.5, 0.5, 0.7, 12, null, new Random(seed))!;
                Assert.Single(move.Shots);
                Assert.Equal(CameraCurve.Smooth, move.Shots[0].Curve);
                Assert.Equal(0, move.Shots[0].Bow);
                Assert.Equal(0, move.Start.Angle);
                Assert.Equal(0, move.End.Angle);
            }
        }

        [Fact]
        public void ABentPath_LeavesTheStraightLineInTheMiddle_AndKeepsItsEnds()
        {
            var from = new CameraView(1.6, 0.35, 0.5);
            var to = new CameraView(1.6, 0.65, 0.5);
            var straight = new CameraMove(CameraKind.Drift, from, to, 10, CameraCurve.Steady, 0);
            var bent = new CameraMove(CameraKind.Drift, from, to, 10, CameraCurve.Steady, CameraMove.MostBow);
            Assert.Equal(0.5, straight.At(5).Y, 6);
            Assert.True(Math.Abs(bent.At(5).Y - 0.5) > 0.03, $"middle at {bent.At(5).Y}");
            Assert.Equal(from.X, bent.At(0).X, 6);
            Assert.Equal(to.X, bent.At(10).X, 6);
            Assert.Equal(0.5, bent.At(10).Y, 6);
        }

        [Fact]
        public void ATurnedView_StillHasPictureInEveryCorner()
        {
            // The view's corners, turned about its centre, must stay inside the canvas once zoomed by TurnCover.
            foreach (var (w, h) in new[] { (1920.0, 1080.0), (1080.0, 1920.0), (1000.0, 1000.0) })
            foreach (double angle in new[] { 1.0, 3.0, 6.0, -4.0 })
            foreach (var view in new[] { new CameraView(1, 0.5, 0.5, angle), new CameraView(1.4, 0.3, 0.7, angle).Covered() })
            {
                double zoom = view.Zoom * CameraView.TurnCover(angle, w, h);
                double cx = view.X * w, cy = view.Y * h, halfW = w / (2 * zoom), halfH = h / (2 * zoom);
                double a = angle * Math.PI / 180;
                foreach (var (sx, sy) in new[] { (-1, -1), (1, -1), (1, 1), (-1, 1) })
                {
                    double x = cx + sx * halfW * Math.Cos(a) - sy * halfH * Math.Sin(a);
                    double y = cy + sx * halfW * Math.Sin(a) + sy * halfH * Math.Cos(a);
                    Assert.InRange(x, -0.5, w + 0.5);
                    Assert.InRange(y, -0.5, h + 0.5);
                }
            }
            Assert.Equal(1, CameraView.TurnCover(0, 1920, 1080));
        }

        [Fact]
        public void TurnsAndCurvesComeFromTheStyle()
        {
            var style = new CameraStyle(new double[] { 0, 0, 0, 1, 0, 0 }, 1, 1, 2, 4, 0);
            var random = new Random(3);
            for (int i = 0; i < 40; i++)
            {
                var move = CameraMove.Plan(Array.Empty<(double, double)>(), 0.5, 0.5, 0.7, 12, null, random, null, style)!;
                Assert.Equal(CameraCurve.Cinematic, move.Shots[0].Curve);
                double turned = Math.Abs(move.End.Angle - move.Start.Angle);
                Assert.InRange(turned, 2 - 1e-9, 4 + 1e-9);
                if (move.Kind == CameraKind.Drift) Assert.Equal(CameraMove.MostBow, Math.Abs(move.Shots[0].Bow), 6);
            }
        }

        [Fact]
        public void ASceneWithAFace_HeldLongEnough_CanBeTwoShots()
        {
            var face = new List<(double, double)> { (0.62, 0.38) };
            var always = new CameraStyle(new SurpriseOptions().CurveOdds, 0, 0, 0, 0, 1);
            int snaps = 0, cuts = 0;
            for (int seed = 0; seed < 60; seed++)
            {
                var move = CameraMove.Plan(face, 0.5, 0.5, 0.7, 12, null, new Random(seed), null, always)!;
                Assert.Equal(2, move.Shots.Count);
                // It ends close on the face.
                Assert.InRange(move.End.X, 0.55, 0.69);
                Assert.True(move.End.Zoom > move.Start.Zoom);
                if (move.Shots[1].Curve == CameraCurve.Snap) { snaps++; Assert.Equal(move.Shots[0].To, move.Shots[1].From); }
                else { cuts++; Assert.NotEqual(move.Shots[0].To, move.Shots[1].From); }
            }
            Assert.True(snaps > 10 && cuts > 10, $"snaps {snaps}, cuts {cuts}");

            // Too short, or no face: one shot.
            Assert.Single(CameraMove.Plan(face, 0.5, 0.5, 0.7, 6, null, new Random(1), null, always)!.Shots);
            Assert.Single(CameraMove.Plan(Array.Empty<(double, double)>(), 0.5, 0.5, 0.7, 12, null, new Random(1), null, always)!.Shots);
            // A chosen camera mode is one kind of move.
            Assert.Equal(0, new SurpriseOptions { CameraMode = SurpriseOptions.CameraZoomIn, TwoShotChance = 1 }.CameraStyle().TwoShotChance);
        }

        [Fact]
        public void ACut_JumpsAtItsShare_AndEachShotTakesItsShareOfTheTime()
        {
            var a = new CameraView(1.5, 0.4, 0.5);
            var b = new CameraView(1.5, 0.6, 0.5);
            var c = new CameraView(1.8, 0.7, 0.3);
            var d = new CameraView(1.9, 0.7, 0.3);
            var move = new CameraMove(CameraKind.ZoomIn, new[] { new CameraShot(a, b, 1, CameraCurve.Steady, 0), new CameraShot(c, d, 1, CameraCurve.Steady, 0) }, 10);
            Assert.Equal(0.5, move.At(2.5).X, 6);
            Assert.Equal(0.6, move.At(4.99).X, 2);
            Assert.Equal(0.7, move.At(5.01).X, 2);
            Assert.True(move.At(5.01).Zoom > 1.75);
            Assert.Equal(1.9, move.At(10).Zoom, 6);
        }

        [Fact]
        public void AScene_KeepsEveryShotCurveBendAndTurn_ThroughSavingAndCopying()
        {
            var move = new CameraMove(CameraKind.ZoomIn, new[]
            {
                new CameraShot(new CameraView(1.1, 0.4, 0.5, -1), new CameraView(1.1, 0.6, 0.5, 1), 0.55, CameraCurve.Gentle, 0.05),
                new CameraShot(new CameraView(1.1, 0.6, 0.5, 1), new CameraView(1.6, 0.65, 0.4, 1), 0.45, CameraCurve.Snap, 0),
                new CameraShot(new CameraView(1.9, 0.3, 0.3), new CameraView(2.0, 0.3, 0.3), 0.5, CameraCurve.Steady, 0)
            }, 10);
            var scene = new ShowScene { CameraOverride = CameraKeyframeOverride.FromMove(move, "TwoShot") };
            var show = new ShowProject();
            show.Scenes.Add(scene);
            var read = ShowProject.FromJson(show.ToJson())!.Scenes[0];

            foreach (var o in new[] { read.CameraOverride!, scene.Clone().CameraOverride! })
            {
                var shots = o.ToShots();
                Assert.Equal(3, shots.Count);
                Assert.Equal(CameraCurve.Gentle, shots[0].Curve);
                Assert.Equal(0.05, shots[0].Bow, 6);
                Assert.Equal(-1, shots[0].From.Angle, 6);
                Assert.Equal(CameraCurve.Snap, shots[1].Curve);
                Assert.Equal(shots[0].To, shots[1].From);
                Assert.Null(o.Shots![0].From);
                Assert.NotNull(o.Shots![1].From);
                Assert.Equal(1.9, shots[2].From.Zoom, 6);
            }

            // A copy is separate.
            var copy = scene.Clone();
            copy.CameraOverride!.Shots![0].To.Zoom = 3;
            Assert.Equal(1.6, scene.CameraOverride!.Shots![0].To.Zoom, 6);
        }

        [Fact]
        public void APathFromBefore_PlaysAsItDid()
        {
            var old = System.Text.Json.JsonSerializer.Deserialize<CameraKeyframeOverride>("{\"start\":{\"zoom\":1,\"x\":0.5,\"y\":0.5},\"end\":{\"zoom\":1.3,\"x\":0.6,\"y\":0.5},\"kind\":\"ZoomIn\"}")!;
            var move = old.ToCameraMove(10);
            Assert.Single(move.Shots);
            Assert.Equal(CameraCurve.Smooth, move.Shots[0].Curve);
            Assert.Equal(0, move.Shots[0].Bow);
            Assert.Equal(0, move.End.Angle);
        }

        [Fact]
        public void Motion_IsARange_AndASavedSingleValueBecomesThatValue()
        {
            var read = SurpriseOptions.FromJson("{\"MotionAmount\":0.5}");
            Assert.Equal(0.5, read.MotionLeast);
            Assert.Equal(0.5, read.MotionMost);
            Assert.DoesNotContain("MotionAmount", read.ToJson());

            var range = new SurpriseOptions { MotionLeast = 0.2, MotionMost = 0.7 };
            var random = new Random(4);
            var picks = Enumerable.Range(0, 500).Select(_ => range.PickMotion(random)).ToList();
            Assert.All(picks, p => Assert.InRange(p, 0.2, 0.7));
            Assert.True(picks.Max() - picks.Min() > 0.4);
            Assert.False(new SurpriseOptions { MotionLeast = 0, MotionMost = 0 }.HasMotion);

            var cine = SurpriseOptions.FromJson("{\"Cinematic\":{\"MotionAmount\":0.3}}");
            Assert.Equal(0.3, cine.Cinematic.MotionLeast);
            Assert.Equal(0.3, cine.WithOverrides(CinematicOverrides.Name).MotionMost);

            var bad = SurpriseOptions.FromJson("{\"TurnLeast\":9,\"TurnMost\":2,\"PathBendMost\":5,\"TwoShotChance\":3,\"CurveSnapOdds\":40}");
            Assert.Equal(SurpriseOptions.MostTurn, bad.TurnLeast);
            Assert.Equal(SurpriseOptions.MostTurn, bad.TurnMost);
            Assert.Equal(1, bad.PathBendMost);
            Assert.Equal(1, bad.TwoShotChance);
            Assert.Equal(SurpriseOptions.MostFavouredDesign, bad.CurveSnapOdds);
        }

        [Fact]
        public async System.Threading.Tasks.Task ADraftedScene_StoresItsTwoShots()
        {
            var facts = new[] { new PhotoFacts(@"C:\p\a.jpg", 1600, 1200, new[] { new UnitRect(0.45, 0.3, 0.12, 0.16) }, 0.5, 0.5, "#808080") };
            var options = new SurpriseOptions { FewestPictures = 1, MostPictures = 1, TwoShotChance = 1, AutoPlayRandom = false, AutoPlaySeconds = 15 };
            var seen = 0;
            for (int i = 0; i < 10; i++)
            {
                var scene = await ShowDirector.DraftSceneAsync(facts, options, 1920, 1080, previous: null);
                if (scene!.CameraOverride?.ShotCount == 2) { seen++; Assert.Equal("TwoShot", scene.CameraOverride.Kind); }
            }
            Assert.True(seen > 0, "no drafted scene had two shots");
        }
    }
}
