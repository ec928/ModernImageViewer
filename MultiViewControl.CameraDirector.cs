using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Shapes;
using System;
using System.Linq;
using System.Numerics;
using System.Threading.Tasks;
using Windows.Foundation;
using Windows.System;
using Windows.UI;

namespace ModernImageViewer
{
    public sealed partial class MultiViewControl
    {
        private bool _isEditingCamera;
        private ShowScene? _editingCameraScene;
        private Canvas? _cameraOverlayCanvas;
        private Border? _cameraDirectorBar;
        private Border? _startWireframe;
        private Border? _endWireframe;
        private Border? _startBadge;
        private Border? _endBadge;
        private Line? _trajectoryLine;
        private TextBlock? _startBadgeText;
        private TextBlock? _endBadgeText;
        private Button? _startToggleBtn;
        private Button? _endToggleBtn;
        private TextBlock? _directorSceneBadgeText;
        private Button? _directorPrevSceneBtn;
        private Button? _directorNextSceneBtn;
        private bool _wasTrayOpenWhenEnteringCamera;

        private bool _activeIsStartKeyframe = true;
        private bool _isDraggingWireframe;
        private Point _wireframeDragStart;
        private double _wireframeInitialX;
        private double _wireframeInitialY;

        private double _curStartZoom = 1.0;
        private double _curStartX = 0.5;
        private double _curStartY = 0.5;

        private double _curEndZoom = 1.25;
        private double _curEndX = 0.5;
        private double _curEndY = 0.5;

        private bool _isPreviewingCameraMove;

        public bool IsEditingCamera => _isEditingCamera;

        public void EnterCameraDirector(ShowScene scene)
        {
            if (scene == null) return;
            if (_isEditingCamera) ExitCameraDirector(save: true);

            SyncCanvasToSelectedScene(showToast: false);
            _canvasSceneId = null;

            _isEditingCamera = true;
            _editingCameraScene = scene;
            _selectedShowScene = scene;
            _shuffleChosen = false;
            _activeIsStartKeyframe = true;

            // Pause playback if active
            if (_autoPlaySeconds > 0)
            {
                _autoPlaySeconds = 0;
                StopPlaybackProgress();
            }

            // Close tray if open to give full canvas view, but remember to reopen on exit
            _wasTrayOpenWhenEnteringCamera = _trayCard != null;
            if (_trayCard != null)
            {
                RemoveTrayCard();
            }

            // Hide bottom HUD so it doesn't overlap canvas wireframes or controls
            UnifiedHud.Visibility = Visibility.Collapsed;

            // Project scene on canvas
            if (scene.Layout != null)
            {
                ApplyLayoutFile(WithRestInTray(scene.Layout, SessionPictures()), restoreAspect: false, preserveUndo: true);
                _canvasSceneId = scene.Id;
            }

            ResetCanvasCamera();

            // Load existing camera keyframes or default
            if (scene.CameraOverride != null)
            {
                _curStartZoom = Math.Max(1.0, scene.CameraOverride.Start.Zoom);
                _curStartX = Math.Clamp(scene.CameraOverride.Start.X, 0.0, 1.0);
                _curStartY = Math.Clamp(scene.CameraOverride.Start.Y, 0.0, 1.0);

                _curEndZoom = Math.Max(1.0, scene.CameraOverride.End.Zoom);
                _curEndX = Math.Clamp(scene.CameraOverride.End.X, 0.0, 1.0);
                _curEndY = Math.Clamp(scene.CameraOverride.End.Y, 0.0, 1.0);
            }
            else
            {
                _curStartZoom = 1.0;
                _curStartX = 0.5;
                _curStartY = 0.5;

                _curEndZoom = 1.25;
                _curEndX = 0.5;
                _curEndY = 0.5;
            }

            BuildCameraDirectorUI();
            LayoutWireframes();
        }

        private void SaveCurrentCameraKeyframes()
        {
            if (_editingCameraScene == null) return;

            double zDiff = _curEndZoom - _curStartZoom;
            double panDist = Math.Abs(_curEndX - _curStartX) + Math.Abs(_curEndY - _curStartY);
            string calculatedKind = Math.Abs(zDiff) < 0.02 && panDist < 0.02
                ? "Still"
                : zDiff > 0.02
                    ? "ZoomIn"
                    : zDiff < -0.02
                        ? "ZoomOut"
                        : "Pan";

            _editingCameraScene.CameraOverride = new CameraKeyframeOverride(
                new CameraViewDto(_curStartZoom, _curStartX, _curStartY),
                new CameraViewDto(_curEndZoom, _curEndX, _curEndY),
                calculatedKind
            )
            {
                StartCrop = CameraDirectorViewToRect(_curStartZoom, _curStartX, _curStartY),
                EndCrop = CameraDirectorViewToRect(_curEndZoom, _curEndX, _curEndY)
            };
            _updateStorySummary?.Invoke();
            RefreshTrayCardIfOpen();
        }

        public void SwitchToCameraScene(ShowScene newScene)
        {
            if (newScene == null) return;

            _editingCameraScene = newScene;
            _selectedShowScene = newScene;
            _shuffleChosen = false;
            _activeIsStartKeyframe = true;

            _canvasSceneId = null;
            if (newScene.Layout != null)
            {
                ApplyLayoutFile(WithRestInTray(newScene.Layout, SessionPictures()), restoreAspect: false, preserveUndo: true);
                _canvasSceneId = newScene.Id;
            }

            ResetCanvasCamera();

            if (newScene.CameraOverride != null)
            {
                _curStartZoom = Math.Max(1.0, newScene.CameraOverride.Start.Zoom);
                _curStartX = Math.Clamp(newScene.CameraOverride.Start.X, 0.0, 1.0);
                _curStartY = Math.Clamp(newScene.CameraOverride.Start.Y, 0.0, 1.0);

                _curEndZoom = Math.Max(1.0, newScene.CameraOverride.End.Zoom);
                _curEndX = Math.Clamp(newScene.CameraOverride.End.X, 0.0, 1.0);
                _curEndY = Math.Clamp(newScene.CameraOverride.End.Y, 0.0, 1.0);
            }
            else
            {
                _curStartZoom = 1.0;
                _curStartX = 0.5;
                _curStartY = 0.5;

                _curEndZoom = 1.25;
                _curEndX = 0.5;
                _curEndY = 0.5;
            }

            UpdateDirectorSceneBadge();
            UpdateKeyframeToggles();
            LayoutWireframes();
        }

        public void NavigateCameraScene(int delta)
        {
            if (!_isEditingCamera || _editingCameraScene == null || _activeShow == null) return;
            var scenes = _activeShow.Scenes;
            int curIdx = scenes.IndexOf(_editingCameraScene);
            if (curIdx < 0) return;

            int targetIdx = curIdx + delta;
            if (targetIdx < 0 || targetIdx >= scenes.Count) return;

            SaveCurrentCameraKeyframes();
            SwitchToCameraScene(scenes[targetIdx]);
        }

        private void UpdateDirectorSceneBadge()
        {
            if (_editingCameraScene == null) return;

            int curIdx = -1;
            int total = 0;
            if (_activeShow != null)
            {
                curIdx = _activeShow.Scenes.IndexOf(_editingCameraScene);
                total = _activeShow.Scenes.Count;
            }

            string recipe = SceneCardTitle(_editingCameraScene);
            if (_directorSceneBadgeText != null)
            {
                if (curIdx >= 0 && total > 0)
                {
                    _directorSceneBadgeText.Text = $"Scene {curIdx + 1} of {total} · {recipe}";
                }
                else
                {
                    _directorSceneBadgeText.Text = $"Camera Director · {recipe}";
                }
            }

            if (_directorPrevSceneBtn != null)
            {
                _directorPrevSceneBtn.IsEnabled = curIdx > 0;
                _directorPrevSceneBtn.Opacity = curIdx > 0 ? 1.0 : 0.35;
            }
            if (_directorNextSceneBtn != null)
            {
                _directorNextSceneBtn.IsEnabled = curIdx >= 0 && curIdx < total - 1;
                _directorNextSceneBtn.Opacity = (curIdx >= 0 && curIdx < total - 1) ? 1.0 : 0.35;
            }
        }

        public void ExitCameraDirector(bool save = true)
        {
            if (!_isEditingCamera) return;

            var editedScene = _editingCameraScene;
            if (save && editedScene != null)
            {
                SaveCurrentCameraKeyframes();
            }

            TeardownCameraDirectorUI();
            _isEditingCamera = false;
            _editingCameraScene = null;
            ResetCanvasCamera();

            // Restore bottom HUD
            UnifiedHud.Visibility = Visibility.Visible;
            _shuffleChosen = false;
            ShowAutoPlayState();

            // If the Story Deck was open when entering, reopen it with the cursor on the edited deck card
            if (_wasTrayOpenWhenEnteringCamera)
            {
                _wasTrayOpenWhenEnteringCamera = false;
                if (editedScene != null)
                {
                    _selectedShowScene = editedScene;
                }
                _trayTab = 1; // Storyboard tab
                ShowTrayCard(NoCell);
            }
        }

        public void ToggleCameraKeyframe()
        {
            if (!_isEditingCamera) return;
            _activeIsStartKeyframe = !_activeIsStartKeyframe;
            UpdateKeyframeToggles();
        }

        public void TriggerCameraPreview()
        {
            if (!_isEditingCamera) return;
            PreviewCameraMove();
        }

        private static NormalizedRectDto CameraDirectorViewToRect(double zoom, double cx, double cy)
        {
            double z = Math.Max(1.0, zoom);
            double w = 1.0 / z;
            double h = 1.0 / z;
            double x = Math.Clamp(cx - w / 2.0, 0.0, 1.0 - w);
            double y = Math.Clamp(cy - h / 2.0, 0.0, 1.0 - h);
            return new NormalizedRectDto(x, y, w, h);
        }

        private void BuildCameraDirectorUI()
        {
            TeardownCameraDirectorUI();

            _cameraOverlayCanvas = new Canvas
            {
                HorizontalAlignment = HorizontalAlignment.Stretch,
                VerticalAlignment = VerticalAlignment.Stretch,
                IsHitTestVisible = true
            };
            Canvas.SetZIndex(_cameraOverlayCanvas, 90000);
            _cameraOverlayCanvas.Translation = new Vector3(0, 0, 8000f);

            _trajectoryLine = new Line
            {
                Stroke = new SolidColorBrush(Color.FromArgb(160, 255, 255, 255)),
                StrokeThickness = 2,
                StrokeDashArray = new DoubleCollection { 4, 3 },
                IsHitTestVisible = false
            };
            _cameraOverlayCanvas.Children.Add(_trajectoryLine);

            // Start Wireframe (Emerald Green #10B981)
            _startWireframe = new Border
            {
                BorderBrush = new SolidColorBrush(Color.FromArgb(255, 16, 185, 129)),
                Background = new SolidColorBrush(Color.FromArgb(28, 16, 185, 129)),
                BorderThickness = new Thickness(3),
                CornerRadius = new CornerRadius(4),
                IsHitTestVisible = true
            };

            _startBadge = new Border
            {
                Background = new SolidColorBrush(Color.FromArgb(230, 6, 46, 30)),
                BorderBrush = new SolidColorBrush(Color.FromArgb(200, 16, 185, 129)),
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(4),
                Padding = new Thickness(8, 3, 8, 3),
                IsHitTestVisible = true
            };
            _startBadgeText = new TextBlock
            {
                Text = "Start · 1.0x",
                Foreground = new SolidColorBrush(Color.FromArgb(255, 16, 185, 129)),
                FontSize = 11,
                FontWeight = Microsoft.UI.Text.FontWeights.SemiBold
            };
            _startBadge.Child = _startBadgeText;
            ToolTipService.SetToolTip(_startBadge, "Start frame — click to select and edit");
            _startBadge.PointerPressed += (s, e) =>
            {
                SelectCameraKeyframe(true);
                e.Handled = true;
            };

            // End Wireframe (Ruby Coral #EF4444)
            _endWireframe = new Border
            {
                BorderBrush = new SolidColorBrush(Color.FromArgb(255, 239, 68, 68)),
                Background = new SolidColorBrush(Color.FromArgb(28, 239, 68, 68)),
                BorderThickness = new Thickness(2),
                CornerRadius = new CornerRadius(4),
                IsHitTestVisible = true
            };

            _endBadge = new Border
            {
                Background = new SolidColorBrush(Color.FromArgb(230, 58, 16, 16)),
                BorderBrush = new SolidColorBrush(Color.FromArgb(200, 239, 68, 68)),
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(4),
                Padding = new Thickness(8, 3, 8, 3),
                IsHitTestVisible = true
            };
            _endBadgeText = new TextBlock
            {
                Text = "End · 1.25x",
                Foreground = new SolidColorBrush(Color.FromArgb(255, 239, 68, 68)),
                FontSize = 11,
                FontWeight = Microsoft.UI.Text.FontWeights.SemiBold
            };
            _endBadge.Child = _endBadgeText;
            ToolTipService.SetToolTip(_endBadge, "End frame — click to select and edit");
            _endBadge.PointerPressed += (s, e) =>
            {
                SelectCameraKeyframe(false);
                e.Handled = true;
            };

            Canvas.SetZIndex(_startBadge, 300);
            Canvas.SetZIndex(_endBadge, 300);

            AttachWireframeEvents(_startWireframe, isStart: true);
            AttachWireframeEvents(_endWireframe, isStart: false);

            _cameraOverlayCanvas.Children.Add(_startWireframe);
            _cameraOverlayCanvas.Children.Add(_endWireframe);
            _cameraOverlayCanvas.Children.Add(_startBadge);
            _cameraOverlayCanvas.Children.Add(_endBadge);
            RootGrid.Children.Add(_cameraOverlayCanvas);

            // Floating Camera Director Toolbar (Strictly above all canvas elements, circles & floating cards)
            _cameraDirectorBar = new Border
            {
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Top,
                Margin = new Thickness(0, 24, 0, 0),
                Background = new SolidColorBrush(Color.FromArgb(245, 20, 20, 20)),
                BorderBrush = new SolidColorBrush(Color.FromArgb(80, 255, 255, 255)),
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(19),
                Padding = new Thickness(8, 3, 8, 3),
                Height = 38
            };
            Canvas.SetZIndex(_cameraDirectorBar, 100000);
            _cameraDirectorBar.Translation = new Vector3(0, 0, 10000f);

            var barStack = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Spacing = 5,
                VerticalAlignment = VerticalAlignment.Center
            };

            // Previous Scene Stepper Button
            _directorPrevSceneBtn = new Button
            {
                Height = 28,
                Width = 28,
                Padding = new Thickness(0),
                Background = new SolidColorBrush(Colors.Transparent),
                BorderThickness = new Thickness(0),
                CornerRadius = new CornerRadius(14),
                IsTabStop = false,
                Content = new FontIcon { FontFamily = new FontFamily("Segoe Fluent Icons, Segoe MDL2 Assets"), Glyph = "\uE76B", FontSize = 10, Foreground = new SolidColorBrush(Colors.White) }
            };
            _directorPrevSceneBtn.Click += (_, _) => NavigateCameraScene(-1);
            ToolTipService.SetToolTip(_directorPrevSceneBtn, "Previous scene (← / PageUp)");
            barStack.Children.Add(_directorPrevSceneBtn);

            // Active Scene Identity Badge Text
            _directorSceneBadgeText = new TextBlock
            {
                FontSize = 12,
                FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
                Foreground = new SolidColorBrush(Colors.White),
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(2, 0, 2, 0)
            };
            barStack.Children.Add(_directorSceneBadgeText);

            // Next Scene Stepper Button
            _directorNextSceneBtn = new Button
            {
                Height = 28,
                Width = 28,
                Padding = new Thickness(0),
                Background = new SolidColorBrush(Colors.Transparent),
                BorderThickness = new Thickness(0),
                CornerRadius = new CornerRadius(14),
                IsTabStop = false,
                Content = new FontIcon { FontFamily = new FontFamily("Segoe Fluent Icons, Segoe MDL2 Assets"), Glyph = "\uE76C", FontSize = 10, Foreground = new SolidColorBrush(Colors.White) }
            };
            _directorNextSceneBtn.Click += (_, _) => NavigateCameraScene(1);
            ToolTipService.SetToolTip(_directorNextSceneBtn, "Next scene (→ / PageDown)");
            barStack.Children.Add(_directorNextSceneBtn);

            barStack.Children.Add(MakeDirectorDivider());

            // Start & End Keyframe selectors
            _startToggleBtn = MakeDirectorPillButton("Start", isStart: true);
            _endToggleBtn = MakeDirectorPillButton("End", isStart: false);
            barStack.Children.Add(_startToggleBtn);
            barStack.Children.Add(_endToggleBtn);

            barStack.Children.Add(MakeDirectorDivider());

            // Preview Move button
            var previewBtn = new Button
            {
                Height = 28,
                Padding = new Thickness(10, 0, 10, 0),
                Background = new SolidColorBrush(Color.FromArgb(24, 255, 255, 255)),
                BorderThickness = new Thickness(0),
                CornerRadius = new CornerRadius(14),
                IsTabStop = false
            };
            var prevStack = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 5, VerticalAlignment = VerticalAlignment.Center };
            prevStack.Children.Add(new FontIcon { FontFamily = new FontFamily("Segoe Fluent Icons, Segoe MDL2 Assets"), Glyph = "\uE768", FontSize = 10, Foreground = new SolidColorBrush(Colors.White) });
            prevStack.Children.Add(new TextBlock { Text = "Preview Move", FontSize = 11.5, Foreground = new SolidColorBrush(Colors.White) });
            previewBtn.Content = prevStack;
            previewBtn.Click += (_, _) => PreviewCameraMove();
            ToolTipService.SetToolTip(previewBtn, "Play live 2.5s Ken Burns move between Start and End (Space)");
            barStack.Children.Add(previewBtn);

            // Reset button
            var resetBtn = new Button
            {
                Height = 28,
                Padding = new Thickness(8, 0, 8, 0),
                Background = new SolidColorBrush(Colors.Transparent),
                BorderThickness = new Thickness(0),
                CornerRadius = new CornerRadius(14),
                IsTabStop = false,
                Content = new TextBlock { Text = "Reset", FontSize = 11.5, Foreground = new SolidColorBrush(Color.FromArgb(180, 255, 255, 255)) }
            };
            resetBtn.Click += (_, _) =>
            {
                _curStartZoom = 1.0;
                _curStartX = 0.5;
                _curStartY = 0.5;
                _curEndZoom = 1.25;
                _curEndX = 0.5;
                _curEndY = 0.5;
                LayoutWireframes();
            };
            ToolTipService.SetToolTip(resetBtn, "Reset Start and End frames to default wide-to-in framing");
            barStack.Children.Add(resetBtn);

            barStack.Children.Add(MakeDirectorDivider());

            // Done button
            var doneBtn = new Button
            {
                Height = 28,
                Padding = new Thickness(12, 0, 12, 0),
                Background = new SolidColorBrush(Color.FromArgb(255, 0, 120, 212)), // WinUI Accent Blue
                BorderThickness = new Thickness(0),
                CornerRadius = new CornerRadius(14),
                IsTabStop = false,
                Content = new TextBlock { Text = "Done", FontSize = 11.5, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold, Foreground = new SolidColorBrush(Colors.White) }
            };
            doneBtn.Click += (_, _) => ExitCameraDirector(save: true);
            ToolTipService.SetToolTip(doneBtn, "Save camera path and exit (Enter)");

            var cancelBtn = new Button
            {
                Height = 28,
                Padding = new Thickness(10, 0, 10, 0),
                Background = new SolidColorBrush(Colors.Transparent),
                BorderThickness = new Thickness(0),
                CornerRadius = new CornerRadius(14),
                IsTabStop = false,
                Content = new TextBlock { Text = "Cancel", FontSize = 11.5, Foreground = new SolidColorBrush(Color.FromArgb(180, 255, 255, 255)) }
            };
            cancelBtn.Click += (_, _) => ExitCameraDirector(save: false);
            ToolTipService.SetToolTip(cancelBtn, "Leave without keeping this scene's changes (Esc). A scene you stepped away from keeps its path.");
            barStack.Children.Add(cancelBtn);
            barStack.Children.Add(doneBtn);

            _cameraDirectorBar.Child = barStack;
            RootGrid.Children.Add(_cameraDirectorBar);

            RootGrid.SizeChanged += CameraDirector_SizeChanged;
            UpdateDirectorSceneBadge();
            UpdateKeyframeToggles();
        }

        private void CameraDirector_SizeChanged(object sender, SizeChangedEventArgs e)
        {
            if (_isEditingCamera) LayoutWireframes();
        }

        private void TeardownCameraDirectorUI()
        {
            RootGrid.SizeChanged -= CameraDirector_SizeChanged;
            if (_cameraOverlayCanvas != null)
            {
                RootGrid.Children.Remove(_cameraOverlayCanvas);
                _cameraOverlayCanvas = null;
            }
            if (_cameraDirectorBar != null)
            {
                RootGrid.Children.Remove(_cameraDirectorBar);
                _cameraDirectorBar = null;
            }
            _startWireframe = null;
            _endWireframe = null;
            _startBadge = null;
            _endBadge = null;
            _trajectoryLine = null;
            _startBadgeText = null;
            _endBadgeText = null;
            _startToggleBtn = null;
            _endToggleBtn = null;
            _directorSceneBadgeText = null;
            _directorPrevSceneBtn = null;
            _directorNextSceneBtn = null;
        }

        private static Border MakeDirectorDivider() => new()
        {
            Width = 1,
            Height = 16,
            Margin = new Thickness(3, 0, 3, 0),
            Background = new SolidColorBrush(Color.FromArgb(40, 255, 255, 255)),
            VerticalAlignment = VerticalAlignment.Center
        };

        private Button MakeDirectorPillButton(string text, bool isStart)
        {
            var btn = new Button
            {
                Height = 28,
                Padding = new Thickness(10, 0, 10, 0),
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(14),
                IsTabStop = false,
                Content = new TextBlock { Text = text, FontSize = 11.5, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold, Foreground = new SolidColorBrush(isStart ? Color.FromArgb(255, 16, 185, 129) : Color.FromArgb(255, 239, 68, 68)) }
            };
            btn.Click += (_, _) => SelectCameraKeyframe(isStart);
            return btn;
        }

        private void SelectCameraKeyframe(bool start)
        {
            _activeIsStartKeyframe = start;
            UpdateKeyframeToggles();
            LayoutWireframes();
        }

        private void UpdateKeyframeToggles()
        {
            if (_startToggleBtn == null || _endToggleBtn == null || _startWireframe == null || _endWireframe == null) return;

            if (_activeIsStartKeyframe)
            {
                _startToggleBtn.Background = new SolidColorBrush(Color.FromArgb(48, 16, 185, 129));
                _startToggleBtn.BorderBrush = new SolidColorBrush(Color.FromArgb(200, 16, 185, 129));
                _endToggleBtn.Background = new SolidColorBrush(Colors.Transparent);
                _endToggleBtn.BorderBrush = new SolidColorBrush(Color.FromArgb(30, 255, 255, 255));

                _startWireframe.BorderThickness = new Thickness(3.5);
                _startWireframe.Opacity = 1.0;
                _startWireframe.Background = new SolidColorBrush(Color.FromArgb(32, 16, 185, 129));
                Canvas.SetZIndex(_startWireframe, 252);

                _endWireframe.BorderThickness = new Thickness(1.8);
                _endWireframe.Opacity = 0.55;
                _endWireframe.Background = new SolidColorBrush(Color.FromArgb(12, 239, 68, 68));
                Canvas.SetZIndex(_endWireframe, 250);

                if (_startBadge != null) _startBadge.Opacity = 1.0;
                if (_endBadge != null) _endBadge.Opacity = 0.70;
            }
            else
            {
                _endToggleBtn.Background = new SolidColorBrush(Color.FromArgb(48, 239, 68, 68));
                _endToggleBtn.BorderBrush = new SolidColorBrush(Color.FromArgb(200, 239, 68, 68));
                _startToggleBtn.Background = new SolidColorBrush(Colors.Transparent);
                _startToggleBtn.BorderBrush = new SolidColorBrush(Color.FromArgb(30, 255, 255, 255));

                _endWireframe.BorderThickness = new Thickness(3.5);
                _endWireframe.Opacity = 1.0;
                _endWireframe.Background = new SolidColorBrush(Color.FromArgb(32, 239, 68, 68));
                Canvas.SetZIndex(_endWireframe, 252);

                _startWireframe.BorderThickness = new Thickness(1.8);
                _startWireframe.Opacity = 0.55;
                _startWireframe.Background = new SolidColorBrush(Color.FromArgb(12, 16, 185, 129));
                Canvas.SetZIndex(_startWireframe, 250);

                if (_endBadge != null) _endBadge.Opacity = 1.0;
                if (_startBadge != null) _startBadge.Opacity = 0.70;
            }
        }

        private void AttachWireframeEvents(Border wireframe, bool isStart)
        {
            wireframe.PointerPressed += (s, e) =>
            {
                SelectCameraKeyframe(isStart);
                _isDraggingWireframe = true;
                _wireframeDragStart = e.GetCurrentPoint(RootGrid).Position;
                _wireframeInitialX = isStart ? _curStartX : _curEndX;
                _wireframeInitialY = isStart ? _curStartY : _curEndY;
                wireframe.CapturePointer(e.Pointer);
                e.Handled = true;
            };

            wireframe.PointerMoved += (s, e) =>
            {
                if (!_isDraggingWireframe) return;
                var current = e.GetCurrentPoint(RootGrid).Position;
                double dx = current.X - _wireframeDragStart.X;
                double dy = current.Y - _wireframeDragStart.Y;

                double W = RootGrid.ActualWidth;
                double H = RootGrid.ActualHeight;
                if (W < 10 || H < 10) return;

                double shareDx = dx / W;
                double shareDy = dy / H;

                double zoom = isStart ? _curStartZoom : _curEndZoom;
                double halfWShare = 0.5 / zoom;
                double halfHShare = 0.5 / zoom;

                double newX = Math.Clamp(_wireframeInitialX + shareDx, halfWShare, 1.0 - halfWShare);
                double newY = Math.Clamp(_wireframeInitialY + shareDy, halfHShare, 1.0 - halfHShare);

                if (isStart)
                {
                    _curStartX = newX;
                    _curStartY = newY;
                }
                else
                {
                    _curEndX = newX;
                    _curEndY = newY;
                }

                LayoutWireframes();
                e.Handled = true;
            };

            wireframe.PointerReleased += (s, e) =>
            {
                if (_isDraggingWireframe)
                {
                    _isDraggingWireframe = false;
                    try { wireframe.ReleasePointerCapture(e.Pointer); } catch { }
                    e.Handled = true;
                }
            };

            wireframe.PointerWheelChanged += (s, e) =>
            {
                SelectCameraKeyframe(isStart);
                double delta = e.GetCurrentPoint(wireframe).Properties.MouseWheelDelta;
                double zoom = isStart ? _curStartZoom : _curEndZoom;

                if (delta > 0)
                {
                    zoom = Math.Clamp(zoom + 0.05, 1.0, 2.5);
                }
                else if (delta < 0)
                {
                    zoom = Math.Clamp(zoom - 0.05, 1.0, 2.5);
                }

                // Keep center within bounds at new zoom
                double halfWShare = 0.5 / zoom;
                double halfHShare = 0.5 / zoom;
                if (isStart)
                {
                    _curStartZoom = zoom;
                    _curStartX = Math.Clamp(_curStartX, halfWShare, 1.0 - halfWShare);
                    _curStartY = Math.Clamp(_curStartY, halfHShare, 1.0 - halfHShare);
                }
                else
                {
                    _curEndZoom = zoom;
                    _curEndX = Math.Clamp(_curEndX, halfWShare, 1.0 - halfWShare);
                    _curEndY = Math.Clamp(_curEndY, halfHShare, 1.0 - halfHShare);
                }

                LayoutWireframes();
                e.Handled = true;
            };
        }

        private void LayoutWireframes()
        {
            if (_cameraOverlayCanvas == null || _startWireframe == null || _endWireframe == null || _trajectoryLine == null) return;

            double W = RootGrid.ActualWidth;
            double H = RootGrid.ActualHeight;
            if (W < 10 || H < 10) return;

            _cameraOverlayCanvas.Width = W;
            _cameraOverlayCanvas.Height = H;

            // Start wireframe layout
            double sW = Math.Max(24, W / _curStartZoom);
            double sH = Math.Max(24, H / _curStartZoom);
            double sLeft = Math.Clamp(_curStartX * W - sW / 2.0, 0.0, W - sW);
            double sTop = Math.Clamp(_curStartY * H - sH / 2.0, 0.0, H - sH);

            _startWireframe.Width = sW;
            _startWireframe.Height = sH;
            Canvas.SetLeft(_startWireframe, sLeft);
            Canvas.SetTop(_startWireframe, sTop);
            if (_startBadgeText != null) _startBadgeText.Text = $"Start · {_curStartZoom:F2}x";

            // End wireframe layout
            double eW = Math.Max(24, W / _curEndZoom);
            double eH = Math.Max(24, H / _curEndZoom);
            double eLeft = Math.Clamp(_curEndX * W - eW / 2.0, 0.0, W - eW);
            double eTop = Math.Clamp(_curEndY * H - eH / 2.0, 0.0, H - eH);

            _endWireframe.Width = eW;
            _endWireframe.Height = eH;
            Canvas.SetLeft(_endWireframe, eLeft);
            Canvas.SetTop(_endWireframe, eTop);
            if (_endBadgeText != null) _endBadgeText.Text = $"End · {_curEndZoom:F2}x";

            if (_startBadge != null)
            {
                Canvas.SetLeft(_startBadge, sLeft + 6);
                Canvas.SetTop(_startBadge, sTop + 6);
            }

            if (_endBadge != null)
            {
                double endBadgeW = _endBadge.ActualWidth > 0 ? _endBadge.ActualWidth : 95;
                double endBadgeH = _endBadge.ActualHeight > 0 ? _endBadge.ActualHeight : 24;
                Canvas.SetLeft(_endBadge, Math.Max(0, eLeft + eW - endBadgeW - 6));
                Canvas.SetTop(_endBadge, Math.Max(0, eTop + eH - endBadgeH - 6));
            }

            // Vector Trajectory Line
            _trajectoryLine.X1 = sLeft + sW / 2.0;
            _trajectoryLine.Y1 = sTop + sH / 2.0;
            _trajectoryLine.X2 = eLeft + eW / 2.0;
            _trajectoryLine.Y2 = eTop + eH / 2.0;
        }

        private async void PreviewCameraMove()
        {
            if (_isPreviewingCameraMove) return;
            _isPreviewingCameraMove = true;

            // Hide overlay wireframes so the viewer sees the cinematics cleanly
            if (_cameraOverlayCanvas != null) _cameraOverlayCanvas.Opacity = 0.0;

            var startView = new CameraView(_curStartZoom, _curStartX, _curStartY).Covered();
            var endView = new CameraView(_curEndZoom, _curEndX, _curEndY).Covered();
            var kind = _curEndZoom > _curStartZoom ? CameraKind.ZoomIn : _curEndZoom < _curStartZoom ? CameraKind.ZoomOut : CameraKind.Drift;
            var move = new CameraMove(kind, startView, endView, 2.5);

            RunCamera(move, goingHome: false);

            await Task.Delay(2600);

            SendCanvasCameraHome();
            await Task.Delay(500);

            if (_cameraOverlayCanvas != null) _cameraOverlayCanvas.Opacity = 1.0;
            _isPreviewingCameraMove = false;
        }

        public static string GetMotionLabel(ShowScene scene)
        {
            if (scene.CameraOverride == null) return "Dynamic";

            if (!string.IsNullOrEmpty(scene.CameraOverride.Kind))
            {
                return scene.CameraOverride.Kind.ToLowerInvariant() switch
                {
                    "zoomin" => "⤢ Zoom in",
                    "zoomout" => "⤡ Zoom out",
                    "pan" or "drift" => "⇄ Pan",
                    "still" => "• Still",
                    _ => "Dynamic"
                };
            }

            double zDiff = scene.CameraOverride.End.Zoom - scene.CameraOverride.Start.Zoom;
            double panDist = Math.Abs(scene.CameraOverride.End.X - scene.CameraOverride.Start.X) +
                             Math.Abs(scene.CameraOverride.End.Y - scene.CameraOverride.Start.Y);

            if (Math.Abs(zDiff) < 0.02 && panDist < 0.02) return "• Still";
            if (zDiff > 0.02) return "⤢ Zoom in";
            if (zDiff < -0.02) return "⤡ Zoom out";
            if (panDist > 0.02) return "⇄ Pan";
            return "• Still";
        }
    }
}
