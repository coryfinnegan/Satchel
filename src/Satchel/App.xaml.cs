using System.Windows;
using System.Windows.Threading;
using Satchel.Core.Providers;
using Satchel.Core.Services;
using Satchel.Core.Storage;
using Satchel.Infrastructure;
using Satchel.ViewModels;

namespace Satchel;

public partial class App : Application
{
    private const string InstanceName = "Satchel.AddonManager.SingleInstance";
    private Mutex? _instanceMutex;
    private EventWaitHandle? _showSignal;
    private RegisteredWaitHandle? _showWait;
    private TrayIcon? _tray;
    private MainWindow? _window;
    private MainViewModel? _vm;
    private bool _toldAboutTray;

    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        // SATCHEL_HOME relocates all data (portable use, or a sandbox for testing).
        var home = Environment.GetEnvironmentVariable("SATCHEL_HOME");
        var instanceSuffix = Environment.UserName;
        if (!string.IsNullOrWhiteSpace(home))
        {
            AppPaths.DataDir = System.IO.Path.Combine(home, "data");
            AppPaths.LocalDir = System.IO.Path.Combine(home, "local");
            instanceSuffix += "." + Convert.ToHexString(System.Security.Cryptography.SHA1.HashData(System.Text.Encoding.UTF8.GetBytes(home.ToLowerInvariant())))[..12];
        }

        // One copy at a time: a second launch just brings the first window forward.
        _instanceMutex = new Mutex(true, InstanceName + "." + instanceSuffix, out var isFirst);
        _showSignal = new EventWaitHandle(false, EventResetMode.AutoReset, InstanceName + ".Show." + instanceSuffix);
        if (!isFirst)
        {
            _showSignal.Set();
            Shutdown();
            return;
        }

        DispatcherUnhandledException += OnUnhandledException;
        AsyncCommand.OnError = ex =>
        {
            Log.Error("Command failed", ex);
            _vm?.ShowBanner(BannerKind.Error, ex.Message);
        };

        var settings = new SettingsStore(new DpapiProtector());
        settings.Load();
        var store = new AddonStore();
        store.Load();

        var providers = new ProviderRegistry(
        [
            new CurseForgeProvider(() => settings.CurseForgeApiKey),
            new WowInterfaceProvider(AppPaths.CacheDir),
            new TukuiProvider(),
            new GitHubProvider(() => settings.GitHubToken),
        ]);
        var manager = new AddonManager(store, settings, providers);

        _vm = new MainViewModel(settings, store, manager);
        _window = new MainWindow(_vm, settings);
        _vm.Confirm = (title, message, confirm) => ConfirmDialog.Ask(_window, title, message, confirm);

        _tray = new TrayIcon(
            show: () => Dispatcher.Invoke(() => _window.ShowAndActivate()),
            checkNow: () => Dispatcher.Invoke(() => _vm.CheckForUpdatesCommand.Execute(null)),
            updateAll: () => Dispatcher.Invoke(() => _ = _vm.UpdateAllFromTrayAsync()),
            exit: () => Dispatcher.Invoke(ExitApp));
        _vm.Notify += (title, message) =>
        {
            if (_window is { IsVisible: true, IsActive: true }) return;
            _tray.Notify(title, message);
        };
        _vm.CountsChanged += () =>
        {
            var n = _vm.TotalUpdateCount;
            _tray.SetTooltip(n > 0 ? $"Satchel — {n} update{(n == 1 ? "" : "s")} available" : "Satchel — all addons up to date");
        };
        _window.HiddenToTray += () =>
        {
            if (_toldAboutTray) return;
            _toldAboutTray = true;
            _tray.Notify("Satchel is still running", "It keeps your addons up to date from the tray. Right-click the icon to exit.");
        };

        _window.Closed += (_, _) => Shutdown();
        _showWait = ThreadPool.RegisterWaitForSingleObject(_showSignal, (_, _) => Dispatcher.Invoke(() => _window.ShowAndActivate()), null, -1, false);

        var startMinimized = e.Args.Any(a => a.Equals("--minimized", StringComparison.OrdinalIgnoreCase));
        if (!startMinimized) _window.Show();

        await _vm.InitializeAsync();
    }

    private void ExitApp()
    {
        if (_window != null)
        {
            _window.AllowClose = true;
            _window.Close();
        }
        Shutdown();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _vm?.Shutdown();
        _showWait?.Unregister(null);
        _tray?.Dispose();
        _showSignal?.Dispose();
        if (_instanceMutex != null)
        {
            try
            {
                _instanceMutex.ReleaseMutex();
            }
            catch (ApplicationException) { }
            _instanceMutex.Dispose();
        }
        base.OnExit(e);
    }

    private void OnUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        Log.Error("Unhandled UI exception", e.Exception);
        _vm?.ShowBanner(BannerKind.Error, "Something went wrong: " + e.Exception.Message);
        e.Handled = true;
    }
}
