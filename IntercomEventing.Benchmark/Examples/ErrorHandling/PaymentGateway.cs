namespace IntercomEventing.Benchmark.Examples.ErrorHandling;

public sealed record PaymentReceived(string InvoiceNumber, decimal Amount);

public sealed record RefundRequested(string InvoiceNumber);

/// <summary>
/// A producer whose events are created with options supplied by the application, including a
/// <see cref="EventingOptions.DefaultExceptionHandler"/> for failures no subscription handles itself.
/// </summary>
public sealed class PaymentGateway : IAsyncDisposable
{
    public IntercomEvent<PaymentReceived> PaymentReceived { get; }

    public IntercomEvent<RefundRequested> RefundRequested { get; }

    public PaymentGateway(EventingOptions options)
    {
        PaymentReceived = new IntercomEvent<PaymentReceived>(options) { Name = "PaymentReceived" };
        RefundRequested = new IntercomEvent<RefundRequested>(options) { Name = "RefundRequested" };
    }

    public Task ReceiveAsync(string invoiceNumber, decimal amount)
    {
        return PaymentReceived.RaiseAsync(new PaymentReceived(invoiceNumber, amount), caller: this);
    }

    public Task RequestRefundAsync(string invoiceNumber, CancellationToken cancellationToken)
    {
        return RefundRequested.RaiseAsync(new RefundRequested(invoiceNumber), caller: this, cancellationToken);
    }

    public async ValueTask DisposeAsync()
    {
        await PaymentReceived.DisposeAsync();
        await RefundRequested.DisposeAsync();
    }
}
