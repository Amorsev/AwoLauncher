using System.Windows;

namespace Launcher.UI;

public partial class RemoveContentDialog : Window
{
    public RemoveContentDialog(string message, string warning)
    {
        InitializeComponent();
        DataContext = new { Message = message, Warning = warning };
        Loaded += (_, _) => CancelButton.Focus();
    }

    private void ConfirmRemoval(object sender, RoutedEventArgs e) => DialogResult = true;
}
