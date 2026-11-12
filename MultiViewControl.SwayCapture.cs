using Microsoft.Graphics.Canvas;
using System;
using System.Globalization;
using System.IO;
using System.Threading.Tasks;

namespace ModernImageViewer
{
    /// <summary>
    /// Debug capture of Sway, for comparing what the app draws with an off-screen render (2026-11-11). Off unless the folder
    /// %LocalAppData%\ModernImageViewer\sway-capture exists. Then each Sway scene writes a subfolder with the exact inputs
    /// (photo as drawn, depth map, subject mask, crisp depth, still point) and up to <see cref="CaptureFramesMost"/> frames
    /// of the warped photo at its own size, before the overscan crop, with each frame's shift in frames.tsv.
    /// Delete the folder to turn it off.
    /// </summary>
    public sealed partial class MultiViewControl
    {
        private const int CaptureFramesMost = 16;
        private const double CaptureEverySeconds = 0.3;

        private static string SwayCaptureRoot => Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ModernImageViewer", "sway-capture");

        /// <summary>Starts a capture for one Sway scene; null when capture is off or the folder cannot be written.</summary>
        private string? BeginSwayCapture(string path, CanvasBitmap photo, byte[] grey, int gw, int gh,
            byte[]? mask, int maskSize, byte[]? crisp, int tw, int th, float middle, float splatStep, int maxOffset)
        {
            try
            {
                if (!Directory.Exists(SwayCaptureRoot)) return null;
                string dir = Path.Combine(SwayCaptureRoot,
                    Path.GetFileNameWithoutExtension(path) + "_" + DateTime.Now.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture));
                Directory.CreateDirectory(dir);
                var size = photo.SizeInPixels;
                var ic = CultureInfo.InvariantCulture;
                string info = string.Join(Environment.NewLine,
                    $"photo={path}", $"photoW={size.Width}", $"photoH={size.Height}", $"gw={gw}", $"gh={gh}",
                    $"maskSize={maskSize}", $"tw={tw}", $"th={th}", $"middle={middle.ToString("R", ic)}",
                    $"splatStep={splatStep.ToString("R", ic)}", $"maxOffset={maxOffset}",
                    $"rotation={SurpriseOptions.ClampedSwayRotation(SurpriseChoices.SwayRotation).ToString("R", ic)}",
                    $"swing={SurpriseOptions.ClampedSwaySwing(SurpriseChoices.SwaySwing).ToString("R", ic)}") + Environment.NewLine;
                // The header first, here: written on the background task it landed after frame 0 and replaced its line.
                File.WriteAllText(Path.Combine(dir, "frames.tsv"), "frame\tseconds\tphase\tshift\tpivot\tstepPx\treach\tramp\tsplat" + Environment.NewLine);
                _ = Task.Run(() =>
                {
                    File.WriteAllText(Path.Combine(dir, "info.txt"), info);
                    File.WriteAllBytes(Path.Combine(dir, "grey.raw"), grey);
                    if (mask != null) File.WriteAllBytes(Path.Combine(dir, "mask.raw"), mask);
                    if (crisp != null) File.WriteAllBytes(Path.Combine(dir, "crisp.bgra"), crisp);
                });
                _ = photo.SaveAsync(Path.Combine(dir, "photo.png")).AsTask();
                PlayTiming.Write($"3D: sway capture to {dir}");
                return dir;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or System.Runtime.InteropServices.COMException)
            {
                PlayTiming.Write($"3D: sway capture failed: {ex.Message}");
                return null;
            }
        }

        /// <summary>Saves this frame's warped photo, at most every <see cref="CaptureEverySeconds"/>.</summary>
        private void CaptureSwayFrame(DepthScene state, ICanvasImage drawn, float shift, float pivot, float reach, double ramp, bool splat)
        {
            if (state.CaptureDir is not string dir || state.CaptureFrames >= CaptureFramesMost) return;
            double t = state.Shown.Elapsed.TotalSeconds;
            if (t < state.CaptureNext) return;
            state.CaptureNext = t + CaptureEverySeconds;
            int n = state.CaptureFrames++;
            try
            {
                var size = state.Photo.SizeInPixels;
                var target = new CanvasRenderTarget(state.Photo.Device, size.Width, size.Height, 96);
                using (var ds = target.CreateDrawingSession())
                {
                    ds.Clear(Microsoft.UI.Colors.Black);
                    ds.DrawImage(drawn);
                }
                var ic = CultureInfo.InvariantCulture;
                string line = string.Join("\t", n.ToString(ic), t.ToString("0.000", ic), state.SwayPhase.ToString("0.0000", ic),
                    shift.ToString("R", ic), pivot.ToString("R", ic), state.SplatStep.ToString("R", ic), reach.ToString("R", ic),
                    ramp.ToString("0.000", ic), splat ? "1" : "0") + Environment.NewLine;
                File.AppendAllText(Path.Combine(dir, "frames.tsv"), line);
                target.SaveAsync(Path.Combine(dir, $"frame{n:00}.png")).AsTask().ContinueWith(_ => target.Dispose(), TaskScheduler.Default);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or System.Runtime.InteropServices.COMException)
            {
                PlayTiming.Write($"3D: sway capture frame failed: {ex.Message}");
                state.CaptureDir = null;
            }
        }
    }
}
