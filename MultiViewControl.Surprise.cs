using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

namespace ModernImageViewer
{
    /// <summary>
    /// Surprise me: arranges the session's pictures with LayoutComposer. The result is an ordinary layout, so it can be
    /// edited, saved and undone like any other. The pictures it draws from stay until the session is replaced or reset.
    /// </summary>
    public sealed partial class MultiViewControl
    {
        private const int FactReaders = 4;

        private readonly ConcurrentDictionary<string, PhotoFacts> _photoFacts = new(StringComparer.OrdinalIgnoreCase);
        // Only set while the session started with more pictures than a layout can show.
        private PictureDeck? _surpriseDeck;
        private bool _surpriseRun;
        private string? _surpriseRecipe;
        private bool _surpriseBusy;
        private SurpriseOptions? _surpriseOptions;

        private static string SurpriseOptionsPath => System.IO.Path.Combine(AppPaths.LocalDirectory, "surprise-options.json");

        private SurpriseOptions SurpriseChoices
        {
            get
            {
                if (_surpriseOptions != null) return _surpriseOptions;
                string? json = null;
                try
                {
                    if (System.IO.File.Exists(SurpriseOptionsPath)) json = System.IO.File.ReadAllText(SurpriseOptionsPath);
                }
                catch (Exception ex) when (ex is System.IO.IOException or UnauthorizedAccessException)
                {
                    // The defaults will do.
                }
                return _surpriseOptions = SurpriseOptions.FromJson(json);
            }
        }

        private void ChangeSurpriseChoices(Action<SurpriseOptions> change)
        {
            change(SurpriseChoices);
            try
            {
                System.IO.Directory.CreateDirectory(AppPaths.LocalDirectory);
                AtomicFile.WriteAllText(SurpriseOptionsPath, SurpriseChoices.ToJson());
            }
            catch (Exception ex) when (ex is System.IO.IOException or UnauthorizedAccessException)
            {
                // It still applies until the app closes.
            }
        }

        /// <summary>One flat list, so nothing here is more than one level below the wheel.</summary>
        private MenuFlyoutSubItem MakeSurpriseMenu()
        {
            var menu = new MenuFlyoutSubItem { Text = "Surprise me", Icon = new FontIcon { Glyph = "\uE8B1" } };
            void Heading(string text) => menu.Items.Add(new MenuFlyoutItem { Text = text, IsEnabled = false });

            Heading("Designs");
            foreach (string design in LayoutComposer.Recipes)
                menu.Items.Add(MakeChoiceItem(design, SurpriseChoices.Designs.Contains(design), () => ChangeSurpriseChoices(o => o.ToggleDesign(design))));

            menu.Items.Add(new MenuFlyoutSeparator());
            Heading("Zoom on faces");
            foreach (var level in SurpriseOptions.ZoomLevels)
                menu.Items.Add(MakeChoiceItem(level.Name, SurpriseChoices.Zoom == level.Name, () => ChangeSurpriseChoices(o => o.Zoom = level.Name)));

            menu.Items.Add(new MenuFlyoutSeparator());
            Heading("Pictures from a large set");
            foreach (var count in SurpriseOptions.PictureCounts)
            {
                var item = MakeChoiceItem(count.Name, SurpriseChoices.Pictures == count.Name, () => ChangeSurpriseChoices(o => o.Pictures = count.Name));
                ToolTipService.SetToolTip(item, "How many each click uses when there are more than 16");
                menu.Items.Add(item);
            }

            menu.Items.Add(new MenuFlyoutSeparator());
            var keep = MakeChoiceItem("Keep my canvas", SurpriseChoices.KeepCanvas, () => ChangeSurpriseChoices(o => o.KeepCanvas = !o.KeepCanvas));
            ToolTipService.SetToolTip(keep, "Keep this canvas color, surface and frame instead of the design's");
            menu.Items.Add(keep);
            return menu;
        }

        private async void SurpriseButton_Click(object sender, RoutedEventArgs e)
        {
            if (_surpriseBusy) return;
            bool sameRun = _surpriseRun;
            var pictures = SessionPictures();
            if (pictures.Count > MosaicLayout.MaxShown && _surpriseDeck == null)
                _surpriseDeck = new PictureDeck(pictures, new Random());
            _surpriseDeck?.Include(pictures);
            if ((_surpriseDeck?.Count ?? pictures.Count) < 2)
            {
                AddImagesRequested?.Invoke(this, EventArgs.Empty);
                return;
            }

            _surpriseBusy = true;
            SurpriseButton.IsEnabled = false;
            try
            {
                // More than a layout can show: a different set each time, so all of them get a turn.
                var options = SurpriseChoices;
                var range = options.PictureRange;
                var chosen = _surpriseDeck?.Deal(Random.Shared.Next(range.Fewest, range.Most + 1)) ?? pictures;
                var ready = DecodedBitmaps();
                // A picture already showing as failed would only put its message into the design.
                var failed = _cellRoots.Select(root => root?.Tag as CellParts).Where(parts => parts?.Bound.Failed == true)
                    .Select(parts => parts!.Bound.Item.Path).ToHashSet(StringComparer.OrdinalIgnoreCase);
                var facts = await ReadFactsAsync(chosen.Where(path => !failed.Contains(path)).ToList(), path => ready.TryGetValue(path, out var bitmap) ? (bitmap.PixelWidth, bitmap.PixelHeight) : null);

                double scale = XamlRoot?.RasterizationScale ?? 1;
                double width = RootGrid.ActualWidth, height = RootGrid.ActualHeight;
                string? avoid = sameRun ? _surpriseRecipe : null;
                var composition = await Task.Run(() => LayoutComposer.Compose(facts, width, height, Random.Shared.Next(), avoid, scale, null, options));
                // The session was changed for other pictures while these were being read, and that wins.
                if (composition == null || !SessionPictures().SequenceEqual(pictures)) return;

                if (options.KeepCanvas) composition.KeepCanvas(_canvasColor, _canvasSurface, _canvasFrame);
                if (!sameRun) AutoSaveSession();
                CaptureUndoPoint();
                _cellBorderFlyout?.Hide();
                _restoringUndo = true;
                _reuseBitmaps = ready;
                try
                {
                    ApplyLayoutFile(composition.ToLayoutFile(), restoreAspect: false);
                }
                finally
                {
                    _reuseBitmaps = null;
                    _restoringUndo = false;
                }
                CaptureUndoPoint();
                _surpriseRun = true;
                _surpriseRecipe = composition.Recipe;
            }
            finally
            {
                _surpriseBusy = false;
                SurpriseButton.IsEnabled = true;
            }
        }

        private List<string> SessionPictures() =>
            _items.Where(i => !IsEmptyCell(i)).Select(i => i.Path).Distinct(StringComparer.OrdinalIgnoreCase).ToList();

        /// <summary>A different session has different pictures to draw from.</summary>
        private void ForgetSurprise()
        {
            _surpriseDeck = null;
            _surpriseRun = false;
            _surpriseRecipe = null;
        }

        /// <summary>A picture the reader cannot open is still arranged, by the size it shows at or as a plain photo shape, rather than dropped from the session.</summary>
        private async Task<List<PhotoFacts>> ReadFactsAsync(List<string> paths, Func<string, (int Width, int Height)?> shown)
        {
            var waiting = new ConcurrentQueue<string>(paths.Where(p => !_photoFacts.ContainsKey(p)));
            if (!waiting.IsEmpty)
            {
                await Task.WhenAll(Enumerable.Range(0, FactReaders).Select(_ => Task.Run(async () =>
                {
                    var detector = await PhotoFactsReader.CreateDetectorAsync();
                    while (waiting.TryDequeue(out var path))
                    {
                        var read = await PhotoFactsReader.ReadAsync(path, detector);
                        if (read != null) _photoFacts[path] = read;
                    }
                })));
            }

            var facts = new List<PhotoFacts>();
            foreach (string path in paths)
            {
                if (_photoFacts.TryGetValue(path, out var known))
                {
                    facts.Add(known);
                }
                else if (System.IO.File.Exists(path))
                {
                    var size = shown(path) ?? (4, 3);
                    facts.Add(new PhotoFacts(path, size.Width, size.Height, Array.Empty<UnitRect>(), 0.5, 0.42, "#FF808080"));
                }
            }
            return facts;
        }
    }
}
