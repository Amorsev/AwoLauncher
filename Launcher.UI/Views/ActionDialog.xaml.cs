using System.Windows;

namespace Launcher.UI;

public partial class ActionDialog : Window
{
    private MessageBoxResult choice = MessageBoxResult.Cancel;

    private ActionDialog(string title, string message, string primary, string? secondary, bool destructive,
        string? warning, FrameworkElement? details)
    {
        InitializeComponent();
        Title = title;
        Message.Text = message;
        Warning.Text = warning ?? "";
        Warning.Visibility = string.IsNullOrWhiteSpace(warning) ? Visibility.Collapsed : Visibility.Visible;
        PrimaryButton.Content = primary;
        SecondaryButton.Content = secondary;
        SecondaryButton.Visibility = secondary is null ? Visibility.Collapsed : Visibility.Visible;
        Details.Content = details;
        Details.Visibility = details is null ? Visibility.Collapsed : Visibility.Visible;
        if (details is not null) Width = 640;
        if (destructive)
        {
            Badge.SetResourceReference(BackgroundProperty, "DangerSurface");
            PrimaryButton.SetResourceReference(StyleProperty, "DangerButton");
            Glyph.Text = "\uE74D";
        }
        Loaded += (_, _) => CancelButton.Focus();
    }

    internal static MessageBoxResult Ask(string title, string message, string primary, string? secondary = null,
        bool destructive = false, string? warning = null, FrameworkElement? details = null)
    {
        var dialog = new ActionDialog(title, message, primary, secondary, destructive, warning, details);
        if (Application.Current.MainWindow is { IsVisible: true } owner) dialog.Owner = owner;
        else dialog.WindowStartupLocation = WindowStartupLocation.CenterScreen;
        dialog.ShowDialog();
        return dialog.choice;
    }

    private void ChoosePrimary(object sender, RoutedEventArgs e) => Choose(MessageBoxResult.Yes);
    private void ChooseSecondary(object sender, RoutedEventArgs e) => Choose(MessageBoxResult.No);

    private void Choose(MessageBoxResult result)
    {
        choice = result;
        DialogResult = true;
    }
}
