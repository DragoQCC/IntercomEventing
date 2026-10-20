namespace IntercomEventing.Benchmark.Examples.Threshold;

/// <summary>
/// The payload delivered with the failed login that crossed the limit.
/// </summary>
public sealed record LoginAttempt(string UserName, string IpAddress);

/// <summary>
/// Counts the failed logins of one account and raises once when they reach the limit.
/// </summary>
public sealed class LoginGuard : IAsyncDisposable
{
    private const int FailedLoginLimit = 3;

    /// <summary>
    /// <see cref="ThresholdRaiseMode.OnlyWhenCrossed"/>: only the increment that reaches the limit raises; further failures
    /// stay quiet until <see cref="RecordSuccessfulLogin"/> resets the count.
    /// </summary>
    public ThresholdEvent<int, LoginAttempt> TooManyFailedLogins { get; } = new(threshold: FailedLoginLimit, ThresholdRaiseMode.OnlyWhenCrossed);

    /// <returns><see langword="true"/> when this failure raised the event.</returns>
    public Task<bool> RecordFailedLoginAsync(LoginAttempt attempt)
    {
        return TooManyFailedLogins.IncrementAsync(1, attempt, caller: this);
    }

    public void RecordSuccessfulLogin()
    {
        TooManyFailedLogins.Reset();
    }

    public ValueTask DisposeAsync()
    {
        return TooManyFailedLogins.DisposeAsync();
    }
}
