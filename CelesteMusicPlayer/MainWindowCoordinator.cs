namespace CelesteMusicPlayer
{
    internal interface IMainWindowSurface
    {
        bool Exists { get; }
        bool IsMinimized { get; }
        void Create();
        void Restore();
        void Show();
        void Activate();
        void HideLegacyOwner();
        void Navigate(string page);
        void Close();
    }

    // Window policy only; platform handles and dispatching remain in the adapter.
    internal sealed class MainWindowCoordinator(IMainWindowSurface surface)
    {
        private bool _showing;
        private string? _pendingPage;
        internal bool IsClosing { get; private set; }

        internal void Show(string? page = null)
        {
            if (IsClosing) return;
            if (!string.IsNullOrEmpty(page)) _pendingPage = page;
            if (_showing) return;
            _showing = true;
            try
            {
                if (!surface.Exists) surface.Create();
                if (IsClosing) return;
                if (surface.IsMinimized) surface.Restore();
                surface.Show();
                surface.Activate();
                surface.HideLegacyOwner();
                while (_pendingPage != null && !IsClosing)
                {
                    string next = _pendingPage;
                    _pendingPage = null;
                    surface.Navigate(next);
                }
            }
            finally { _showing = false; }
        }

        internal void Close()
        {
            if (IsClosing) return;
            IsClosing = true;
            _pendingPage = null;
            if (surface.Exists) surface.Close();
        }
    }
}
