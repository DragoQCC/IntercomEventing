namespace IntercomEventing.Samples.WebApi.EventKinds.Batch;

/// <summary>
/// Why a <see cref="BatchEvent{TItem}"/> raised.
/// </summary>
public enum BatchRaiseReason
{
    /// <summary>
    /// An added item filled the batch.
    /// </summary>
    BatchFull,

    /// <summary>
    /// <see cref="BatchEvent{TItem}.FlushAsync"/> raised the items buffered so far.
    /// </summary>
    Flushed,
}

/// <summary>
/// The call delivered by a <see cref="BatchEvent{TItem}"/>: the items of one batch.
/// </summary>
/// <remarks>
/// It derives from the <see cref="EventCall"/> base class rather than <see cref="EventCall{TPayload}"/>, because a batch
/// has no single payload. Handlers written for <see cref="EventCall"/> still attach to a batch event.
/// </remarks>
/// <typeparam name="TItem">The type of the buffered items.</typeparam>
public sealed class BatchEventCall<TItem> : EventCall
{
    /// <summary>
    /// The items of this batch, in the order they were added. Read-only, and never changed after the raise.
    /// </summary>
    public IReadOnlyList<TItem> Items { get; }

    public BatchRaiseReason Reason { get; }

    /// <summary>
    /// The number of items that fills a batch; a flushed batch can hold fewer.
    /// </summary>
    public int BatchSize { get; }

    public BatchEventCall(IReadOnlyList<TItem> items, BatchRaiseReason reason, int batchSize, EventMetadata metadata,
        CancellationToken cancellationToken = default)
        : base(metadata, cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(items);
        Items = items;
        Reason = reason;
        BatchSize = batchSize;
    }

    public override string ToString()
    {
        return $"{Metadata.EventName} call {Metadata.EventCallId}: {Items.Count} item(s), {Reason}";
    }
}
