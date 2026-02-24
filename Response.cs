Great catch. The Task.WhenAll fan-out per key defeats the purpose of batch fetching. We need to coalesce concurrent misses into a single batch call. Here's the fix — using a batch coalescing window that collects missing keys briefly, then fires one bulk fetch:
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;

// ──────────────────────────────────────────────
// Contracts
// ──────────────────────────────────────────────

public enum ChangeType { Updated, Deleted }

public readonly record struct CacheChangeNotification<TKey>(
    IReadOnlyCollection<TKey> Keys,
    ChangeType ChangeType
);

public interface ICacheDataSource<TKey, TValue>
{
    Task<IDictionary<TKey, TValue>> FetchAsync(
        IReadOnlyCollection<TKey> keys,
        CancellationToken ct = default);
}

public interface ICacheChangeListener<TKey>
{
    Task NotifyChangedAsync(IReadOnlyCollection<TKey> keys, ChangeType changeType);
    Task NotifyChangedAsync(TKey key, ChangeType changeType);
}

// ──────────────────────────────────────────────
// Cache Entry
// ──────────────────────────────────────────────

internal sealed class CacheEntry<TValue>
{
    public TValue Value { get; }
    public DateTime CreatedAtUtc { get; }
    private long _lastAccessedTicks;

    public DateTime LastAccessedUtc
        => new(Interlocked.Read(ref _lastAccessedTicks), DateTimeKind.Utc);

    public CacheEntry(TValue value)
    {
        Value = value;
        CreatedAtUtc = DateTime.UtcNow;
        _lastAccessedTicks = DateTime.UtcNow.Ticks;
    }

    public void Touch()
        => Interlocked.Exchange(ref _lastAccessedTicks, DateTime.UtcNow.Ticks);
}

// ──────────────────────────────────────────────
// Options
// ──────────────────────────────────────────────

public sealed class CacheOptions
{
    public TimeSpan PurgeInterval { get; init; } = TimeSpan.FromMinutes(5);
    public TimeSpan UnusedThreshold { get; init; } = TimeSpan.FromMinutes(30);
    public TimeSpan AbsoluteExpiration { get; init; } = TimeSpan.FromHours(2);
    public int? MaxItems { get; init; }
    public int ChangeQueueCapacity { get; init; } = 1_000;

    /// <summary>
    /// How long the batcher waits to collect keys before firing a bulk fetch.
    /// Shorter = lower latency, longer = better batching.
    /// </summary>
    public TimeSpan BatchCoalesceWindow { get; init; } = TimeSpan.FromMilliseconds(50);

    /// <summary>
    /// Max keys per batch fetch call. If more keys accumulate, multiple
    /// batches are dispatched.
    /// </summary>
    public int MaxBatchSize { get; init; } = 500;
}

// ──────────────────────────────────────────────
// Pending fetch request: a key + its completion
// ──────────────────────────────────────────────

internal sealed class FetchRequest<TKey, TValue>
{
    public TKey Key { get; }
    public TaskCompletionSource<TValue> Completion { get; }

    public FetchRequest(TKey key)
    {
        Key = key;
        Completion = new TaskCompletionSource<TValue>(
            TaskCreationOptions.RunContinuationsAsynchronously);
    }
}

// ──────────────────────────────────────────────
// Batch Fetcher: collects keys → single bulk call
// ──────────────────────────────────────────────

internal sealed class BatchFetcher<TKey, TValue> : IDisposable
    where TKey : notnull
{
    private readonly ICacheDataSource<TKey, TValue> _dataSource;
    private readonly Channel<FetchRequest<TKey, TValue>> _requestChannel;
    private readonly CacheOptions _options;
    private readonly CancellationTokenSource _cts;
    private readonly Task _processorTask;

    public BatchFetcher(
        ICacheDataSource<TKey, TValue> dataSource,
        CacheOptions options,
        CancellationTokenSource cts)
    {
        _dataSource = dataSource;
        _options = options;
        _cts = cts;

        _requestChannel = Channel.CreateUnbounded<FetchRequest<TKey, TValue>>(
            new UnboundedChannelOptions { SingleReader = true });

        _processorTask = ProcessBatchesAsync(_cts.Token);
    }

    /// <summary>
    /// Enqueue a key to be fetched. Returns a task that completes
    /// when the batch containing this key finishes.
    /// </summary>
    public Task<TValue> EnqueueAsync(TKey key)
    {
        var request = new FetchRequest<TKey, TValue>(key);
        _requestChannel.Writer.TryWrite(request);
        return request.Completion.Task;
    }

    private async Task ProcessBatchesAsync(CancellationToken ct)
    {
        try
        {
            while (await _requestChannel.Reader.WaitToReadAsync(ct))
            {
                var batch = new List<FetchRequest<TKey, TValue>>();

                // Drain everything immediately available
                while (_requestChannel.Reader.TryRead(out var request))
                {
                    batch.Add(request);
                }

                // Brief window to let more keys arrive
                if (batch.Count < _options.MaxBatchSize)
                {
                    using var delayCts = CancellationTokenSource
                        .CreateLinkedTokenSource(ct);

                    try
                    {
                        delayCts.CancelAfter(_options.BatchCoalesceWindow);
                        while (batch.Count < _options.MaxBatchSize)
                        {
                            var request = await _requestChannel.Reader
                                .ReadAsync(delayCts.Token);
                            batch.Add(request);
                        }
                    }
                    catch (OperationCanceledException) when (!ct.IsCancellationRequested)
                    {
                        // Window elapsed — that's fine, process what we have
                    }
                }

                // Dispatch in chunks of MaxBatchSize
                foreach (var chunk in Chunk(batch, _options.MaxBatchSize))
                {
                    await DispatchBatchAsync(chunk, ct);
                }
            }
        }
        catch (OperationCanceledException) { /* shutting down */ }
    }

    private async Task DispatchBatchAsync(
        List<FetchRequest<TKey, TValue>> batch,
        CancellationToken ct)
    {
        // Deduplicate: multiple callers may want the same key
        var grouped = batch
            .GroupBy(r => r.Key)
            .ToDictionary(g => g.Key, g => g.ToList());

        try
        {
            var results = await _dataSource.FetchAsync(
                grouped.Keys.ToList(), ct);

            foreach (var (key, requests) in grouped)
            {
                var found = results != null &&
                    results.TryGetValue(key, out var value);

                foreach (var req in requests)
                {
                    if (found)
                        req.Completion.TrySetResult(value);
                    else
                        req.Completion.TrySetResult(default);
                }
            }
        }
        catch (Exception ex)
        {
            // Fail all waiters in this batch
            foreach (var req in batch)
                req.Completion.TrySetException(ex);
        }
    }

    private static IEnumerable<List<T>> Chunk<T>(List<T> source, int size)
    {
        for (int i = 0; i < source.Count; i += size)
            yield return source.GetRange(i, Math.Min(size, source.Count - i));
    }

    public void Dispose()
    {
        _requestChannel.Writer.TryComplete();
        try { _processorTask.GetAwaiter().GetResult(); } catch { }
    }
}

// ──────────────────────────────────────────────
// Cache
// ──────────────────────────────────────────────

public sealed class Cache<TKey, TValue> : ICacheChangeListener<TKey>, IDisposable
    where TKey : notnull
{
    private readonly ConcurrentDictionary<TKey, CacheEntry<TValue>> _store = new();
    private readonly ICacheDataSource<TKey, TValue> _dataSource;
    private readonly CacheOptions _options;
    private readonly BatchFetcher<TKey, TValue> _batchFetcher;
    private readonly Channel<CacheChangeNotification<TKey>> _changeChannel;
    private readonly CancellationTokenSource _cts = new();
    private readonly Task _changeProcessorTask;
    private readonly Task _purgeTask;

    public Cache(ICacheDataSource<TKey, TValue> dataSource, CacheOptions options = null)
    {
        _dataSource = dataSource ?? throw new ArgumentNullException(nameof(dataSource));
        _options = options ?? new CacheOptions();

        _batchFetcher = new BatchFetcher<TKey, TValue>(_dataSource, _options, _cts);

        _changeChannel = Channel.CreateBounded<CacheChangeNotification<TKey>>(
            new BoundedChannelOptions(_options.ChangeQueueCapacity)
            {
                FullMode = BoundedChannelFullMode.DropOldest,
                SingleReader = true
            });

        _changeProcessorTask = ProcessChangesAsync(_cts.Token);
        _purgeTask = PurgeLoopAsync(_cts.Token);
    }

    // ──────────────────────────────────────────────
    // Get single
    // ──────────────────────────────────────────────
    public async Task<TValue> GetAsync(TKey key)
    {
        if (TryGetFromStore(key, out var value))
            return value;

        // Enqueue into batcher — coalesced with other concurrent misses
        var fetched = await _batchFetcher.EnqueueAsync(key);

        // Store result (batcher returns it, but we need it in our store)
        if (fetched is not null)
            _store.TryAdd(key, new CacheEntry<TValue>(fetched));

        return fetched;
    }

    // ──────────────────────────────────────────────
    // Get batch
    // ──────────────────────────────────────────────
    public async Task<IDictionary<TKey, TValue>> GetAsync(HashSet<TKey> keys)
    {
        var result = new Dictionary<TKey, TValue>(keys.Count);
        var fetchTasks = new List<(TKey Key, Task<TValue> Task)>();

        foreach (var key in keys)
        {
            if (TryGetFromStore(key, out var value))
            {
                result[key] = value;
            }
            else
            {
                // All misses funnel into the same batcher —
                // they'll be collected into one bulk fetch
                fetchTasks.Add((key, _batchFetcher.EnqueueAsync(key)));
            }
        }

        if (fetchTasks.Count > 0)
        {
            await Task.WhenAll(fetchTasks.Select(t => t.Task));

            foreach (var (key, task) in fetchTasks)
            {
                var fetched = await task;
                if (fetched is not null)
                {
                    _store.TryAdd(key, new CacheEntry<TValue>(fetched));
                    result[key] = fetched;
                }
            }
        }

        return result;
    }

    // ──────────────────────────────────────────────
    // Change notifications
    // ──────────────────────────────────────────────
    public async Task NotifyChangedAsync(IReadOnlyCollection<TKey> keys, ChangeType changeType)
    {
        await _changeChannel.Writer.WriteAsync(
            new CacheChangeNotification<TKey>(keys, changeType));
    }

    public Task NotifyChangedAsync(TKey key, ChangeType changeType)
        => NotifyChangedAsync(new[] { key }, changeType);

    // ──────────────────────────────────────────────
    // Background: Process changes
    // ──────────────────────────────────────────────
    private async Task ProcessChangesAsync(CancellationToken ct)
    {
        try
        {
            await foreach (var notification in _changeChannel.Reader.ReadAllAsync(ct))
            {
                try
                {
                    switch (notification.ChangeType)
                    {
                        case ChangeType.Deleted:
                            foreach (var key in notification.Keys)
                                _store.TryRemove(key, out _);
                            break;

                        case ChangeType.Updated:
                            var keysToRefresh = notification.Keys
                                .Where(k => _store.ContainsKey(k))
                                .ToList();

                            if (keysToRefresh.Count == 0)
                                break;

                            foreach (var key in keysToRefresh)
                                _store.TryRemove(key, out _);

                            var freshData = await _dataSource.FetchAsync(keysToRefresh, ct);
                            if (freshData != null)
                            {
                                foreach (var kvp in freshData)
                                    _store[kvp.Key] = new CacheEntry<TValue>(kvp.Value);
                            }
                            break;
                    }
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    System.Diagnostics.Debug.WriteLine(
                        $"Cache change processing error: {ex.Message}");
                }
            }
        }
        catch (OperationCanceledException) { /* shutting down */ }
    }

    // ──────────────────────────────────────────────
    // Background: Purge
    // ──────────────────────────────────────────────
    private async Task PurgeLoopAsync(CancellationToken ct)
    {
        using var timer = new PeriodicTimer(_options.PurgeInterval);
        try
        {
            while (await timer.WaitForNextTickAsync(ct))
            {
                var now = DateTime.UtcNow;

                var stale = _store
                    .Where(kvp =>
                        (now - kvp.Value.LastAccessedUtc) > _options.UnusedThreshold ||
                        (now - kvp.Value.CreatedAtUtc) > _options.AbsoluteExpiration)
                    .Select(kvp => kvp.Key)
                    .ToList();

                foreach (var key in stale)
                    _store.TryRemove(key, out _);

                if (_options.MaxItems.HasValue && _store.Count > _options.MaxItems.Value)
                {
                    var excess = _store
                        .OrderBy(x => x.Value.LastAccessedUtc)
                        .Take(_store.Count - _options.MaxItems.Value)
                        .Select(x => x.Key)
                        .ToList();

                    foreach (var key in excess)
                        _store.TryRemove(key, out _);
                }
            }
        }
        catch (OperationCanceledException) { /* shutting down */ }
    }

    // ──────────────────────────────────────────────
    // Helpers
    // ──────────────────────────────────────────────
    private bool TryGetFromStore(TKey key, out TValue value)
    {
        if (_store.TryGetValue(key, out var entry) &&
            (DateTime.UtcNow - entry.CreatedAtUtc) <= _options.AbsoluteExpiration)
        {
            entry.Touch();
            value = entry.Value;
            return true;
        }
        value = default;
        return false;
    }

    public int Count => _store.Count;

    public void Dispose()
    {
        _cts.Cancel();
        _changeChannel.Writer.TryComplete();
        _batchFetcher.Dispose();
        try { _changeProcessorTask.GetAwaiter().GetResult(); } catch { }
        try { _purgeTask.GetAwaiter().GetResult(); } catch { }
        _cts.Dispose();
        _store.Clear();
    }
}
What the BatchFetcher does:
Thread 1: GetAsync(key: 1)  ──┐
Thread 2: GetAsync(key: 2)  ──┤
Thread 3: GetAsync(key: 3)  ──┼──→ BatchFetcher collects [1,2,3]
Thread 4: GetAsync(key: 1)  ──┤    during 50ms window, deduplicates,
Thread 5: GetAsync(key: 4)  ──┘    fires ONE FetchAsync([1,2,3,4])
                                          │
                                          ▼
                                   DataSource.FetchAsync()
                                          │
                                          ▼
                              All 5 TaskCompletionSources resolved
The key differences from the previous version:
No per-key fetches at all. Both GetAsync(TKey) and GetAsync(HashSet<TKey>) funnel misses into the same BatchFetcher. A single GetAsync(key) that happens alongside a GetAsync(1000 keys) will all land in one bulk call.
Coalesce window is configurable. The BatchCoalesceWindow (default 50ms) controls the tradeoff between latency and batching efficiency. Set it lower for real-time use cases, higher for throughput-heavy workloads.
MaxBatchSize prevents giant queries. If 10,000 keys accumulate, they're chunked into batches of 500 (configurable), each dispatched as a separate FetchAsync call.
Deduplication is automatic. If 50 threads all request the same key concurrently, there's one FetchRequest per unique key, and all waiters share the result via their TaskCompletionSource.
