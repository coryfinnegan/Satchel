using System.Collections.ObjectModel;
using System.Reflection;
using System.Windows.Input;
using Microsoft.Win32;
using Satchel.Core.Models;
using Satchel.Core.Providers;
using Satchel.Core.Services;
using Satchel.Core.Storage;
using Satchel.Infrastructure;

namespace Satchel.ViewModels;

public sealed class InstallationRowViewModel : ObservableObject
{
    private readonly SettingsViewModel _owner;

    public InstallationRowViewModel(WowInstallation model, SettingsViewModel owner, int addonCount)
    {
        Model = model;
        _owner = owner;
        AddonCount = addonCount;
        RemoveCommand = new RelayCommand(() => _owner.Remove(this));
        OpenFolderCommand = new RelayCommand(() => Shell.OpenFolder(Model.AddonsPath));
    }

    public WowInstallation Model { get; }

    public string ClientName => WowClients.DisplayName(Model.ClientType);

    public string Path => Model.Path;

    public int AddonCount { get; }

    public string Label
    {
        get => Model.Label;
        set
        {
            if (Model.Label == value) return;
            Model.Label = value.Trim();
            OnPropertyChanged();
            _owner.SaveInstallations();
        }
    }

    public AddonChannel DefaultChannel
    {
        get => Model.DefaultChannel;
        set
        {
            if (Model.DefaultChannel == value) return;
            Model.DefaultChannel = value;
            OnPropertyChanged();
            _owner.SaveInstallations();
        }
    }

    public bool DefaultAutoUpdate
    {
        get => Model.DefaultAutoUpdate;
        set
        {
            if (Model.DefaultAutoUpdate == value) return;
            Model.DefaultAutoUpdate = value;
            OnPropertyChanged();
            _owner.SaveInstallations();
        }
    }

    public ICommand RemoveCommand { get; }

    public ICommand OpenFolderCommand { get; }
}

public sealed record IntervalOption(int Hours, string Label);

public sealed class SettingsViewModel : ObservableObject
{
    private readonly MainViewModel _main;
    private readonly SettingsStore _settings;
    private readonly AddonStore _store;
    private string _curseKeyStatus = "";
    private string _gitHubStatus = "";

    public SettingsViewModel(MainViewModel main, SettingsStore settings, AddonStore store)
    {
        _main = main;
        _settings = settings;
        _store = store;
        AddInstallationCommand = new RelayCommand(AddInstallation);
        DetectCommand = new RelayCommand(Detect);
        ImportWowUpCommand = new RelayCommand(ImportWowUp, WowUpImporter.IsAvailable);
        TestCurseForgeKeyCommand = new AsyncCommand(TestCurseForgeKeyAsync, () => HasCurseForgeKey);
        RemoveCurseForgeKeyCommand = new RelayCommand(() => SaveCurseForgeKey(""), () => HasCurseForgeKey);
        RemoveGitHubTokenCommand = new RelayCommand(() => SaveGitHubToken(""), () => HasGitHubToken);
        ReplaceCurseKeyCommand = new RelayCommand(() => SetEditing(curse: true, editing: true));
        CancelCurseKeyCommand = new RelayCommand(() => SetEditing(curse: true, editing: false));
        ReplaceGitHubTokenCommand = new RelayCommand(() => SetEditing(curse: false, editing: true));
        CancelGitHubTokenCommand = new RelayCommand(() => SetEditing(curse: false, editing: false));
        GetCurseForgeKeyCommand = new RelayCommand(() => Shell.OpenUrl("https://console.curseforge.com/"));
        OpenDataFolderCommand = new RelayCommand(() => Shell.OpenFolder(AppPaths.DataDir));
        OpenLogsCommand = new RelayCommand(() => Shell.OpenFolder(AppPaths.LogsDir));
    }

    public ObservableCollection<InstallationRowViewModel> Installations { get; } = [];

    public IReadOnlyList<AddonChannel> Channels { get; } = [AddonChannel.Stable, AddonChannel.Beta, AddonChannel.Alpha];

    public IReadOnlyList<IntervalOption> Intervals { get; } =
    [
        new(0, "Off"), new(1, "Every hour"), new(3, "Every 3 hours"), new(6, "Every 6 hours"), new(12, "Every 12 hours"), new(24, "Once a day"),
    ];

    public void Load()
    {
        Installations.Clear();
        foreach (var inst in _settings.Settings.Installations)
        {
            Installations.Add(new InstallationRowViewModel(inst, this, _store.ForInstallation(inst.Id).Count));
        }
        _editingCurseKey = false;
        _editingGitHubToken = false;
        CurseKeyStatus = "";
        GitHubStatus = "";
        OnAllPropertiesChanged();
    }

    // ---------------------------------------------------------------- keys

    public bool HasCurseForgeKey => !string.IsNullOrWhiteSpace(_settings.CurseForgeApiKey);

    public bool HasGitHubToken => !string.IsNullOrWhiteSpace(_settings.GitHubToken);

    private bool _editingCurseKey;
    private bool _editingGitHubToken;

    /// <summary>A saved key is shown as "Key saved" (never the key itself); the entry box appears only to add or replace one.</summary>
    public bool ShowCurseKeySaved => HasCurseForgeKey && !_editingCurseKey;

    public bool ShowCurseKeyEditor => !ShowCurseKeySaved;

    public bool ShowGitHubTokenSaved => HasGitHubToken && !_editingGitHubToken;

    public bool ShowGitHubTokenEditor => !ShowGitHubTokenSaved;

    private void SetEditing(bool curse, bool editing)
    {
        if (curse) _editingCurseKey = editing;
        else _editingGitHubToken = editing;
        OnPropertyChanged(nameof(ShowCurseKeySaved));
        OnPropertyChanged(nameof(ShowCurseKeyEditor));
        OnPropertyChanged(nameof(ShowGitHubTokenSaved));
        OnPropertyChanged(nameof(ShowGitHubTokenEditor));
    }

    public string CurseKeyStatus
    {
        get => _curseKeyStatus;
        private set => Set(ref _curseKeyStatus, value);
    }

    public string GitHubStatus
    {
        get => _gitHubStatus;
        private set => Set(ref _gitHubStatus, value);
    }

    public void SaveCurseForgeKey(string key)
    {
        _settings.CurseForgeApiKey = key;
        _settings.Save();
        OnPropertyChanged(nameof(HasCurseForgeKey));
        SetEditing(curse: true, editing: false);
        CurseKeyStatus = HasCurseForgeKey ? "Saved. Testing…" : "Key removed.";
        CommandManager.InvalidateRequerySuggested();
        _main.SyncRows();
        if (HasCurseForgeKey)
        {
            TestCurseForgeKeyCommand.Execute(null);
        }
    }

    public void SaveGitHubToken(string token)
    {
        _settings.GitHubToken = token;
        _settings.Save();
        OnPropertyChanged(nameof(HasGitHubToken));
        SetEditing(curse: false, editing: false);
        GitHubStatus = HasGitHubToken ? "Token saved." : "Token removed.";
        CommandManager.InvalidateRequerySuggested();
    }

    private async Task TestCurseForgeKeyAsync()
    {
        var inst = _main.Selected ?? new WowInstallation { ClientType = WowClientType.Retail };
        CurseKeyStatus = "Testing…";
        try
        {
            var provider = new CurseForgeProvider(() => _settings.CurseForgeApiKey);
            var results = await provider.SearchAsync("details", inst, _main.ShutdownToken);
            CurseKeyStatus = $"Key works ✓ — CurseForge is enabled.";
            await _main.RefreshSelectedAsync(scan: true, check: true);
        }
        catch (Exception ex)
        {
            CurseKeyStatus = "That key didn't work: " + ex.Message;
        }
    }

    // ---------------------------------------------------------------- installations

    public void SaveInstallations()
    {
        _settings.Save();
        _main.ReloadInstallations();
    }

    public void Remove(InstallationRowViewModel row)
    {
        var ok = _main.Confirm?.Invoke($"Stop managing {row.Model.DisplayName}?",
            "Satchel will forget this game install. Your addons stay on disk; you can add the folder back later.", "Stop managing") ?? false;
        if (!ok) return;
        _settings.Settings.Installations.Remove(row.Model);
        _store.RemoveInstallation(row.Model.Id);
        _store.Save();
        SaveInstallations();
        Load();
    }

    private void AddInstallation()
    {
        var dialog = new OpenFolderDialog
        {
            Title = "Select your World of Warcraft folder (or a _retail_ / _classic_ folder inside it)",
        };
        if (dialog.ShowDialog() != true) return;
        var added = AddFound(InstallationDetector.FromRoot(dialog.FolderName));
        if (added == 0) _main.ShowBanner(BannerKind.Warning, "No World of Warcraft client was found in that folder (looked for _retail_, _classic_, _classic_era_, …).");
    }

    private void Detect()
    {
        var added = AddFound(InstallationDetector.Detect());
        _main.ShowBanner(BannerKind.Info, added == 0 ? "No new game installs found." : $"Added {added} game install{(added == 1 ? "" : "s")}.");
    }

    private int AddFound(IEnumerable<WowInstallation> found)
    {
        var known = _settings.Settings.Installations.Select(i => InstallationDetector.Normalize(i.Path)).ToHashSet();
        var added = 0;
        foreach (var inst in found)
        {
            if (!known.Add(InstallationDetector.Normalize(inst.Path))) continue;
            _settings.Settings.Installations.Add(inst);
            added++;
        }
        if (added > 0)
        {
            SaveInstallations();
            Load();
        }
        return added;
    }

    private void ImportWowUp()
    {
        var result = WowUpImporter.Import();
        if (result == null)
        {
            _main.ShowBanner(BannerKind.Warning, "No WowUp data was found.");
            return;
        }

        var byPath = _settings.Settings.Installations.ToDictionary(i => InstallationDetector.Normalize(i.Path));
        var addedInstalls = 0;
        var addedAddons = 0;
        foreach (var inst in result.Installations)
        {
            var key = InstallationDetector.Normalize(inst.Path);
            if (!byPath.TryGetValue(key, out var target))
            {
                _settings.Settings.Installations.Add(inst);
                byPath[key] = target = inst;
                addedInstalls++;
            }

            var existing = _store.ForInstallation(target.Id).Where(a => a.IsManaged).ToList();
            foreach (var addon in result.Addons.Where(a => a.InstallationId == inst.Id))
            {
                if (existing.Any(e => e.ProviderName == addon.ProviderName && e.ExternalId == addon.ExternalId)) continue;
                addon.InstallationId = target.Id;
                _store.Upsert(addon);
                addedAddons++;
            }
        }
        _store.Save();
        SaveInstallations();
        Load();
        _main.ShowBanner(BannerKind.Success, $"Imported {addedInstalls} game installs and {addedAddons} addons from {result.Source}.");
        _ = _main.RefreshSelectedAsync(scan: true, check: true);
    }

    // ---------------------------------------------------------------- behavior

    public bool CheckForUpdatesOnStartup
    {
        get => _settings.Settings.CheckForUpdatesOnStartup;
        set
        {
            _settings.Settings.CheckForUpdatesOnStartup = value;
            _settings.Save();
            OnPropertyChanged();
        }
    }

    public IntervalOption SelectedInterval
    {
        get => Intervals.FirstOrDefault(i => i.Hours == _settings.Settings.CheckIntervalHours) ?? Intervals[2];
        set
        {
            if (value == null) return;
            _settings.Settings.CheckIntervalHours = value.Hours;
            _settings.Save();
            _main.ApplyTimer();
            OnPropertyChanged();
        }
    }

    public bool MinimizeToTray
    {
        get => _settings.Settings.MinimizeToTray;
        set
        {
            _settings.Settings.MinimizeToTray = value;
            _settings.Save();
            OnPropertyChanged();
        }
    }

    public bool StartWithWindows
    {
        get => _settings.Settings.StartWithWindows;
        set
        {
            _settings.Settings.StartWithWindows = value;
            _settings.Save();
            StartupRegistration.Apply(value);
            OnPropertyChanged();
        }
    }

    public bool InstallRequiredDependencies
    {
        get => _settings.Settings.InstallRequiredDependencies;
        set
        {
            _settings.Settings.InstallRequiredDependencies = value;
            _settings.Save();
            OnPropertyChanged();
        }
    }

    public bool IdentifyUnknownAddons
    {
        get => _settings.Settings.IdentifyUnknownAddons;
        set
        {
            _settings.Settings.IdentifyUnknownAddons = value;
            _settings.Save();
            OnPropertyChanged();
        }
    }

    public string Version => "Version " + (Assembly.GetExecutingAssembly().GetName().Version?.ToString(3) ?? "1.0.0");

    public string DataFolder => AppPaths.DataDir;

    public ICommand AddInstallationCommand { get; }
    public ICommand DetectCommand { get; }
    public ICommand ImportWowUpCommand { get; }
    public ICommand TestCurseForgeKeyCommand { get; }
    public ICommand RemoveCurseForgeKeyCommand { get; }
    public ICommand RemoveGitHubTokenCommand { get; }
    public ICommand GetCurseForgeKeyCommand { get; }
    public ICommand ReplaceCurseKeyCommand { get; }
    public ICommand CancelCurseKeyCommand { get; }
    public ICommand ReplaceGitHubTokenCommand { get; }
    public ICommand CancelGitHubTokenCommand { get; }
    public ICommand OpenDataFolderCommand { get; }
    public ICommand OpenLogsCommand { get; }
}
