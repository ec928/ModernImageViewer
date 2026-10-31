using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media.Imaging;
using System;
using System.Collections.Generic;
using System.Linq;

namespace ModernImageViewer
{
    /// <summary>
    /// Undo keeps whole-session snapshots in the layout file format. After any user action the session
    /// is compared with the last snapshot, so a new feature that saves in layouts is undoable with no extra code.
    /// </summary>
    public sealed partial class MultiViewControl
    {
        private const int MaxUndoSteps = 50;

        // The last entry of _undoStack is the current state.
        private readonly List<string> _undoStack = new();
        private readonly List<string> _redoStack = new();
        private DispatcherTimer? _undoTimer;
        private bool _restoringUndo;
        private Dictionary<string, BitmapSource>? _reuseBitmaps;

        private bool CanUndo => _undoStack.Count > 1;
        private bool CanRedo => _redoStack.Count > 0;

        private void AttachUndoWatchers()
        {
            RootGrid.AddHandler(PointerReleasedEvent, new PointerEventHandler((_, _) => NoteUserAction()), true);
            RootGrid.AddHandler(PointerWheelChangedEvent, new PointerEventHandler((_, _) => NoteUserAction()), true);
            RootGrid.AddHandler(KeyUpEvent, new KeyEventHandler((_, _) => NoteUserAction()), true);
            AttachFileDrop();
            AttachCanvasClip();
        }

        private void NoteUserAction()
        {
            if (_undoTimer == null)
            {
                // Long enough to treat a burst of wheel steps as one change.
                _undoTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(250) };
                _undoTimer.Tick += (_, _) => CaptureUndoPoint();
            }
            _undoTimer.Stop();
            _undoTimer.Start();
        }

        private string UndoSnapshot()
        {
            var file = BuildLayoutFile();
            file.Created = default;
            file.Canvas.Aspect = null;

            // Floating cells as shares of the canvas, so resizing the window is not a change to undo.
            double width = file.Canvas.Width ?? 0;
            double height = file.Canvas.Height ?? 0;
            if (width >= 1 && height >= 1)
            {
                foreach (var cell in file.Freeform ?? new List<FreeformCellDto>())
                {
                    cell.X = Math.Round(cell.X / width, 6);
                    cell.Y = Math.Round(cell.Y / height, 6);
                    cell.Width = Math.Round(cell.Width / width, 6);
                    cell.Height = Math.Round(cell.Height / height, 6);
                }
                file.Canvas.Width = 1;
                file.Canvas.Height = 1;
            }
            return file.ToJson();
        }

        private void CaptureUndoPoint()
        {
            _undoTimer?.Stop();
            if (_restoringUndo || _items.Count == 0 || _undoStack.Count == 0) return;
            string now = UndoSnapshot();
            if (now == _undoStack[^1]) return;
            _undoStack.Add(now);
            if (_undoStack.Count > MaxUndoSteps + 1) _undoStack.RemoveAt(0);
            _redoStack.Clear();
        }

        /// <summary>
        /// A session can open before the canvas has its size, and settling into it is not a change.
        /// While nothing has been done yet, the starting point follows the canvas.
        /// </summary>
        private void RebaseUntouchedUndo()
        {
            if (_restoringUndo || _undoStack.Count != 1 || _redoStack.Count != 0) return;
            _undoStack[0] = UndoSnapshot();
        }

        private void ResetUndo()
        {
            _undoTimer?.Stop();
            _undoStack.Clear();
            _redoStack.Clear();
            if (_items.Count > 0) _undoStack.Add(UndoSnapshot());
        }

        private void ClearUndo()
        {
            _undoTimer?.Stop();
            _undoStack.Clear();
            _redoStack.Clear();
        }

        private void Undo()
        {
            CaptureUndoPoint();
            if (!CanUndo) return;
            _redoStack.Add(_undoStack[^1]);
            _undoStack.RemoveAt(_undoStack.Count - 1);
            RestoreSnapshot(_undoStack[^1]);
        }

        private void Redo()
        {
            CaptureUndoPoint();
            if (!CanRedo) return;
            string next = _redoStack[^1];
            _redoStack.RemoveAt(_redoStack.Count - 1);
            _undoStack.Add(next);
            RestoreSnapshot(next);
        }

        private const int AutoSaveCount = 3;

        private static string AutoSavePath(int age) =>
            System.IO.Path.Combine(AppPaths.LocalDirectory, "autosave", $"autosave-{age + 1}.mivlayout");

        /// <summary>
        /// Keeps the last three sessions, so work that gets replaced or left behind can be had back from Save/Load.
        /// Taken whenever a session is left or replaced, and when the app closes. Blank sessions and repeats are skipped.
        /// </summary>
        public void AutoSaveSession()
        {
            try
            {
                if (_items.Count == 0 || _items.All(IsEmptyCell)) return;
                string json = UndoSnapshot();
                string last = AutoSavePath(0);
                if (System.IO.File.Exists(last) && System.IO.File.ReadAllText(last) == json) return;
                System.IO.Directory.CreateDirectory(System.IO.Path.GetDirectoryName(last)!);
                for (int age = AutoSaveCount - 1; age > 0; age--)
                {
                    if (System.IO.File.Exists(AutoSavePath(age - 1)))
                        System.IO.File.Copy(AutoSavePath(age - 1), AutoSavePath(age), true);
                }
                AtomicFile.WriteAllText(last, json);
            }
            catch (Exception ex) when (ex is System.IO.IOException or UnauthorizedAccessException or System.Runtime.InteropServices.COMException)
            {
                // Autosave is a safety net. It never gets in the way of what the user is doing.
            }
        }

        private void RestoreSnapshot(string json)
        {
            var file = MultiViewLayoutFile.FromJson(json);
            if (file == null) return;

            // The open card edits a frame object the restore is about to replace.
            _cellBorderFlyout?.Hide();

            // The rebuilt cells take the images already decoded, so an undo does not flash or decode again.
            var ready = new Dictionary<string, BitmapSource>(StringComparer.OrdinalIgnoreCase);
            foreach (var root in _cellRoots)
            {
                if (root?.Tag is CellParts parts && CellImageReady(parts) && parts.Photo.Source is BitmapSource bitmap
                    && !string.IsNullOrEmpty(parts.Bound.Item.Path))
                {
                    ready[parts.Bound.Item.Path] = bitmap;
                }
            }

            _restoringUndo = true;
            _reuseBitmaps = ready;
            try
            {
                ApplyLayoutFile(file, restoreAspect: false);
            }
            finally
            {
                _reuseBitmaps = null;
                _restoringUndo = false;
            }
        }
    }
}
