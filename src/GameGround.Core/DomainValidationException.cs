namespace GameGround.Core;

public enum ValidationError
{
    InvalidManifest,
    InvalidDefinition,
    InvalidEvent,
    UnknownTeam,
    UnknownPlayer,
    PlayerTeamMismatch,
    InvalidSequence,
    DuplicateEvent,
    InvalidPeriod,
    InvalidClock,
    InvalidPeriodBoundary,
    InvalidControl,
    InvalidAction,
    InvalidOutcome,
    UnsupportedEvent
}

public sealed class DomainValidationException(
    ValidationError code, string message, EventId? eventId = null) : Exception(message)
{
    public ValidationError Code { get; } = code;
    public EventId? EventId { get; } = eventId;
}
