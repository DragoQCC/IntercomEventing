namespace IntercomEventing.Benchmark.Examples.Debounce;

/// <summary>
/// The payload of each keystroke; only the last one of a burst is delivered.
/// </summary>
public sealed record SearchQuery(string Text);

/// <summary>
/// A search box that searches once the user stops typing, or immediately when the user presses Enter.
/// </summary>
public sealed class SearchBox : IAsyncDisposable
{
    /// <summary>
    /// Raises once the input has been quiet for the interval, with the last query and the number of keystrokes it absorbed.
    /// </summary>
    public DebounceEvent<SearchQuery> SearchChanged { get; }

    public SearchBox(TimeSpan quietPeriod)
    {
        SearchChanged = new DebounceEvent<SearchQuery>(quietPeriod);
    }

    /// <summary>
    /// Called on every keystroke. Never waits: the raise happens later in the background.
    /// </summary>
    public void Type(string text)
    {
        SearchChanged.Trigger(new SearchQuery(text), caller: this);
    }

    /// <summary>
    /// Enter pressed: raise the pending query now and wait for its handlers.
    /// </summary>
    /// <returns><see langword="true"/> when a pending query was raised.</returns>
    public Task<bool> SubmitAsync()
    {
        return SearchChanged.FlushAsync();
    }

    public ValueTask DisposeAsync()
    {
        return SearchChanged.DisposeAsync();
    }
}
