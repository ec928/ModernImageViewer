using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Animation;
using System;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading.Tasks;

namespace ModernImageViewer
{
    /// <summary>
    /// Prototype (branch prototype-titles-3d): words over a playing show. A title over the first scene when the show plays
    /// from its start, and the date in the lower left when a scene's photos come from a new day. They sit over the picture,
    /// above the cover a change fades out, and do not move with the camera.
    /// </summary>
    public sealed partial class MultiViewControl
    {
        private const int WordsLayer = 5800;
        private bool _titleDue;
        private DateTime? _captionedDay;
        private string? _captionedFolder;
        private FrameworkElement? _showWords;

        /// <summary>Called as a show starts to play: a title is due if it starts from its first scene.</summary>
        private void PrepareShowWords(int startIndex)
        {
            _titleDue = startIndex == 0;
            _captionedDay = null;
            _captionedFolder = null;
        }

        /// <summary>Called as each scene of a playing show comes on screen.</summary>
        private async Task ShowSceneWordsAsync(ShowScene scene)
        {
            try
            {
                var options = SurpriseChoices;
                string? first = scene.GetPhotoPaths().FirstOrDefault(p => !string.IsNullOrEmpty(p));
                if (string.IsNullOrEmpty(first) || _autoPlaySeconds == 0) return;
                DateTime taken = await PhotoDates.TakenAsync(first);
                string folder = Path.GetFileName(Path.GetDirectoryName(first) ?? "") ?? "";
                if (_autoPlaySeconds == 0) return;

                if (_titleDue)
                {
                    _titleDue = false;
                    _captionedDay = taken.Date;
                    _captionedFolder = folder;
                    if (!options.ShowTitleCard) return;
                    string title = _activeShow is { } show && !IsPlaceholderTitle(show.Title) ? show.Title : folder;
                    string when = taken == DateTime.MinValue ? "" : taken.ToString("MMMM yyyy", CultureInfo.CurrentCulture);
                    ShowWords(TitleCard(title, when), holdSeconds: 3.2);
                    return;
                }

                if (!options.ShowDateCaptions || taken == DateTime.MinValue) return;
                bool newDay = _captionedDay != taken.Date;
                bool newFolder = !string.Equals(_captionedFolder, folder, StringComparison.OrdinalIgnoreCase);
                if (!newDay) return;
                _captionedDay = taken.Date;
                _captionedFolder = folder;
                string date = taken.ToString("dddd d MMMM yyyy", CultureInfo.CurrentCulture);
                ShowWords(Caption(newFolder ? $"{date}  ·  {folder}" : date), holdSeconds: 4);
            }
            catch (Exception ex)
            {
                PlayTiming.Write($"ShowSceneWordsAsync error: {ex.Message}");
            }
        }

        /// <summary>Names the app gives a show it made, which say nothing to a viewer.</summary>
        private static bool IsPlaceholderTitle(string? title) =>
            string.IsNullOrWhiteSpace(title) || title is "Storyboard" or "Cinematic storyboard" or ShowProject.DefaultTitle
            or "Custom Presentation" or "Auto-Drafted Presentation";

        private static FrameworkElement TitleCard(string title, string subtitle)
        {
            var white = new SolidColorBrush(Microsoft.UI.Colors.White);
            var stack = new StackPanel { HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center, Spacing = 8 };
            stack.Children.Add(Shadowed(new TextBlock
            {
                Text = title,
                FontFamily = new FontFamily("Segoe UI Variable Display"),
                FontWeight = Microsoft.UI.Text.FontWeights.Light,
                FontSize = 72,
                Foreground = white,
                HorizontalAlignment = HorizontalAlignment.Center,
                TextAlignment = TextAlignment.Center,
                TextWrapping = TextWrapping.Wrap,
                MaxWidth = 1400
            }, blur: 24));
            if (!string.IsNullOrEmpty(subtitle))
                stack.Children.Add(Shadowed(new TextBlock
                {
                    Text = subtitle.ToUpper(CultureInfo.CurrentCulture),
                    FontFamily = new FontFamily("Segoe UI Variable Text"),
                    FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
                    FontSize = 16,
                    CharacterSpacing = 400,
                    Foreground = white,
                    Opacity = 0.9,
                    HorizontalAlignment = HorizontalAlignment.Center
                }, blur: 10));
            // A soft dark band behind the words, so they read on any picture without a box round them.
            var band = new Grid
            {
                VerticalAlignment = VerticalAlignment.Center,
                Padding = new Thickness(40, 90, 40, 90),
                Background = new LinearGradientBrush
                {
                    StartPoint = new Windows.Foundation.Point(0, 0),
                    EndPoint = new Windows.Foundation.Point(0, 1),
                    GradientStops =
                    {
                        new GradientStop { Color = Windows.UI.Color.FromArgb(0x00, 0, 0, 0), Offset = 0 },
                        new GradientStop { Color = Windows.UI.Color.FromArgb(0x90, 0, 0, 0), Offset = 0.35 },
                        new GradientStop { Color = Windows.UI.Color.FromArgb(0x90, 0, 0, 0), Offset = 0.65 },
                        new GradientStop { Color = Windows.UI.Color.FromArgb(0x00, 0, 0, 0), Offset = 1 }
                    }
                }
            };
            band.Children.Add(stack);
            return band;
        }

        /// <summary>The words with a soft dark shadow cut from their own letters, so they hold on a bright picture.</summary>
        private static FrameworkElement Shadowed(TextBlock words, float blur)
        {
            var host = new Canvas { IsHitTestVisible = false };
            var holder = new Grid { HorizontalAlignment = words.HorizontalAlignment };
            holder.Children.Add(host);
            holder.Children.Add(words);
            void Fit()
            {
                try
                {
                    if (words.ActualWidth <= 0 || words.ActualHeight <= 0) return;
                    var compositor = Microsoft.UI.Xaml.Hosting.ElementCompositionPreview.GetElementVisual(host)?.Compositor;
                    if (compositor == null) return;
                    var shadow = compositor.CreateDropShadow();
                    var mask = words.GetAlphaMask();
                    if (mask == null) return;
                    shadow.Mask = mask;
                    shadow.BlurRadius = blur;
                    shadow.Color = Microsoft.UI.Colors.Black;
                    shadow.Opacity = 0.85f;
                    shadow.Offset = new System.Numerics.Vector3(0, 2, 0);
                    var visual = compositor.CreateSpriteVisual();
                    visual.Size = new System.Numerics.Vector2((float)words.ActualWidth, (float)words.ActualHeight);
                    visual.Offset = new System.Numerics.Vector3((float)words.ActualOffset.X, (float)words.ActualOffset.Y, 0);
                    visual.Shadow = shadow;
                    Microsoft.UI.Xaml.Hosting.ElementCompositionPreview.SetElementChildVisual(host, visual);
                }
                catch
                {
                    // Composition shadow failure ignored
                }
            }
            words.SizeChanged += (_, _) => Fit();
            return holder;
        }

        private static FrameworkElement Caption(string text)
        {
            var words = new TextBlock
            {
                Text = text,
                FontFamily = new FontFamily("Segoe UI Variable Display"),
                FontWeight = Microsoft.UI.Text.FontWeights.Normal,
                FontSize = 24,
                Foreground = new SolidColorBrush(Microsoft.UI.Colors.White)
            };
            return new Border
            {
                Child = words,
                HorizontalAlignment = HorizontalAlignment.Left,
                VerticalAlignment = VerticalAlignment.Bottom,
                Margin = new Thickness(36, 0, 0, 44),
                Padding = new Thickness(14, 8, 16, 8),
                CornerRadius = new CornerRadius(6),
                Background = new SolidColorBrush(Windows.UI.Color.FromArgb(0x66, 0, 0, 0))
            };
        }

        /// <summary>Fades the words in, holds them, fades them out. A new one replaces any still on screen.</summary>
        private void ShowWords(FrameworkElement words, double holdSeconds)
        {
            try
            {
                ClearShowWords();
                words.IsHitTestVisible = false;
                words.Opacity = 0;
                _showWords = words;
                AddOverCanvas(words, WordsLayer);
                var life = new DoubleAnimationUsingKeyFrames();
                life.KeyFrames.Add(new DiscreteDoubleKeyFrame { KeyTime = TimeSpan.Zero, Value = 0 });
                life.KeyFrames.Add(new LinearDoubleKeyFrame { KeyTime = TimeSpan.FromSeconds(0.7), Value = 1 });
                life.KeyFrames.Add(new LinearDoubleKeyFrame { KeyTime = TimeSpan.FromSeconds(0.7 + holdSeconds), Value = 1 });
                life.KeyFrames.Add(new LinearDoubleKeyFrame { KeyTime = TimeSpan.FromSeconds(1.6 + holdSeconds), Value = 0 });
                Storyboard.SetTarget(life, words);
                Storyboard.SetTargetProperty(life, "Opacity");
                var story = new Storyboard();
                story.Children.Add(life);
                story.Completed += (_, _) =>
                {
                    try { RootGrid.Children.Remove(words); } catch { }
                    if (ReferenceEquals(_showWords, words)) _showWords = null;
                };
                story.Begin();
            }
            catch (Exception ex)
            {
                PlayTiming.Write($"ShowWords error: {ex.Message}");
            }
        }

        /// <summary>Takes any title or caption off at once, as a pause or a stop does.</summary>
        private void ClearShowWords()
        {
            if (_showWords == null) return;
            RootGrid.Children.Remove(_showWords);
            _showWords = null;
        }
    }
}
