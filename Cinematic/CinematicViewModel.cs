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
            ".jpg", ".jpeg", ".png", ".bmp", ".gif", ".tif", ".tiff", ".webp", ".heic", ".heif", ".avif"
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
        public Microsoft.UI.Xaml.Controls.Symbol PlayPauseIcon => IsPlaying ? Microsoft.UI.Xaml.Controls.Symbol.Pause : Microsoft.UI.Xaml.Controls.Symbol.Play;

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
                    OnPropertyChanged(nameof(SlideBadge));
                    OnPropertyChanged(nameof(CanRevertZoomPan));
                    OnPropertyChanged(nameof(CanSaveZoomPan));
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
                    OnPropertyChanged(nameof(SlideBadge));
                    OnPropertyChanged(nameof(CanSaveZoomPan));
                    OnPropertyChanged(nameof(CanRevertZoomPan));
                }
            }
        }

        public string SlideBadge => HasUnsavedZoomPan ? "Not saved" : IsOverrideMode ? "Custom" : "Auto";
        public bool CanSaveZoomPan => HasUnsavedZoomPan;
        public bool CanRevertZoomPan => HasUnsavedZoomPan || IsOverrideMode;

        // Where the slider rests while the slide is on Auto. Auto rolls 5 to 20 seconds per slide.
        private const double AutoSliderSeconds = 12.0;

        public double DurationSeconds
        {
            get => CurrentDraft.DurationSeconds ?? AutoSliderSeconds;
            set
            {
                if (Math.Abs(DurationSeconds - value) < 0.001) return;
                if (!IsOverrideMode) IsOverrideMode = true;
                CurrentDraft.DurationSeconds = value;
                OnPropertyChanged();
                OnPropertyChanged(nameof(DurationLabel));
                OnPropertyChanged(nameof(SlideDurationIsSet));
                RequestRefresh();
                _ = AutoSaveAsync();
            }
        }

        public string DurationLabel => CurrentDraft.DurationSeconds != null ? $"{DurationSeconds:0} s" : "Auto";
        public bool SlideDurationIsSet => CurrentDraft.DurationSeconds != null;

        public void UseAutoDuration()
        {
            CurrentDraft.DurationSeconds = null;
            ForgetSlideIfEmpty();
            RefreshAllProperties();
            SlideMotionChanged?.Invoke();
            _ = AutoSaveAsync();
        }

        public const string SameAsAllSlides = "same";

        /// <summary>This slide's move. It follows all slides until one is chosen.</summary>
        public string SlideMove
        {
            get => CurrentDraft.DirectionOverride?.ToString() ?? SameAsAllSlides;
            set
            {
                if (string.IsNullOrEmpty(value) || SlideMove == value) return;
                if (value == SameAsAllSlides)
                {
                    CurrentDraft.DirectionOverride = null;
                    ForgetSlideIfEmpty();
                }
                else
                {
                    if (!IsOverrideMode) IsOverrideMode = true;
                    CurrentDraft.DirectionOverride = int.TryParse(value, out int move) ? move : 0;
                }
                OnPropertyChanged();
                SlideMotionChanged?.Invoke();
                _ = AutoSaveAsync();
            }
        }

        public string AllMove
        {
            get => ActiveProject.GlobalDefaults.DirectionOverride.ToString();
            set
            {
                if (string.IsNullOrEmpty(value) || AllMove == value) return;
                ActiveProject.GlobalDefaults.DirectionOverride = int.TryParse(value, out int move) ? move : 0;
                OnPropertyChanged();
                SlideMotionChanged?.Invoke();
                _ = AutoSaveAsync();
            }
        }

        public double Movement
        {
            get => ActiveProject.GlobalDefaults.IntensityPercent;
            set
            {
                if (Math.Abs(Movement - value) < 0.001) return;
                ActiveProject.GlobalDefaults.IntensityPercent = value;
                OnPropertyChanged();
                SlideMotionChanged?.Invoke();
                _ = AutoSaveAsync();
            }
        }

        /// <summary>A slide with nothing of its own left goes back to Auto, so its badge stays true.</summary>
        private void ForgetSlideIfEmpty()
        {
            if (IsOverrideMode && !CurrentDraft.HasAnything && !HasUnsavedZoomPan) RevertToAuto();
        }

        // Music
        public string? MusicPath => ActiveProject.MusicPath;
        public string? MusicFolder => ActiveProject.MusicFolder;
        public double MusicLowestStars => ActiveProject.MusicLowestStars;
        public bool MusicIncludesUnrated => ActiveProject.MusicIncludesUnrated;
        public bool IsMusicFolder => !string.IsNullOrEmpty(ActiveProject.MusicFolder);
        public bool MusicIsOff => ActiveProject.MusicOff;
        private bool _musicOn;
        private bool _musicFromFolder;
        public Visibility NextSongVisibility => _musicOn && _musicFromFolder ? Visibility.Visible : Visibility.Collapsed;

        public void SetMusicState(bool on, bool fromFolder)
        {
            _musicOn = on;
            _musicFromFolder = fromFolder;
            OnPropertyChanged(nameof(MusicVolumeVisibility));
            OnPropertyChanged(nameof(NextSongVisibility));
            OnPropertyChanged(nameof(MusicLabel));
        }
        public Visibility MusicVolumeVisibility => _musicOn ? Visibility.Visible : Visibility.Collapsed;
        public string MusicLabel => _musicOn ? "Music: " + (_nowPlaying ?? "looking for songs") : "Music";

        private string? _nowPlaying;
        public void SetNowPlaying(string? song)
        {
            _nowPlaying = song;
            OnPropertyChanged(nameof(MusicLabel));
        }

        public double MusicVolume
        {
            get => ActiveProject.MusicVolume;
            set
            {
                if (Math.Abs(MusicVolume - value) < 0.001) return;
                ActiveProject.MusicVolume = Math.Clamp(value, 0, 100);
                OnPropertyChanged();
                MusicVolumeChanged?.Invoke();
                _ = AutoSaveAsync();
            }
        }

        public void SetMusic(string? path)
        {
            ActiveProject.MusicPath = path;
            ActiveProject.MusicFolder = null;
            ActiveProject.MusicOff = path == null;
            RaiseMusic();
            _ = AutoSaveAsync();
        }

        public void SetMusicFolder(string folder, double lowestStars, bool includeUnrated)
        {
            ActiveProject.MusicPath = null;
            ActiveProject.MusicOff = false;
            ActiveProject.MusicFolder = folder;
            ActiveProject.MusicLowestStars = lowestStars;
            ActiveProject.MusicIncludesUnrated = includeUnrated;
            RaiseMusic();
            _ = AutoSaveAsync();
        }

        public void StartAtVolume(double percent)
        {
            ActiveProject.MusicVolume = percent;
            OnPropertyChanged(nameof(MusicVolume));
        }

        public void UseMyMusic()
        {
            ActiveProject.MusicPath = null;
            ActiveProject.MusicFolder = null;
            ActiveProject.MusicOff = false;
            RaiseMusic();
            _ = AutoSaveAsync();
        }

        private void RaiseMusic()
        {
            OnPropertyChanged(nameof(MusicVolume));
            MusicChanged?.Invoke();
        }

        public event Action? SlideMotionChanged;
        public event Action? MusicChanged;
        public event Action? MusicVolumeChanged;

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
            OnPropertyChanged(nameof(SlideDurationIsSet));
            OnPropertyChanged(nameof(SlideMove));
            OnPropertyChanged(nameof(AllMove));
            OnPropertyChanged(nameof(Movement));
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
            OnPropertyChanged(nameof(CurrentFileName));
            OnPropertyChanged(nameof(CanHideOrRemove));
        }

        public string HideButtonLabel => CurrentClip?.IsHidden == true ? "Bring back" : "Hide in this show";

        // The last picture showing cannot go: a show needs something to play.
        public bool CanHideOrRemove => CurrentClip?.IsHidden == true || VisibleIndices().Count > 1;

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
            OnPropertyChanged(nameof(CanHideOrRemove));
            PersistSequence();
            _pendingNextClipId = null;
        }

        public void RemoveCurrentClip()
        {
            if (Track.Count <= 1 || CurrentClip == null || !CanHideOrRemove) return;
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
            var was = ActiveProject;
            ActiveProject = new CinematicProject
            {
                MusicPath = was.MusicPath,
                MusicVolume = was.MusicVolume,
                MusicFolder = was.MusicFolder,
                MusicLowestStars = was.MusicLowestStars,
                MusicIncludesUnrated = was.MusicIncludesUnrated,
                MusicOff = was.MusicOff
            };
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
            RefreshAllProperties();
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
                RefreshActiveClip();
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
                RefreshActiveClip();
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
            OnPropertyChanged(nameof(SlideBadge));
            OnPropertyChanged(nameof(HasUnsavedZoomPan));
            OnPropertyChanged(nameof(CanSaveZoomPan));
            OnPropertyChanged(nameof(CanRevertZoomPan));
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
            OnPropertyChanged(nameof(SlideBadge));
            OnPropertyChanged(nameof(CanRevertZoomPan));
            OnPropertyChanged(nameof(CanSaveZoomPan));
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

        /// <summary>Reads a saved show. Null if the file is not one. Older shows keep their pictures, order and framing; settings that no longer exist are dropped.</summary>
        public static (CinematicProject? Show, int Missing) ReadShow(string json)
        {
            CinematicProject? show;
            try { show = JsonSerializer.Deserialize<CinematicProject>(json); }
            catch (JsonException) { return (null, 0); }
            if (show?.Sequence == null || show.Sequence.Count == 0) return (null, 0);
            int missing = CountMissing(show);
            return missing >= show.Sequence.Count ? (null, missing) : (show, missing);
        }

        public static List<string> PathsOnDisk(CinematicProject show) =>
            show.Sequence.Where(r => !string.IsNullOrWhiteSpace(r.FilePath) && File.Exists(r.FilePath)).Select(r => r.FilePath).ToList();

        public void ApplyShow(CinematicProject show)
        {
            show.DropEmptySlides();
            ActiveProject = show;
            MusicWasMissing = (!string.IsNullOrEmpty(show.MusicPath) && !File.Exists(show.MusicPath))
                || (!string.IsNullOrEmpty(show.MusicFolder) && !Directory.Exists(show.MusicFolder));
            if (MusicWasMissing) { show.MusicPath = null; show.MusicFolder = null; }
            _currentIndex = 0;
            ApplySequenceFromProject();
            var visible = VisibleIndices();
            if (visible.Count > 0 && !visible.Contains(_currentIndex)) _currentIndex = visible[0];
            OnPropertyChanged(nameof(CurrentIndex));
            RefreshActiveClip();
            _pendingNextClipId = null;
            _failedClipIds.Clear();
            SyncStateForCurrentSlide();
            RefreshAllProperties();
            RaiseMusic();
            RequestRefresh();
            SequenceChanged?.Invoke();
            RaiseNav();
        }

        public bool MusicWasMissing { get; private set; }

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
                ApplyShow(loadedProject);
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