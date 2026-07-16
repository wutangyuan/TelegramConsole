namespace TelegramConsole.Core;

public enum OutgoingMessageStatus
{
    Queued,
    Sending,
    Sent,
    Failed,
    Unknown
}

public sealed record OutgoingMessageRecord(
    long Id,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt,
    long TargetId,
    string TargetKind,
    string TargetTitle,
    string Purpose,
    string MessagePreview,
    OutgoingMessageStatus Status,
    int AttemptCount,
    int? TelegramMessageId,
    string Error);

