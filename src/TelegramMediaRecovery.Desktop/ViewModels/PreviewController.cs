using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using TelegramMediaRecovery.Core.Browsing;
using TelegramMediaRecovery.Core.Media;

namespace TelegramMediaRecovery.Desktop.ViewModels;

/// <summary>Owns preview requests, playback frames and temporary-file leases independently of catalog state.</summary>
internal sealed class PreviewController(Dispatcher dispatcher) : ObservableObject, IDisposable
{
    private sealed class Request(CancellationToken source)
    {
        public CancellationTokenSource Cancellation { get; } = CancellationTokenSource.CreateLinkedTokenSource(source);
        public TaskCompletionSource<bool> Ready { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public bool Completed { get; set; }
        public void Cancel() { if (!Completed) { Cancellation.Cancel(); } }
    }
    private readonly HashSet<Task> _tasks = [];
    private readonly SemaphoreSlim _worker = new(1);
    private Request? _current;
    private RecoveryPreview? _lease;
    private BitmapSource? _image;
    private Uri? _uri;
    private string _message = "Select an item to preview it and inspect its source.";
    public BitmapSource? Image { get => _image; private set => Set(ref _image, value); }
    public Uri? Uri { get => _uri; private set => Set(ref _uri, value); }
    public string Message { get => _message; set => Set(ref _message, value); }
    public event Action? Changing;

    public Task ShowAsync(RecoverySession session, MediaItemViewModel item, CancellationToken source)
    {
        Clear();
        var request = new Request(source);
        _current = request;
        Message = "Loading preview...";
        var task = RunAsync(request, session, item);
        _tasks.Add(task);
        _ = ObserveAsync(task);
        return request.Ready.Task;
    }

    private async Task ObserveAsync(Task task)
    {
        try { await task; }
        finally { _tasks.Remove(task); }
    }

    private bool IsCurrent(Request request) => ReferenceEquals(_current, request) && !request.Cancellation.IsCancellationRequested;

    private async Task RunAsync(Request request, RecoverySession session, MediaItemViewModel item)
    {
        var token = request.Cancellation.Token;
        var entered = false;
        try
        {
            await _worker.WaitAsync(token);
            entered = true;
            if (item.Item.Format.Kind is MediaKind.Image or MediaKind.Animation)
            {
                var bytes = await Task.Run(() => session.ReadPreviewBytes(item.Item, token), token);
                if (!IsCurrent(request)) { return; }
                if (item.Item.Format.Extension == "tgs")
                {
                    await AnimateAsync(request, bytes);
                    return;
                }
                var bitmap = await Task.Run(() => PreviewRenderer.Decode(bytes, item.Item.Format.Extension, 1440), token);
                if (!IsCurrent(request)) { return; }
                Image = bitmap;
                Message = item.Item.IsStream ? "Preview loaded. The original file's completeness remains unconfirmed." : "Preview loaded. Dates are estimated cache times.";
            }
            else if (item.Item.Format.Kind is MediaKind.Video or MediaKind.Audio)
            {
                var lease = await Task.Run(() => session.CreatePreview(item.Item, token), token);
                if (!IsCurrent(request)) { lease.Dispose(); return; }
                _lease = lease;
                Uri = new Uri(lease.Path);
                Message = "Press Play. Use an external player if the Windows decoder does not support this format.";
            }
            else { Message = "This format can be exported; an embedded preview is not available."; }
        }
        catch (Exception ex) when (PreviewErrors.IsExpected(ex))
        {
            if (IsCurrent(request)) { Message = "Preview unavailable: " + ex.Message; }
        }
        finally
        {
            if (entered) { _worker.Release(); }
            request.Completed = true;
            request.Ready.TrySetResult(true);
            request.Cancellation.Dispose();
        }
    }

    private Task AnimateAsync(Request request, byte[] bytes) => Task.Run(async () =>
    {
        var token = request.Cancellation.Token;
        using var animation = new StickerPreview(bytes);
        var clock = Stopwatch.StartNew();
        WriteableBitmap? image = null;
        while (!token.IsCancellationRequested)
        {
            var started = clock.Elapsed;
            var bitmap = animation.Render((clock.Elapsed.TotalSeconds % animation.Duration) / animation.Duration, 480);
            await dispatcher.InvokeAsync(() =>
            {
                if (!IsCurrent(request)) { return; }
                image ??= new WriteableBitmap(bitmap.Width, bitmap.Height, 96, 96, PixelFormats.Pbgra32, null);
                image.WritePixels(new Int32Rect(0, 0, bitmap.Width, bitmap.Height), bitmap.GetPixels(), bitmap.ByteCount, bitmap.RowBytes);
                Image = image;
                Message = "Animated sticker preview. Source message and saving restrictions are unknown.";
                request.Ready.TrySetResult(true);
            }, DispatcherPriority.Background, token);
            var remaining = TimeSpan.FromMilliseconds(50) - (clock.Elapsed - started);
            if (remaining > TimeSpan.Zero) { await Task.Delay(remaining, token).ConfigureAwait(false); }
        }
    });

    public void Clear()
    {
        _current?.Cancel();
        _current = null;
        Changing?.Invoke();
        Uri = null;
        Image = null;
        _lease?.Dispose(); _lease = null;
        Message = "Select an item to preview it and inspect its source.";
    }
    public async Task StopAsync() { Clear(); await Task.WhenAll(_tasks.ToArray()); }
    public void Dispose() => Clear();
}

internal static class PreviewErrors
{
    public static bool IsExpected(Exception ex) => ex is IOException or InvalidDataException or UnauthorizedAccessException
        or ArgumentException or NotSupportedException or DllNotFoundException or TypeInitializationException
        or System.Security.Cryptography.CryptographicException or OperationCanceledException or ObjectDisposedException;
}
