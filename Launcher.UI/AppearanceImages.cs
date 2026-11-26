using System.IO;
using System.Security.Cryptography;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Launcher.Core;

namespace Launcher.UI;

internal static class AppearanceImages
{
    internal static BitmapSource Decode(string path, bool skin)
    {
        using var stream = File.OpenRead(path);
        if (stream.Length is <= 0 || stream.Length > (skin ? 1024 * 1024 : 24 * 1024 * 1024))
            throw new InvalidDataException("Image file size is outside the supported range.");
        var decoder = BitmapDecoder.Create(stream, BitmapCreateOptions.DelayCreation, BitmapCacheOption.None);
        var frame = decoder.Frames[0];
        if (skin && (decoder is not PngBitmapDecoder || frame.PixelWidth != 64 || frame.PixelHeight is not (32 or 64)))
            throw new InvalidDataException("Expected a 64×64 or 64×32 PNG skin.");
        if (!skin && (decoder is not (PngBitmapDecoder or JpegBitmapDecoder or BmpBitmapDecoder) ||
            frame.PixelWidth is < 1 or > 8192 || frame.PixelHeight is < 1 or > 8192))
            throw new InvalidDataException("Unsupported background image.");
        stream.Position = 0;
        var result = new BitmapImage();
        result.BeginInit();
        result.StreamSource = stream;
        result.CacheOption = BitmapCacheOption.OnLoad;
        if (!skin && Math.Max(frame.PixelWidth, frame.PixelHeight) > 2400)
        {
            if (frame.PixelWidth >= frame.PixelHeight) result.DecodePixelWidth = 2400;
            else result.DecodePixelHeight = 2400;
        }
        result.EndInit();
        result.Freeze();
        return result;
    }

    internal static string SaveImage(LocalData data, BitmapSource image, bool skin)
    {
        using var buffer = new MemoryStream();
        BitmapEncoder encoder = skin ? new PngBitmapEncoder() : new JpegBitmapEncoder { QualityLevel = 92 };
        encoder.Frames.Add(BitmapFrame.Create(image));
        encoder.Save(buffer);
        var bytes = buffer.ToArray();
        var folder = Path.Combine(data.Root, "appearance", skin ? "skins" : "backgrounds");
        var name = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant() + (skin ? ".png" : ".jpg");
        var destination = Path.Combine(folder, name);
        Directory.CreateDirectory(folder);
        if (File.Exists(destination)) return destination;
        var temporary = destination + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            File.WriteAllBytes(temporary, bytes);
            File.Move(temporary, destination, true);
        }
        finally { File.Delete(temporary); }
        return destination;
    }

    internal static (ImageSource Head, ImageSource Body) SkinPreview(BitmapSource skin)
    {
        var modern = skin.PixelHeight == 64;
        DrawingImage Draw(bool headOnly)
        {
            var drawing = new DrawingGroup();
            RenderOptions.SetBitmapScalingMode(drawing, BitmapScalingMode.NearestNeighbor);
            using (var canvas = drawing.Open())
            {
                void DrawPart(Int32Rect area, Rect where, bool mirror = false)
                {
                    var crop = new CroppedBitmap(skin, area);
                    if (mirror) canvas.PushTransform(new ScaleTransform(-1, 1, where.X + where.Width / 2, 0));
                    canvas.DrawImage(crop, where);
                    if (mirror) canvas.Pop();
                }
                var head = new Rect(headOnly ? 0 : 4, 0, 8, 8);
                DrawPart(new(8, 8, 8, 8), head);
                DrawPart(new(40, 8, 8, 8), head);
                if (!headOnly)
                {
                    DrawPart(new(20, 20, 8, 12), new(4, 8, 8, 12));
                    DrawPart(new(44, 20, 4, 12), new(0, 8, 4, 12));
                    DrawPart(modern ? new(36, 52, 4, 12) : new(44, 20, 4, 12), new(12, 8, 4, 12), !modern);
                    DrawPart(new(4, 20, 4, 12), new(4, 20, 4, 12));
                    DrawPart(modern ? new(20, 52, 4, 12) : new(4, 20, 4, 12), new(8, 20, 4, 12), !modern);
                    if (modern)
                    {
                        DrawPart(new(20, 36, 8, 12), new(4, 8, 8, 12));
                        DrawPart(new(44, 36, 4, 12), new(0, 8, 4, 12));
                        DrawPart(new(52, 52, 4, 12), new(12, 8, 4, 12));
                        DrawPart(new(4, 36, 4, 12), new(4, 20, 4, 12));
                        DrawPart(new(4, 52, 4, 12), new(8, 20, 4, 12));
                    }
                }
            }
            var image = new DrawingImage(drawing);
            image.Freeze();
            return image;
        }
        return (Draw(true), Draw(false));
    }

    internal static byte[] GameSkin(BitmapSource skin, bool slim)
    {
        const int stride = 64 * 4;
        var source = new byte[64 * stride];
        new FormatConvertedBitmap(skin, PixelFormats.Bgra32, null, 0)
            .CopyPixels(new Int32Rect(0, 0, 64, skin.PixelHeight), source, stride, 0);
        var target = (byte[])source.Clone();

        void Face(int sx, int sy, int dx, int dy, int width, int height, bool flip = false, int sourceWidth = 0)
        {
            if (sourceWidth == 0) sourceWidth = width;
            for (var y = 0; y < height; y++)
            for (var x = 0; x < width; x++)
            {
                var offset = x * sourceWidth / width;
                var input = (sy + y) * stride + (sx + (flip ? sourceWidth - 1 - offset : offset)) * 4;
                Buffer.BlockCopy(source, input, target, (dy + y) * stride + (dx + x) * 4, 4);
            }
        }
        void Limb(int sx, int sy, int dx, int dy, bool flip, bool arm, int width)
        {
            var from = 4;
            if (arm && skin.PixelHeight == 64 && source[(sy * 64 + sx + 7) * 4 + 3] == 0 &&
                source[((sy + 5) * 64 + sx + 15) * 4 + 3] == 0) from = 3;
            Face(sx + 4, sy, dx + 4, dy, width, 4, flip, from);
            Face(sx + 4 + from, sy, dx + 4 + width, dy, width, 4, flip, from);
            Face(sx + (flip ? 4 + from : 0), sy + 4, dx, dy + 4, 4, 12, flip);
            Face(sx + 4, sy + 4, dx + 4, dy + 4, width, 12, flip, from);
            Face(sx + (flip ? 0 : 4 + from), sy + 4, dx + 4 + width, dy + 4, 4, 12, flip);
            Face(sx + 8 + from, sy + 4, dx + 8 + width, dy + 4, width, 12, flip, from);
        }
        var armWidth = slim ? 3 : 4;
        Limb(40, 16, 40, 16, false, true, armWidth);
        if (skin.PixelHeight == 32)
        {
            Limb(40, 16, 32, 48, true, true, armWidth);
            Limb(0, 16, 16, 48, true, false, 4);
        }
        else
        {
            Limb(32, 48, 32, 48, false, true, armWidth);
            Limb(40, 32, 40, 32, false, true, armWidth);
            Limb(48, 48, 48, 48, false, true, armWidth);
        }
        using var output = new MemoryStream();
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(BitmapSource.Create(64, 64, 96, 96, PixelFormats.Bgra32, null, target, stride)));
        encoder.Save(output);
        return output.ToArray();
    }
}
