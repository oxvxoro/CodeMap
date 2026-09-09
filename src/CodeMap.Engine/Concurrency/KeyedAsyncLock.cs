namespace CodeMap.Engine.Concurrency;

/// <summary>Per-key async mutual exclusion with reference-counted cleanup.</summary>
public sealed class KeyedAsyncLock<TKey> where TKey : notnull
{
    private readonly object _gate = new();
    private readonly Dictionary<TKey, Entry> _entries = new();

    public async ValueTask<IAsyncDisposable> AcquireAsync(TKey key, CancellationToken cancellationToken = default)
    {
        Entry entry;
        lock (_gate)
        {
            if (!_entries.TryGetValue(key, out entry!))
                _entries[key] = entry = new Entry();
            entry.References++;
        }

        try
        {
            await entry.Semaphore.WaitAsync(cancellationToken).ConfigureAwait(false);
            return new Releaser(this, key, entry);
        }
        catch
        {
            Release(key, entry);
            throw;
        }
    }

    private void Release(TKey key, Entry entry)
    {
        entry.Semaphore.Release();
        lock (_gate)
        {
            entry.References--;
            if (entry.References == 0)
            {
                _entries.Remove(key);
                entry.Semaphore.Dispose();
            }
        }
    }

    private sealed class Entry
    {
        public readonly SemaphoreSlim Semaphore = new(1, 1);
        public int References;
    }

    private sealed class Releaser(KeyedAsyncLock<TKey> owner, TKey key, Entry entry) : IAsyncDisposable
    {
        private int _released;

        public ValueTask DisposeAsync()
        {
            if (Interlocked.Exchange(ref _released, 1) == 0)
                owner.Release(key, entry);
            return ValueTask.CompletedTask;
        }
    }
}
