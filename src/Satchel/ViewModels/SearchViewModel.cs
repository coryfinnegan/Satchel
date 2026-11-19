using System.Collections.ObjectModel;
using System.Windows.Input;
using Satchel.Core.Models;
using Satchel.Core.Providers;
using Satchel.Core.Services;
using Satchel.Core.Storage;
using Satchel.Infrastructure;

namespace Satchel.ViewModels;

public sealed class SearchRowViewModel : ObservableObject
{
    private readonly SearchViewModel _owner;
    private bool _isInstalled;
    private bool _isBusy;
    private double _progress;
    private string _busyText = "";

    public SearchRowViewModel(RemoteAddon remote, SearchViewModel owner)
    {
        Remote = remote;
        _owner = owner;
        InstallCommand = new RelayCommand(() => _owner.Install(this), () => !IsBusy && !IsInstalled && Remote.UnavailableReason.Length == 0);
        OpenWebsiteCommand = new RelayCommand(() => Shell.OpenUrl(Remote.WebsiteUrl), () => Remote.WebsiteUrl.Length > 0);
    }

    public RemoteAddon Remote { get; }

    public string Name => Remote.Name;

    public string Initial => Remote.Name.Length > 0 ? char.ToUpperInvariant(Remote.Name[0]).ToString() : "?";

    public string Author => Remote.Author;

    public string Summary => Remote.Summary;

    public string ThumbnailUrl => Remote.ThumbnailUrl;

    public string ProviderName => Remote.ProviderName;

    public string Downloads => Remote.DownloadCount switch
    {
        <= 0 => "",
        >= 1_000_000 => $"{Remote.DownloadCount / 1_000_000d:0.#}M downloads",
        >= 1_000 => $"{Remote.DownloadCount / 1_000d:0.#}K downloads",
        _ => $"{Remote.DownloadCount} downloads",
    };

    public string Latest
    {
        get
        {
            var f = Remote.PickFile(AddonChannel.Stable) ?? Remote.Files.FirstOrDefault();
            if (f == null) return "";
            var when = f.ReleasedAt == default ? "" : "  ·  " + AddonRowViewModel.Relative(f.ReleasedAt);
            return f.Version + when;
        }
    }

    public string UnavailableReason => Remote.UnavailableReason;

    public bool IsInstalled
    {
        get => _isInstalled;
        set
        {
            if (Set(ref _isInstalled, value)) OnPropertyChanged(nameof(ButtonText));
        }
    }

    public bool IsBusy
    {
        get => _isBusy;
        set
        {
            if (Set(ref _isBusy, value)) OnPropertyChanged(nameof(ButtonText));
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
        set
        {
            if (Set(ref _busyText, value)) OnPropertyChanged(nameof(ButtonText));
        }
    }

    public string ButtonText => IsBusy ? BusyText : IsInstalled ? "Installed" : Remote.UnavailableReason.Length > 0 ? "Unavailable" : "Install";

    public ICommand InstallCommand { get; }

    public ICommand OpenWebsiteCommand { get; }
}

public sealed class SearchViewModel : ObservableObject
{
    public const string AllSources = "All sources";

    private readonly MainViewModel _main;
    private readonly AddonManager _manager;
    private string _query = "";
    private string _source = AllSources;
    private string _url = "";
    private bool _isLoading;
    private string _emptyText = "";
    private bool _loaded;
    private CancellationTokenSource? _searchCts;

    public SearchViewModel(MainViewModel main, AddonManager manager)
    {
        _main = main;
        _manager = manager;
        SearchCommand = new AsyncCommand(() => RunSearchAsync(Query));
        InstallFromUrlCommand = new AsyncCommand(InstallFromUrlAsync, () => Url.Trim().Length > 0);
        ClearCommand = new AsyncCommand(() =>
        {
            Query = "";
            return RunSearchAsync("");
        });
    }

    public ObservableCollection<SearchRowViewModel> Results { get; } = [];

    public IReadOnlyList<string> Sources =>
        [AllSources, .. _manager.Providers.All.Where(p => p.SupportsSearch).Select(p => p.Name)];

    public string Query
    {
        get => _query;
        set => Set(ref _query, value);
    }

    public string Source
    {
        get => _source;
        set
        {
            if (Set(ref _source, value)) _ = RunSearchAsync(Query);
        }
    }

    public string Url
    {
        get => _url;
        set => Set(ref _url, value);
    }

    public bool IsLoading
    {
        get => _isLoading;
        private set => Set(ref _isLoading, value);
    }

    public string EmptyText
    {
        get => _emptyText;
        private set => Set(ref _emptyText, value);
    }

    public string Heading => Query.Trim().Length == 0 ? "Popular addons" : $"Results for “{Query.Trim()}”";

    public ICommand SearchCommand { get; }

    public ICommand InstallFromUrlCommand { get; }

    public ICommand ClearCommand { get; }

    public void EnsureLoaded()
    {
        if (_loaded) return;
        _loaded = true;
        OnPropertyChanged(nameof(Sources));
        _ = RunSearchAsync("");
    }

    public void OnInstallationChanged()
    {
        if (!_loaded) return;
        _ = RunSearchAsync(Query);
    }

    public void RefreshInstalledFlags()
    {
        if (_main.Selected is not { } inst) return;
        foreach (var r in Results)
        {
            r.IsInstalled = _manager.FindTracked(inst, r.Remote.ProviderName, r.Remote.ExternalId) != null;
        }
    }

    private async Task RunSearchAsync(string query)
    {
        if (_main.Selected is not { } inst) return;
        _searchCts?.Cancel();
        var cts = _searchCts = CancellationTokenSource.CreateLinkedTokenSource(_main.ShutdownToken);
        OnPropertyChanged(nameof(Heading));
        IsLoading = true;
        EmptyText = "";

        var providers = _manager.Providers.All
            .Where(p => p.SupportsSearch && p.IsAvailable && (Source == AllSources || p.Name == Source))
            .ToList();
        var errors = new List<string>();
        IReadOnlyList<RemoteAddon>[] batches;
        try
        {
            batches = await Task.WhenAll(providers.Select(async p =>
            {
                try
                {
                    return string.IsNullOrWhiteSpace(query)
                        ? await p.GetPopularAsync(inst, cts.Token)
                        : await p.SearchAsync(query, inst, cts.Token);
                }
                catch (Exception ex) when (!cts.IsCancellationRequested)
                {
                    lock (errors) errors.Add($"{p.Name}: {ex.Message}");
                    Log.Error($"Search failed on {p.Name}", ex);
                    return (IReadOnlyList<RemoteAddon>)[];
                }
            }));
        }
        catch (OperationCanceledException)
        {
            return;
        }
        if (cts.IsCancellationRequested) return;

        var q = query.Trim();
        IEnumerable<RemoteAddon> merged;
        if (q.Length == 0)
        {
            // Popular: interleave sources so one doesn't drown out the others.
            merged = Interleave(batches);
        }
        else
        {
            merged = batches.SelectMany(b => b)
                .OrderBy(r => r.Name.Equals(q, StringComparison.OrdinalIgnoreCase) ? 0
                    : r.Name.StartsWith(q, StringComparison.OrdinalIgnoreCase) ? 1
                    : r.Name.Contains(q, StringComparison.OrdinalIgnoreCase) ? 2 : 3)
                .ThenByDescending(r => r.DownloadCount);
        }

        Results.Clear();
        foreach (var r in merged.Take(100))
        {
            Results.Add(new SearchRowViewModel(r, this)
            {
                IsInstalled = _manager.FindTracked(inst, r.ProviderName, r.ExternalId) != null,
            });
        }
        IsLoading = false;

        var unavailable = _manager.Providers.All.Where(p => p.SupportsSearch && !p.IsAvailable).Select(p => p.Name).ToList();
        if (Results.Count == 0)
        {
            EmptyText = errors.Count > 0 ? string.Join("\n", errors) : q.Length > 0 ? $"Nothing found for “{q}”." : "Nothing to show yet.";
        }
        else if (errors.Count > 0)
        {
            _main.ShowBanner(BannerKind.Warning, string.Join("\n", errors));
        }
        if (unavailable.Count > 0 && Source == AllSources)
        {
            EmptyText = (EmptyText.Length > 0 ? EmptyText + "\n" : "") + $"{string.Join(", ", unavailable)} results need an API key (Settings).";
        }
    }

    private static IEnumerable<RemoteAddon> Interleave(IReadOnlyList<RemoteAddon>[] batches)
    {
        var max = batches.Length == 0 ? 0 : batches.Max(b => b.Count);
        for (var i = 0; i < max; i++)
        {
            foreach (var b in batches)
            {
                if (i < b.Count) yield return b[i];
            }
        }
    }

    public async void Install(SearchRowViewModel row)
    {
        if (_main.Selected is not { } inst || row.IsBusy) return;
        row.IsBusy = true;
        row.BusyText = "Downloading…";
        var progress = new Progress<double>(p =>
        {
            row.Progress = p;
            row.BusyText = p >= 0.85 ? "Installing…" : $"{(int)(p * 100)}%";
        });
        try
        {
            var addon = await _manager.InstallAsync(inst, row.Remote, progress, _main.ShutdownToken);
            row.IsInstalled = true;
            _main.ShowBanner(BannerKind.Success, $"Installed {addon.Name} {addon.InstalledVersion}.");
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            Log.Error($"Install {row.Name} failed", ex);
            _main.ShowBanner(BannerKind.Error, $"Couldn't install {row.Name}: {ex.Message}");
        }
        finally
        {
            row.IsBusy = false;
            _main.SyncRows();
        }
    }

    private async Task InstallFromUrlAsync()
    {
        if (_main.Selected is not { } inst) return;
        var text = Url.Trim();
        if (!text.StartsWith("http", StringComparison.OrdinalIgnoreCase)) text = "https://" + text;
        if (!Uri.TryCreate(text, UriKind.Absolute, out var uri))
        {
            _main.ShowBanner(BannerKind.Error, "That doesn't look like a link.");
            return;
        }

        var provider = _manager.Providers.All.FirstOrDefault(p => p.CanHandleUrl(uri));
        if (provider == null)
        {
            _main.ShowBanner(BannerKind.Error, "Paste a link from CurseForge, WoWInterface, GitHub or Tukui.");
            return;
        }
        if (!provider.IsAvailable)
        {
            _main.ShowBanner(BannerKind.Warning, provider.UnavailableMessage, "Open Settings", () => _main.Page = AppPage.Settings);
            return;
        }

        IsLoading = true;
        try
        {
            var remote = await provider.GetByUrlAsync(uri, inst, _main.ShutdownToken);
            if (remote == null)
            {
                _main.ShowBanner(BannerKind.Error, $"Couldn't find an addon at that {provider.Name} link.");
                return;
            }
            var row = new SearchRowViewModel(remote, this);
            Results.Insert(0, row);
            Url = "";
            IsLoading = false;
            Install(row);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _main.ShowBanner(BannerKind.Error, ex.Message);
        }
        finally
        {
            IsLoading = false;
        }
    }
}
