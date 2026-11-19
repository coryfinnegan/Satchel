using System.Collections;
using System.Collections.ObjectModel;
using System.IO;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Threading;
using Satchel.Core.Models;
using Satchel.Core.Providers;
using Satchel.Core.Services;
using Satchel.Core.Storage;
using Satchel.Infrastructure;

namespace Satchel.ViewModels;

public enum AppPage
{
    MyAddons,
    GetAddons,
    Settings,
}

public enum BannerKind
{
    Info,
    Success,
    Warning,
    Error,
}

public sealed class MainViewModel : ObservableObject
{
    private readonly SettingsStore _settings;
    private readonly AddonStore _store;
    private readonly AddonManager _manager;
    private readonly Dictionary<string, AddonRowViewModel> _rowCache = new();
    private readonly DispatcherTimer _timer = new();
    private readonly CancellationTokenSource _shutdown = new();

    private WowInstallation? _selected;
    private string _filterText = "";
    private bool _updatesOnly;
    private string _sortKey = "status";
    private bool _sortDescending;
    private AppPage _page = AppPage.MyAddons;
    private int _busyDepth;
    private string _busyText = "";
    private string _bannerText = "";
    private BannerKind _bannerKind;
    private string _bannerActionText = "";
    private Action? _bannerAction;
    private DateTimeOffset? _lastChecked;
    private int _updateCount;
    private int _updatingCount;

    public MainViewModel(SettingsStore settings, AddonStore store, AddonManager manager)
    {
        _settings = settings;
        _store = store;
        _manager = manager;

        RowsView = (ListCollectionView)CollectionViewSource.GetDefaultView(Rows);
        RowsView.Filter = FilterRow;
        RowsView.CustomSort = new RowComparer(this);

        Search = new SearchViewModel(this, manager);
        SettingsPage = new SettingsViewModel(this, settings, store);

        CheckForUpdatesCommand = new AsyncCommand(() => RefreshSelectedAsync(scan: true, check: true), () => Selected != null && !IsBusy);
        RescanCommand = new AsyncCommand(() => RefreshSelectedAsync(scan: true, check: false), () => Selected != null && !IsBusy);
        UpdateAllCommand = new AsyncCommand(UpdateAllAsync, () => UpdateCount > 0 && _updatingCount == 0);
        SortCommand = new RelayCommand(p => SortBy(p as string ?? "name"));
        OpenAddonsFolderCommand = new RelayCommand(() => Shell.OpenFolder(Selected?.AddonsPath), () => Selected != null);
        NavigateCommand = new RelayCommand(p =>
        {
            if (p is AppPage page) Page = page;
            else if (p is string s && Enum.TryParse<AppPage>(s, out var parsed)) Page = parsed;
        });
        DismissBannerCommand = new RelayCommand(() => BannerText = "");
        BannerActionCommand = new RelayCommand(() =>
        {
            var action = _bannerAction;
            BannerText = "";
            action?.Invoke();
        });

        _timer.Tick += async (_, _) => await BackgroundCheckAsync();
    }

    // ---------------------------------------------------------------- bindable state

    public ObservableCollection<WowInstallation> Installations { get; } = [];

    public ObservableCollection<AddonRowViewModel> Rows { get; } = [];

    public ListCollectionView RowsView { get; }

    public SearchViewModel Search { get; }

    public SettingsViewModel SettingsPage { get; }

    /// <summary>Raised for things worth a tray notification (title, message).</summary>
    public event Action<string, string>? Notify;

    /// <summary>Set by the view: shows a confirmation dialog (title, message, confirm label) and returns the choice.</summary>
    public Func<string, string, string, bool>? Confirm { get; set; }

    public WowInstallation? Selected
    {
        get => _selected;
        set
        {
            if (ReferenceEquals(_selected, value)) return;
            _selected = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(HasInstallations));
            _settings.Settings.SelectedInstallationId = value?.Id;
            _settings.Save();
            Rows.Clear();
            _rowCache.Clear();
            SyncRows();
            Search.OnInstallationChanged();
            if (value != null && !_initializing) _ = RefreshSelectedAsync(scan: true, check: NeedsCheck(value));
        }
    }

    public bool HasInstallations => Installations.Count > 0;

    public AppPage Page
    {
        get => _page;
        set
        {
            if (!Set(ref _page, value)) return;
            if (value == AppPage.GetAddons) Search.EnsureLoaded();
            if (value == AppPage.Settings) SettingsPage.Load();
        }
    }

    public string FilterText
    {
        get => _filterText;
        set
        {
            if (Set(ref _filterText, value)) RowsView.Refresh();
        }
    }

    public bool UpdatesOnly
    {
        get => _updatesOnly;
        set
        {
            if (Set(ref _updatesOnly, value)) RowsView.Refresh();
        }
    }

    public string SortKey => _sortKey;

    public bool SortDescending => _sortDescending;

    public bool IsBusy => _busyDepth > 0;

    public string BusyText
    {
        get => _busyText;
        private set => Set(ref _busyText, value);
    }

    public int UpdateCount
    {
        get => _updateCount;
        private set
        {
            if (Set(ref _updateCount, value)) OnPropertyChanged(nameof(UpdateAllText));
        }
    }

    public string UpdateAllText => UpdateCount > 0 ? $"Update all ({UpdateCount})" : "Update all";

    public string SummaryText
    {
        get
        {
            var managed = Rows.Count(r => r.IsManaged);
            var local = Rows.Count - managed;
            var parts = new List<string> { $"{Rows.Count} addons" };
            if (local > 0) parts.Add($"{local} not tracked");
            parts.Add(UpdateCount == 1 ? "1 update" : $"{UpdateCount} updates");
            if (_lastChecked is { } c) parts.Add($"checked {AddonRowViewModel.Relative(c)}");
            return string.Join("  ·  ", parts);
        }
    }

    public string BannerText
    {
        get => _bannerText;
        private set
        {
            if (Set(ref _bannerText, value)) OnPropertyChanged(nameof(HasBanner));
        }
    }

    public bool HasBanner => BannerText.Length > 0;

    public BannerKind BannerKind
    {
        get => _bannerKind;
        private set => Set(ref _bannerKind, value);
    }

    public string BannerActionText
    {
        get => _bannerActionText;
        private set => Set(ref _bannerActionText, value);
    }

    public ICommand CheckForUpdatesCommand { get; }
    public ICommand RescanCommand { get; }
    public ICommand UpdateAllCommand { get; }
    public ICommand SortCommand { get; }
    public ICommand OpenAddonsFolderCommand { get; }
    public ICommand NavigateCommand { get; }
    public ICommand DismissBannerCommand { get; }
    public ICommand BannerActionCommand { get; }

    public CancellationToken ShutdownToken => _shutdown.Token;

    public void ShowBanner(BannerKind kind, string text, string actionText = "", Action? action = null)
    {
        BannerKind = kind;
        BannerActionText = actionText;
        _bannerAction = action;
        BannerText = text;
    }

    // ---------------------------------------------------------------- startup

    private bool _initializing;

    public async Task InitializeAsync()
    {
        _initializing = true;
        try
        {
            if (_settings.Settings.Installations.Count == 0) FirstRun();
            ReloadInstallations();
        }
        finally
        {
            _initializing = false;
        }

        ShowKeyReminderIfNeeded();
        ApplyTimer();

        if (Selected == null) return;
        await RefreshSelectedAsync(scan: true, check: _settings.Settings.CheckForUpdatesOnStartup, autoUpdate: true);

        // Bring the other game installs up to date quietly.
        if (_settings.Settings.CheckForUpdatesOnStartup)
        {
            foreach (var inst in Installations.Where(i => !ReferenceEquals(i, Selected)).ToList())
            {
                await RefreshAsync(inst, scan: true, check: true, autoUpdate: true, quiet: true);
            }
        }
    }

    private void FirstRun()
    {
        var imported = WowUpImporter.Import();
        var messages = new List<string>();
        if (imported != null)
        {
            _settings.Settings.Installations.AddRange(imported.Installations);
            foreach (var inst in imported.Installations)
            {
                _store.ReplaceForInstallation(inst.Id, imported.Addons.Where(a => a.InstallationId == inst.Id));
            }
            _store.Save();
            messages.Add($"Imported {imported.Installations.Count} game installs and {imported.Addons.Count} addons from {imported.Source}. WowUp itself was not changed.");
        }

        var known = _settings.Settings.Installations.Select(i => InstallationDetector.Normalize(i.Path)).ToHashSet();
        foreach (var found in InstallationDetector.Detect())
        {
            if (known.Add(InstallationDetector.Normalize(found.Path))) _settings.Settings.Installations.Add(found);
        }

        _settings.Settings.WowUpImportOffered = true;
        _settings.Settings.SelectedInstallationId ??= _settings.Settings.Installations.FirstOrDefault()?.Id;
        _settings.Save();

        if (_settings.Settings.Installations.Count == 0)
        {
            ShowBanner(BannerKind.Warning, "No World of Warcraft install was found. Add your game folder in Settings.", "Open Settings", () => Page = AppPage.Settings);
        }
        else if (messages.Count > 0)
        {
            _firstRunMessage = string.Join(" ", messages);
            ShowBanner(BannerKind.Success, _firstRunMessage);
        }
    }

    private string _firstRunMessage = "";

    public void ReloadInstallations()
    {
        var selectedId = _selected?.Id ?? _settings.Settings.SelectedInstallationId;
        // Sync in place: clearing would make the picker push a null selection back at us.
        var desired = _settings.Settings.Installations;
        for (var i = Installations.Count - 1; i >= 0; i--)
        {
            if (!desired.Contains(Installations[i])) Installations.RemoveAt(i);
        }
        foreach (var inst in desired)
        {
            if (!Installations.Contains(inst)) Installations.Add(inst);
        }
        CollectionViewSource.GetDefaultView(Installations).Refresh(); // picks up renamed labels
        OnPropertyChanged(nameof(HasInstallations));
        var target = Installations.FirstOrDefault(i => i.Id == selectedId) ?? Installations.FirstOrDefault();
        if (!ReferenceEquals(target, _selected))
        {
            Selected = target;
        }
        else
        {
            SyncRows();
        }
    }

    public void ShowKeyReminderIfNeeded()
    {
        var curse = _manager.Providers.Get<CurseForgeProvider>();
        if (curse == null || curse.IsAvailable) return;
        var count = _store.All().Count(a => a.IsManaged && a.ProviderName == ProviderNames.CurseForge);
        if (count == 0) return;
        var prefix = _firstRunMessage.Length > 0 ? _firstRunMessage + Environment.NewLine : "";
        ShowBanner(BannerKind.Warning,
            prefix + $"{count} of your addons come from CurseForge. Add your own free CurseForge API key to update them (other sources work without one).",
            "Add key", () => Page = AppPage.Settings);
    }

    public void ApplyTimer()
    {
        _timer.Stop();
        var hours = _settings.Settings.CheckIntervalHours;
        if (hours <= 0) return;
        _timer.Interval = TimeSpan.FromHours(hours);
        _timer.Start();
    }

    private bool NeedsCheck(WowInstallation inst) =>
        _store.ForInstallation(inst.Id).Where(a => a.IsManaged).Any(a => a.LastCheckedAt == null || DateTimeOffset.Now - a.LastCheckedAt > TimeSpan.FromMinutes(30));

    // ---------------------------------------------------------------- scanning & checking

    public Task RefreshSelectedAsync(bool scan, bool check, bool autoUpdate = false) =>
        Selected == null ? Task.CompletedTask : RefreshAsync(Selected, scan, check, autoUpdate, quiet: false);

    private async Task RefreshAsync(WowInstallation inst, bool scan, bool check, bool autoUpdate, bool quiet)
    {
        var isSelected = ReferenceEquals(inst, Selected);
        if (isSelected) BeginBusy(scan ? "Scanning addons…" : "Checking for updates…");
        try
        {
            if (scan)
            {
                var result = await _manager.ScanAsync(inst, identify: true, ShutdownToken);
                if (result.Errors.Count > 0 && !quiet) ShowBanner(BannerKind.Warning, string.Join(Environment.NewLine, result.Errors));
                if (ReferenceEquals(inst, Selected)) SyncRows();
            }

            if (check)
            {
                if (isSelected) BusyText = "Checking for updates…";
                var result = await _manager.CheckForUpdatesAsync(inst, ShutdownToken);
                if (ReferenceEquals(inst, Selected)) _lastChecked = DateTimeOffset.Now;
                if (result.Errors.Count > 0 && !quiet) ShowBanner(BannerKind.Error, string.Join("\n", result.Errors));
                if (ReferenceEquals(inst, Selected)) SyncRows();
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            Log.Error($"Refresh failed for {inst.DisplayName}", ex);
            if (!quiet) ShowBanner(BannerKind.Error, ex.Message);
        }
        finally
        {
            if (isSelected) EndBusy();
        }

        if (autoUpdate) await AutoUpdateAsync(inst);
        RefreshCounts();
    }

    private async Task BackgroundCheckAsync()
    {
        foreach (var inst in Installations.ToList())
        {
            await RefreshAsync(inst, scan: true, check: true, autoUpdate: true, quiet: true);
        }
        var pending = _store.All().Count(a => a.Status == AddonStatus.UpdateAvailable && IsProviderAvailable(a.ProviderName));
        if (pending > 0) Notify?.Invoke("Addon updates available", pending == 1 ? "1 addon can be updated." : $"{pending} addons can be updated.");
    }

    private async Task AutoUpdateAsync(WowInstallation inst)
    {
        var candidates = _store.ForInstallation(inst.Id)
            .Where(a => a.AutoUpdate && a.Status == AddonStatus.UpdateAvailable && IsProviderAvailable(a.ProviderName))
            .ToList();
        if (candidates.Count == 0) return;

        var updated = 0;
        foreach (var addon in candidates)
        {
            var row = ReferenceEquals(inst, Selected) ? _rowCache.GetValueOrDefault(addon.Id) : null;
            var ok = row != null
                ? await RunRowAsync(row, "Updating", (p, ct) => _manager.UpdateAsync(inst, row.Model, p, ct), showErrors: false)
                : await TryAsync(() => _manager.UpdateAsync(inst, addon, null, ShutdownToken));
            if (ok) updated++;
        }
        if (updated > 0) Notify?.Invoke("Addons updated", $"Auto-updated {updated} addon{(updated == 1 ? "" : "s")} in {inst.DisplayName}.");
    }

    private static async Task<bool> TryAsync(Func<Task> action)
    {
        try
        {
            await action();
            return true;
        }
        catch (Exception ex)
        {
            Log.Error("Background update failed", ex);
            return false;
        }
    }

    private void BeginBusy(string text)
    {
        _busyDepth++;
        BusyText = text;
        OnPropertyChanged(nameof(IsBusy));
        CommandManager.InvalidateRequerySuggested();
    }

    private void EndBusy()
    {
        _busyDepth = Math.Max(0, _busyDepth - 1);
        OnPropertyChanged(nameof(IsBusy));
        CommandManager.InvalidateRequerySuggested();
    }

    /// <summary>Brings the row list in line with the store without resetting scroll position.</summary>
    public void SyncRows()
    {
        if (Selected == null)
        {
            Rows.Clear();
            _rowCache.Clear();
            RefreshCounts();
            return;
        }

        var addons = _store.ForInstallation(Selected.Id);
        var ids = addons.Select(a => a.Id).ToHashSet();
        for (var i = Rows.Count - 1; i >= 0; i--)
        {
            if (!ids.Contains(Rows[i].Model.Id) && !Rows[i].IsBusy)
            {
                _rowCache.Remove(Rows[i].Model.Id);
                Rows.RemoveAt(i);
            }
        }

        var present = Rows.ToHashSet();
        foreach (var addon in addons)
        {
            if (_rowCache.TryGetValue(addon.Id, out var row))
            {
                if (!ReferenceEquals(row.Model, addon)) row.SetModel(addon);
                else row.Refresh();
                if (!present.Contains(row)) Rows.Add(row);
            }
            else
            {
                row = new AddonRowViewModel(addon, this);
                _rowCache[addon.Id] = row;
                Rows.Add(row);
            }
        }
        // Re-apply sort/filter once (statuses may have changed).
        RowsView.Refresh();
        RefreshCounts();
    }

    public void RefreshCounts()
    {
        UpdateCount = Rows.Count(r => r.State == RowState.UpdateAvailable);
        OnPropertyChanged(nameof(SummaryText));
        CommandManager.InvalidateRequerySuggested();
        CountsChanged?.Invoke();
    }

    public event Action? CountsChanged;

    public int TotalUpdateCount => _store.All().Count(a => a.Status == AddonStatus.UpdateAvailable && IsProviderAvailable(a.ProviderName));

    // ---------------------------------------------------------------- row actions

    public bool IsProviderAvailable(string providerName) => _manager.Providers.Get(providerName)?.IsAvailable ?? false;

    public string ProviderUnavailableMessage(string providerName) =>
        _manager.Providers.Get(providerName)?.UnavailableMessage ?? $"{providerName} isn't supported.";

    public void UpdateRow(AddonRowViewModel row)
    {
        if (Selected is not { } inst) return;
        _ = RunRowAsync(row, "Updating", (p, ct) => _manager.UpdateAsync(inst, row.Model, p, ct), showErrors: true);
    }

    public void ReinstallRow(AddonRowViewModel row)
    {
        if (Selected is not { } inst) return;
        _ = RunRowAsync(row, "Reinstalling", (p, ct) => _manager.ReinstallAsync(inst, row.Model, p, ct), showErrors: true);
    }

    public async void UninstallRow(AddonRowViewModel row)
    {
        if (Selected is not { } inst) return;
        var folders = string.Join(", ", row.Model.Folders);
        var ok = Confirm?.Invoke($"Remove {row.Name}?", $"These folders will be moved to the Recycle Bin:\n{folders}", "Remove") ?? false;
        if (!ok) return;
        row.IsBusy = true;
        row.BusyText = "Removing…";
        try
        {
            await _manager.UninstallAsync(inst, row.Model, ShutdownToken);
        }
        catch (Exception ex)
        {
            ShowBanner(BannerKind.Error, $"Couldn't remove {row.Name}: {ex.Message}");
        }
        finally
        {
            row.IsBusy = false;
            SyncRows();
            Search.RefreshInstalledFlags();
        }
    }

    public void SetIgnored(AddonRowViewModel row, bool ignored)
    {
        row.Model.Ignored = ignored;
        _manager.Save(row.Model);
        row.Refresh();
        RefreshCounts();
    }

    public void SetAutoUpdate(AddonRowViewModel row, bool enabled)
    {
        row.Model.AutoUpdate = enabled;
        _manager.Save(row.Model);
        row.Refresh();
        if (enabled && row.State == RowState.UpdateAvailable) UpdateRow(row);
    }

    public async void SetChannel(AddonRowViewModel row, AddonChannel channel)
    {
        if (row.Model.Channel == channel) return;
        row.Model.Channel = channel;
        _manager.Save(row.Model);
        row.Refresh();
        await RefreshSelectedAsync(scan: false, check: true);
    }

    public void OpenAddonFolder(AddonRowViewModel row)
    {
        if (Selected == null) return;
        var path = Path.Combine(Selected.AddonsPath, row.Model.PrimaryFolder);
        Shell.OpenFolder(Directory.Exists(path) ? path : Selected.AddonsPath);
    }

    private async Task UpdateAllAsync()
    {
        if (Selected is not { } inst) return;
        var rows = Rows.Where(r => r.State == RowState.UpdateAvailable).ToList();
        if (rows.Count == 0) return;

        var failures = new List<string>();
        using var gate = new SemaphoreSlim(3);
        var tasks = rows.Select(async row =>
        {
            await gate.WaitAsync();
            try
            {
                var ok = await RunRowAsync(row, "Updating", (p, ct) => _manager.UpdateAsync(inst, row.Model, p, ct), showErrors: false);
                if (!ok) failures.Add($"{row.Name}: {row.Error}");
            }
            finally
            {
                gate.Release();
            }
        });
        await Task.WhenAll(tasks);

        var done = rows.Count - failures.Count;
        if (failures.Count == 0) ShowBanner(BannerKind.Success, $"Updated {done} addon{(done == 1 ? "" : "s")}.");
        else ShowBanner(BannerKind.Error, $"Updated {done}, {failures.Count} failed:\n" + string.Join("\n", failures.Take(5)));
    }

    public Task UpdateAllFromTrayAsync() => UpdateAllAsync();

    /// <summary>Runs an install-type operation with row progress. Returns false on failure.</summary>
    public async Task<bool> RunRowAsync(AddonRowViewModel row, string verb, Func<IProgress<double>, CancellationToken, Task<Addon>> work, bool showErrors)
    {
        if (row.IsBusy) return false;
        row.IsBusy = true;
        row.Progress = 0;
        row.BusyText = verb + "…";
        row.Error = "";
        Interlocked.Increment(ref _updatingCount);
        CommandManager.InvalidateRequerySuggested();
        var progress = new Progress<double>(p =>
        {
            row.Progress = p;
            row.BusyText = p >= 0.85 ? "Installing…" : $"{verb} {(int)(p * 100)}%";
        });
        try
        {
            var addon = await work(progress, ShutdownToken);
            if (!ReferenceEquals(addon, row.Model)) row.SetModel(addon);
            return true;
        }
        catch (OperationCanceledException)
        {
            return false;
        }
        catch (Exception ex)
        {
            Log.Error($"{verb} {row.Name} failed", ex);
            row.Error = ex.Message;
            if (showErrors) ShowBanner(BannerKind.Error, $"{row.Name}: {ex.Message}");
            return false;
        }
        finally
        {
            row.IsBusy = false;
            Interlocked.Decrement(ref _updatingCount);
            row.Refresh();
            SyncRows();
            Search.RefreshInstalledFlags();
        }
    }

    // ---------------------------------------------------------------- filtering & sorting

    private bool FilterRow(object o)
    {
        if (o is not AddonRowViewModel row) return false;
        if (UpdatesOnly && row.State != RowState.UpdateAvailable) return false;
        var text = FilterText.Trim();
        return text.Length == 0 || row.SearchText.Contains(text, StringComparison.OrdinalIgnoreCase);
    }

    private void SortBy(string key)
    {
        if (_sortKey == key) _sortDescending = !_sortDescending;
        else
        {
            _sortKey = key;
            _sortDescending = key == "released";
        }
        OnPropertyChanged(nameof(SortKey));
        OnPropertyChanged(nameof(SortDescending));
        RowsView.CustomSort = new RowComparer(this);
    }

    private sealed class RowComparer(MainViewModel vm) : IComparer
    {
        public int Compare(object? x, object? y)
        {
            if (x is not AddonRowViewModel a || y is not AddonRowViewModel b) return 0;
            var result = vm._sortKey switch
            {
                "name" => 0,
                "status" => ((int)a.State).CompareTo((int)b.State),
                "provider" => string.Compare(a.ProviderName, b.ProviderName, StringComparison.OrdinalIgnoreCase),
                "released" => Nullable.Compare(a.SortDate, b.SortDate),
                "game" => string.Compare(a.GameVersion, b.GameVersion, StringComparison.OrdinalIgnoreCase),
                _ => 0,
            };
            if (vm._sortDescending) result = -result;
            return result != 0 ? result : string.Compare(a.Name, b.Name, StringComparison.CurrentCultureIgnoreCase) * (vm._sortKey == "name" && vm._sortDescending ? -1 : 1);
        }
    }

    public void Shutdown()
    {
        _timer.Stop();
        _shutdown.Cancel();
    }
}
