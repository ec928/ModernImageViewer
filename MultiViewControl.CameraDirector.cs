using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Shapes;
using System;
using System.Collections.Generic;
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

        private double _curStartAngle;
        private double _curEndAngle;

        // The cells' rotate handle, on the selected frame: the same look and the same drag. Camera turns stop at
        // MostTurn, so Shift steps by whole degrees, not the cells' 15.
        private Border? _turnHandle;
        private bool _turningFrame;
        private double _turnPointerStart;
        private double _turnAngleStart;
        private const double TurnHandleInset = 26;

        /// <summary>One shot as the Director edits it. Cut: it starts somewhere other than where the shot before ended.</summary>
        private sealed class DirectorShot
        {
            public CameraView From;
            public CameraView To;
            public double Share = 1;
            public CameraCurve Curve = CameraCurve.Smooth;
            public double Bow;
            public bool Cut;
        }

        // Every shot of the scene being edited; the Start and End frames are the selected shot's.
        private List<DirectorShot> _directorShots = new();
        private int _shotIndex;
        private TextBlock? _shotBadgeText;
        private StackPanel? _shotStepper;
        private DropDownButton? _curveButton;

        private bool _isPreviewingCameraMove;

        // Faces in the scene on the canvas, in RootGrid's space, largest first, and their outlines on the overlay.
        private List<Rect> _facesOnCanvas = new();
        private readonly List<FrameworkElement> _faceOutlines = new();
        private int _aimFaceIndex;
        private Button? _aimFaceBtn;
        private const double FaceFillsFrame = 0.35;
        private const double MostDirectorZoom = 2.5;

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

            LoadDirectorShots(scene);

            BuildCameraDirectorUI();
            LayoutWireframes();
            ShowFaceOutlines();
        }

        private void SaveCurrentCameraKeyframes()
        {
            if (_editingCameraScene == null) return;
            StoreCurrentShot();

            var move = DirectorMove(1);
            string calculatedKind;
            if (move.Shots.Count > 1)
            {
                calculatedKind = "TwoShot";
            }
            else
            {
                double zDiff = move.End.Zoom - move.Start.Zoom;
                double panDist = Math.Abs(move.End.X - move.Start.X) + Math.Abs(move.End.Y - move.Start.Y);
                calculatedKind = Math.Abs(zDiff) < 0.02 && panDist < 0.02
                    ? "Still"
                    : zDiff > 0.02
                        ? "ZoomIn"
                        : zDiff < -0.02
                            ? "ZoomOut"
                            : "Pan";
            }

            // Every shot, with its curve, bend and turn, so editing one frame never loses the rest of the path.
            _editingCameraScene.CameraOverride = CameraKeyframeOverride.FromMove(move, calculatedKind);
            _updateStorySummary?.Invoke();
            RefreshTrayCardIfOpen();
        }

        /// <summary>The scene's shots into the Director, the first one selected. A scene with no path gets the default wide-to-in.</summary>
        private void LoadDirectorShots(ShowScene scene)
        {
            _directorShots = new List<DirectorShot>();
            if (scene.CameraOverride != null)
            {
                var shots = scene.CameraOverride.ToShots();
                for (int i = 0; i < shots.Count; i++)
                {
                    _directorShots.Add(new DirectorShot
                    {
                        From = shots[i].From,
                        To = shots[i].To,
                        Share = shots[i].Share,
                        Curve = shots[i].Curve,
                        Bow = shots[i].Bow,
                        Cut = i > 0 && shots[i].From != shots[i - 1].To
                    });
                }
            }
            if (_directorShots.Count == 0)
                _directorShots.Add(new DirectorShot { From = new CameraView(1.0, 0.5, 0.5), To = new CameraView(1.25, 0.5, 0.5) });
            _shotIndex = 0;
            _activeIsStartKeyframe = true;
            LoadShot(0);
        }

        private void LoadShot(int index)
        {
            _shotIndex = Math.Clamp(index, 0, _directorShots.Count - 1);
            var s = _directorShots[_shotIndex];
            _curStartZoom = Math.Max(1.0, s.From.Zoom);
            _curStartX = Math.Clamp(s.From.X, 0.0, 1.0);
            _curStartY = Math.Clamp(s.From.Y, 0.0, 1.0);
            _curStartAngle = s.From.Angle;
            _curEndZoom = Math.Max(1.0, s.To.Zoom);
            _curEndX = Math.Clamp(s.To.X, 0.0, 1.0);
            _curEndY = Math.Clamp(s.To.Y, 0.0, 1.0);
            _curEndAngle = s.To.Angle;
        }

        /// <summary>
        /// The frames being edited back into their shot. A shot that continues starts where the one before ends, so moving
        /// either of those frames moves the other with it.
        /// </summary>
        private void StoreCurrentShot()
        {
            if (_directorShots.Count == 0) return;
            var s = _directorShots[_shotIndex];
            s.From = new CameraView(_curStartZoom, _curStartX, _curStartY, _curStartAngle);
            s.To = new CameraView(_curEndZoom, _curEndX, _curEndY, _curEndAngle);
            if (_shotIndex > 0 && !s.Cut) _directorShots[_shotIndex - 1].To = s.From;
            if (_shotIndex < _directorShots.Count - 1 && !_directorShots[_shotIndex + 1].Cut) _directorShots[_shotIndex + 1].From = s.To;
        }

        /// <summary>All the shots as one move, each taking its share of the time.</summary>
        private CameraMove DirectorMove(double seconds)
        {
            var shots = _directorShots.Select(s => new CameraShot(s.From.Covered(), s.To.Covered(), s.Share, s.Curve, s.Bow)).ToList();
            var kind = shots[^1].To.Zoom > shots[0].From.Zoom ? CameraKind.ZoomIn : shots[^1].To.Zoom < shots[0].From.Zoom ? CameraKind.ZoomOut : CameraKind.Drift;
            return new CameraMove(kind, shots, seconds);
        }

        private void SelectShot(int index)
        {
            if (index < 0 || index >= _directorShots.Count || index == _shotIndex) return;
            StoreCurrentShot();
            LoadShot(index);
            _activeIsStartKeyframe = true;
            UpdateShotControls();
            UpdateKeyframeToggles();
            LayoutWireframes();
        }

        /// <summary>The shot stepper shows only with more than one shot; the curve menu names the selected shot's curve.</summary>
        private void UpdateShotControls()
        {
            if (_shotStepper != null) _shotStepper.Visibility = _directorShots.Count > 1 ? Visibility.Visible : Visibility.Collapsed;
            if (_shotBadgeText != null)
            {
                var s = _directorShots[_shotIndex];
                _shotBadgeText.Text = $"Shot {_shotIndex + 1} of {_directorShots.Count}" + (s.Cut ? " · cut" : "");
            }
            if (_curveButton != null) _curveButton.Content = $"Curve: {_directorShots[_shotIndex].Curve}";
        }

        public void SwitchToCameraScene(ShowScene newScene)
        {
            if (newScene == null) return;

            _editingCameraScene = newScene;
            _selectedShowScene = newScene;
            _activeIsStartKeyframe = true;

            _canvasSceneId = null;
            if (newScene.Layout != null)
            {
                ApplyLayoutFile(WithRestInTray(newScene.Layout, SessionPictures()), restoreAspect: false, preserveUndo: true);
                _canvasSceneId = newScene.Id;
            }

            ResetCanvasCamera();

            LoadDirectorShots(newScene);

            UpdateDirectorSceneBadge();
            UpdateShotControls();
            UpdateKeyframeToggles();
            LayoutWireframes();
            ShowFaceOutlines();
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
            _turnHandle = MakeFrameTurnHandle();
            _cameraOverlayCanvas.Children.Add(_turnHandle);
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

            // A scene of more than one shot: which shot Start and End belong to.
            _shotStepper = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 2, VerticalAlignment = VerticalAlignment.Center };
            Button ShotStep(string glyph, int delta, string tip)
            {
                var b = new Button
                {
                    Height = 28,
                    Width = 24,
                    Padding = new Thickness(0),
                    Background = new SolidColorBrush(Colors.Transparent),
                    BorderThickness = new Thickness(0),
                    CornerRadius = new CornerRadius(12),
                    IsTabStop = false,
                    Content = new FontIcon { FontFamily = new FontFamily("Segoe Fluent Icons, Segoe MDL2 Assets"), Glyph = glyph, FontSize = 9, Foreground = new SolidColorBrush(Colors.White) }
                };
                b.Click += (_, _) => SelectShot(_shotIndex + delta);
                ToolTipService.SetToolTip(b, tip);
                Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(b, tip);
                return b;
            }
            _shotBadgeText = new TextBlock { FontSize = 11.5, Foreground = new SolidColorBrush(Color.FromArgb(230, 255, 255, 255)), VerticalAlignment = VerticalAlignment.Center };
            _shotStepper.Children.Add(MakeDirectorDivider());
            _shotStepper.Children.Add(ShotStep("", -1, "Previous shot"));
            _shotStepper.Children.Add(_shotBadgeText);
            _shotStepper.Children.Add(ShotStep("", 1, "Next shot"));
            barStack.Children.Add(_shotStepper);

            // How the selected shot gets going and comes to rest.
            var curveMenu = new MenuFlyout();
            foreach (var c in CameraCurves.All)
            {
                var item = new RadioMenuFlyoutItem { Text = c.ToString(), GroupName = "DirectorCurve" };
                var curve = c;
                item.Click += (_, _) =>
                {
                    _directorShots[_shotIndex].Curve = curve;
                    UpdateShotControls();
                };
                curveMenu.Items.Add(item);
            }
            curveMenu.Opening += (_, _) =>
            {
                foreach (var item in curveMenu.Items.OfType<RadioMenuFlyoutItem>())
                    item.IsChecked = item.Text == _directorShots[_shotIndex].Curve.ToString();
            };
            _curveButton = new DropDownButton
            {
                Height = 28,
                Padding = new Thickness(10, 0, 8, 0),
                Background = new SolidColorBrush(Colors.Transparent),
                BorderThickness = new Thickness(0),
                CornerRadius = new CornerRadius(14),
                IsTabStop = false,
                FontSize = 11.5,
                Foreground = new SolidColorBrush(Color.FromArgb(220, 255, 255, 255)),
                Flyout = curveMenu
            };
            ToolTipService.SetToolTip(_curveButton, "How the selected shot gets going and comes to rest");
            Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(_curveButton, "Curve");
            barStack.Children.Add(_curveButton);

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
            Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(previewBtn, "Preview move");
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
                _curStartAngle = 0;
                _curEndZoom = 1.25;
                _curEndX = 0.5;
                _curEndY = 0.5;
                _curEndAngle = 0;
                LayoutWireframes();
            };
            ToolTipService.SetToolTip(resetBtn, "Reset this shot's Start and End frames to the default wide-to-in framing, untilted");
            barStack.Children.Add(resetBtn);

            _aimFaceBtn = new Button
            {
                Height = 28,
                Padding = new Thickness(8, 0, 8, 0),
                Background = new SolidColorBrush(Colors.Transparent),
                BorderThickness = new Thickness(0),
                CornerRadius = new CornerRadius(14),
                IsTabStop = false,
                IsEnabled = false,
                Content = new TextBlock { Text = "Aim at face", FontSize = 11.5, Foreground = new SolidColorBrush(Color.FromArgb(220, 255, 255, 255)) }
            };
            _aimFaceBtn.Click += (_, _) => AimSelectedFrameAtFace();
            ToolTipService.SetToolTip(_aimFaceBtn, "Looking for faces");
            barStack.Children.Add(_aimFaceBtn);

            var exactBtn = new Button
            {
                Height = 28,
                Padding = new Thickness(8, 0, 8, 0),
                Background = new SolidColorBrush(Colors.Transparent),
                BorderThickness = new Thickness(0),
                CornerRadius = new CornerRadius(14),
                IsTabStop = false,
                Content = new TextBlock { Text = "Exact…", FontSize = 11.5, Foreground = new SolidColorBrush(Color.FromArgb(220, 255, 255, 255)) },
                Flyout = MakeExactFrameFlyout()
            };
            ToolTipService.SetToolTip(exactBtn, "Type the selected frame's zoom, centre and turn");
            barStack.Children.Add(exactBtn);

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
            UpdateShotControls();
            UpdateKeyframeToggles();
        }

        private void CameraDirector_SizeChanged(object sender, SizeChangedEventArgs e)
        {
            if (!_isEditingCamera) return;
            LayoutWireframes();
            DrawFaceOutlines();
        }

        /// <summary>
        /// Faces in the scene on the canvas, in RootGrid's space, largest first. Read from each cell's image as it is shown:
        /// its crop, zoom and pan, and the cell's place and turn, clipped to the cell.
        /// </summary>
        private List<Rect> FacesOnCanvas()
        {
            var found = new List<Rect>();
            foreach (var root in _cellRoots)
            {
                if (root?.Tag is not CellParts parts || root.Visibility != Visibility.Visible) continue;
                string path = parts.Bound.Item.Path;
                if (string.IsNullOrEmpty(path) || !_photoFacts.TryGetValue(path, out var facts) || facts.Faces.Count == 0) continue;
                var photo = parts.Photo;
                double ew = photo.ActualWidth, eh = photo.ActualHeight;
                if (ew < 1 || eh < 1 || facts.Width < 1 || facts.Height < 1) continue;

                // Where the picture sits in its element: covering it when filled, inside it when fitted, centred either way.
                double scale = photo.Stretch == Stretch.Uniform
                    ? Math.Min(ew / facts.Width, eh / facts.Height)
                    : Math.Max(ew / facts.Width, eh / facts.Height);
                double dw = facts.Width * scale, dh = facts.Height * scale;
                double ox = (ew - dw) / 2, oy = (eh - dh) / 2;

                GeneralTransform toCanvas, cellToCanvas;
                try
                {
                    toCanvas = photo.TransformToVisual(RootGrid);
                    cellToCanvas = root.TransformToVisual(RootGrid);
                }
                catch (ArgumentException)
                {
                    continue;
                }
                var cell = cellToCanvas.TransformBounds(new Rect(0, 0, root.ActualWidth, root.ActualHeight));
                foreach (var face in facts.Faces)
                {
                    var r = toCanvas.TransformBounds(new Rect(ox + face.X * dw, oy + face.Y * dh, face.W * dw, face.H * dh));
                    r.Intersect(cell);
                    if (!r.IsEmpty && r.Width >= 4 && r.Height >= 4) found.Add(r);
                }
            }
            return found.OrderByDescending(r => r.Width * r.Height).ToList();
        }

        /// <summary>
        /// Outlines the faces once the scene's cells have laid out. A scene added by hand may not have had its faces read
        /// yet, so they are read first. A later scene switch makes this one give up.
        /// </summary>
        private async void ShowFaceOutlines()
        {
            var scene = _editingCameraScene;
            var paths = _cellRoots
                .Select(r => (r?.Tag as CellParts)?.Bound.Item.Path)
                .Where(p => !string.IsNullOrEmpty(p))
                .Select(p => p!)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
            if (paths.Any(p => !_photoFacts.ContainsKey(p)))
            {
                var ready = DecodedBitmaps();
                await ReadFactsAsync(paths, path => ready.TryGetValue(path, out var bitmap) ? (bitmap.PixelWidth, bitmap.PixelHeight) : null);
            }
            bool CellsShown() => _cellRoots.All(r => r?.Tag is not CellParts parts || r.Visibility != Visibility.Visible || parts.Photo.ActualWidth > 0);
            for (int i = 0; i < 20 && !CellsShown(); i++) await Task.Delay(100);
            if (!_isEditingCamera || _editingCameraScene != scene) return;
            DrawFaceOutlines();
        }

        private void DrawFaceOutlines()
        {
            if (_cameraOverlayCanvas == null) return;
            foreach (var outline in _faceOutlines) _cameraOverlayCanvas.Children.Remove(outline);
            _faceOutlines.Clear();
            _facesOnCanvas = FacesOnCanvas();
            _aimFaceIndex = 0;
            foreach (var face in _facesOnCanvas)
            {
                var outline = new Rectangle
                {
                    Width = face.Width,
                    Height = face.Height,
                    Stroke = new SolidColorBrush(Color.FromArgb(220, 255, 255, 255)),
                    StrokeThickness = 1.5,
                    StrokeDashArray = new DoubleCollection { 3, 2 },
                    RadiusX = 3,
                    RadiusY = 3,
                    IsHitTestVisible = false
                };
                Canvas.SetLeft(outline, face.X);
                Canvas.SetTop(outline, face.Y);
                Canvas.SetZIndex(outline, 240);
                _cameraOverlayCanvas.Children.Add(outline);
                _faceOutlines.Add(outline);
            }
            if (_aimFaceBtn != null)
            {
                _aimFaceBtn.IsEnabled = _facesOnCanvas.Count > 0;
                ToolTipService.SetToolTip(_aimFaceBtn, _facesOnCanvas.Count == 0
                    ? "No face found in this scene"
                    : $"Centres the selected frame on a face, largest first. {_facesOnCanvas.Count} found; click again for the next.");
            }
        }

        /// <summary>The selected frame on the next face, largest first, zoomed so the face fills about a third of it.</summary>
        private void AimSelectedFrameAtFace()
        {
            double W = RootGrid.ActualWidth, H = RootGrid.ActualHeight;
            if (_facesOnCanvas.Count == 0 || W < 10 || H < 10) return;
            var face = _facesOnCanvas[_aimFaceIndex % _facesOnCanvas.Count];
            _aimFaceIndex++;
            double faceShare = Math.Max(face.Width / W, face.Height / H);
            double zoom = Math.Clamp(FaceFillsFrame / Math.Max(0.01, faceShare), 1.0, MostDirectorZoom);
            SetSelectedFrame(zoom, (face.X + face.Width / 2) / W, (face.Y + face.Height / 2) / H);
        }

        /// <summary>Moves the selected frame, kept whole on the canvas.</summary>
        private void SetSelectedFrame(double zoom, double x, double y)
        {
            zoom = Math.Clamp(zoom, 1.0, MostDirectorZoom);
            double half = 0.5 / zoom;
            x = Math.Clamp(x, half, 1.0 - half);
            y = Math.Clamp(y, half, 1.0 - half);
            if (_activeIsStartKeyframe) { _curStartZoom = zoom; _curStartX = x; _curStartY = y; }
            else { _curEndZoom = zoom; _curEndX = x; _curEndY = y; }
            LayoutWireframes();
        }

        /// <summary>Typed values for the selected frame, for when dragging is too coarse.</summary>
        private Flyout MakeExactFrameFlyout()
        {
            var zoomBox = new NumberBox { Header = "Zoom", Minimum = 1, Maximum = MostDirectorZoom, SmallChange = 0.05, LargeChange = 0.25, SpinButtonPlacementMode = NumberBoxSpinButtonPlacementMode.Compact, Width = 170 };
            var acrossBox = new NumberBox { Header = "Across (% of the width)", Minimum = 0, Maximum = 100, SmallChange = 1, LargeChange = 10, SpinButtonPlacementMode = NumberBoxSpinButtonPlacementMode.Compact, Width = 170 };
            var downBox = new NumberBox { Header = "Down (% of the height)", Minimum = 0, Maximum = 100, SmallChange = 1, LargeChange = 10, SpinButtonPlacementMode = NumberBoxSpinButtonPlacementMode.Compact, Width = 170 };
            var turnBox = new NumberBox { Header = "Turn (degrees)", Minimum = -SurpriseOptions.MostTurn, Maximum = SurpriseOptions.MostTurn, SmallChange = 0.5, LargeChange = 1, SpinButtonPlacementMode = NumberBoxSpinButtonPlacementMode.Compact, Width = 170 };
            zoomBox.NumberFormatter = new Windows.Globalization.NumberFormatting.DecimalFormatter { FractionDigits = 2, IntegerDigits = 1 };
            turnBox.NumberFormatter = new Windows.Globalization.NumberFormatting.DecimalFormatter { FractionDigits = 1, IntegerDigits = 1 };
            var note = new TextBlock { FontSize = 11, Opacity = 0.7, TextWrapping = TextWrapping.Wrap, MaxWidth = 170 };
            var panel = new StackPanel { Spacing = 8, Children = { note, zoomBox, acrossBox, downBox, turnBox } };
            var flyout = new Flyout { Content = panel, Placement = Microsoft.UI.Xaml.Controls.Primitives.FlyoutPlacementMode.Bottom };
            bool loading = false;
            flyout.Opening += (_, _) =>
            {
                loading = true;
                note.Text = _activeIsStartKeyframe ? "Start frame. Across and down are its centre." : "End frame. Across and down are its centre.";
                zoomBox.Value = Math.Round(_activeIsStartKeyframe ? _curStartZoom : _curEndZoom, 2);
                acrossBox.Value = Math.Round((_activeIsStartKeyframe ? _curStartX : _curEndX) * 100);
                downBox.Value = Math.Round((_activeIsStartKeyframe ? _curStartY : _curEndY) * 100);
                turnBox.Value = Math.Round(_activeIsStartKeyframe ? _curStartAngle : _curEndAngle, 1);
                loading = false;
            };
            void Apply()
            {
                if (loading || double.IsNaN(zoomBox.Value) || double.IsNaN(acrossBox.Value) || double.IsNaN(downBox.Value)) return;
                double turn = double.IsNaN(turnBox.Value) ? 0 : Math.Clamp(turnBox.Value, -SurpriseOptions.MostTurn, SurpriseOptions.MostTurn);
                if (_activeIsStartKeyframe) _curStartAngle = turn; else _curEndAngle = turn;
                SetSelectedFrame(zoomBox.Value, acrossBox.Value / 100, downBox.Value / 100);
            }
            zoomBox.ValueChanged += (_, _) => Apply();
            acrossBox.ValueChanged += (_, _) => Apply();
            downBox.ValueChanged += (_, _) => Apply();
            turnBox.ValueChanged += (_, _) => Apply();
            return flyout;
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
            _faceOutlines.Clear();
            _facesOnCanvas = new List<Rect>();
            _aimFaceBtn = null;
            _startWireframe = null;
            _endWireframe = null;
            _turnHandle = null;
            _turningFrame = false;
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
            _shotBadgeText = null;
            _shotStepper = null;
            _curveButton = null;
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
            PlaceTurnHandle();
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

            // Start wireframe layout. A turned frame is what playback shows: zoomed a little further, so it stays on the canvas.
            double sZoom = _curStartZoom * CameraView.TurnCover(_curStartAngle, W, H);
            double sW = Math.Max(24, W / sZoom);
            double sH = Math.Max(24, H / sZoom);
            double sLeft = Math.Clamp(_curStartX * W - sW / 2.0, 0.0, W - sW);
            double sTop = Math.Clamp(_curStartY * H - sH / 2.0, 0.0, H - sH);

            _startWireframe.Width = sW;
            _startWireframe.Height = sH;
            Canvas.SetLeft(_startWireframe, sLeft);
            Canvas.SetTop(_startWireframe, sTop);
            // A turned frame drawn turned: the overlay is only an outline, so a XAML turn is safe here.
            _startWireframe.RenderTransform = new RotateTransform { Angle = _curStartAngle, CenterX = sW / 2, CenterY = sH / 2 };
            if (_startBadgeText != null) _startBadgeText.Text = $"Start · {_curStartZoom:F2}x" + (Math.Abs(_curStartAngle) >= 0.05 ? $" · {_curStartAngle:0.#}°" : "");

            // End wireframe layout
            double eZoom = _curEndZoom * CameraView.TurnCover(_curEndAngle, W, H);
            double eW = Math.Max(24, W / eZoom);
            double eH = Math.Max(24, H / eZoom);
            double eLeft = Math.Clamp(_curEndX * W - eW / 2.0, 0.0, W - eW);
            double eTop = Math.Clamp(_curEndY * H - eH / 2.0, 0.0, H - eH);

            _endWireframe.Width = eW;
            _endWireframe.Height = eH;
            Canvas.SetLeft(_endWireframe, eLeft);
            Canvas.SetTop(_endWireframe, eTop);
            _endWireframe.RenderTransform = new RotateTransform { Angle = _curEndAngle, CenterX = eW / 2, CenterY = eH / 2 };
            if (_endBadgeText != null) _endBadgeText.Text = $"End · {_curEndZoom:F2}x" + (Math.Abs(_curEndAngle) >= 0.05 ? $" · {_curEndAngle:0.#}°" : "");

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

            PlaceTurnHandle();
        }

        /// <summary>The selected frame's centre on the overlay, and its turn.</summary>
        private (double X, double Y, double HalfHeight, double Angle)? SelectedFrameCentre()
        {
            var frame = _activeIsStartKeyframe ? _startWireframe : _endWireframe;
            if (frame == null || double.IsNaN(frame.Width)) return null;
            return (Canvas.GetLeft(frame) + frame.Width / 2, Canvas.GetTop(frame) + frame.Height / 2, frame.Height / 2,
                _activeIsStartKeyframe ? _curStartAngle : _curEndAngle);
        }

        /// <summary>Just inside the selected frame's top edge, at its middle, turned with it: where a cell's handle sits.</summary>
        private void PlaceTurnHandle()
        {
            if (_turnHandle == null || SelectedFrameCentre() is not { } c) return;
            double reach = c.HalfHeight - TurnHandleInset;
            double radians = c.Angle * Math.PI / 180;
            double x = c.X + reach * Math.Sin(radians);
            double y = c.Y - reach * Math.Cos(radians);
            // A frame reaching the top of the canvas would put it under the Director's bar: then inside the bottom edge.
            if (_cameraDirectorBar != null && _cameraOverlayCanvas != null && _cameraDirectorBar.ActualHeight > 0)
            {
                double barBottom = _cameraDirectorBar.TransformToVisual(_cameraOverlayCanvas).TransformPoint(new Point(0, _cameraDirectorBar.ActualHeight)).Y;
                if (y - _turnHandle.Height / 2 < barBottom + 8)
                {
                    x = c.X - reach * Math.Sin(radians);
                    y = c.Y + reach * Math.Cos(radians);
                }
            }
            Canvas.SetLeft(_turnHandle, x - _turnHandle.Width / 2);
            Canvas.SetTop(_turnHandle, y - _turnHandle.Height / 2);
        }

        private Border MakeFrameTurnHandle()
        {
            var handle = MakeWindowHandle(28, "");
            handle.Opacity = 1;
            Canvas.SetZIndex(handle, 310);
            ToolTipService.SetToolTip(handle, $"Drag to turn the selected frame, up to {SurpriseOptions.MostTurn:0}° either way. Hold Shift for whole degrees.");
            Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(handle, "Turn frame");
            double PointerAngle(Microsoft.UI.Xaml.Input.PointerRoutedEventArgs e)
            {
                var p = e.GetCurrentPoint(_cameraOverlayCanvas).Position;
                var c = SelectedFrameCentre() ?? (0, 0, 0, 0);
                return Math.Atan2(p.Y - c.Y, p.X - c.X) * 180 / Math.PI;
            }
            handle.PointerPressed += (_, e) =>
            {
                _turningFrame = true;
                _turnPointerStart = PointerAngle(e);
                _turnAngleStart = _activeIsStartKeyframe ? _curStartAngle : _curEndAngle;
                handle.CapturePointer(e.Pointer);
                e.Handled = true;
            };
            handle.PointerMoved += (_, e) =>
            {
                if (!_turningFrame) return;
                double change = PointerAngle(e) - _turnPointerStart;
                if (change > 180) change -= 360;
                if (change < -180) change += 360;
                double angle = Math.Clamp(_turnAngleStart + change, -SurpriseOptions.MostTurn, SurpriseOptions.MostTurn);
                bool shift = Microsoft.UI.Input.InputKeyboardSource.GetKeyStateForCurrentThread(Windows.System.VirtualKey.Shift)
                    .HasFlag(Windows.UI.Core.CoreVirtualKeyStates.Down);
                angle = shift ? Math.Round(angle) : Math.Round(angle, 1);
                if (_activeIsStartKeyframe) _curStartAngle = angle; else _curEndAngle = angle;
                LayoutWireframes();
                e.Handled = true;
            };
            void EndTurn(object _, Microsoft.UI.Xaml.Input.PointerRoutedEventArgs e)
            {
                if (!_turningFrame) return;
                _turningFrame = false;
                try { handle.ReleasePointerCapture(e.Pointer); } catch { }
                e.Handled = true;
            }
            handle.PointerReleased += EndTurn;
            handle.PointerCanceled += EndTurn;
            handle.PointerCaptureLost += (_, _) => _turningFrame = false;
            return handle;
        }

        private async void PreviewCameraMove()
        {
            if (_isPreviewingCameraMove) return;
            _isPreviewingCameraMove = true;

            // Hide overlay wireframes so the viewer sees the cinematics cleanly
            if (_cameraOverlayCanvas != null) _cameraOverlayCanvas.Opacity = 0.0;

            // Every shot, with its curve, 2.5 seconds each.
            StoreCurrentShot();
            double seconds = 2.5 * _directorShots.Count;
            RunCamera(DirectorMove(seconds), goingHome: false);

            await Task.Delay((int)(seconds * 1000) + 100);

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
                    "twoshot" => "Two shots",
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
