namespace IntercomEventing.Samples.WebApi.EventKinds.Batch;

/// <summary>
/// A custom event kind: buffers items and raises them together, once <see cref="BatchSize"/> items have been added or when
/// <see cref="FlushAsync"/> is called. Use it to turn many small writes (audit entries, metrics) into fewer large ones.
/// </summary>
/// <remarks>
/// <list type="bullet">
/// <item><description><see cref="AddAsync"/> raises, and waits for the handlers, only when its item fills the batch; otherwise
/// it returns at once.</description></item>
/// <item><description>The event has no timer of its own. Call <see cref="FlushAsync"/> periodically (a
/// <see cref="TimedEvent"/> works well) so a quiet period does not leave items waiting.</description></item>
/// <item><description>Disposing the event drops buffered items, and so does a raise without subscribers: flush, and keep the
/// subscribers, until the producer is done.</description></item>
/// </list>
/// <para>It is built on the protected <c>DispatchAsync</c> and <c>OnDisposingAsync</c> of
/// <see cref="IntercomEventBase{TCall}"/>; subscribing, exception routing, timeouts and disposal come from the base.</para>
/// </remarks>
/// <typeparam name="TItem">The type of the buffered items.</typeparam>
/// <example>
/// <code>
/// var entriesBatched = new BatchEvent&lt;AuditEntry&gt;(batchSize: 50);
/// entriesBatched.Subscribe(async call => await auditStore.WriteAsync(call.Items, call.CancellationToken));
///
/// await entriesBatched.AddAsync(entry, caller: this);   // raises on every 50th entry
/// await entriesBatched.FlushAsync(caller: this);        // raises whatever is left
/// </code>
/// </example>
public sealed class BatchEvent<TItem> : IntercomEventBase<BatchEventCall<TItem>>
{
    private readonly Lock _bufferLock = new();

    //replaced, never cleared, when a batch is taken, so the list a call carries is never changed afterwards
    private List<TItem> _buffer = [];

    /// <summary>
    /// The number of items that fills a batch and raises the event.
    /// </summary>
    public int BatchSize { get; }

    /// <summary>
    /// The number of items added since the last raise.
    /// </summary>
    public int PendingCount
    {
        get
        {
            lock (_bufferLock)
            {
                return _buffer.Count;
            }
        }
    }

    /// <summary>
    /// Creates the event.
    /// </summary>
    /// <param name="batchSize">The number of items that fills a batch; at least 1.</param>
    /// <param name="options">Dispatch settings; <see langword="null"/> uses <see cref="EventingOptions.Default"/>.</param>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="batchSize"/> is less than 1.</exception>
    public BatchEvent(int batchSize, EventingOptions? options = null)
        : base(options)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(batchSize, 1);
        BatchSize = batchSize;
    }

    /// <summary>
    /// Adds an item. When it fills the batch, raises the batch and waits for its handlers.
    /// </summary>
    /// <remarks>
    /// Takes no <see cref="CancellationToken"/>: once a batch has left the buffer, cancelling its raise would lose the items.
    /// </remarks>
    /// <param name="item">The item to buffer.</param>
    /// <param name="caller">Optional object recorded as <see cref="EventMetadata.Caller"/> if this item raises the batch.</param>
    /// <returns><see langword="true"/> if this item filled the batch and it was raised.</returns>
    /// <exception cref="ObjectDisposedException">The event has been disposed.</exception>
    public async Task<bool> AddAsync(TItem item, object? caller = null)
    {
        ObjectDisposedException.ThrowIf(IsDisposed, this);
        IReadOnlyList<TItem>? fullBatch = null;
        lock (_bufferLock)
        {
            _buffer.Add(item);
            if (_buffer.Count >= BatchSize)
            {
                fullBatch = TakeBufferLocked();
            }
        }
        if (fullBatch is null)
        {
            return false;
        }
        await RaiseBatchAsync(fullBatch, BatchRaiseReason.BatchFull, caller);
        return true;
    }

    /// <summary>
    /// Raises the buffered items now, even when they do not fill a batch, and waits for the handlers.
    /// </summary>
    /// <param name="caller">Optional object recorded as <see cref="EventMetadata.Caller"/>.</param>
    /// <returns><see langword="false"/> when nothing was buffered, so nothing was raised.</returns>
    /// <exception cref="ObjectDisposedException">The event has been disposed.</exception>
    public async Task<bool> FlushAsync(object? caller = null)
    {
        ObjectDisposedException.ThrowIf(IsDisposed, this);
        IReadOnlyList<TItem> pendingItems;
        lock (_bufferLock)
        {
            if (_buffer.Count == 0)
            {
                return false;
            }
            pendingItems = TakeBufferLocked();
        }
        await RaiseBatchAsync(pendingItems, BatchRaiseReason.Flushed, caller);
        return true;
    }

    /// <inheritdoc />
    protected override ValueTask OnDisposingAsync()
    {
        //raising is no longer possible at this point, so buffered items are dropped
        lock (_bufferLock)
        {
            _buffer = [];
        }
        return ValueTask.CompletedTask;
    }

    //caller holds _bufferLock
    private IReadOnlyList<TItem> TakeBufferLocked()
    {
        var batch = _buffer;
        _buffer = [];
        //a read-only wrapper, so a handler cannot cast the items back to a List and change them for the other handlers
        return batch.AsReadOnly();
    }

    private Task RaiseBatchAsync(IReadOnlyList<TItem> items, BatchRaiseReason reason, object? caller)
    {
        //the raise data goes into the call through the state argument, never through fields read later
        var raiseState = (Items: items, Reason: reason, BatchSize: BatchSize);
        return DispatchAsync(
            raiseState,
            static (state, metadata, callCancellationToken) => new BatchEventCall<TItem>(
                state.Items,
                state.Reason,
                state.BatchSize,
                metadata,
                callCancellationToken),
            caller,
            CancellationToken.None);
    }
}
