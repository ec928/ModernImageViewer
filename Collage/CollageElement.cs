using Microsoft.Graphics.Canvas;
using Microsoft.Graphics.Canvas.Geometry;
using System;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Text.Json.Serialization;
using Windows.Foundation;

namespace ModernImageViewer.Collage
{
    public partial class CollageElement : INotifyPropertyChanged
    {
        private Rect _layout;
        private float _zoom = 1.0f;
        private double _panX;
        private double _panY;
        private bool _isLocked;
        private bool _isAnchored;
        private bool _isContentLocked;
        private string _imagePath = string.Empty;
        private string _displayName = string.Empty;

        // Appearance
        private float _borderThickness = 0f;
        private float _cornerRadius = 0f;
        private float _shadowSize = 0f;

        // === NEW: High-Resolution Support ===
        [JsonIgnore]
        public CanvasBitmap? HighResBitmap { get; set; }

        [JsonIgnore]
        public bool IsHighResLoading { get; set; }

        [JsonIgnore]
        public bool HasHighRes => HighResBitmap != null;

        // === Existing properties remain unchanged below this line ===

        [JsonIgnore]
        public Rect Layout
        {
            get => _layout;
            set { if (_layout != value) { _layout = value; OnPropertyChanged(); } }
        }

        [JsonPropertyName("x")]
        public double X
        {
            get => _layout.X;
            set => _layout = new Rect(value, _layout.Y, _layout.Width, _layout.Height);
        }

        [JsonPropertyName("y")]
        public double Y
        {
            get => _layout.Y;
            set => _layout = new Rect(_layout.X, value, _layout.Width, _layout.Height);
        }

        [JsonPropertyName("width")]
        public double Width
        {
            get => _layout.Width;
            set => _layout = new Rect(_layout.X, _layout.Y, value, _layout.Height);
        }

        [JsonPropertyName("height")]
        public double Height
        {
            get => _layout.Height;
            set => _layout = new Rect(_layout.X, _layout.Y, _layout.Width, value);
        }

        public float Zoom
        {
            get => _zoom;
            set { if (Math.Abs(_zoom - value) > 0.001f) { _zoom = value; OnPropertyChanged(); } }
        }

        public double PanX
        {
            get => _panX;
            set { if (Math.Abs(_panX - value) > 0.01) { _panX = value; OnPropertyChanged(); } }
        }

        public double PanY
        {
            get => _panY;
            set { if (Math.Abs(_panY - value) > 0.01) { _panY = value; OnPropertyChanged(); } }
        }

        public bool IsLocked
        {
            get => _isLocked;
            set { if (_isLocked != value) { _isLocked = value; OnPropertyChanged(); } }
        }

        public bool IsAnchored
        {
            get => _isAnchored;
            set { if (_isAnchored != value) { _isAnchored = value; OnPropertyChanged(); } }
        }

        public bool IsContentLocked
        {
            get => _isContentLocked;
            set { if (_isContentLocked != value) { _isContentLocked = value; OnPropertyChanged(); } }
        }

        public string ImagePath
        {
            get => _imagePath;
            set
            {
                if (_imagePath != value)
                {
                    _imagePath = value ?? string.Empty;
                    _displayName = string.IsNullOrEmpty(_imagePath) ? "" : System.IO.Path.GetFileName(_imagePath);
                    OnPropertyChanged();
                    OnPropertyChanged(nameof(DisplayName));
                }
            }
        }

        public float BorderThickness
        {
            get => _borderThickness;
            set { if (Math.Abs(_borderThickness - value) > 0.01f) { _borderThickness = value; OnPropertyChanged(); } }
        }

        public float CornerRadius
        {
            get => _cornerRadius;
            set { if (Math.Abs(_cornerRadius - value) > 0.01f) { _cornerRadius = value; OnPropertyChanged(); } }
        }

        public float ShadowSize
        {
            get => _shadowSize;
            set { if (Math.Abs(_shadowSize - value) > 0.01f) { _shadowSize = value; OnPropertyChanged(); } }
        }

        [JsonIgnore]
        public string DisplayName
        {
            get => _displayName;
            private set { if (_displayName != value) { _displayName = value; OnPropertyChanged(); } }
        }

        [JsonIgnore]
        public ImageCacheEntry? CachedEntry { get; set; }

        [JsonIgnore]
        public bool NeedsContentFill { get; set; }

        public float Rotation { get; set; }

        [JsonIgnore]
        private CanvasGeometry? _cachedClip;
        [JsonIgnore]
        private Rect _lastClipLayout;
        [JsonIgnore]
        private float _lastClipRadius;

        public event PropertyChangedEventHandler? PropertyChanged;

        protected void OnPropertyChanged([CallerMemberName] string? propertyName = null)
            => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));

        public CanvasGeometry GetOrUpdateClipGeometry(ICanvasResourceCreator resourceCreator)
        {
            if (_cachedClip == null || _lastClipLayout != Layout || Math.Abs(_lastClipRadius - CornerRadius) > 0.01f)
            {
                _cachedClip?.Dispose();
                if (CornerRadius > 0)
                    _cachedClip = CanvasGeometry.CreateRoundedRectangle(resourceCreator, Layout, CornerRadius, CornerRadius);
                else
                    _cachedClip = CanvasGeometry.CreateRectangle(resourceCreator, Layout);

                _lastClipLayout = Layout;
                _lastClipRadius = CornerRadius;
            }
            return _cachedClip;
        }

        public void FitToCell(double cellWidth, double cellHeight, double imageLogicalWidth, double imageLogicalHeight)
        {
            if (cellWidth <= 0 || cellHeight <= 0 || imageLogicalWidth <= 0 || imageLogicalHeight <= 0)
            {
                Zoom = 1.0f; PanX = 0; PanY = 0; return;
            }

            float fitZoom = (float)Math.Min(cellWidth / imageLogicalWidth, cellHeight / imageLogicalHeight);
            Zoom = Math.Max(0.05f, fitZoom);
            PanX = 0;
            PanY = 0;
        }

        public void FillToCell(double imageLogicalWidth, double imageLogicalHeight)
        {
            if (Layout.Width <= 0 || Layout.Height <= 0 || imageLogicalWidth <= 0 || imageLogicalHeight <= 0) return;

            float fitZoom = (float)Math.Max(Layout.Width / imageLogicalWidth, Layout.Height / imageLogicalHeight);
            Zoom = Math.Max(0.05f, fitZoom);

            double scaledW = imageLogicalWidth * Zoom;
            double scaledH = imageLogicalHeight * Zoom;

            PanX = -(scaledW - Layout.Width) / (2.0 * Zoom);
            PanY = -(scaledH - Layout.Height) / (2.0 * Zoom);
        }

        public void InvalidateClipGeometry()
        {
            _cachedClip?.Dispose();
            _cachedClip = null;
        }

        public CollageElement Clone()
        {
            return new CollageElement
            {
                Layout = this.Layout,
                Zoom = this.Zoom,
                PanX = this.PanX,
                PanY = this.PanY,
                IsLocked = this.IsLocked,
                IsAnchored = this.IsAnchored,
                IsContentLocked = this.IsContentLocked,
                ImagePath = this.ImagePath,
                BorderThickness = this.BorderThickness,
                CornerRadius = this.CornerRadius,
                ShadowSize = this.ShadowSize,
                Rotation = this.Rotation
            };
        }

        public void ReleaseHighResBitmap()
        {
            HighResBitmap?.Dispose();
            HighResBitmap = null;
            IsHighResLoading = false;
        }
    }
}