using System;
using System.Collections.Generic;

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
        string? OnlyDesign = null);

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
            var move = CameraMove.Plan(subjects, amount, options.MotionZoomChance, options.MotionFaceChance,
                request.HoldSeconds + OverrunSeconds, request.LastKind, random, options.ForcedCameraKind, options.CameraStyle());
            return new SceneResult(composition, move, move == null ? 0 : amount);
        }
    }
}
