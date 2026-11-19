using System.Windows;

namespace Satchel;

public partial class ConfirmDialog : Window
{
    public ConfirmDialog(string title, string message, string confirmText)
    {
        InitializeComponent();
        TitleText.Text = title;
        MessageText.Text = message;
        ConfirmButton.Content = confirmText;
        MouseLeftButtonDown += (_, _) => DragMove();
    }

    public static bool Ask(Window? owner, string title, string message, string confirmText)
    {
        var dialog = new ConfirmDialog(title, message, confirmText);
        if (owner is { IsVisible: true }) dialog.Owner = owner;
        else dialog.WindowStartupLocation = WindowStartupLocation.CenterScreen;
        return dialog.ShowDialog() == true;
    }

    private void Confirm_Click(object sender, RoutedEventArgs e) => DialogResult = true;

    private void Cancel_Click(object sender, RoutedEventArgs e) => DialogResult = false;
}
