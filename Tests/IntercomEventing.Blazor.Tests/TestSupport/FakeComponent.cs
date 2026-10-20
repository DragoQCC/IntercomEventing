using Microsoft.AspNetCore.Components;

namespace IntercomEventing.Blazor.Tests.TestSupport;

/// <summary>
/// Stands in for the protected members a component hands to <see cref="ComponentEventSubscriptions"/>, so the helper can be
/// tested without a renderer: it counts renders, records dispatched exceptions and can hold work items until released.
/// </summary>
public sealed class FakeComponent
{
    private readonly Lock _stateLock = new();
    private readonly List<Func<Task>> _heldWorkItems = [];
    private readonly List<Exception> _dispatchedExceptions = [];
    private int _renderCount;
    private int _invokeCount;

    /// <summary>
    /// When <see langword="true"/>, <see cref="InvokeAsync"/> queues work instead of running it, like a busy renderer.
    /// </summary>
    public bool HoldWorkItems { get; set; }

    /// <summary>
    /// What <see cref="GetRendererInfo"/> reports; <see langword="null"/> makes it throw, like bUnit before SetRendererInfo.
    /// </summary>
    public RendererInfo? RendererInfo { get; set; } = new("Server", isInteractive: true);

    public int RenderCount => Volatile.Read(ref _renderCount);

    public int InvokeCount => Volatile.Read(ref _invokeCount);

    public int HeldWorkItemCount
    {
        get
        {
            lock (_stateLock)
            {
                return _heldWorkItems.Count;
            }
        }
    }

    public IReadOnlyList<Exception> DispatchedExceptions
    {
        get
        {
            lock (_stateLock)
            {
                return [.. _dispatchedExceptions];
            }
        }
    }

    public ComponentEventSubscriptions CreateSubscriptions(bool dispatchExceptions = true, bool guardPrerendering = true)
    {
        Func<Exception, Task>? dispatchExceptionAsync = dispatchExceptions ? DispatchExceptionAsync : null;
        Func<RendererInfo>? getRendererInfo = guardPrerendering ? GetRendererInfo : null;
        return new ComponentEventSubscriptions(InvokeAsync, StateHasChanged, dispatchExceptionAsync, getRendererInfo);
    }

    public Task InvokeAsync(Func<Task> workItem)
    {
        Interlocked.Increment(ref _invokeCount);
        if (!HoldWorkItems)
        {
            return workItem();
        }
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        lock (_stateLock)
        {
            _heldWorkItems.Add(() => RunAndComplete(workItem, completion));
        }
        return completion.Task;
    }

    public void StateHasChanged()
    {
        Interlocked.Increment(ref _renderCount);
    }

    public Task DispatchExceptionAsync(Exception exception)
    {
        lock (_stateLock)
        {
            _dispatchedExceptions.Add(exception);
        }
        return Task.CompletedTask;
    }

    public RendererInfo GetRendererInfo()
    {
        return RendererInfo ?? throw new InvalidOperationException("This renderer does not report its RendererInfo.");
    }

    /// <summary>
    /// Runs the work items queued while <see cref="HoldWorkItems"/> was set.
    /// </summary>
    public async Task RunHeldWorkItemsAsync()
    {
        Func<Task>[] workItems;
        lock (_stateLock)
        {
            workItems = [.. _heldWorkItems];
            _heldWorkItems.Clear();
        }
        foreach (var workItem in workItems)
        {
            await workItem();
        }
    }

    private static async Task RunAndComplete(Func<Task> workItem, TaskCompletionSource completion)
    {
        try
        {
            await workItem();
            completion.SetResult();
        }
        catch (Exception exception)
        {
            completion.SetException(exception);
        }
    }
}
