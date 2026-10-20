namespace IntercomEventing.Generators.Tests.Fixtures;

/// <summary>
/// A producer whose field is deliberately not readonly. The INTEV005 warning is suppressed with <c>#pragma</c>, which
/// proves the generator's diagnostics are placed in the field's syntax tree: the project builds without warnings.
/// </summary>
public sealed partial class ReplaceableProducer
{
#pragma warning disable INTEV005 // replaced on purpose to show what the warning is about
    [IntercomEvent]
    private IntercomEvent<string> _messageSent = new();
#pragma warning restore INTEV005

    public Task SendAsync(string message)
    {
        return _messageSent.RaiseAsync(message);
    }

    //what INTEV005 warns about: handlers added before the replacement stay on the old event
    public void ReplaceEvent()
    {
        _messageSent = new IntercomEvent<string>();
    }
}
