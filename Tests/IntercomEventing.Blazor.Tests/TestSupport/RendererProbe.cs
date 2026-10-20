using Microsoft.AspNetCore.Components;

namespace IntercomEventing.Blazor.Tests.TestSupport;

/// <summary>
/// Records how many handler calls the sample components saw and whether each one ran on the renderer's dispatcher.
/// </summary>
public sealed class RendererProbe
{
    //read lazily: in bUnit, touching the renderer early would stop the test from registering more services
    private readonly Func<Dispatcher> _getRendererDispatcher;
    private int _handlerCalls;
    private int _callsOffTheRenderer;

    public int HandlerCalls => Volatile.Read(ref _handlerCalls);

    public int CallsOffTheRenderer => Volatile.Read(ref _callsOffTheRenderer);

    public RendererProbe(Func<Dispatcher> getRendererDispatcher)
    {
        _getRendererDispatcher = getRendererDispatcher;
    }

    public void RecordHandlerCall()
    {
        Interlocked.Increment(ref _handlerCalls);
        var runsOnTheRenderer = _getRendererDispatcher().CheckAccess();
        if (!runsOnTheRenderer)
        {
            Interlocked.Increment(ref _callsOffTheRenderer);
        }
    }
}

/// <summary>
/// Holds asynchronous handlers at an await until the test releases them, so a test can look at the component in between.
/// </summary>
public sealed class HandlerGate
{
    private readonly TaskCompletionSource _release = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public Task WaitAsync()
    {
        return _release.Task;
    }

    public void Release()
    {
        _release.TrySetResult();
    }
}
