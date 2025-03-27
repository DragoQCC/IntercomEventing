namespace IntercomEventing.Features.Events;

public record EventMetadata
{
    /// <summary>
    /// The ID of the event this event call belongs to <br/>
    /// This ID is consistent for all event calls of the same event instance
    /// </summary>
    public Guid EventId { get; internal set; }
    
    /// <summary>
    /// The ID of this event call <br/>
    /// This ID is unique for each event call
    /// </summary>
    public Guid EventCallId { get; init; } = Guid.CreateVersion7(DateTime.UtcNow);
    
    /// <summary>
    /// The time the event was last raised in UTC <br/>
    /// </summary>
    public DateTime LastEventTime { get; set; } = DateTime.UtcNow;
    
    /// <summary>
    /// Optional: The object that raised the event
    /// </summary>
    public object? EventCaller { get; set; }
}