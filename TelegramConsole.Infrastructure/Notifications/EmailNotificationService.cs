using System.Net;
using System.Net.Mail;
using TelegramConsole.Core;

namespace TelegramConsole.Infrastructure;

internal static class EmailNotificationService
{
    public static async Task SendAsync(EmailSettings settings, string recipient, string subject, string body)
    {
        if (string.IsNullOrWhiteSpace(settings.SmtpHost))
            throw new InvalidOperationException("尚未配置 SMTP 服务器");
        if (string.IsNullOrWhiteSpace(settings.FromAddress))
            throw new InvalidOperationException("尚未配置发件人邮箱");

        using var message = new MailMessage(settings.FromAddress, recipient, subject, body);
        using var client = new SmtpClient(settings.SmtpHost, settings.SmtpPort)
        {
            EnableSsl = settings.EnableSsl,
            UseDefaultCredentials = false,
            Credentials = new NetworkCredential(settings.UserName, settings.Password)
        };
        await client.SendMailAsync(message);
    }
}
