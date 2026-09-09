using System.Collections.Concurrent;
using CodeMap.Core;
using CodeMap.Engine.Concurrency;
using CodeMap.Storage;

namespace CodeMap.Engine.Application;

/// <summary>
/// Long-lived freshness implementation for MCP/LSP hosts. Each repository has
/// its own watcher and single-flight probe, so unrelated roots do not serialize.
/// </summary>
public sealed class CachedIndexFreshnessService : IIndexFreshnessService, IDisposable
{
    private readonly ConcurrentDictionary<string, RootFreshnessState> _states = new(StringComparer.OrdinalIgnoreCase);
    private readonly Func<string, CancellationToken, Task<bool>> _probe;
    private readonly Func<string, FileSystemWatcher> _watcherFactory;
    private int _disposed;

    public CachedIndexFreshnessService(
        Func<string, CancellationToken, Task<bool>>? probe = null,
        Func<string, FileSystemWatcher>? watcherFactory = null)
    {
        _probe = probe ?? ((root, cancellationToken) => new IncrementalCodeMapIndexer().IsUpToDateAsync(root, cancellationToken));
        _watcherFactory = watcherFactory ?? (root => new FileSystemWatcher(root)
        {
            IncludeSubdirectories = true,
            NotifyFilter = NotifyFilters.FileName | NotifyFilters.LastWrite | NotifyFilters.Size
        });
    }

    public async Task<bool> IsUpToDateAsync(string root, CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed != 0, this);
        var key = Path.GetFullPath(root);
        var state = _states.GetOrAdd(key, _ => new RootFreshnessState());
        Task<bool> probeTask;
        lock (state.Gate)
        {
            if (state.CachedUpToDate is { } cached)
                return cached;
            EnsureWatcher(key, state);
            state.InFlight ??= ProbeAndCacheAsync(key, state);
            probeTask = state.InFlight;
        }
        return await probeTask.WaitAsync(cancellationToken).ConfigureAwait(false);
    }

    public void Invalidate(string root)
    {
        if (_states.TryGetValue(Path.GetFullPath(root), out var state))
            Invalidate(state);
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
            return;
        foreach (var state in _states.Values)
        {
            lock (state.Gate)
                state.Watcher?.Dispose();
        }
        _states.Clear();
    }

    private async Task<bool> ProbeAndCacheAsync(string root, RootFreshnessState state)
    {
        try
        {
            int generation;
            lock (state.Gate)
                generation = state.Generation;
            var result = await _probe(root, CancellationToken.None).ConfigureAwait(false);
            lock (state.Gate)
            {
                if (state.Generation == generation && state.Watcher is not null)
                    state.CachedUpToDate = result;
                return state.Generation == generation && result;
            }
        }
        finally
        {
            lock (state.Gate)
                state.InFlight = null;
        }
    }

    private void EnsureWatcher(string root, RootFreshnessState state)
    {
        if (state.Watcher is not null)
            return;
        try
        {
            var watcher = _watcherFactory(root);
            void Changed(object? _, FileSystemEventArgs args)
            {
                if (!IgnoreRules.IsIgnored(root, args.FullPath) && !IgnoreRules.IsOutsideRoot(root, args.FullPath))
                    Invalidate(state);
            }
            watcher.Changed += Changed;
            watcher.Created += Changed;
            watcher.Deleted += Changed;
            watcher.Renamed += (_, args) =>
            {
                if ((!IgnoreRules.IsIgnored(root, args.OldFullPath) && !IgnoreRules.IsOutsideRoot(root, args.OldFullPath))
                    || (!IgnoreRules.IsIgnored(root, args.FullPath) && !IgnoreRules.IsOutsideRoot(root, args.FullPath)))
                    Invalidate(state);
            };
            watcher.Error += (_, _) =>
            {
                lock (state.Gate)
                {
                    state.CachedUpToDate = null;
                    state.Generation++;
                    state.Watcher?.Dispose();
                    state.Watcher = null;
                }
            };
            watcher.EnableRaisingEvents = true;
            state.Watcher = watcher;
        }
        catch
        {
            // A failed watcher must not turn a one-time probe into a durable
            // cache. The next caller will retry watcher construction.
        }
    }

    private static void Invalidate(RootFreshnessState state)
    {
        lock (state.Gate)
        {
            state.CachedUpToDate = null;
            state.Generation++;
        }
    }

    private sealed class RootFreshnessState
    {
        public readonly object Gate = new();
        public int Generation;
        public bool? CachedUpToDate;
        public Task<bool>? InFlight;
        public FileSystemWatcher? Watcher;
    }
}

public sealed class UncachedIndexFreshnessService : IIndexFreshnessService
{
    public Task<bool> IsUpToDateAsync(string root, CancellationToken cancellationToken = default) =>
        new IncrementalCodeMapIndexer().IsUpToDateAsync(root, cancellationToken);

    public void Invalidate(string root)
    {
    }
}
