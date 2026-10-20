namespace IntercomEventing.Benchmark.Examples.Debounce;

/// <summary>
/// <c>DebounceEvent</c>: a burst of triggers becomes one raise with the last payload, and <c>FlushAsync</c> raises a pending
/// trigger immediately.
/// </summary>
internal sealed class DebounceEventExample : IExample
{
    private static readonly TimeSpan QuietPeriod = TimeSpan.FromMilliseconds(200);

    //generous upper bound for the background raise to arrive on a busy machine
    private static readonly TimeSpan RaiseWaitLimit = TimeSpan.FromSeconds(5);

    public string Name => "Debounce";

    public string Summary => "DebounceEvent: coalesce bursts of triggers into one raise";

    public async Task RunAsync()
    {
        await using var searchBox = new SearchBox(QuietPeriod);
        var searches = new List<DebounceEventCall<SearchQuery>>();
        var firstSearch = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        searchBox.SearchChanged.Subscribe(call =>
        {
            lock (searches)
            {
                searches.Add(call);
            }
            Console.WriteLine($"Searching for '{call.Payload.Text}' (keystrokes coalesced: {call.CoalescedTriggerCount})");
            firstSearch.TrySetResult();
        });

        ExampleCheck.Step($"Typing 'ada' in one burst, then pausing for {QuietPeriod.TotalMilliseconds} ms");
        searchBox.Type("a");
        searchBox.Type("ad");
        searchBox.Type("ada");
        await firstSearch.Task.WaitAsync(RaiseWaitLimit);
        var burstSearch = GetSearches(searches)[0];
        ExampleCheck.That(burstSearch.Payload.Text == "ada", "the burst raised once with the last payload");
        ExampleCheck.That(burstSearch.CoalescedTriggerCount == 3, "CoalescedTriggerCount reports the three keystrokes");

        ExampleCheck.Step("Typing 'grace' and pressing Enter at once");
        searchBox.Type("grace");
        var flushed = await searchBox.SubmitAsync();
        ExampleCheck.That(flushed, "FlushAsync found the pending trigger and raised it");
        ExampleCheck.That(GetSearches(searches).Count == 2, "the flushed search was handled before FlushAsync returned");

        await Task.Delay(QuietPeriod * 2);
        ExampleCheck.That(GetSearches(searches).Count == 2, "the flushed trigger did not raise a second time after its quiet period");
        ExampleCheck.That(!searchBox.SearchChanged.HasPendingRaise, "nothing is pending");
    }

    private static IReadOnlyList<DebounceEventCall<SearchQuery>> GetSearches(List<DebounceEventCall<SearchQuery>> searches)
    {
        lock (searches)
        {
            return [.. searches];
        }
    }
}
