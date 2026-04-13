namespace Nevolution.Core.Models;

public sealed class MailAccount
{
    public string Id { get; set; } = Guid.NewGuid().ToString();

    public string DisplayName { get; set; } = string.Empty;

    public string Email { get; set; } = string.Empty;

    public string ImapHost { get; set; } = string.Empty;

    public int ImapPort { get; set; }

    public string SmtpHost { get; set; } = string.Empty;

    public int SmtpPort { get; set; }

    public string Username { get; set; } = string.Empty;

    public string Password { get; set; } = string.Empty;

    public bool IsActive { get; set; }

    public string PreferredFolder { get; set; } = string.Empty;
}
