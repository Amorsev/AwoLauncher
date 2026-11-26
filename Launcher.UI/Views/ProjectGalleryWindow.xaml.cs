using System.IO;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media.Imaging;
using Launcher.Core;

namespace Launcher.UI;

public partial class ProjectGalleryWindow : Window
{
    private readonly ProjectImage[] images;
    private int index;
    private BitmapImage? bitmap;

    public ProjectGalleryWindow(string title, ProjectImage[] images, int selectedIndex)
    {
        if (images.Length == 0) throw new ArgumentException("The gallery is empty.", nameof(images));
        if ((uint)selectedIndex >= (uint)images.Length) throw new ArgumentOutOfRangeException(nameof(selectedIndex));
        this.images = images;
        index = selectedIndex;
        InitializeComponent();
        Title = title;
        ShowImage();
    }

    private string Label(string key) => TryFindResource("L_" + key) as string ?? key;

    private void ShowImage()
    {
        var entry = images[index];
        Caption.Text = string.IsNullOrWhiteSpace(entry.Title) ? Title : entry.Title;
        Counter.Text = $"{index + 1} / {images.Length}";
        PreviousPhoto.IsEnabled = index > 0;
        NextPhoto.IsEnabled = index < images.Length - 1;
        SetZoom(1);
        Viewport.ScrollToHome();
        Photo.Source = null;
        bitmap = null;
        if (entry.OriginalImage is not { } uri)
        {
            Status.Text = Label("GalleryImageFailed");
            return;
        }

        LoadImage(uri, entry.Image != uri ? entry.Image : null);
    }

    private void LoadImage(Uri uri, Uri? fallback)
    {
        Status.Text = Label("GalleryImageLoading");
        var image = new BitmapImage();
        bitmap = image;
        void Failed()
        {
            if (bitmap != image) return;
            bitmap = null;
            Photo.Source = null;
            if (fallback is not null) LoadImage(fallback, null);
            else Status.Text = Label("GalleryImageFailed");
        }
        try
        {
            image.BeginInit();
            image.UriSource = uri;
            image.DownloadCompleted += (_, _) => { if (bitmap == image) Status.Text = ""; };
            image.DownloadFailed += (_, _) => Failed();
            image.DecodeFailed += (_, _) => Failed();
            image.EndInit();
            if (bitmap != image) return;
            Photo.Source = image;
            if (!image.IsDownloading && image.PixelWidth > 0) Status.Text = "";
        }
        catch (Exception error) when (error is IOException or NotSupportedException or ArgumentException)
        {
            Failed();
        }
    }

    private void Move(int step)
    {
        var next = index + step;
        if ((uint)next >= (uint)images.Length) return;
        index = next;
        ShowImage();
    }

    private void SetZoom(double value)
    {
        PhotoScale.ScaleX = PhotoScale.ScaleY = Math.Clamp(value, 1, 4);
        ZoomLabel.Content = PhotoScale.ScaleX.ToString("P0");
    }

    private void ResizePhoto(object sender, SizeChangedEventArgs e)
    {
        Photo.Width = Math.Max(1, e.NewSize.Width - 4);
        Photo.Height = Math.Max(1, e.NewSize.Height - 4);
    }

    private void Previous(object sender, RoutedEventArgs e) => Move(-1);
    private void Next(object sender, RoutedEventArgs e) => Move(1);
    private void ZoomOut(object sender, RoutedEventArgs e) => SetZoom(PhotoScale.ScaleX - .25);
    private void ZoomIn(object sender, RoutedEventArgs e) => SetZoom(PhotoScale.ScaleX + .25);
    private void ResetZoom(object sender, RoutedEventArgs e) => SetZoom(1);

    private void HandleKey(object sender, KeyEventArgs e)
    {
        switch (e.Key)
        {
            case Key.Left: Move(-1); break;
            case Key.Right: Move(1); break;
            case Key.Add: case Key.OemPlus: SetZoom(PhotoScale.ScaleX + .25); break;
            case Key.Subtract: case Key.OemMinus: SetZoom(PhotoScale.ScaleX - .25); break;
            case Key.D0: case Key.NumPad0: SetZoom(1); break;
            default: return;
        }
        e.Handled = true;
    }
}
