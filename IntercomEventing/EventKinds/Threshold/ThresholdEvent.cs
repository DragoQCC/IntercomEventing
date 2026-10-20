using System.Numerics;

namespace IntercomEventing;

/// <summary>
/// When a <see cref="ThresholdEvent{TValue,TPayload}"/> raises.
/// </summary>
public enum ThresholdRaiseMode
{
    /// <summary>
    /// Every increment that leaves the value at or past the threshold raises (the 1.x behaviour).
    /// </summary>
    EveryTimeReached,

    /// <summary>
    /// Only the increment that moves the value from below the threshold to at or past it raises. The event can raise again
    /// after the value drops below the threshold (or <see cref="ThresholdEvent{TValue,TPayload}.Reset()"/>) and crosses it again.
    /// </summary>
    OnlyWhenCrossed,
}

/// <summary>
/// Tracks a running value and raises when it reaches a threshold; <see cref="RaiseMode"/> decides whether every increment past
/// the threshold raises or only the one that crosses it. Each raise carries the payload passed to that increment.
/// </summary>
/// <remarks>
/// <para>Each increment updates the value atomically, so no increment is lost and every call carries its own consistent
/// <see cref="ThresholdEventCall{TValue,TPayload}.PreviousValue"/> and <see cref="ThresholdEventCall{TValue,TPayload}.CurrentValue"/>.</para>
/// <para>Raises are not serialized: increments from several threads deliver concurrently, so handlers can see the calls of
/// concurrent increments interleaved or out of order. Await each increment if subscribers need strict ordering.</para>
/// </remarks>
/// <typeparam name="TValue">A numeric type such as <see cref="int"/> or <see cref="decimal"/>.</typeparam>
/// <typeparam name="TPayload">Data passed with the increment that raised, for example the login attempt that crossed the limit.</typeparam>
/// <example>
/// <code>
/// public ThresholdEvent&lt;int, LoginAttempt&gt; TooManyFailedLogins { get; } = new(threshold: 3);
///
/// await TooManyFailedLogins.IncrementAsync(1, new LoginAttempt("ada", ipAddress));
///
/// security.TooManyFailedLogins.Subscribe(call =>
///     Console.WriteLine($"{call.Payload.UserName}: {call.CurrentValue}/{call.Threshold} failed logins"));
/// </code>
/// </example>
public class ThresholdEvent<TValue, TPayload> : IntercomEventBase<ThresholdEventCall<TValue, TPayload>>
    where TValue : INumber<TValue>
{
    private readonly Lock _valueLock = new();
    private readonly Func<TValue, TValue, bool> _isThresholdReached;
    private TValue _currentValue = TValue.Zero;

    /// <summary>
    /// The value at which the event raises.
    /// </summary>
    public TValue Threshold { get; }

    /// <summary>
    /// Whether every increment past the threshold raises, or only the crossing one.
    /// </summary>
    public ThresholdRaiseMode RaiseMode { get; }

    /// <summary>
    /// The running value.
    /// </summary>
    public TValue CurrentValue
    {
        get
        {
            lock (_valueLock)
            {
                return _currentValue;
            }
        }
    }

    /// <summary>
    /// Creates the event with a current value of zero.
    /// </summary>
    /// <param name="threshold">The value at which the event raises.</param>
    /// <param name="raiseMode">Whether every increment past the threshold raises (default) or only the crossing one.</param>
    /// <param name="isThresholdReached">Optional check <c>(currentValue, threshold) =&gt; bool</c>; defaults to
    /// <c>currentValue &gt;= threshold</c>. It runs inside the update lock, so keep it fast and free of side effects.</param>
    /// <param name="options">Dispatch settings; <see langword="null"/> uses <see cref="EventingOptions.Default"/> at each raise.</param>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="raiseMode"/> is not a defined <see cref="ThresholdRaiseMode"/>
    /// value, for example a cast from a number.</exception>
    /// <example>
    /// <code>
    /// var lowBattery = new ThresholdEvent&lt;int, Device&gt;(threshold: 15, ThresholdRaiseMode.OnlyWhenCrossed,
    ///     isThresholdReached: (charge, limit) => charge &lt;= limit);
    /// </code>
    /// </example>
    public ThresholdEvent(TValue threshold, ThresholdRaiseMode raiseMode = ThresholdRaiseMode.EveryTimeReached,
        Func<TValue, TValue, bool>? isThresholdReached = null, EventingOptions? options = null)
        : base(options)
    {
        //an undefined value (a bad cast, a numeric configuration value) would otherwise behave as OnlyWhenCrossed without a word
        var isDefinedRaiseMode = Enum.IsDefined(raiseMode);
        if (!isDefinedRaiseMode)
        {
            throw new ArgumentOutOfRangeException(nameof(raiseMode), raiseMode, "The value is not a defined ThresholdRaiseMode.");
        }
        Threshold = threshold;
        RaiseMode = raiseMode;
        _isThresholdReached = isThresholdReached ?? IsAtOrAboveThreshold;
    }

    /// <summary>
    /// Adds <paramref name="amount"/> to <see cref="CurrentValue"/> and raises if the threshold is reached according to
    /// <see cref="RaiseMode"/>.
    /// </summary>
    /// <remarks>
    /// The update is atomic, so concurrent increments each see their own resulting value in the call. A cancelled
    /// <paramref name="cancellationToken"/> or a disposed event is reported before the value changes; cancellation while
    /// handlers are starting leaves the increment applied.
    /// </remarks>
    /// <param name="amount">The amount to add (may be negative).</param>
    /// <param name="payload">Data delivered with the raise, if this increment raises.</param>
    /// <param name="caller">Optional object recorded as <see cref="EventMetadata.Caller"/>.</param>
    /// <param name="cancellationToken">Stops starting further handlers.</param>
    /// <returns><see langword="true"/> if this increment met the raise condition and the event was raised (to every current
    /// subscriber, possibly none).</returns>
    /// <exception cref="ObjectDisposedException">The event has been disposed.</exception>
    /// <exception cref="OperationCanceledException"><paramref name="cancellationToken"/> was cancelled.</exception>
    /// <example>
    /// <code>
    /// bool raised = await TooManyFailedLogins.IncrementAsync(1, new LoginAttempt("ada", ipAddress), caller: this);
    /// </code>
    /// </example>
    public async Task<bool> IncrementAsync(TValue amount, TPayload payload, object? caller = null, CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(IsDisposed, this);
        cancellationToken.ThrowIfCancellationRequested();

        TValue valueBeforeIncrement;
        TValue valueAfterIncrement;
        bool shouldRaise;
        lock (_valueLock)
        {
            valueBeforeIncrement = _currentValue;
            valueAfterIncrement = valueBeforeIncrement + amount;
            //decided before the value is stored, so a throwing custom check leaves the value unchanged
            shouldRaise = ShouldRaise(valueBeforeIncrement, valueAfterIncrement);
            _currentValue = valueAfterIncrement;
        }
        if (!shouldRaise)
        {
            return false;
        }

        //the values are captured per increment and travel in the call, so concurrent raises never share them
        var raiseState = (Threshold: Threshold, PreviousValue: valueBeforeIncrement, CurrentValue: valueAfterIncrement, Payload: payload);
        await DispatchAsync(
            raiseState,
            static (state, metadata, callCancellationToken) => new ThresholdEventCall<TValue, TPayload>(
                state.Threshold,
                state.PreviousValue,
                state.CurrentValue,
                state.Payload,
                metadata,
                callCancellationToken),
            caller,
            cancellationToken).ConfigureAwait(false);
        return true;
    }

    /// <summary>
    /// Sets <see cref="CurrentValue"/> back to zero without raising.
    /// </summary>
    /// <example>
    /// <code>
    /// TooManyFailedLogins.Reset();   // after a successful login
    /// </code>
    /// </example>
    public void Reset()
    {
        Reset(TValue.Zero);
    }

    /// <summary>
    /// Sets <see cref="CurrentValue"/> to <paramref name="startingValue"/> without raising.
    /// </summary>
    /// <param name="startingValue">The new current value.</param>
    /// <example>
    /// <code>
    /// queueBacklog.Reset(startingValue: queue.Count);
    /// </code>
    /// </example>
    public void Reset(TValue startingValue)
    {
        lock (_valueLock)
        {
            _currentValue = startingValue;
        }
    }

    private static bool IsAtOrAboveThreshold(TValue currentValue, TValue threshold)
    {
        return currentValue >= threshold;
    }

    private bool ShouldRaise(TValue valueBeforeIncrement, TValue valueAfterIncrement)
    {
        var reachedNow = _isThresholdReached(valueAfterIncrement, Threshold);
        if (!reachedNow)
        {
            return false;
        }
        if (RaiseMode == ThresholdRaiseMode.EveryTimeReached)
        {
            return true;
        }
        //OnlyWhenCrossed: raise only if the value was not already past the threshold before this increment
        var reachedBefore = _isThresholdReached(valueBeforeIncrement, Threshold);
        return !reachedBefore;
    }
}

/// <summary>
/// A <see cref="ThresholdEvent{TValue,TPayload}"/> without a payload, for plain counters.
/// </summary>
/// <typeparam name="TValue">A numeric type such as <see cref="int"/>.</typeparam>
/// <example>
/// <code>
/// var queueBacklog = new ThresholdEvent&lt;int&gt;(threshold: 1_000);
/// queueBacklog.Subscribe(call => alerts.Raise($"backlog at {call.CurrentValue}"));
/// await queueBacklog.IncrementAsync(1);
/// </code>
/// </example>
public class ThresholdEvent<TValue> : ThresholdEvent<TValue, NoPayload>
    where TValue : INumber<TValue>
{
    /// <summary>
    /// Creates the event with a current value of zero.
    /// </summary>
    /// <param name="threshold">The value at which the event raises.</param>
    /// <param name="raiseMode">Whether every increment past the threshold raises (default) or only the crossing one.</param>
    /// <param name="isThresholdReached">Optional check <c>(currentValue, threshold) =&gt; bool</c>; defaults to <c>currentValue &gt;= threshold</c>.</param>
    /// <param name="options">Dispatch settings; <see langword="null"/> uses <see cref="EventingOptions.Default"/> at each raise.</param>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="raiseMode"/> is not a defined <see cref="ThresholdRaiseMode"/> value.</exception>
    /// <example>
    /// <code>
    /// var lockout = new ThresholdEvent&lt;int&gt;(threshold: 3, ThresholdRaiseMode.OnlyWhenCrossed);
    /// </code>
    /// </example>
    public ThresholdEvent(TValue threshold, ThresholdRaiseMode raiseMode = ThresholdRaiseMode.EveryTimeReached,
        Func<TValue, TValue, bool>? isThresholdReached = null, EventingOptions? options = null)
        : base(threshold, raiseMode, isThresholdReached, options)
    {
    }

    /// <summary>
    /// Adds <paramref name="amount"/> and raises if the threshold is reached according to the raise mode.
    /// </summary>
    /// <param name="amount">The amount to add (may be negative).</param>
    /// <param name="caller">Optional object recorded as <see cref="EventMetadata.Caller"/>.</param>
    /// <param name="cancellationToken">Stops starting further handlers.</param>
    /// <returns><see langword="true"/> if this increment met the raise condition and the event was raised.</returns>
    /// <exception cref="ObjectDisposedException">The event has been disposed.</exception>
    /// <exception cref="OperationCanceledException"><paramref name="cancellationToken"/> was cancelled.</exception>
    /// <example>
    /// <code>
    /// bool raised = await queueBacklog.IncrementAsync(1);
    /// </code>
    /// </example>
    public Task<bool> IncrementAsync(TValue amount, object? caller = null, CancellationToken cancellationToken = default)
    {
        return IncrementAsync(amount, NoPayload.Value, caller, cancellationToken);
    }
}
