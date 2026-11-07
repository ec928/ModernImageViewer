using System;
using System.Collections.Generic;
using System.Linq;

namespace ModernImageViewer
{
    /// <summary>
    /// A layout already made, kept for another call that plans only the move: the composition when the caller has it,
    /// else where its faces sit, as shares of the canvas, read from the layout as it is shown.
    /// </summary>
    public sealed record KeptLayout(Composition? Composition, IReadOnlyList<(double X, double Y)> Subjects)
    {
        public static KeptLayout Of(Composition composition) => new(composition, composition.CameraSubjects());
    }

    /// <summary>
    /// Everything one call needs. The values are resolved before the call (scene, else show, else Layout Generator
    /// Settings); the call never knows which level each came from. Memory comes in here and is never kept.
    /// </summary>
    public sealed record SceneRequest(
        IReadOnlyList<PhotoFacts> Photos,
        double Width,
        double Height,
        SurpriseOptions Options,
        double HoldSeconds,
        int Seed,
        string? AvoidRecipe = null,
        CameraKind? LastKind = null,
        KeptLayout? Keep = null,
        double RasterScale = 1,
        string? OnlyDesign = null,
        UnitRect? Face = null,
        CameraView? StartFrom = null);

    /// <summary>
    /// One layout and one move. The composition is null only when the call kept a layout it was given as faces alone.
    /// The move is null when the scene is still: Motion at 0, a Still scenes roll, or no hold to plan for.
    /// Amount is the strength the move was planned with, 0 to 1, so floating cells can move by the same.
    /// </summary>
    public sealed record SceneResult(Composition? Composition, CameraMove? Move, double Amount);

    /// <summary>
    /// The one entry point for the multi-image engine: every layout and every move comes from here, for the canvas,
    /// Auto-Mix, the storyboard and the director alike. It holds nothing between calls.
    /// </summary>
    public static class SceneCall
    {
        /// <summary>The move runs this far past the change, so the layout is still moving while the next fades in.</summary>
        public const double OverrunSeconds = 2.5;

        /// <summary>Null when no layout can be made from the photos.</summary>
        public static SceneResult? Make(SceneRequest request)
        {
            var random = new Random(request.Seed);
            Composition? composition;
            IReadOnlyList<(double X, double Y)> subjects;
            if (request.Keep != null)
            {
                composition = request.Keep.Composition;
                subjects = request.Keep.Subjects;
            }
            else
            {
                composition = LayoutComposer.Compose(request.Photos, request.Width, request.Height, random.Next(),
                    request.AvoidRecipe, request.RasterScale, request.OnlyDesign, request.Options);
                if (composition == null) return null;
                subjects = composition.CameraSubjects();
            }
            if (request.HoldSeconds <= 0) return new SceneResult(composition, null, 0);
            var options = request.Options;
            if (options.StillChance > 0 && random.NextDouble() < options.StillChance) return new SceneResult(composition, null, 0);
            double amount = options.PickMotion(random);
            double seconds = request.HoldSeconds + OverrunSeconds;
            var one = composition?.Cells.Where(c => c.Photo != null && !string.IsNullOrEmpty(c.Photo.Path)).Take(2).ToList();
            if (request.Face is UnitRect face && one is { Count: 1 })
                return new SceneResult(composition, FaceMove(one[0], face, composition!, request.StartFrom, options, seconds, random), amount);
            var move = one is { Count: 1 }
                ? OneImageMove(one[0], composition!, subjects, options, amount, seconds, request.LastKind, random)
                : CameraMove.Plan(subjects, amount, options.MotionZoomChance, options.MotionFaceChance,
                    seconds, request.LastKind, random, options.ForcedCameraKind, options.CameraStyle());
            return new SceneResult(composition, move, move == null ? 0 : amount);
        }

        /// <summary>
        /// A view of one face of a one-image layout, the next scene of a sequence beat: either a snap from where the last
        /// scene ended in to the face, then a slow push, or the face already framed and pushing in slowly. The zoom is from
        /// the Zoom range, never past where the face would take 85% of the view or the photo show past its detail.
        /// </summary>
        private static CameraMove FaceMove(ComposedCell cell, UnitRect face, Composition composition, CameraView? startFrom,
            SurpriseOptions options, double seconds, Random random)
        {
            var (point, mostZoom) = LayoutComposer.OnFace(cell, face, composition.Width, composition.Height);
            var start = (startFrom ?? CameraView.Whole) with { Angle = 0 };
            double wanted = options.MostZoom > 1 ? options.PickZoom(random) : 1.6;
            double close = Math.Min(mostZoom, Math.Max(start.Zoom + 0.15, wanted));
            var closeView = CameraView.OnEyes(close, point.X, point.Y);
            var nearer = CameraView.OnEyes(Math.Min(mostZoom, close * 1.06), point.X, point.Y);
            var calm = CameraCurves.Pick(options.CurveOdds, false, random);
            if (random.Next(2) == 0)
                return new CameraMove(CameraKind.ZoomIn, new[]
                {
                    new CameraShot(start.Covered(), closeView, 0.3, CameraCurve.Snap, 0),
                    new CameraShot(closeView, nearer, 0.7, calm, 0)
                }, seconds);
            return new CameraMove(CameraKind.ZoomIn, new[] { new CameraShot(closeView, nearer, 1, calm, 0) }, seconds);
        }

        /// <summary>
        /// One image is one move (design 3.5). The layout did not zoom it: it opens on its fit or fill, and Zoom is where the
        /// move arrives. Camera mode Zoom in or Zoom out travels between that frame and the Zoom amount every scene. On Auto,
        /// Zoom frequency is rolled first: a hit zooms in or out to the Zoom amount, toward the faces or the focus point; a
        /// miss is the ordinary camera move, a Camera zoom or a drift. No view goes past where a face would be cut or the
        /// photo shown past its detail.
        /// </summary>
        private static CameraMove? OneImageMove(ComposedCell cell, Composition composition, IReadOnlyList<(double X, double Y)> subjects,
            SurpriseOptions options, double amount, double seconds, CameraKind? last, Random random)
        {
            double mostZoom = LayoutComposer.MostCameraZoom(cell, composition.Width, composition.Height);
            var aim = new List<(double X, double Y)> { LayoutComposer.AimFor(cell, composition.Width, composition.Height) };
            var style = options.CameraStyle();
            // A two-shot snaps to a face; an image without one has none to snap to.
            var toZoom = style with
            {
                ZoomLeast = options.LeastZoom,
                ZoomMost = options.MostZoom,
                TwoShotChance = cell.Photo.Faces.Count > 0 ? style.TwoShotChance : 0
            };
            var forced = options.ForcedCameraKind;
            // The aim is the faces' eye line, or with none the focus point, which is centred.
            bool eyes = cell.Photo.Faces.Count > 0;
            if (forced is CameraKind.ZoomIn or CameraKind.ZoomOut)
                return CameraMove.Plan(aim, amount, 1, 1, seconds, last, random, forced, options.MostZoom > 1 ? toZoom : style, mostZoom, eyes);
            if (forced == CameraKind.Drift)
                return CameraMove.Plan(subjects, amount, 0, options.MotionFaceChance, seconds, last, random, forced, style, mostZoom);
            if (options.Zooms(random))
            {
                // Never the same kind twice running, as the planner keeps it.
                var kind = last == CameraKind.ZoomIn ? CameraKind.ZoomOut
                    : last == CameraKind.ZoomOut ? CameraKind.ZoomIn
                    : random.Next(2) == 0 ? CameraKind.ZoomIn : CameraKind.ZoomOut;
                return CameraMove.Plan(aim, amount, 1, 1, seconds, last, random, kind, toZoom, mostZoom, eyes);
            }
            return CameraMove.Plan(subjects, amount, options.MotionZoomChance, options.MotionFaceChance, seconds, last, random, null, style, mostZoom);
        }
    }
}
