using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;

namespace CelesteMusicPlayer
{
    public sealed class LibraryWatchService : IDisposable
    {
        private static readonly string[] AudioExtensions =
        {
            ".mp3", ".wav", ".m4a", ".flac", ".wma", ".ogg", ".aac"
        };

        private readonly List<FileSystemWatcher> _watchers = new();
        private readonly HashSet<string> _pendingPaths = new(StringComparer.OrdinalIgnoreCase);
        private readonly object _gate = new();
        private System.Threading.Timer? _debounceTimer;
        private bool _disposed;

        public event Action<IReadOnlyList<string>>? Changed;

        public void Start(IEnumerable<string> folders)
        {
            Stop();

            foreach (string folder in folders.Where(f => !string.IsNullOrWhiteSpace(f)).Distinct(StringComparer.OrdinalIgnoreCase))
            {
                if (!Directory.Exists(folder))
                {
                    continue;
                }

                var watcher = new FileSystemWatcher(folder)
                {
                    IncludeSubdirectories = true,
                    NotifyFilter = NotifyFilters.FileName | NotifyFilters.LastWrite | NotifyFilters.CreationTime
                };

                watcher.Created += Watcher_OnChange;
                watcher.Changed += Watcher_OnChange;
                watcher.Deleted += Watcher_OnChange;
                watcher.Renamed += Watcher_OnRenamed;
                watcher.EnableRaisingEvents = true;
                _watchers.Add(watcher);
            }
        }

        public void Stop()
        {
            foreach (FileSystemWatcher watcher in _watchers)
            {
                watcher.EnableRaisingEvents = false;
                watcher.Created -= Watcher_OnChange;
                watcher.Changed -= Watcher_OnChange;
                watcher.Deleted -= Watcher_OnChange;
                watcher.Renamed -= Watcher_OnRenamed;
                watcher.Dispose();
            }

            _watchers.Clear();
            _debounceTimer?.Dispose();
            _debounceTimer = null;

            lock (_gate)
            {
                _pendingPaths.Clear();
            }
        }

        private void Watcher_OnChange(object sender, FileSystemEventArgs e)
        {
            QueuePath(e.FullPath);
        }

        private void Watcher_OnRenamed(object sender, RenamedEventArgs e)
        {
            QueuePath(e.FullPath);
            if (!string.IsNullOrWhiteSpace(e.OldFullPath))
            {
                QueuePath(e.OldFullPath);
            }
        }

        private void QueuePath(string path)
        {
            if (string.IsNullOrWhiteSpace(path))
            {
                return;
            }

            string ext = Path.GetExtension(path);
            if (!AudioExtensions.Contains(ext, StringComparer.OrdinalIgnoreCase))
            {
                return;
            }

            lock (_gate)
            {
                _pendingPaths.Add(Path.GetFullPath(path));
            }

            _debounceTimer ??= new System.Threading.Timer(_ => FlushPending(), null, Timeout.Infinite, Timeout.Infinite);
            _debounceTimer.Change(500, Timeout.Infinite);
        }

        private void FlushPending()
        {
            string[] paths;
            lock (_gate)
            {
                paths = _pendingPaths.ToArray();
                _pendingPaths.Clear();
            }

            if (paths.Length == 0)
            {
                return;
            }

            try
            {
                Changed?.Invoke(paths);
            }
            catch
            {
            }
        }

        public void Dispose()
        {
            if (_disposed)
            {
                return;
            }

            Stop();
            _disposed = true;
            GC.SuppressFinalize(this);
        }
    }
}
