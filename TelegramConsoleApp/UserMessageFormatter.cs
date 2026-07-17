using System.ComponentModel;
using System.IO;
using System.Net.Mail;
using System.Net.Sockets;
using System.Text.RegularExpressions;

namespace TelegramConsoleApp;

internal static partial class UserMessageFormatter
{
    private static readonly (string Code, string Resource)[] TelegramErrors =
    [
        ("CHANNEL_FORUM_MISSING", "ChatIsNotForum"),
        ("CHAT_WRITE_FORBIDDEN", "TelegramNoWritePermission"),
        ("USER_BANNED_IN_CHANNEL", "TelegramNoWritePermission"),
        ("CHAT_ADMIN_REQUIRED", "TelegramAdminRequired"),
        ("CHANNEL_PRIVATE", "TelegramChannelUnavailable"),
        ("USER_PRIVACY_RESTRICTED", "TelegramPrivacyRestricted"),
        ("PEER_ID_INVALID", "TelegramPeerInvalid"),
        ("MESSAGE_ID_INVALID", "TelegramMessageUnavailable"),
        ("MSG_ID_INVALID", "TelegramMessageUnavailable"),
        ("MESSAGE_EDIT_TIME_EXPIRED", "TelegramEditExpired"),
        ("MESSAGE_AUTHOR_REQUIRED", "TelegramEditOwnOnly"),
        ("MESSAGE_NOT_MODIFIED", "TelegramMessageNotModified"),
        ("MESSAGE_DELETE_FORBIDDEN", "TelegramDeleteForbidden"),
        ("REACTION_INVALID", "TelegramReactionUnavailable"),
        ("MESSAGE_TOO_LONG", "TelegramMessageTooLong"),
        ("TOPIC_CLOSED", "TelegramTopicClosed"),
        ("TOPIC_DELETED", "TelegramTopicUnavailable"),
        ("TOPIC_ID_INVALID", "TelegramTopicUnavailable"),
        ("SCHEDULE_DATE_TOO_LATE", "TelegramScheduleTooLate"),
        ("SCHEDULE_TOO_MUCH", "TelegramScheduleLimit"),
        ("SCHEDULE_STATUS_PRIVATE", "TelegramScheduleNotAllowed"),
        ("SCHEDULE_BOT_NOT_ALLOWED", "TelegramScheduleNotAllowed"),
        ("USER_IS_BLOCKED", "TelegramUserBlocked"),
        ("YOU_BLOCKED_USER", "TelegramUserBlocked"),
        ("INPUT_USER_DEACTIVATED", "TelegramUserUnavailable"),
        ("AUTH_KEY_UNREGISTERED", "TelegramLoginExpired"),
        ("SESSION_REVOKED", "TelegramLoginExpired")
    ];

    public static string From(Exception exception)
    {
        var ex = Unwrap(exception);
        var message = ex.Message ?? "";

        var wait = FloodWaitRegex().Match(message);
        if (wait.Success)
            return LocalizationManager.Format("TelegramFloodWait", wait.Groups[1].Value);

        foreach (var (code, resource) in TelegramErrors)
            if (message.Contains(code, StringComparison.OrdinalIgnoreCase))
                return LocalizationManager.Text(resource);

        if (ex is TimeoutException or TaskCanceledException or OperationCanceledException)
            return LocalizationManager.Text("OperationTimedOutFriendly");
        if (ex is SocketException || IsConnectionMessage(message))
            return LocalizationManager.Text("NetworkConnectionFriendly");
        if (ex is SmtpException)
            return LocalizationManager.Text("EmailSendFailedFriendly");
        if (ex is UnauthorizedAccessException)
            return LocalizationManager.Text("FileAccessDeniedFriendly");
        if (ex is FileNotFoundException)
            return LocalizationManager.Text("FileNotFoundFriendly");
        if (ex is Win32Exception)
            return LocalizationManager.Text("SystemOpenFailedFriendly");
        if (ex is NullReferenceException)
            return LocalizationManager.Text("UnexpectedDataError");

        var rpcCode = RpcCodeRegex().Match(message);
        if (rpcCode.Success)
            return LocalizationManager.Format("TelegramRejectedUnknown", rpcCode.Groups[1].Value);

        // Application validation messages are already localized and safe to show.
        if (ex is InvalidOperationException or ArgumentException)
            return message;

        return LocalizationManager.Text("UnexpectedOperationFriendly");
    }

    private static Exception Unwrap(Exception exception)
    {
        while (exception is AggregateException { InnerExceptions.Count: 1 } aggregate)
            exception = aggregate.InnerExceptions[0];
        return exception.InnerException is not null &&
               exception.GetType().Name is "TargetInvocationException" or "TypeInitializationException"
            ? Unwrap(exception.InnerException)
            : exception;
    }

    private static bool IsConnectionMessage(string message) =>
        message.Contains("Connection shut down", StringComparison.OrdinalIgnoreCase) ||
        message.Contains("connection lost", StringComparison.OrdinalIgnoreCase) ||
        message.Contains("connection refused", StringComparison.OrdinalIgnoreCase) ||
        message.Contains("Could not read payload", StringComparison.OrdinalIgnoreCase) ||
        message.Contains("timed out", StringComparison.OrdinalIgnoreCase);

    [GeneratedRegex(@"(?:FLOOD(?:_PREMIUM)?_WAIT_?)(\d+)", RegexOptions.IgnoreCase)]
    private static partial Regex FloodWaitRegex();

    [GeneratedRegex(@"\b([A-Z][A-Z0-9_]{4,})\b")]
    private static partial Regex RpcCodeRegex();
}
