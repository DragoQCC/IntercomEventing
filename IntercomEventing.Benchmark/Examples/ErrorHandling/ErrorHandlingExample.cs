using System.Collections.Concurrent;

namespace IntercomEventing.Benchmark.Examples.ErrorHandling;

/// <summary>
/// Exception routing: a subscription's own exception handler first, then the event's
/// <see cref="EventingOptions.DefaultExceptionHandler"/>, else the exception is swallowed. Handler exceptions never reach the
/// code that raised the event, and a handler that stops because the raise was cancelled has not failed.
/// </summary>
internal sealed class ErrorHandlingExample : IExample
{
    private static readonly TimeSpan RefundCancelAfter = TimeSpan.FromMilliseconds(50);

    public string Name => "ErrorHandling";

    public string Summary => "Exception routing: subscription handler, DefaultExceptionHandler, swallowed";

    public async Task RunAsync()
    {
        var reportedFailures = new ConcurrentQueue<EventHandlerFailure>();
        var options = new EventingOptions
        {
            DefaultExceptionHandler = failure =>
            {
                reportedFailures.Enqueue(failure);
                Console.WriteLine($"DefaultExceptionHandler: {failure.EventName} {failure.Stage} failed: {failure.Exception.Message}");
            },
        };
        await using var gateway = new PaymentGateway(options);

        await ShowRoutingOrderAsync(gateway, reportedFailures);
        await ShowCancelledHandlerIsNotAFailureAsync(gateway, reportedFailures);
        await ShowSwallowedWithoutAnyHandlerAsync();
    }

    private static async Task ShowRoutingOrderAsync(PaymentGateway gateway, ConcurrentQueue<EventHandlerFailure> reportedFailures)
    {
        ExampleCheck.Step("Three subscribers: one healthy, one failing with its own handler, one failing without");
        var bookedInvoices = new ConcurrentQueue<string>();
        var handledBySubscriber = new ConcurrentQueue<string>();

        gateway.PaymentReceived.Subscribe(call => bookedInvoices.Enqueue(call.Payload.InvoiceNumber));
        gateway.PaymentReceived.Subscribe(
            call => throw new InvalidOperationException("receipt printer is offline"),
            onException: (exception, call) =>
            {
                handledBySubscriber.Enqueue(call.Payload.InvoiceNumber);
                Console.WriteLine($"Receipt printer handled its own failure for {call.Payload.InvoiceNumber}: {exception.Message}");
            });
        gateway.PaymentReceived.Raised += async call =>
        {
            await Task.Yield();
            throw new TimeoutException("customer notification timed out");
        };

        //completes normally: handler exceptions never propagate to the raiser
        await gateway.ReceiveAsync("INV-1001", 120m);

        ExampleCheck.That(bookedInvoices.SequenceEqual(["INV-1001"]), "the healthy handler ran despite the failing ones");
        ExampleCheck.That(handledBySubscriber.SequenceEqual(["INV-1001"]), "the subscription's own handler received its failure");
        var failure = reportedFailures.Single();
        var failureDescribesTheHandler = failure.Stage == EventFailureStage.Handler && failure.SubscriptionId is not null;
        ExampleCheck.That(failure.Exception is TimeoutException, "only the unhandled failure reached DefaultExceptionHandler");
        ExampleCheck.That(failureDescribesTheHandler, "EventHandlerFailure names the stage and the failing subscription");
        var failureCarriesTheCall = failure.Call is EventCall<PaymentReceived> { Payload.InvoiceNumber: "INV-1001" };
        ExampleCheck.That(failureCarriesTheCall, "EventHandlerFailure carries the call being handled");
    }

    private static async Task ShowCancelledHandlerIsNotAFailureAsync(PaymentGateway gateway, ConcurrentQueue<EventHandlerFailure> reportedFailures)
    {
        ExampleCheck.Step($"A refund handler that honours the call's token, with the raise cancelled after {RefundCancelAfter.TotalMilliseconds} ms");
        var failuresBefore = reportedFailures.Count;
        var refundStopped = false;
        gateway.RefundRequested.Raised += async call =>
        {
            try
            {
                //stands in for a slow payment-provider call
                await Task.Delay(Timeout.InfiniteTimeSpan, call.CancellationToken);
            }
            catch (OperationCanceledException)
            {
                refundStopped = true;
                Console.WriteLine($"Refund for {call.Payload.InvoiceNumber} stopped: the raise was cancelled");
                throw;
            }
        };

        using var refundCancellation = new CancellationTokenSource(RefundCancelAfter);
        await gateway.RequestRefundAsync("INV-1001", refundCancellation.Token);

        ExampleCheck.That(refundStopped, "the handler observed call.CancellationToken");
        ExampleCheck.That(reportedFailures.Count == failuresBefore, "an OperationCanceledException after cancellation is not reported as a failure");
    }

    private static async Task ShowSwallowedWithoutAnyHandlerAsync()
    {
        ExampleCheck.Step("An event whose options have no DefaultExceptionHandler, and a failing handler without its own");
        await using var unguardedPayments = new IntercomEvent<PaymentReceived>(new EventingOptions());
        var handlerRan = false;
        unguardedPayments.Subscribe(call =>
        {
            handlerRan = true;
            throw new InvalidOperationException("nobody hears this");
        });

        await unguardedPayments.RaiseAsync(new PaymentReceived("INV-1002", 80m));
        ExampleCheck.That(handlerRan, "the raise completed and the exception was swallowed");
    }
}
