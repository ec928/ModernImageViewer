using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Windows.Foundation;
using Windows.Media.Editing;
using Windows.Media.MediaProperties;
using Windows.Media.Transcoding;
using Windows.Storage;

namespace ModernImageViewer.VideoDirector.Models
{
    // Renders the composite to a real .mp4 via Windows.Media.Editing.MediaComposition.
    //
    // Prototype scope (spine only): every Track 1 clip is laid end-to-end, trimmed to its
    // Clip Start / Clip End; image clips are held for their Duration. NOT yet baked into the
    // export (they all work in the live preview): per-clip Speed, Ken Burns motion, Transitions,
    // and overlay PiP layers. MediaComposition supports overlay layers and image-hold, so those
    // are the next layers to add; Speed needs a retiming effect and is the hardest.
    public class VideoExporter
    {
        private static readonly string[] ImageExtensions =
            { ".jpg", ".jpeg", ".png", ".bmp", ".gif", ".webp", ".tif", ".tiff" };

        public enum ExportOutcome { Success, NothingToRender, Failed }

        public class ExportResult
        {
            public ExportOutcome Outcome { get; init; }
            public string Message { get; init; } = string.Empty;
        }

        // Build a composition from the spine (Track 1) clips, applying trim and image durations.
        public async Task<MediaComposition> BuildSpineCompositionAsync(IEnumerable<CinematicOperation> spine)
        {
            var composition = new MediaComposition();

            foreach (var op in spine)
            {
                if (op == null || string.IsNullOrWhiteSpace(op.FilePath)) continue;

                StorageFile file;
                try { file = await StorageFile.GetFileFromPathAsync(op.FilePath); }
                catch { continue; } // skip missing source files rather than fail the whole render

                var ext = System.IO.Path.GetExtension(op.FilePath);
                bool isImage = Array.Exists(ImageExtensions, e => string.Equals(e, ext, StringComparison.OrdinalIgnoreCase));

                MediaClip clip;
                if (isImage)
                {
                    var hold = op.OpDuration > TimeSpan.Zero ? op.OpDuration : TimeSpan.FromSeconds(3);
                    clip = await MediaClip.CreateFromImageFileAsync(file, hold);
                }
                else
                {
                    clip = await MediaClip.CreateFromFileAsync(file);

                    // Trim to the clip's source window (Clip Start / Clip End). TrimTimeFromEnd is
                    // measured back from the source's real end, so derive it from OriginalDuration.
                    var start = op.VideoStartTime;
                    if (start > TimeSpan.Zero && start < clip.OriginalDuration)
                        clip.TrimTimeFromStart = start;

                    var end = op.VideoEndTime > TimeSpan.Zero ? op.VideoEndTime : clip.OriginalDuration;
                    var fromEnd = clip.OriginalDuration - end;
                    if (fromEnd > TimeSpan.Zero && fromEnd < clip.OriginalDuration)
                        clip.TrimTimeFromEnd = fromEnd;
                }

                composition.Clips.Add(clip);
            }

            return composition;
        }

        // Render the spine composition to `output`. Reports 0..100 progress. Never throws for the
        // expected cases (missing files, nothing to render) — returns a described ExportResult.
        public async Task<ExportResult> ExportSpineAsync(
            IEnumerable<CinematicOperation> spine, StorageFile output, IProgress<double> progress)
        {
            MediaComposition composition;
            try
            {
                composition = await BuildSpineCompositionAsync(spine);
            }
            catch (Exception ex)
            {
                return new ExportResult { Outcome = ExportOutcome.Failed, Message = ex.Message };
            }

            if (composition.Clips.Count == 0)
                return new ExportResult { Outcome = ExportOutcome.NothingToRender, Message = "No renderable Track 1 clips." };

            var profile = MediaEncodingProfile.CreateMp4(VideoEncodingQuality.HD1080p);

            try
            {
                var render = composition.RenderToFileAsync(output, MediaTrimmingPreference.Precise, profile);
                if (progress != null)
                    render.Progress = (info, pct) => progress.Report(pct);

                var reason = await render;
                return reason == TranscodeFailureReason.None
                    ? new ExportResult { Outcome = ExportOutcome.Success, Message = output.Path }
                    : new ExportResult { Outcome = ExportOutcome.Failed, Message = reason.ToString() };
            }
            catch (Exception ex)
            {
                return new ExportResult { Outcome = ExportOutcome.Failed, Message = ex.Message };
            }
        }
    }
}
