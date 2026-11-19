using System.Collections.Concurrent;
using System.Globalization;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Media.Imaging;
using Satchel.Core.Net;
using Satchel.Core.Storage;

namespace Satchel.Infrastructure;

/// <summary>
/// Attached property that loads a small, decoded-to-size thumbnail off the UI thread with memory and disk caches.
/// Safe with recycled list rows: a late result is dropped if the row now shows a different addon.
/// </summary>
public static class Thumbnails
{
    public static readonly DependencyProperty UrlProperty = DependencyProperty.RegisterAttached(
        "Url", typeof(string), typeof(Thumbnails), new PropertyMetadata(null, OnUrlChanged));

    private static readonly ConcurrentDictionary<string, BitmapSource?> Memory = new();
    private static readonly ConcurrentDictionary<string, Task<BitmapSource?>> InFlight = new();
    private static readonly SemaphoreSlim DownloadGate = new(6);
    private static string CacheDir => Path.Combine(AppPaths.CacheDir, "thumbs");

    public static string? GetUrl(DependencyObject d) => (string?)d.GetValue(UrlProperty);

    public static void SetUrl(DependencyObject d, string? value) => d.SetValue(UrlProperty, value);

    private static async void OnUrlChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not Image image) return;
        var url = e.NewValue as string;
        image.Source = null;
        if (string.IsNullOrWhiteSpace(url)) return;
        if (Memory.TryGetValue(url, out var cached))
        {
            image.Source = cached;
            return;
        }

        var bitmap = await InFlight.GetOrAdd(url, u => Task.Run(() => LoadAsync(u)));
        if (GetUrl(image) == url) image.Source = bitmap;
    }

    private static async Task<BitmapSource?> LoadAsync(string url)
    {
        try
        {
            var file = Path.Combine(CacheDir, Hash(url) + ".img");
            byte[] bytes;
            if (File.Exists(file))
            {
                bytes = await File.ReadAllBytesAsync(file).ConfigureAwait(false);
            }
            else
            {
                await DownloadGate.WaitAsync().ConfigureAwait(false);
                try
                {
                    bytes = await Http.Client.GetByteArrayAsync(url).ConfigureAwait(false);
                }
                finally
                {
                    DownloadGate.Release();
                }
                Directory.CreateDirectory(CacheDir);
                await File.WriteAllBytesAsync(file, bytes).ConfigureAwait(false);
            }

            var bmp = new BitmapImage();
            bmp.BeginInit();
            bmp.CacheOption = BitmapCacheOption.OnLoad;
            bmp.CreateOptions = BitmapCreateOptions.IgnoreColorProfile;
            bmp.DecodePixelWidth = 80;
            bmp.StreamSource = new MemoryStream(bytes);
            bmp.EndInit();
            bmp.Freeze();
            Memory[url] = bmp;
            return bmp;
        }
        catch (Exception)
        {
            Memory[url] = null; // don't retry a broken image every time the row scrolls into view
            return null;
        }
        finally
        {
            InFlight.TryRemove(url, out _);
        }
    }

    private static string Hash(string s) => Convert.ToHexString(SHA1.HashData(Encoding.UTF8.GetBytes(s)))[..20];
}

public sealed class NotEmptyToVisibilityConverter : IValueConverter
{
    public bool Invert { get; set; }

    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        var visible = value switch
        {
            null => false,
            string s => s.Length > 0,
            bool b => b,
            int i => i > 0,
            _ => true,
        };
        if (Invert) visible = !visible;
        return visible ? Visibility.Visible : Visibility.Collapsed;
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => throw new NotSupportedException();
}

/// <summary>Visible when the bound value's name equals the parameter (comma-separated list allowed).</summary>
public sealed class EqualsToVisibilityConverter : IValueConverter
{
    public bool Invert { get; set; }

    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        var current = value?.ToString() ?? "";
        var match = (parameter?.ToString() ?? "").Split(',', StringSplitOptions.TrimEntries).Contains(current);
        if (Invert) match = !match;
        return match ? Visibility.Visible : Visibility.Collapsed;
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => throw new NotSupportedException();
}

public sealed class EqualsToBoolConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        string.Equals(value?.ToString(), parameter?.ToString(), StringComparison.Ordinal);

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is true && parameter != null ? Enum.Parse(targetType, parameter.ToString()!) : Binding.DoNothing;
}
