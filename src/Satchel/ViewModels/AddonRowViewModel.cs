using System.Windows.Input;
using Satchel.Core.Models;
using Satchel.Infrastructure;

namespace Satchel.ViewModels;

public enum RowState
{
    UpdateAvailable,
    Busy,
    UpToDate,
    NeedsKey,
    Unavailable,
    Ignored,
    Local,
}

public sealed class AddonRowViewModel : ObservableObject
{
    private readonly MainViewModel _owner;
    private bool _isBusy;
    private double _progress;
    private string _busyText = "";
    private string _error = "";

    public AddonRowViewModel(Addon model, MainViewModel owner)
    {
        Model = model;
        _owner = owner;
        UpdateCommand = new RelayCommand(() => _owner.UpdateRow(this), () => !IsBusy && State == RowState.UpdateAvailable);
        ReinstallCommand = new RelayCommand(() => _owner.ReinstallRow(this), () => !IsBusy && IsManaged && State != RowState.NeedsKey);
        UninstallCommand = new RelayCommand(() => _owner.UninstallRow(this), () => !IsBusy);
        ToggleIgnoreCommand = new RelayCommand(() => _owner.SetIgnored(this, !Model.Ignored), () => IsManaged);
        ToggleAutoUpdateCommand = new RelayCommand(() => _owner.SetAutoUpdate(this, !Model.AutoUpdate), () => IsManaged);
        SetStableCommand = new RelayCommand(() => _owner.SetChannel(this, AddonChannel.Stable), () => IsManaged);
        SetBetaCommand = new RelayCommand(() => _owner.SetChannel(this, AddonChannel.Beta), () => IsManaged);
        SetAlphaCommand = new RelayCommand(() => _owner.SetChannel(this, AddonChannel.Alpha), () => IsManaged);
        OpenWebsiteCommand = new RelayCommand(() => Shell.OpenUrl(Model.WebsiteUrl), () => Model.WebsiteUrl.Length > 0);
        OpenFolderCommand = new RelayCommand(() => _owner.OpenAddonFolder(this));
    }

    public Addon Model { get; private set; }

    public void SetModel(Addon model)
    {
        Model = model;
        Refresh();
    }

    public void Refresh() => OnAllPropertiesChanged();

    public bool IsManaged => Model.IsManaged;

    public string Name => string.IsNullOrWhiteSpace(Model.Name) ? Model.PrimaryFolder : Model.Name;

    public string Initial
    {
        get
        {
            var t = Name.TrimStart('!', '_', '<', '[', ' ', '|', '"');
            return t.Length > 0 ? char.ToUpperInvariant(t[0]).ToString() : "?";
        }
    }

    public string ThumbnailUrl => Model.ThumbnailUrl;

    public string Subtitle
    {
        get
        {
            var parts = new List<string>();
            if (Model.Author.Length > 0) parts.Add(Model.Author);
            if (Model.Folders.Count > 1) parts.Add($"{Model.Folders.Count} folders");
            else if (!IsManaged && Model.Folders.Count == 1 && !Model.PrimaryFolder.Equals(Name, StringComparison.OrdinalIgnoreCase)) parts.Add(Model.PrimaryFolder);
            return string.Join("  ·  ", parts);
        }
    }

    public string Tooltip
    {
        get
        {
            var lines = new List<string>();
            if (Model.Summary.Length > 0) lines.Add(Model.Summary);
            lines.Add("Folders: " + string.Join(", ", Model.Folders));
            if (Model.InstalledAt is { } at) lines.Add($"Installed {at.LocalDateTime:g}");
            return string.Join("\n", lines);
        }
    }

    public string InstalledVersion => string.IsNullOrWhiteSpace(Model.InstalledVersion) ? "—" : Model.InstalledVersion;

    public string LatestVersion => Model.LatestVersion;

    public bool ShowLatest => State == RowState.UpdateAvailable && Model.LatestVersion.Length > 0 && Model.LatestVersion != Model.InstalledVersion;

    public string GameVersion => Model.GameVersion;

    public string ProviderName => IsManaged ? Model.ProviderName : "Local";

    public string ChannelBadge => IsManaged && Model.Channel != AddonChannel.Stable ? Model.Channel.ToString() : "";

    public bool IsStable => Model.Channel == AddonChannel.Stable;

    public bool IsBeta => Model.Channel == AddonChannel.Beta;

    public bool IsAlpha => Model.Channel == AddonChannel.Alpha;

    public bool AutoUpdate => Model.AutoUpdate;

    public bool Ignored => Model.Ignored;

    public DateTimeOffset? SortDate => Model.LatestReleasedAt ?? Model.InstalledAt;

    public string ReleasedText => SortDate is { } d ? Relative(d) : "";

    public string ReleasedTooltip => SortDate is { } d ? d.LocalDateTime.ToString("f") : "";

    public bool IsBusy
    {
        get => _isBusy;
        set
        {
            if (Set(ref _isBusy, value))
            {
                OnPropertyChanged(nameof(State));
                OnPropertyChanged(nameof(StatusText));
            }
        }
    }

    public double Progress
    {
        get => _progress;
        set => Set(ref _progress, value);
    }

    public string BusyText
    {
        get => _busyText;
        set => Set(ref _busyText, value);
    }

    /// <summary>Last operation error for this row, shown as a tooltip.</summary>
    public string Error
    {
        get => _error;
        set
        {
            if (Set(ref _error, value)) OnPropertyChanged(nameof(StatusTooltip));
        }
    }

    public RowState State
    {
        get
        {
            if (IsBusy) return RowState.Busy;
            if (!IsManaged) return RowState.Local;
            if (Model.Ignored) return RowState.Ignored;
            if (!_owner.IsProviderAvailable(Model.ProviderName)) return RowState.NeedsKey;
            if (!string.IsNullOrEmpty(Model.UnavailableReason)) return RowState.Unavailable;
            return Model.HasUpdate ? RowState.UpdateAvailable : RowState.UpToDate;
        }
    }

    public string StatusText => State switch
    {
        RowState.Busy => BusyText,
        RowState.UpdateAvailable => "Update",
        RowState.UpToDate => "Up to date",
        RowState.NeedsKey => "Needs API key",
        RowState.Unavailable => "Unavailable",
        RowState.Ignored => "Ignored",
        _ => "Not tracked",
    };

    public string StatusTooltip
    {
        get
        {
            if (Error.Length > 0) return Error;
            return State switch
            {
                RowState.NeedsKey => _owner.ProviderUnavailableMessage(Model.ProviderName)
                                     + (Model.HasUpdate ? $"{Environment.NewLine}An update is waiting: {Model.InstalledVersion} → {Model.LatestVersion}" : ""),
                RowState.Unavailable => Model.UnavailableReason,
                RowState.Local => "Installed manually and not matched to any source. Use Get Addons to install a tracked copy.",
                RowState.UpdateAvailable => $"{Model.InstalledVersion} → {Model.LatestVersion}",
                RowState.Ignored => "Updates are ignored for this addon.",
                _ => Model.LastCheckedAt is { } c ? $"Checked {Relative(c)}" : "",
            };
        }
    }

    /// <summary>Lower-case text used by the filter box.</summary>
    public string SearchText => $"{Name} {Model.Author} {string.Join(' ', Model.Folders)} {ProviderName}".ToLowerInvariant();

    public ICommand UpdateCommand { get; }
    public ICommand ReinstallCommand { get; }
    public ICommand UninstallCommand { get; }
    public ICommand ToggleIgnoreCommand { get; }
    public ICommand ToggleAutoUpdateCommand { get; }
    public ICommand SetStableCommand { get; }
    public ICommand SetBetaCommand { get; }
    public ICommand SetAlphaCommand { get; }
    public ICommand OpenWebsiteCommand { get; }
    public ICommand OpenFolderCommand { get; }

    public static string Relative(DateTimeOffset when)
    {
        var span = DateTimeOffset.Now - when;
        if (span.TotalSeconds < 0) return "just now";
        if (span.TotalMinutes < 1) return "just now";
        if (span.TotalHours < 1) return $"{(int)span.TotalMinutes}m ago";
        if (span.TotalDays < 1) return $"{(int)span.TotalHours}h ago";
        if (span.TotalDays < 30) return $"{(int)span.TotalDays}d ago";
        if (span.TotalDays < 365) return $"{(int)(span.TotalDays / 30)}mo ago";
        return $"{(int)(span.TotalDays / 365)}y ago";
    }
}
