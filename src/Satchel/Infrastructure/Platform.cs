using System.Diagnostics;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Win32;
using Satchel.Core.Storage;

namespace Satchel.Infrastructure;

/// <summary>Encrypts secrets with Windows DPAPI so they're only readable by this Windows user.</summary>
public sealed class DpapiProtector : ISecretProtector
{
    private static readonly byte[] Entropy = "Satchel.v1"u8.ToArray();

    public string Protect(string plainText) =>
        Convert.ToBase64String(ProtectedData.Protect(Encoding.UTF8.GetBytes(plainText), Entropy, DataProtectionScope.CurrentUser));

    public string? Unprotect(string protectedText)
    {
        try
        {
            return Encoding.UTF8.GetString(ProtectedData.Unprotect(Convert.FromBase64String(protectedText), Entropy, DataProtectionScope.CurrentUser));
        }
        catch (Exception ex) when (ex is CryptographicException or FormatException)
        {
            Log.Error("Could not decrypt a stored secret", ex);
            return null;
        }
    }
}

public static class Shell
{
    public static void OpenUrl(string? url)
    {
        if (string.IsNullOrWhiteSpace(url)) return;
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || (uri.Scheme != Uri.UriSchemeHttps && uri.Scheme != Uri.UriSchemeHttp)) return;
        Process.Start(new ProcessStartInfo(uri.AbsoluteUri) { UseShellExecute = true });
    }

    public static void OpenFolder(string? path)
    {
        if (string.IsNullOrWhiteSpace(path) || !Directory.Exists(path)) return;
        Process.Start(new ProcessStartInfo("explorer.exe", $"\"{path}\"") { UseShellExecute = true });
    }
}

/// <summary>Per-user "start with Windows" via the HKCU Run key.</summary>
public static class StartupRegistration
{
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ValueName = "Satchel";

    public static void Apply(bool enabled)
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKey, writable: true) ?? Registry.CurrentUser.CreateSubKey(RunKey);
            if (enabled && Environment.ProcessPath is { } exe) key.SetValue(ValueName, $"\"{exe}\" --minimized");
            else if (key.GetValue(ValueName) != null) key.DeleteValue(ValueName, throwOnMissingValue: false);
        }
        catch (Exception ex)
        {
            Log.Error("Could not update startup registration", ex);
        }
    }
}
