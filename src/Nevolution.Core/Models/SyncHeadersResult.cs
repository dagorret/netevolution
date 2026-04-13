namespace Nevolution.Core.Models;

public sealed class SyncHeadersResult
{
    public required uint UidValidity { get; init; }

    public required IReadOnlyCollection<uint> ServerUids { get; init; }

    public required IList<EmailMessage> NewEmails { get; init; }
}
