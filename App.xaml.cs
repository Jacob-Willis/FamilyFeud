namespace FamilyFeud
{
    public partial class App : Application
    {
        private Window? _mainWindow;
        private bool _isQuitting;

        public App()
        {
            InitializeComponent();
        }

        protected override Window CreateWindow(IActivationState? activationState)
        {
            Window window = new Window(new MainPage()) { Title = "FamilyFeud" };
            // Only the first (host) window ends the app when it closes; the Audience window
            // is opened separately via OpenWindow and closing it must leave the app running.
            _mainWindow ??= window;
            if (ReferenceEquals(window, _mainWindow))
            {
                window.Destroying += OnMainWindowDestroying;
            }
            return window;
        }

        private void OnMainWindowDestroying(object? sender, EventArgs e)
        {
#if WINDOWS || MACCATALYST
            if (_isQuitting) return;
            _isQuitting = true;

            // Close any other open windows (e.g. the Audience window) and then exit the app,
            // otherwise the app keeps running with only the Audience window left.
            List<Window> others = Windows.Where(w => !ReferenceEquals(w, sender)).ToList();
            foreach (Window other in others)
            {
                try { CloseWindow(other); } catch { }
            }

            try { Quit(); } catch { }
#endif
        }
    }
}
