using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;

namespace IntercomEventing.Samples.BlazorServer.Features.Search;

public partial class SearchPage
{
    private const int MaxLogEntries = 8;

    private readonly List<string> _searchLog = [];

    [Inject]
    private ProductSearch Search { get; set; } = default!;

    protected IReadOnlyList<CatalogProduct> Results { get; private set; } = [];

    protected IReadOnlyList<string> SearchLog => _searchLog;

    protected string? LastSearchText { get; private set; }

    protected bool IsSearching { get; private set; }

    protected int SearchesRun { get; private set; }

    protected override void OnInitialized()
    {
        //the debounce raise happens on a background thread; the subscription moves the handler onto this page's renderer.
        //an asynchronous handler renders at its first await (showing "Searching…") and again when it completes
        Events.Subscribe(Search.QueryChanged, OnQueryChangedAsync);
    }

    protected void OnInput(ChangeEventArgs args)
    {
        Search.Type(args.Value?.ToString() ?? string.Empty);
    }

    protected async Task OnKeyDownAsync(KeyboardEventArgs args)
    {
        if (args.Key != "Enter")
        {
            return;
        }
        //raises the pending query now; false when the debounce had already raised it
        await Search.SubmitAsync();
    }

    private async Task OnQueryChangedAsync(DebounceEventCall<SearchQuery> call)
    {
        IsSearching = true;
        //the call's token is cancelled when the event is disposed, which happens when this tab's circuit ends
        Results = await Search.FindAsync(call.Payload.Text, call.CancellationToken);
        IsSearching = false;
        LastSearchText = call.Payload.Text;
        SearchesRun++;

        var keystrokeText = call.CoalescedTriggerCount == 1 ? "1 keystroke" : $"{call.CoalescedTriggerCount} keystrokes";
        _searchLog.Insert(0, $"\"{call.Payload.Text}\" after {keystrokeText}");
        if (_searchLog.Count > MaxLogEntries)
        {
            _searchLog.RemoveAt(_searchLog.Count - 1);
        }
    }
}
