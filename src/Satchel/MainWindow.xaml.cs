using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Interop;
using Satchel.Core.Storage;
using Satchel.ViewModels;

namespace Satchel;

public partial class MainWindow : Window
{
    private readonly MainViewModel _vm;
    private readonly SettingsStore _settings;

    public MainWindow(MainViewModel vm, SettingsStore settings)
    {
        _vm = vm;
        _settings = settings;
        InitializeComponent();
        DataContext = vm;
        ApplySavedBounds(settings.Settings.Window);
        SourceInitialized += (_, _) => UseDarkTitleBar();
    }

    /// <summary>Set by the app when the user really wants to quit (tray → Exit).</summary>
    public bool AllowClose { get; set; }

    public event Action? HiddenToTray;

    protected override void OnClosing(System.ComponentModel.CancelEventArgs e)
    {
        SaveBounds();
        if (!AllowClose && _settings.Settings.MinimizeToTray)
        {
            e.Cancel = true;
            Hide();
            HiddenToTray?.Invoke();
            return;
        }
        base.OnClosing(e);
    }

    public void ShowAndActivate()
    {
        if (!IsVisible) Show();
        if (WindowState == WindowState.Minimized) WindowState = WindowState.Normal;
        Activate();
        Topmost = true;
        Topmost = false;
        Focus();
    }

    private void ApplySavedBounds(WindowBounds? b)
    {
        if (b == null || b.Width < 400 || b.Height < 300) return;
        var vs = new Rect(SystemParameters.VirtualScreenLeft, SystemParameters.VirtualScreenTop, SystemParameters.VirtualScreenWidth, SystemParameters.VirtualScreenHeight);
        if (!vs.IntersectsWith(new Rect(b.Left, b.Top, b.Width, b.Height))) return;
        WindowStartupLocation = WindowStartupLocation.Manual;
        Left = b.Left;
        Top = b.Top;
        Width = b.Width;
        Height = b.Height;
        if (b.Maximized) WindowState = WindowState.Maximized;
    }

    private void SaveBounds()
    {
        var r = WindowState == WindowState.Normal ? new Rect(Left, Top, Width, Height) : RestoreBounds;
        if (r.IsEmpty) return;
        _settings.Settings.Window = new WindowBounds
        {
            Left = r.Left, Top = r.Top, Width = r.Width, Height = r.Height, Maximized = WindowState == WindowState.Maximized,
        };
        _settings.Save();
    }

    // Opens the row's context menu under the "⋯" button.
    private void RowMenu_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: FrameworkElement row } button || row.ContextMenu is not { } menu) return;
        menu.PlacementTarget = button;
        menu.Placement = PlacementMode.Custom;
        // Right-align the menu under the button so it never hangs off the window edge.
        menu.CustomPopupPlacementCallback = (popupSize, targetSize, _) =>
            [new CustomPopupPlacement(new Point(targetSize.Width - popupSize.Width, targetSize.Height + 2), PopupPrimaryAxis.Horizontal)];
        menu.IsOpen = true;
    }

    private void AddonList_MouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if ((e.OriginalSource as FrameworkElement)?.DataContext is AddonRowViewModel row) row.OpenWebsiteCommand.Execute(null);
    }

    private void FocusFilter_Executed(object sender, ExecutedRoutedEventArgs e)
    {
        if (_vm.Page == AppPage.GetAddons)
        {
            SearchBox.Focus();
            SearchBox.SelectAll();
        }
        else
        {
            _vm.Page = AppPage.MyAddons;
            FilterBox.Focus();
            FilterBox.SelectAll();
        }
    }

    private void SaveCurseKey_Click(object sender, RoutedEventArgs e)
    {
        var key = CurseKeyBox.Password.Trim();
        if (key.Length == 0) return;
        _vm.SettingsPage.SaveCurseForgeKey(key);
        CurseKeyBox.Clear();
    }

    private void SaveGitHubToken_Click(object sender, RoutedEventArgs e)
    {
        var token = GitHubTokenBox.Password.Trim();
        if (token.Length == 0) return;
        _vm.SettingsPage.SaveGitHubToken(token);
        GitHubTokenBox.Clear();
    }

    // Windows 11: dark caption that matches the app background.
    private void UseDarkTitleBar()
    {
        try
        {
            var hwnd = new WindowInteropHelper(this).Handle;
            var dark = 1;
            DwmSetWindowAttribute(hwnd, 20, ref dark, sizeof(int)); // DWMWA_USE_IMMERSIVE_DARK_MODE
            var caption = 0x00201916; // COLORREF (BGR) for #161920
            DwmSetWindowAttribute(hwnd, 35, ref caption, sizeof(int)); // DWMWA_CAPTION_COLOR
        }
        catch (Exception)
        {
            // Older Windows: keep the default title bar.
        }
    }

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);
}
