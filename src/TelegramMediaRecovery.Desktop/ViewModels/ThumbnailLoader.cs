using System.Windows.Media.Imaging;
using TelegramMediaRecovery.Core.Browsing;
using TelegramMediaRecovery.Core.Media;

namespace TelegramMediaRecovery.Desktop.ViewModels;

/// <summary>Two decode workers, a bounded visible-first queue and an LRU of frozen thumbnails. UI-thread owned.</summary>
internal sealed class ThumbnailLoader
{
    private sealed class Request(MediaItemViewModel item, RecoverySession session, CancellationToken token, int generation)
    {
        public MediaItemViewModel Item { get; } = item;
        public RecoverySession Session { get; } = session;
        public CancellationTokenSource Cancellation { get; } = CancellationTokenSource.CreateLinkedTokenSource(token);
        public int Generation { get; } = generation;
        public TaskCompletionSource<bool> Ready { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public LinkedListNode<Request>? Node { get; set; }
    }
    private readonly Dictionary<string, (BitmapSource Image, LinkedListNode<string> Node)> _cache = [];
    private readonly LinkedList<string> _lru = [];
    private readonly Dictionary<string, Request> _requests = [];
    private readonly LinkedList<Request> _queue = [];
    private readonly HashSet<Request> _running = [];
    private int _workers;
    private int _generation;

    public Task EnsureAsync(MediaItemViewModel item, RecoverySession session, CancellationToken token)
    {
        if (token.IsCancellationRequested || item.Item.Format.Kind is not (MediaKind.Image or MediaKind.Animation)) { return Task.CompletedTask; }
        if (_cache.TryGetValue(item.Item.Id, out var cached))
        {
            _lru.Remove(cached.Node); _lru.AddFirst(cached.Node);
            item.SetThumbnail(cached.Image);
            return Task.CompletedTask;
        }
        if (item.Thumbnail is not null) { return Task.CompletedTask; }
        if (_requests.TryGetValue(item.Item.Id, out var existing) && !existing.Cancellation.IsCancellationRequested) { return existing.Ready.Task; }
        var request = new Request(item, session, token, _generation);
        request.Node = _queue.AddFirst(request);
        _requests[item.Item.Id] = request;
        if (_queue.Count > 64) { Release(_queue.Last!.Value.Item); }
        StartWorkers();
        return request.Ready.Task;
    }

    public void Release(MediaItemViewModel item)
    {
        if (!_requests.TryGetValue(item.Item.Id, out var request)) { return; }
        request.Cancellation.Cancel();
        if (request.Node is not null)
        {
            _queue.Remove(request.Node);
            _requests.Remove(item.Item.Id);
            request.Cancellation.Dispose();
            request.Ready.TrySetResult(true);
        }
    }

    private void StartWorkers()
    {
        while (_workers < 2 && _queue.Count > 0) { _workers++; _ = WorkAsync(); }
    }

    private async Task WorkAsync()
    {
        var request = _queue.First!.Value;
        _queue.RemoveFirst(); request.Node = null; _running.Add(request);
        var token = request.Cancellation.Token;
        try
        {
            var image = await Task.Run(() => PreviewRenderer.Decode(request.Session.ReadPreviewBytes(request.Item.Item, token), request.Item.Item.Format.Extension, 104), token);
            if (token.IsCancellationRequested || request.Generation != _generation) { return; }
            if (_cache.Remove(request.Item.Item.Id, out var previous)) { _lru.Remove(previous.Node); }
            _cache[request.Item.Item.Id] = (image, _lru.AddFirst(request.Item.Item.Id));
            while (_cache.Count > 180) { _cache.Remove(_lru.Last!.Value); _lru.RemoveLast(); }
            request.Item.SetThumbnail(image);
        }
        catch (Exception ex) when (PreviewErrors.IsExpected(ex)) { }
        finally
        {
            if (_requests.TryGetValue(request.Item.Item.Id, out var current) && current == request) { _requests.Remove(request.Item.Item.Id); }
            _running.Remove(request);
            request.Cancellation.Dispose(); request.Ready.TrySetResult(true);
            _workers--; StartWorkers();
        }
    }

    public void Reset()
    {
        _generation++;
        foreach (var request in _requests.Values.ToArray())
        {
            request.Cancellation.Cancel();
            if (request.Node is not null) { request.Cancellation.Dispose(); request.Ready.TrySetResult(true); }
        }
        _queue.Clear(); _requests.Clear(); _cache.Clear(); _lru.Clear();
    }
    public async Task StopAsync() { Reset(); await Task.WhenAll(_running.Select(r => r.Ready.Task)); }
}
