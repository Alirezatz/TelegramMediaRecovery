using System.IO;
using System.IO.Compression;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using SkiaSharp;
using SkiaSharp.Skottie;

namespace TelegramMediaRecovery.Desktop;

internal static class PreviewRenderer
{
    public static BitmapSource Decode(byte[] bytes, string extension, int maximum)
    {
        if (extension == "tgs")
        {
            using var animation = new StickerPreview(bytes);
            return animation.Frame(0, maximum);
        }
        using var stream = new SKMemoryStream(bytes);
        using var codec = SKCodec.Create(stream) ?? throw new InvalidDataException("This image could not be decoded.");
        var original = codec.Info;
        if ((long)original.Width * original.Height > 100_000_000)
        {
            throw new InvalidDataException("This image is too large to preview. Export it to inspect it.");
        }
        var scale = Math.Min(1f, (float)maximum / Math.Max(original.Width, original.Height));
        var dimensions = codec.GetScaledDimensions(scale);
        var info = new SKImageInfo(Math.Max(1, dimensions.Width), Math.Max(1, dimensions.Height), SKColorType.Bgra8888, SKAlphaType.Premul);
        using var bitmap = new SKBitmap(info);
        var result = codec.GetPixels(info, bitmap.GetPixels());
        if (result != SKCodecResult.Success)
        {
            throw new InvalidDataException($"Image could not be fully decoded ({result}).");
        }
        if (bitmap.Width <= maximum && bitmap.Height <= maximum) { return ToImage(bitmap); }
        var ratio = (double)maximum / Math.Max(bitmap.Width, bitmap.Height);
        using var resized = bitmap.Resize(new SKImageInfo(Math.Max(1, (int)(bitmap.Width * ratio)), Math.Max(1, (int)(bitmap.Height * ratio)),
            SKColorType.Bgra8888, SKAlphaType.Premul), new SKSamplingOptions(SKFilterMode.Linear))
            ?? throw new InvalidDataException("Image resizing failed.");
        return ToImage(resized);
    }

    internal static BitmapSource ToImage(SKBitmap bitmap)
    {
        var source = BitmapSource.Create(bitmap.Width, bitmap.Height, 96, 96, PixelFormats.Pbgra32,
            null, bitmap.GetPixels(), bitmap.ByteCount, bitmap.RowBytes);
        source.Freeze();
        return source;
    }
}

internal sealed class StickerPreview : IDisposable
{
    private readonly Animation _animation;
    private SKBitmap? _bitmap;
    private SKCanvas? _canvas;
    public StickerPreview(byte[] compressed)
    {
        using var input = new MemoryStream(compressed);
        using var gzip = new GZipStream(input, CompressionMode.Decompress);
        using var json = new MemoryStream();
        var buffer = new byte[65536];
        int read;
        while ((read = gzip.Read(buffer)) > 0)
        {
            if (json.Length + read > 16 * 1024 * 1024) { throw new InvalidDataException("Animation data exceeds the preview limit."); }
            json.Write(buffer, 0, read);
        }
        using var data = SKData.CreateCopy(json.ToArray());
        _animation = Animation.Create(data) ?? throw new InvalidDataException("The Lottie animation could not be read.");
    }
    public double Duration => Math.Max(0.1, _animation.Duration.TotalSeconds);
    public BitmapSource Frame(double normalizedTime, int maximum)
    {
        return PreviewRenderer.ToImage(Render(normalizedTime, maximum));
    }
    public SKBitmap Render(double normalizedTime, int maximum)
    {
        if (_bitmap is null || _bitmap.Width != maximum)
        {
            _canvas?.Dispose(); _bitmap?.Dispose();
            _bitmap = new SKBitmap(maximum, maximum, SKColorType.Bgra8888, SKAlphaType.Premul);
            _canvas = new SKCanvas(_bitmap);
        }
        _canvas!.Clear(SKColors.Transparent);
        _animation.Seek(normalizedTime, null);
        _animation.Render(_canvas, new SKRect(0, 0, maximum, maximum));
        return _bitmap;
    }
    public void Dispose() { _canvas?.Dispose(); _bitmap?.Dispose(); _animation.Dispose(); }
}
