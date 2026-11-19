using System.Runtime.CompilerServices;
using Satchel.Core.Storage;

namespace Satchel.Tests;

internal static class TestSetup
{
    /// <summary>Keeps every test (and anything it logs) out of the real %APPDATA% / %LOCALAPPDATA% Satchel folders.</summary>
    [ModuleInitializer]
    internal static void IsolateAppData()
    {
        var root = Path.Combine(Path.GetTempPath(), "satchel-tests", "appdata-" + Environment.ProcessId);
        AppPaths.DataDir = Path.Combine(root, "data");
        AppPaths.LocalDir = Path.Combine(root, "local");
    }
}
