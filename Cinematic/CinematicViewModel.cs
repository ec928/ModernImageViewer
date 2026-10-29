using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Threading.Tasks;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using Windows.Storage;
using ModernImageViewer.Cinematic.Data;
using Microsoft.UI.Dispatching;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading;

namespace ModernImageViewer.Cinematic.ViewModels
{
    public class TrackClip : INotifyPropertyChanged
    {
        public event PropertyChangedEventHandler? PropertyChanged;
        public string Id { get; set; } = Guid.NewGuid().ToString("N");
        public string FilePath { get; set; } = string.Empty;
        public string FileName => Path.GetFileName(FilePath);

        private BitmapImage? _thumbnail;
        public BitmapImage? Thumbnail
        {
            get => _thumbnail;
            set { _thumbnail = value; PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Thumbnail))); }
        }

        private bool _isHidden;
        public bool IsHidden
        {
            get => _isHidden;
            set
            {
                if (_isHidden == value) return;
                _isHidden = value;
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsHidden)));
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(StatusLabel)));
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(TileOpacity)));
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(StatusBrush)));
            }
        }

        private bool _isCustom;
        public bool IsCustom
        {
            get => _isCustom;
            set
            {
                if (_isCustom == value) return;
                _isCustom = value;
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsCustom)));
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(StatusLabel)));
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(StatusBrush)));
            }
        }

        private bool _isActive;
        public bool IsActive
        {
            get => _isActive;
            set
            {
                if (_isActive == value) return;
                _isActive = value;
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsActive)));
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(TileOpacity)));
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(ActiveBorderOpacity)));
            }
        }

        public string StatusLabel => IsHidden ? "Hidden" : (IsCustom ? "Custom" : "Auto");
        public double TileOpacity => IsHidden ? 0.32 : (IsActive ? 1.0 : 0.72);
        public double ActiveBorderOpacity => IsActive ? 1.0 : 0.0;
        public SolidColorBrush StatusBrush => new SolidColorBrush(IsHidden
            ? Windows.UI.Color.FromArgb(200, 80, 80, 80)
            : IsCustom
                ? Windows.UI.Color.FromArgb(220, 200, 110, 20)
                : Windows.UI.Color.FromArgb(200, 50, 90, 140));
    }

    public class CinematicViewModel : INotifyPropertyChanged
    {
        private readonly DispatcherQueue _dispatcher;
        public ObservableCollection<TrackClip> Track { get; } = new();
        public event PropertyChangedEventHandler? PropertyChanged;
        protected void OnPropertyChanged([CallerMemberName] string? propertyName = null)
        {
            if (_dispatcher != null && !_dispatcher.HasThreadAccess)
            {
                _dispatcher.TryEnqueue(() => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName)));
            }
            else
            {
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
            }
        }

        protected bool SetProperty<T>(ref T storage, T value, [CallerMemberName] string? propertyName = null)
        {
            if (Equals(storage, value)) return false;
            storage = value;
            OnPropertyChanged(propertyName);
            return true;
        }

        public CinematicProject ActiveProject { get; private set; } = new CinematicProject();
        public SlideSequenceDefinition CurrentDraft { get; private set; } = new SlideSequenceDefinition();
        private (string FileName, string Snapshot) _undoSnapshot = (string.Empty, string.Empty);

        public List<string> ImagePaths { get; }
        private readonly List<string> _launchPaths = new();
        private readonly HashSet<string> _sourceFolders = new(StringComparer.OrdinalIgnoreCase);
        private static readonly HashSet<string> ImageExtensions = new(StringComparer.OrdinalIgnoreCase)
        {
            ".jpg", ".jpeg", ".png", ".bmp", ".gif", ".webp", ".heic", ".heif", ".avif"
        };
        private int _currentIndex;
        private string? _pendingNextClipId;
        private readonly HashSet<string> _failedClipIds = new();
        private readonly SemaphoreSlim _autoSaveLock = new(1, 1);

        public static string AutoSaveFilePath => Path.Combine(AppPaths.LocalDirectory, "CinematicAutoSave.json");
        public static bool CrashAutoSaveExists() => File.Exists(AutoSaveFilePath);
        public int CurrentIndex
        {
            get => _currentIndex;
            private set 
            { 
                if (SetProperty(ref _currentIndex, value))
                {
                    SyncStateForCurrentSlide();
                    RefreshActiveClip();
                }
            }
        }

        public TrackClip? CurrentClip => Track.Count > 0 && _currentIndex >= 0 && _currentIndex < Track.Count ? Track[_currentIndex] : null;
        public string CurrentClipId => CurrentClip?.Id ?? string.Empty;
        public string CurrentFileName => CurrentClip?.FileName ?? string.Empty;

        // Events that the View code-behind listens to
        public event Action? TrajectoryRefreshRequested;
        public event Action? SlideNavigationRequested;

        private bool _isPlaying = true;
        public bool IsPlaying
        {
            get => _isPlaying;
            set { if (SetProperty(ref _isPlaying, value)) OnPropertyChanged(nameof(PlayPauseIcon)); }
        }
        public Microsoft.UI.Xaml.Controls.Symbol PlayPauseIcon => IsPlaying ? Microsoft.UI.Xaml.Controls.Symbol.Stop : Microsoft.UI.Xaml.Controls.Symbol.Play;

        // Playlist Toggles
        private bool _isShuffleEnabled;
        public bool IsShuffleEnabled
        {
            get => _isShuffleEnabled;
            set => SetProperty(ref _isShuffleEnabled, value);
        }

        private bool _isLoopEnabled = true;
        public bool IsLoopEnabled
        {
            get => _isLoopEnabled;
            set => SetProperty(ref _isLoopEnabled, value);
        }

        private bool _isOverrideMode;
        public bool IsOverrideMode
        {
            get => _isOverrideMode;
            set
            {
                if (SetProperty(ref _isOverrideMode, value))
                {
                    if (value)
                    {
                        CurrentDraft.IsUserOverridden = true;
                        CurrentDraft.FileName = CurrentFileName;
                        if (!string.IsNullOrEmpty(CurrentClipId)) ActiveProject.Ledger[CurrentClipId] = CurrentDraft;
                    }
                    else
                    {
                        RemoveLedgerForCurrent();
                        SyncStateForCurrentSlide();
                        RequestRefresh();
                    }
                    OnPropertyChanged(nameof(PanelHeader));
                    OnPropertyChanged(nameof(OverrideButtonText));
                    OnPropertyChanged(nameof(RevertButtonVisibility));
                    OnPropertyChanged(nameof(CanRevertZoomPan));
                    OnPropertyChanged(nameof(CanSaveZoomPan));
                    OnPropertyChanged(nameof(SaveHint));
                    RefreshAllProperties();
                    RefreshClipStatuses();
                    _ = AutoSaveAsync();
                }
            }
        }

        private bool _hasUnsavedZoomPan;
        public bool HasUnsavedZoomPan
        {
            get => _hasUnsavedZoomPan;
            set
            {
                if (SetProperty(ref _hasUnsavedZoomPan, value))
                {
                    OnPropertyChanged(nameof(PanelHeader));
                    OnPropertyChanged(nameof(CanSaveZoomPan));
                    OnPropertyChanged(nameof(CanRevertZoomPan));
                    OnPropertyChanged(nameof(SaveHint));
                }
            }
        }

        public string PanelHeader => HasUnsavedZoomPan
            ? (IsOverrideMode
                ? "DRAFT \u2014 custom zoom/pan changed, not saved"
                : "DRAFT \u2014 you moved zoom/pan, not saved")
            : (IsOverrideMode
                ? "CUSTOM \u2014 you saved zoom/pan for this slide"
                : "AUTO \u2014 engine zoom/pan, not customized");
        public string OverrideButtonText => IsOverrideMode ? "Revert to Auto" : "Using Auto";
        public Visibility RevertButtonVisibility => Visibility.Visible;
        public bool CanSaveZoomPan => HasUnsavedZoomPan;
        public bool CanRevertZoomPan => HasUnsavedZoomPan || IsOverrideMode;
        public string SaveHint => HasUnsavedZoomPan
            ? "Not saved. The show still uses the last saved state (Auto or previous Custom) until you Save."
            : IsOverrideMode
                ? "Saved. This slide uses your zoom/pan, not the engine."
                : "Not customized. The rectangles are the engine's zoom/pan. Dragging them makes a draft \u2014 Save to keep it.";

        // TwoWay Bound Slider Properties
        public double DurationSeconds
        {
            get => IsOverrideMode ? (CurrentDraft.DurationSeconds ?? ActiveProject.GlobalDefaults.DurationSeconds) : ActiveProject.GlobalDefaults.DurationSeconds;
            set
            {
                if (Math.Abs(DurationSeconds - value) < 0.001) return;
                if (!IsOverrideMode) IsOverrideMode = true;
                CurrentDraft.DurationSeconds = value;
                OnPropertyChanged();
                OnPropertyChanged(nameof(DurationLabel));
                RequestRefresh();
                _ = AutoSaveAsync();
            }
        }

        public string DurationLabel => $"{DurationSeconds:0} s";

        public string CurveOverride
        {
            get => IsOverrideMode
                ? (CurrentDraft.CurveOverride ?? ActiveProject.GlobalDefaults.CurveOverride)
                : ActiveProject.GlobalDefaults.CurveOverride;
            set
            {
                if (string.IsNullOrEmpty(value) || CurveOverride == value) return;
                if (IsOverrideMode) CurrentDraft.CurveOverride = value; else ActiveProject.GlobalDefaults.CurveOverride = value;
                OnPropertyChanged();
                RequestRefresh();
                _ = AutoSaveAsync();
            }
        }

        public double IntensityPercent
        {
            get => IsOverrideMode ? (CurrentDraft.IntensityPercent ?? ActiveProject.GlobalDefaults.IntensityPercent) : ActiveProject.GlobalDefaults.IntensityPercent;
            set
            {
                if (Math.Abs(IntensityPercent - value) < 0.001) return;
                if (IsOverrideMode) CurrentDraft.IntensityPercent = value; else ActiveProject.GlobalDefaults.IntensityPercent = value;
                OnPropertyChanged();
                RequestRefresh();
                _ = AutoSaveAsync();
            }
        }

        public int BeatCount
        {
            get => IsOverrideMode ? (CurrentDraft.BeatCount ?? ActiveProject.GlobalDefaults.BeatCount) : ActiveProject.GlobalDefaults.BeatCount;
            set
            {
                if (BeatCount == value) return;
                if (IsOverrideMode) CurrentDraft.BeatCount = value; else ActiveProject.GlobalDefaults.BeatCount = value;
                OnPropertyChanged();
                RequestRefresh();
                _ = AutoSaveAsync();
            }
        }

        public string TechniqueOverride
        {
            get => IsOverrideMode ? (CurrentDraft.TechniqueOverride ?? ActiveProject.GlobalDefaults.TechniqueOverride) : ActiveProject.GlobalDefaults.TechniqueOverride;
            set
            {
                if (TechniqueOverride == value) return;
                if (IsOverrideMode) CurrentDraft.TechniqueOverride = value; else ActiveProject.GlobalDefaults.TechniqueOverride = value;
                OnPropertyChanged();
                RequestRefresh();
                _ = AutoSaveAsync();
            }
        }

        public string DirectionOverride
        {
            get => IsOverrideMode ? (CurrentDraft.DirectionOverride?.ToString() ?? ActiveProject.GlobalDefaults.DirectionOverride.ToString()) : ActiveProject.GlobalDefaults.DirectionOverride.ToString();
            set
            {
                if (DirectionOverride == value) return;
                int val = int.TryParse(value, out int v) ? v : 0;
                if (IsOverrideMode) CurrentDraft.DirectionOverride = val; else ActiveProject.GlobalDefaults.DirectionOverride = val;
                OnPropertyChanged();
                RequestRefresh();
                _ = AutoSaveAsync();
            }
        }

        public CinematicViewModel(List<string> imagePaths, int startIndex)
        {
            _dispatcher = DispatcherQueue.GetForCurrentThread();
            ImagePaths = imagePaths ?? new List<string>();
            _launchPaths.AddRange(ImagePaths);
            foreach (var path in ImagePaths)
            {
                Track.Add(CreateClip(path));
                var dir = Path.GetDirectoryName(path);
                if (!string.IsNullOrEmpty(dir)) _sourceFolders.Add(dir);
            }
            _currentIndex = Math.Max(0, Math.Min(startIndex, Track.Count - 1));
            RefreshActiveClip();
            SyncStateForCurrentSlide();
        }

        public void TogglePlay() => IsPlaying = !IsPlaying;

        public int PeekNextIndex()
        {
            if (_pendingNextClipId != null)
            {
                int stored = IndexOfClipId(_pendingNextClipId);
                if (stored >= 0 && !Track[stored].IsHidden && !_failedClipIds.Contains(_pendingNextClipId))
                    return stored;
                _pendingNextClipId = null;
            }

            var visible = VisiblePlayableIndices();
            if (visible.Count == 0) return CurrentIndex;
            if (visible.Count == 1) return visible[0];

            int next;
            if (IsShuffleEnabled)
            {
                next = visible[Random.Shared.Next(visible.Count)];
                if (next == CurrentIndex)
                    next = visible[(visible.IndexOf(next) + 1) % visible.Count];
            }
            else
            {
                int pos = visible.IndexOf(CurrentIndex);
                if (pos >= 0)
                {
                    if (pos + 1 < visible.Count) next = visible[pos + 1];
                    else if (IsLoopEnabled) next = visible[0];
                    else next = CurrentIndex;
                }
                else
                {
                    int after = visible.FindIndex(i => i > CurrentIndex);
                    if (after >= 0) next = visible[after];
                    else if (IsLoopEnabled) next = visible[0];
                    else next = visible[^1];
                }
            }

            if (next >= 0 && next < Track.Count)
                _pendingNextClipId = Track[next].Id;
            return next;
        }

        public void ClearPendingNextIndex() => _pendingNextClipId = null;

        public void MarkClipFailed(int index)
        {
            if (index < 0 || index >= Track.Count) return;
            _failedClipIds.Add(Track[index].Id);
            _pendingNextClipId = null;
        }

        public void MoveNext()
        {
            if (Track.Count == 0) return;
            int next = PeekNextIndex();
            if (!IsLoopEnabled && next == CurrentIndex && !IsShuffleEnabled)
                return;
            CurrentIndex = next;
            _pendingNextClipId = null;
            RaiseNav();
        }

        public void AdvanceToNextSlideSilently()
        {
            if (Track.Count == 0) return;
            int next = PeekNextIndex();
            if (!IsLoopEnabled && next == CurrentIndex && !IsShuffleEnabled)
            {
                IsPlaying = false;
                return;
            }
            CurrentIndex = next;
            _pendingNextClipId = null;
        }

        public void MovePrevious()
        {
            if (Track.Count == 0) return;
            var visible = VisibleIndices();
            if (visible.Count == 0) return;
            int pos = visible.IndexOf(CurrentIndex);
            int prev;
            if (pos > 0) prev = visible[pos - 1];
            else if (pos == 0) prev = visible[^1];
            else
            {
                int before = visible.FindLastIndex(i => i < CurrentIndex);
                prev = before >= 0 ? visible[before] : visible[^1];
            }
            CurrentIndex = prev;
            RaiseNav();
        }

        public void NavigateTo(int index)
        {
            if (index < 0 || index >= Track.Count || index == CurrentIndex) return;
            CurrentIndex = index;
            RaiseNav();
        }

        private void RequestRefresh() 
        { 
            if (!IsPlaying) 
            {
                if (_dispatcher != null && !_dispatcher.HasThreadAccess)
                    _dispatcher.TryEnqueue(() => TrajectoryRefreshRequested?.Invoke());
                else
                    TrajectoryRefreshRequested?.Invoke();
            } 
        }

        private void RefreshAllProperties()
        {
            OnPropertyChanged(nameof(DurationSeconds));
            OnPropertyChanged(nameof(DurationLabel));
            OnPropertyChanged(nameof(CurveOverride));
            OnPropertyChanged(nameof(IntensityPercent));
            OnPropertyChanged(nameof(BeatCount));
            OnPropertyChanged(nameof(TechniqueOverride));
            OnPropertyChanged(nameof(DirectionOverride));
        }

        private List<int> VisibleIndices()
        {
            var list = new List<int>();
            for (int i = 0; i < Track.Count; i++)
                if (!Track[i].IsHidden) list.Add(i);
            return list;
        }

        private List<int> VisiblePlayableIndices()
        {
            var list = new List<int>();
            for (int i = 0; i < Track.Count; i++)
            {
                if (!Track[i].IsHidden && !_failedClipIds.Contains(Track[i].Id))
                    list.Add(i);
            }
            return list;
        }

        private int IndexOfClipId(string id)
        {
            for (int i = 0; i < Track.Count; i++)
            {
                if (Track[i].Id == id) return i;
            }
            return -1;
        }

        private void RaiseNav()
        {
            if (_dispatcher != null && !_dispatcher.HasThreadAccess)
                _dispatcher.TryEnqueue(() => SlideNavigationRequested?.Invoke());
            else
                SlideNavigationRequested?.Invoke();
        }

        private void RefreshActiveClip()
        {
            for (int i = 0; i < Track.Count; i++)
                Track[i].IsActive = (i == _currentIndex);
            RefreshClipStatuses();
            OnPropertyChanged(nameof(HideButtonLabel));
        }

        public string HideButtonLabel => CurrentClip?.IsHidden == true ? "Unhide" : "Hide";

        public void RefreshClipStatuses()
        {
            foreach (var clip in Track)
            {
                clip.IsCustom = (ActiveProject.Ledger.TryGetValue(clip.Id, out var byId) && byId.IsUserOverridden)
                    || (ActiveProject.Ledger.TryGetValue(clip.FileName, out var byName) && byName.IsUserOverridden);
            }
        }

        private TrackClip CreateClip(string path, bool hidden = false)
        {
            var clip = new TrackClip { FilePath = path, IsHidden = hidden };
            try
            {
                var bmp = new BitmapImage();
                bmp.DecodePixelWidth = 160;
                bmp.UriSource = new Uri(path, UriKind.Absolute);
                clip.Thumbnail = bmp;
            }
            catch { }
            return clip;
        }

        private void RebuildImagePaths()
        {
            ImagePaths.Clear();
            foreach (var clip in Track)
                ImagePaths.Add(clip.FilePath);
        }

        private void PersistSequence()
        {
            ActiveProject.Sequence = Track.Select(c => new TrackClipRecord
            {
                Id = c.Id,
                FilePath = c.FilePath,
                IsHidden = c.IsHidden
            }).ToList();
            _ = AutoSaveAsync();
        }

        private void RemoveLedgerForCurrent()
        {
            if (!string.IsNullOrEmpty(CurrentClipId)) ActiveProject.Ledger.Remove(CurrentClipId);
            if (!string.IsNullOrEmpty(CurrentFileName)) ActiveProject.Ledger.Remove(CurrentFileName);
        }

        public void HideCurrentClip()
        {
            if (CurrentClip == null) return;
            if (VisibleIndices().Count <= 1 && !CurrentClip.IsHidden) return;
            CurrentClip.IsHidden = !CurrentClip.IsHidden;
            OnPropertyChanged(nameof(HideButtonLabel));
            PersistSequence();
            _pendingNextClipId = null;
        }

        public void RemoveCurrentClip()
        {
            if (Track.Count <= 1 || CurrentClip == null) return;
            int removeAt = CurrentIndex;
            RemoveLedgerForCurrent();
            Track.RemoveAt(removeAt);
            RebuildImagePaths();
            int next = Math.Min(removeAt, Track.Count - 1);
            var visible = VisibleIndices();
            if (visible.Count > 0 && (next < 0 || Track[next].IsHidden))
                next = visible[0];
            _currentIndex = Math.Max(0, next);
            OnPropertyChanged(nameof(CurrentIndex));
            RefreshActiveClip();
            SyncStateForCurrentSlide();
            PersistSequence();
            RaiseNav();
        }

        public void ResetToLaunchSet()
        {
            ActiveProject = new CinematicProject();
            string? keep = CurrentClip?.FilePath;
            Track.Clear();
            _failedClipIds.Clear();
            foreach (var path in _launchPaths)
            {
                if (!File.Exists(path)) continue;
                Track.Add(CreateClip(path));
            }
            RebuildImagePaths();
            _pendingNextClipId = null;
            int idx = -1;
            if (!string.IsNullOrEmpty(keep))
            {
                for (int i = 0; i < Track.Count; i++)
                    if (string.Equals(Track[i].FilePath, keep, StringComparison.OrdinalIgnoreCase)) { idx = i; break; }
            }
            _currentIndex = idx >= 0 ? idx : 0;
            OnPropertyChanged(nameof(CurrentIndex));
            RefreshActiveClip();
            SyncStateForCurrentSlide();
            PersistSequence();
            SequenceChanged?.Invoke();
            RaiseNav();
        }

        public void DuplicateCurrentClip()
        {
            if (CurrentClip == null) return;
            var src = CurrentClip;
            var copy = CreateClip(src.FilePath, src.IsHidden);
            if (src.IsCustom && ActiveProject.Ledger.TryGetValue(src.Id, out var def))
            {
                var clone = JsonSerializer.Deserialize<SlideSequenceDefinition>(JsonSerializer.Serialize(def)) ?? new SlideSequenceDefinition();
                clone.FileName = copy.FileName;
                ActiveProject.Ledger[copy.Id] = clone;
                copy.IsCustom = true;
            }
            else if (src.IsCustom && ActiveProject.Ledger.TryGetValue(src.FileName, out var byName))
            {
                var clone = JsonSerializer.Deserialize<SlideSequenceDefinition>(JsonSerializer.Serialize(byName)) ?? new SlideSequenceDefinition();
                ActiveProject.Ledger[copy.Id] = clone;
                copy.IsCustom = true;
            }
            int insertAt = Math.Min(CurrentIndex + 1, Track.Count);
            Track.Insert(insertAt, copy);
            RebuildImagePaths();
            PersistSequence();
            NavigateTo(insertAt);
        }

        public int AddFromFolder(string dir)
        {
            if (string.IsNullOrWhiteSpace(dir) || !Directory.Exists(dir)) return 0;
            _sourceFolders.Add(dir);
            var existing = new HashSet<string>(Track.Select(c => c.FilePath), StringComparer.OrdinalIgnoreCase);
            int added = 0;
            string[] files;
            try { files = Directory.GetFiles(dir); }
            catch { return 0; }
            foreach (var file in files.OrderBy(f => f, StringComparer.OrdinalIgnoreCase))
            {
                if (!ImageExtensions.Contains(Path.GetExtension(file))) continue;
                if (!existing.Add(file)) continue;
                Track.Add(CreateClip(file));
                added++;
            }
            if (added > 0)
            {
                RebuildImagePaths();
                PersistSequence();
                SequenceChanged?.Invoke();
            }
            return added;
        }

        public int AddPaths(IEnumerable<string> paths)
        {
            var existing = new HashSet<string>(Track.Select(c => c.FilePath), StringComparer.OrdinalIgnoreCase);
            int added = 0;
            int insertAt = Math.Min(CurrentIndex + 1, Track.Count);
            foreach (var path in paths)
            {
                if (string.IsNullOrWhiteSpace(path) || !File.Exists(path)) continue;
                if (!ImageExtensions.Contains(Path.GetExtension(path))) continue;
                if (!existing.Add(path)) continue;
                var dir = Path.GetDirectoryName(path);
                if (!string.IsNullOrEmpty(dir)) _sourceFolders.Add(dir);
                Track.Insert(insertAt, CreateClip(path));
                insertAt++;
                added++;
            }
            if (added > 0)
            {
                RebuildImagePaths();
                PersistSequence();
                SequenceChanged?.Invoke();
            }
            return added;
        }

        public void NotifyTrackReordered()
        {
            string id = CurrentClipId;
            RebuildImagePaths();
            if (!string.IsNullOrEmpty(id))
            {
                for (int i = 0; i < Track.Count; i++)
                {
                    if (Track[i].Id == id)
                    {
                        _currentIndex = i;
                        OnPropertyChanged(nameof(CurrentIndex));
                        break;
                    }
                }
            }
            RefreshActiveClip();
            PersistSequence();
            SequenceChanged?.Invoke();
        }

        public event Action? SequenceChanged;

        public void SyncStateForCurrentSlide()
        {
            if (string.IsNullOrEmpty(CurrentFileName) && string.IsNullOrEmpty(CurrentClipId)) return;

            SlideSequenceDefinition? savedDef = null;
            if (!string.IsNullOrEmpty(CurrentClipId) && ActiveProject.Ledger.TryGetValue(CurrentClipId, out var byId))
                savedDef = byId;
            else if (!string.IsNullOrEmpty(CurrentFileName) && ActiveProject.Ledger.TryGetValue(CurrentFileName, out var byName))
                savedDef = byName;

            if (savedDef != null)
            {
                CurrentDraft = savedDef;
                _isOverrideMode = CurrentDraft.IsUserOverridden;
            }
            else
            {
                CurrentDraft = new SlideSequenceDefinition { FileName = CurrentFileName, IsUserOverridden = false };
                _isOverrideMode = false;
            }

            _hasUnsavedZoomPan = false;
            OnPropertyChanged(nameof(IsOverrideMode));
            OnPropertyChanged(nameof(PanelHeader));
            OnPropertyChanged(nameof(OverrideButtonText));
            OnPropertyChanged(nameof(RevertButtonVisibility));
            OnPropertyChanged(nameof(HasUnsavedZoomPan));
            OnPropertyChanged(nameof(CanSaveZoomPan));
            OnPropertyChanged(nameof(CanRevertZoomPan));
            OnPropertyChanged(nameof(SaveHint));
            RefreshAllProperties();
            RefreshClipStatuses();
        }

        public void RegisterFocusTarget(NormalizedRect rect)
        {
            if (!IsOverrideMode) IsOverrideMode = true;
            CurrentDraft.FocusTargetRect = rect;
            RequestRefresh();
            _ = AutoSaveAsync();
        }

        public void SaveKeyframeCrops(NormalizedRect start, NormalizedRect end)
        {
            if (!IsOverrideMode) IsOverrideMode = true;
            CurrentDraft.StartCrop = start;
            CurrentDraft.EndCrop = end;
            HasUnsavedZoomPan = false;
            RefreshClipStatuses();
            RequestRefresh();
            _ = AutoSaveAsync();
        }

        public void MarkZoomPanDraft()
        {
            HasUnsavedZoomPan = true;
        }

        public void DiscardZoomPanDraft()
        {
            if (!HasUnsavedZoomPan) return;
            HasUnsavedZoomPan = false;
        }

        public void RevertToAuto()
        {
            CurrentDraft.StartCrop = null;
            CurrentDraft.EndCrop = null;
            CurrentDraft.FocusTargetRect = null;
            HasUnsavedZoomPan = false;
            RemoveLedgerForCurrent();
            CurrentDraft = new SlideSequenceDefinition { FileName = CurrentFileName, IsUserOverridden = false };
            _isOverrideMode = false;
            OnPropertyChanged(nameof(IsOverrideMode));
            OnPropertyChanged(nameof(PanelHeader));
            OnPropertyChanged(nameof(OverrideButtonText));
            OnPropertyChanged(nameof(CanRevertZoomPan));
            OnPropertyChanged(nameof(CanSaveZoomPan));
            OnPropertyChanged(nameof(SaveHint));
            RefreshAllProperties();
            RefreshClipStatuses();
            _ = AutoSaveAsync();
        }

        public void CaptureUndoSnapshot()
        {
            if (string.IsNullOrEmpty(CurrentFileName)) return;
            _undoSnapshot = (CurrentFileName, JsonSerializer.Serialize(CurrentDraft));
        }

        public void ExecuteUndo()
        {
            if (string.IsNullOrEmpty(CurrentFileName) || string.IsNullOrEmpty(_undoSnapshot.Snapshot)) return;
            if (_undoSnapshot.FileName != CurrentFileName) return;

            CurrentDraft = JsonSerializer.Deserialize<SlideSequenceDefinition>(_undoSnapshot.Snapshot) ?? new SlideSequenceDefinition();

            if (CurrentDraft.IsUserOverridden)
            {
                if (!string.IsNullOrEmpty(CurrentClipId)) ActiveProject.Ledger[CurrentClipId] = CurrentDraft;
            }
            else RemoveLedgerForCurrent();

            SyncStateForCurrentSlide();
            RequestRefresh();
            _ = AutoSaveAsync();
        }

        public SlideSettings GetCurrentEffectiveSettings()
        {
            if (!string.IsNullOrEmpty(CurrentClipId) && ActiveProject.Ledger.ContainsKey(CurrentClipId))
                return ActiveProject.GetEffectiveSettings(CurrentClipId);
            if (string.IsNullOrEmpty(CurrentFileName)) return new SlideSettings();
            return ActiveProject.GetEffectiveSettings(CurrentFileName);
        }

        public SlideSettings GetEffectiveSettingsFor(string fileName) => ActiveProject.GetEffectiveSettings(fileName);

        public SlideSettings GetEffectiveSettingsForIndex(int index)
        {
            if (index < 0 || index >= Track.Count) return new SlideSettings();
            var clip = Track[index];
            if (ActiveProject.Ledger.ContainsKey(clip.Id))
                return ActiveProject.GetEffectiveSettings(clip.Id);
            return ActiveProject.GetEffectiveSettings(clip.FileName);
        }

        public async Task ExportProjectAsync(StorageFile file)
        {
            SnapshotSequence();
            string json = JsonSerializer.Serialize(ActiveProject, new JsonSerializerOptions { WriteIndented = true });
            await Windows.Storage.FileIO.WriteTextAsync(file, json);
        }

        public async Task<(bool Applied, int Loaded, int Missing)> LoadProjectAsync(StorageFile file)
        {
            string json = await Windows.Storage.FileIO.ReadTextAsync(file);
            var loadedProject = JsonSerializer.Deserialize<CinematicProject>(json);
            if (loadedProject == null) return (false, 0, 0);

            int missing = CountMissing(loadedProject);
            int present = (loadedProject.Sequence?.Count ?? 0) - missing;
            if (loadedProject.Sequence != null && loadedProject.Sequence.Count > 0 && present <= 0)
                return (false, 0, missing);

            ActiveProject = loadedProject;
            ApplySequenceFromProject();
            _pendingNextClipId = null;
            _failedClipIds.Clear();
            SyncStateForCurrentSlide();
            RequestRefresh();
            SequenceChanged?.Invoke();
            RaiseNav();
            return (true, Track.Count, missing);
        }

        public async Task AutoSaveAsync()
        {
            await _autoSaveLock.WaitAsync();
            try
            {
                SnapshotSequence();
                string path = AutoSaveFilePath;
                string tmp = path + ".tmp";
                string json = JsonSerializer.Serialize(ActiveProject, new JsonSerializerOptions { WriteIndented = true });
                await File.WriteAllTextAsync(tmp, json);
                File.Copy(tmp, path, overwrite: true);
                File.Delete(tmp);
            }
            catch (Exception ex) { Debug.WriteLine($"[Cinematic] AutoSave: {ex.Message}"); }
            finally { _autoSaveLock.Release(); }
        }

        public async Task DiscardAutoSaveAsync()
        {
            await _autoSaveLock.WaitAsync();
            try
            {
                string path = AutoSaveFilePath;
                if (File.Exists(path)) File.Delete(path);
                string tmp = path + ".tmp";
                if (File.Exists(tmp)) File.Delete(tmp);
            }
            catch (Exception ex) { Debug.WriteLine($"[Cinematic] DiscardAutoSave: {ex.Message}"); }
            finally { _autoSaveLock.Release(); }
        }

        public async Task RestoreAutoSaveAsync()
        {
            try
            {
                string path = AutoSaveFilePath;
                if (!File.Exists(path)) return;
                string json = await File.ReadAllTextAsync(path);
                var loadedProject = JsonSerializer.Deserialize<CinematicProject>(json);
                if (loadedProject == null) return;
                ActiveProject = loadedProject;
                ApplySequenceFromProject();
                _pendingNextClipId = null;
                _failedClipIds.Clear();
                SyncStateForCurrentSlide();
                RequestRefresh();
                SequenceChanged?.Invoke();
                RaiseNav();
            }
            catch (Exception ex) { Debug.WriteLine($"[Cinematic] RestoreAutoSave: {ex.Message}"); }
        }

        private void SnapshotSequence()
        {
            ActiveProject.Sequence = Track.Select(c => new TrackClipRecord
            {
                Id = c.Id,
                FilePath = c.FilePath,
                IsHidden = c.IsHidden
            }).ToList();
        }

        private static int CountMissing(CinematicProject project)
        {
            if (project.Sequence == null) return 0;
            int missing = 0;
            foreach (var rec in project.Sequence)
            {
                if (string.IsNullOrWhiteSpace(rec.FilePath) || !File.Exists(rec.FilePath))
                    missing++;
            }
            return missing;
        }

        private void ApplySequenceFromProject()
        {
            if (ActiveProject.Sequence == null || ActiveProject.Sequence.Count == 0) return;
            var clips = new List<TrackClip>();
            foreach (var rec in ActiveProject.Sequence)
            {
                if (string.IsNullOrWhiteSpace(rec.FilePath) || !File.Exists(rec.FilePath)) continue;
                var clip = CreateClip(rec.FilePath, rec.IsHidden);
                if (!string.IsNullOrEmpty(rec.Id)) clip.Id = rec.Id;
                clips.Add(clip);
            }
            if (clips.Count == 0) return;
            Track.Clear();
            foreach (var clip in clips) Track.Add(clip);
            RebuildImagePaths();
            if (_currentIndex >= Track.Count) _currentIndex = Math.Max(0, Track.Count - 1);
            OnPropertyChanged(nameof(CurrentIndex));
            RefreshActiveClip();
        }
    }
}