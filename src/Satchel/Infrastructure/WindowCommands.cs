using System.Windows.Input;

namespace Satchel.Infrastructure;

public static class WindowCommands
{
    public static readonly RoutedUICommand FocusFilter = new("Find", nameof(FocusFilter), typeof(WindowCommands));
}
