namespace TelegramMediaRecovery.Core.Cache;

/// <summary>Decrypted cache bytes valid until disposal. Callers must not retain the memory after releasing the lease.</summary>
public sealed class CacheValueLease : IDisposable
{
    private Action? _release;
    internal CacheValueLease(ReadOnlyMemory<byte> memory, Action? release = null) => (Memory, _release) = (memory, release);
    public ReadOnlyMemory<byte> Memory { get; }
    public void Dispose() => Interlocked.Exchange(ref _release, null)?.Invoke();
}
