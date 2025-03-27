namespace IntercomEventing.Features.Events;

/// <summary>
/// Controls whether event handlers are executed sequentially or in parallel on a per event call basis
/// </summary>
public enum EventingSyncType
{
    Sequential,
    Parallel,
}