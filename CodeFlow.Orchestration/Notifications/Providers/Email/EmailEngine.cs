namespace CodeFlow.Orchestration.Notifications.Providers.Email;

/// <summary>
/// Selects the underlying transport for an email provider configuration. The factory chooses
/// the engine when building a provider; the rest of the email pipeline is engine-neutral.
/// </summary>
public enum EmailEngine
{
    /// <summary>Amazon SES via the v2 SimpleEmail SDK (HTTPS).</summary>
    Ses = 1,

    /// <summary>Generic SMTP relay via MailKit. Works against SES SMTP, SendGrid SMTP, on-prem postfix, …</summary>
    Smtp = 2,
}
