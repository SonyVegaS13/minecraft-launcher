using System.Net;
using System.Net.Mail;
using Microsoft.AspNetCore.Identity.UI.Services;
using Microsoft.AspNetCore.Identity;

namespace Solaris.Id.Api;

/// <summary>
/// No success-shaped fake emails. SMTP must be configured server-side before
/// registration or password recovery is enabled in a deployed environment.
/// </summary>
public sealed class SolarisSmtpSender(IConfiguration config, ILogger<SolarisSmtpSender> logger)
    : IEmailSender<SolarisUser>
{
    private async Task SendAsync(string address, string subject, string text)
    {
        string host = config["SOLARIS_SMTP_HOST"]
            ?? throw new InvalidOperationException("SMTP host not configured.");
        string from = config["SOLARIS_SMTP_FROM"]
            ?? throw new InvalidOperationException("SMTP sender not configured.");
        string user = config["SOLARIS_SMTP_USER"]
            ?? throw new InvalidOperationException("SMTP user not configured.");
        string pass = config["SOLARIS_SMTP_PASSWORD"]
            ?? throw new InvalidOperationException("SMTP password not configured.");
        using var smtp = new SmtpClient(host, int.TryParse(config["SOLARIS_SMTP_PORT"], out int port) ? port : 587)
        {
            EnableSsl = true,
            Credentials = new NetworkCredential(user, pass)
        };
        using var message = new MailMessage(from, address, subject, text);
        await smtp.SendMailAsync(message);
        logger.LogInformation("Account email sent successfully for user notification");
    }

    public Task SendConfirmationLinkAsync(SolarisUser user, string email, string confirmationLink)
        => SendAsync(email, "Solaris ID — подтвердите email",
            "Для подтверждения адреса откройте ссылку:\n" + confirmationLink);

    public Task SendPasswordResetLinkAsync(SolarisUser user, string email, string resetLink)
        => SendAsync(email, "Solaris ID — восстановление пароля",
            "Восстановление доступа:\n" + resetLink);

    public Task SendPasswordResetCodeAsync(SolarisUser user, string email, string resetCode)
        => SendAsync(email, "Solaris ID — код сброса пароля",
            "Код сброса пароля:\n" + resetCode);
}
