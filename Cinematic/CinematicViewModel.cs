using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Threading.Tasks;
using Microsoft.UI.Xaml;
using Windows.Storage;
using ModernImageViewer.Cinematic.Data;
using Microsoft.UI.Dispatching;
using System.Collections.ObjectModel;

namespace ModernImageViewer.Cinematic.ViewModels
{
    public class SlideIndicatorItem : INotifyPropertyChanged
    {
        public event PropertyChangedEventHandler? PropertyChanged;
        public int Index { get; set; }
        private bool _isActive;
        public bool IsActive 
        { 
            get => _isActive; 
            set 
            { 
                if (_isActive != value) 
                { 
                    _isActive = value; 
                    PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsActive))); 
                    PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Opacity))); 
                } 
            } 
        }
        public double Opacity => IsActive ? 1.0 : 0.3;
    }

    public class CinematicViewModel : INotifyPropertyChanged
    {
        private readonly DispatcherQueue _dispatcher;
        public ObservableCollection<SlideIndicatorItem> SlideIndicators { get; } = new();
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
        private int _currentIndex;
        public int CurrentIndex
        {
            get => _currentIndex;
            private set 
            { 
                if (SetProperty(ref _currentIndex, value))
                {
                    SyncStateForCurrentSlide();
                    for (int i = 0; i < SlideIndicators.Count; i++)
                        SlideIndicators[i].IsActive = (i == _currentIndex);
                }
            }
        }

        public string CurrentFileName => ImagePaths.Count > 0 ? Path.GetFileName(ImagePaths[_currentIndex]) : string.Empty;

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
                        if (!string.IsNullOrEmpty(CurrentFileName)) ActiveProject.Ledger[CurrentFileName] = CurrentDraft;
                    }
                    else
                    {
                        if (!string.IsNullOrEmpty(CurrentFileName)) ActiveProject.Ledger.Remove(CurrentFileName);
                        SyncStateForCurrentSlide();
                        RequestRefresh();
                    }
                    OnPropertyChanged(nameof(PanelHeader));
                    OnPropertyChanged(nameof(OverrideButtonText));
                    RefreshAllProperties();
                    _ = AutoSaveAsync();
                }
            }
        }

        public string PanelHeader => IsOverrideMode ? "Slide Override" : "Global Defaults";
        public string OverrideButtonText => IsOverrideMode ? "Revert to Global Auto" : "Customize This Slide";

        // TwoWay Bound Slider Properties
        public double DurationSeconds
        {
            get => IsOverrideMode ? (CurrentDraft.DurationSeconds ?? ActiveProject.GlobalDefaults.DurationSeconds) : ActiveProject.GlobalDefaults.DurationSeconds;
            set
            {
                if (Math.Abs(DurationSeconds - value) < 0.001) return;
                if (IsOverrideMode) CurrentDraft.DurationSeconds = value; else ActiveProject.GlobalDefaults.DurationSeconds = value;
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
                if (!IsOverrideMode) IsOverrideMode = true;
                CurrentDraft.BeatCount = value;
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
            for (int i = 0; i < ImagePaths.Count; i++)
                SlideIndicators.Add(new SlideIndicatorItem { Index = i });
            _currentIndex = Math.Max(0, Math.Min(startIndex, ImagePaths.Count - 1));
            if (SlideIndicators.Count > 0)
                SlideIndicators[_currentIndex].IsActive = true;
            SyncStateForCurrentSlide();
        }

        public void TogglePlay() => IsPlaying = !IsPlaying;

        public void MoveNext()
        {
            if (ImagePaths.Count == 0) return;

            if (IsShuffleEnabled)
            {
                CurrentIndex = Random.Shared.Next(ImagePaths.Count);
            }
            else
            {
                if (!IsLoopEnabled && CurrentIndex >= ImagePaths.Count - 1) return;
                CurrentIndex = (CurrentIndex + 1) % ImagePaths.Count;
            }

            if (_dispatcher != null && !_dispatcher.HasThreadAccess)
                _dispatcher.TryEnqueue(() => SlideNavigationRequested?.Invoke());
            else
                SlideNavigationRequested?.Invoke();
        }

        public void AdvanceToNextSlideSilently()
        {
            if (ImagePaths.Count == 0) return;

            if (IsShuffleEnabled)
            {
                CurrentIndex = Random.Shared.Next(ImagePaths.Count);
            }
            else
            {
                if (!IsLoopEnabled && CurrentIndex >= ImagePaths.Count - 1)
                {
                    IsPlaying = false;
                    return;
                }
                CurrentIndex = (CurrentIndex + 1) % ImagePaths.Count;
            }
        }

        public void MovePrevious()
        {
            if (ImagePaths.Count == 0) return;
            CurrentIndex = (CurrentIndex - 1 >= 0) ? CurrentIndex - 1 : ImagePaths.Count - 1;
            
            if (_dispatcher != null && !_dispatcher.HasThreadAccess)
                _dispatcher.TryEnqueue(() => SlideNavigationRequested?.Invoke());
            else
                SlideNavigationRequested?.Invoke();
        }

        public void NavigateTo(int index)
        {
            if (index >= 0 && index < ImagePaths.Count)
            {
                CurrentIndex = index;
                if (_dispatcher != null && !_dispatcher.HasThreadAccess)
                    _dispatcher.TryEnqueue(() => SlideNavigationRequested?.Invoke());
                else
                    SlideNavigationRequested?.Invoke();
            }
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
            OnPropertyChanged(nameof(IntensityPercent));
            OnPropertyChanged(nameof(BeatCount));
            OnPropertyChanged(nameof(TechniqueOverride));
            OnPropertyChanged(nameof(DirectionOverride));
        }

        public void SyncStateForCurrentSlide()
        {
            if (string.IsNullOrEmpty(CurrentFileName)) return;

            if (ActiveProject.Ledger.TryGetValue(CurrentFileName, out var savedDef))
            {
                CurrentDraft = savedDef;
                _isOverrideMode = CurrentDraft.IsUserOverridden;
            }
            else
            {
                CurrentDraft = new SlideSequenceDefinition { FileName = CurrentFileName, IsUserOverridden = false };
                _isOverrideMode = false;
            }

            OnPropertyChanged(nameof(IsOverrideMode));
            OnPropertyChanged(nameof(PanelHeader));
            OnPropertyChanged(nameof(OverrideButtonText));
            RefreshAllProperties();
        }

        public void RegisterFocusTarget(NormalizedRect rect)
        {
            if (!IsOverrideMode) IsOverrideMode = true;
            CurrentDraft.FocusTargetRect = rect;
            RequestRefresh();
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

            if (CurrentDraft.IsUserOverridden) ActiveProject.Ledger[CurrentFileName] = CurrentDraft;
            else ActiveProject.Ledger.Remove(CurrentFileName);

            SyncStateForCurrentSlide();
            RequestRefresh();
            _ = AutoSaveAsync();
        }

        public SlideSettings GetCurrentEffectiveSettings()
        {
            if (string.IsNullOrEmpty(CurrentFileName)) return new SlideSettings();
            return ActiveProject.GetEffectiveSettings(CurrentFileName);
        }

        public SlideSettings GetEffectiveSettingsFor(string fileName) => ActiveProject.GetEffectiveSettings(fileName);

        public async Task ExportProjectAsync(StorageFile file)
        {
            string json = JsonSerializer.Serialize(ActiveProject, new JsonSerializerOptions { WriteIndented = true });
            await Windows.Storage.FileIO.WriteTextAsync(file, json);
        }

        public async Task LoadProjectAsync(StorageFile file)
        {
            string json = await Windows.Storage.FileIO.ReadTextAsync(file);
            var loadedProject = JsonSerializer.Deserialize<CinematicProject>(json);
            if (loadedProject != null)
            {
                ActiveProject = loadedProject;
                SyncStateForCurrentSlide();
                RequestRefresh();
            }
        }

        public async Task AutoSaveAsync()
        {
            try
            {
                var folder = ApplicationData.Current.LocalFolder;
                var file = await folder.CreateFileAsync("CinematicAutoSave.json", CreationCollisionOption.ReplaceExisting);
                string json = JsonSerializer.Serialize(ActiveProject, new JsonSerializerOptions { WriteIndented = true });
                await Windows.Storage.FileIO.WriteTextAsync(file, json);
            }
            catch { }
        }

        public async Task RestoreAutoSaveAsync()
        {
            try
            {
                var folder = ApplicationData.Current.LocalFolder;
                var file = await folder.GetFileAsync("CinematicAutoSave.json");
                string json = await Windows.Storage.FileIO.ReadTextAsync(file);
                var loadedProject = JsonSerializer.Deserialize<CinematicProject>(json);
                if (loadedProject != null)
                {
                    ActiveProject = loadedProject;
                    SyncStateForCurrentSlide();
                    RequestRefresh();
                }
            }
            catch { }
        }
    }
}