using System.Collections.Specialized;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;

namespace Launcher.UI;

public partial class MainWindow : Window
{
    public MainWindow()
    {
        InitializeComponent();
        ((INotifyCollectionChanged)LogList.Items).CollectionChanged += OnLogChanged;
    }

    private void Minimize(object sender, RoutedEventArgs e) => SystemCommands.MinimizeWindow(this);
    private void Maximize(object sender, RoutedEventArgs e)
    {
        if (WindowState == WindowState.Maximized) SystemCommands.RestoreWindow(this);
        else SystemCommands.MaximizeWindow(this);
    }
    private void CloseWindow(object sender, RoutedEventArgs e) => Close();
    private void ToggleLog(object sender, RoutedEventArgs e) =>
        LogDrawer.Visibility = LogDrawer.Visibility == Visibility.Visible ? Visibility.Collapsed : Visibility.Visible;

    private void FocusCreation(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (e.NewValue is true)
            _ = Dispatcher.InvokeAsync(() => { if (CreationOverlay.IsVisible) CreationName.Focus(); },
                System.Windows.Threading.DispatcherPriority.Input);
    }

    private void OnLogChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (DataContext is MainViewModel { AutoScroll: true } && LogList.Items.Count > 0)
            LogList.ScrollIntoView(LogList.Items[LogList.Items.Count - 1]);
    }

    private void AnimatePage(object sender, SelectionChangedEventArgs e)
    {
        if (!ReferenceEquals(e.Source, Pages) || Pages.Template.FindName("PART_SelectedContentHost", Pages) is not ContentPresenter content)
            return;
        if (!SystemParameters.ClientAreaAnimation) return;
        var duration = TimeSpan.FromMilliseconds(180);
        var easing = new CubicEase { EasingMode = EasingMode.EaseOut };
        var slide = new TranslateTransform();
        content.RenderTransform = slide;
        content.BeginAnimation(OpacityProperty, new DoubleAnimation(.45, 1, duration));
        slide.BeginAnimation(TranslateTransform.XProperty, new DoubleAnimation(9, 0, duration) { EasingFunction = easing });
    }

    private void RenameCard(object sender, MouseButtonEventArgs e)
    {
        if (DataContext is not MainViewModel { Busy: false, SelectedInstance: not null } ||
            ItemsControl.ContainerFromElement((ItemsControl)sender, e.OriginalSource as DependencyObject) is not ListBoxItem) return;
        RenameBox.Focus();
        RenameBox.SelectAll();
        e.Handled = true;
    }

    private void CardAction(object sender, RoutedEventArgs e)
    {
        if (DataContext is MainViewModel viewModel && sender is Button { DataContext: Launcher.Core.InstanceCard card, Tag: string action })
            viewModel.RunCardAction(card, action);
        e.Handled = true;
    }

    private static string[] DroppedFiles(IDataObject data) =>
        data.GetData(DataFormats.FileDrop) is string[] files ? files.Where(File.Exists).ToArray() : [];

    private void PreviewFiles(object sender, DragEventArgs e)
    {
        if (!e.Data.GetDataPresent(DataFormats.FileDrop)) return;
        e.Effects = DataContext is MainViewModel { Busy: false, ShowCreation: false } vm &&
                    DroppedFiles(e.Data).Any(path => Path.GetExtension(path).Equals(".mrpack", StringComparison.OrdinalIgnoreCase) ||
                        Path.GetExtension(path).Equals(".jar", StringComparison.OrdinalIgnoreCase) ||
                        vm.Section == 6 && vm.SkinSection == 0 && vm.HasAccount &&
                        Path.GetExtension(path).Equals(".png", StringComparison.OrdinalIgnoreCase))
            ? DragDropEffects.Copy : DragDropEffects.None;
        e.Handled = true;
    }

    private async void DropFiles(object sender, DragEventArgs e)
    {
        if (DataContext is MainViewModel { ShowCreation: true }) { e.Handled = true; return; }
        if (DataContext is MainViewModel viewModel && e.Data.GetDataPresent(DataFormats.FileDrop))
        {
            e.Handled = true;
            var paths = DroppedFiles(e.Data);
            if (viewModel.Section == 6 && viewModel.SkinSection == 0 && viewModel.HasAccount &&
                paths.FirstOrDefault(path => Path.GetExtension(path).Equals(".png", StringComparison.OrdinalIgnoreCase)) is { } skin)
                await viewModel.DropSkinAsync(skin);
            else await viewModel.DropAsync(paths);
        }
    }

    private void HandleShortcut(object sender, KeyEventArgs e)
    {
        if (DataContext is not MainViewModel viewModel) return;
        if (viewModel.ShowCreation)
        {
            if (e.Key == Key.Escape) { viewModel.CloseCreation.Execute(null); e.Handled = true; }
            return;
        }
        if (!Keyboard.Modifiers.HasFlag(ModifierKeys.Control)) return;
        if (e.Key == Key.Tab)
        {
            if (viewModel.CycleInstance.CanExecute(null)) viewModel.CycleInstance.Execute(null);
            e.Handled = true;
        }
        else if (e.Key == Key.K)
        {
            ShowPalette(sender, e);
            e.Handled = true;
        }
    }

    private void ShowPalette(object sender, RoutedEventArgs e)
    {
        if (DataContext is not MainViewModel viewModel) return;
        string Read(string key) => TryFindResource("L_" + key) as string ?? key;
        List<PaletteEntry> entries = [];
        foreach (var (key, page) in new[] { ("Play", 0), ("Catalog", 1), ("Instances", 2), ("Accounts", 3),
                     ("Settings", 4), ("Skins", 6), ("Screenshots", 7) })
            entries.Add(new(Read(key), () => viewModel.Section = page));
        entries.AddRange(viewModel.Instances.Select(instance => new PaletteEntry($"{instance.Name} · {instance.GameVersion}", () =>
        {
            viewModel.SelectedInstance = instance;
            viewModel.Section = 0;
        })));
        entries.AddRange(viewModel.Profiles.Select(profile => new PaletteEntry(profile.Nick, () =>
        {
            viewModel.Account = profile;
            viewModel.Section = 3;
        })));
        var query = new TextBox();
        System.Windows.Automation.AutomationProperties.SetName(query, Read("CommandPalette"));
        var results = new ListBox { DisplayMemberPath = nameof(PaletteEntry.Label) };
        var panel = new DockPanel { Margin = new Thickness(15) };
        DockPanel.SetDock(query, Dock.Top);
        panel.Children.Add(query);
        panel.Children.Add(results);
        var dialog = new Window
        {
            Owner = this, Title = Read("CommandPalette"), Width = 480, Height = 390,
            WindowStartupLocation = WindowStartupLocation.CenterOwner, Content = panel, ShowInTaskbar = false,
            Background = (Brush)FindResource("Surface")
        };
        void Refresh()
        {
            results.ItemsSource = entries.Where(item => item.Label.Contains(query.Text, StringComparison.CurrentCultureIgnoreCase)).ToArray();
            results.SelectedIndex = results.Items.Count > 0 ? 0 : -1;
        }
        void Execute()
        {
            if (results.SelectedItem is not PaletteEntry item) return;
            dialog.Close();
            item.Action();
        }
        query.TextChanged += (_, _) => Refresh();
        dialog.PreviewKeyDown += (_, key) =>
        {
            if (key.Key == Key.Escape) dialog.Close();
            else if (key.Key == Key.Enter) { key.Handled = true; Execute(); }
            else if (key.Key == Key.Down && query.IsKeyboardFocused)
            {
                if (results.SelectedItem is { } item && results.ItemContainerGenerator.ContainerFromItem(item) is ListBoxItem selected)
                    selected.Focus();
            }
        };
        results.MouseDoubleClick += (_, _) => Execute();
        dialog.Loaded += (_, _) => query.Focus();
        Refresh();
        dialog.ShowDialog();
    }

    private sealed record PaletteEntry(string Label, Action Action);
}
