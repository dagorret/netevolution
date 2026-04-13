namespace Nevolution.Core.Models;

public sealed class SyncFolderResult
{
    public required string AccountId { get; init; }

    public required string Folder { get; init; }

    public required uint PreviousLastUid { get; init; }

    public required uint NewLastUid { get; init; }

    public required int FetchedHeadersCount { get; init; }

    public required bool ResetFolder { get; init; }

    public required int SoftDeletedCount { get; init; }

    public required int RestoredCount { get; init; }

    public required bool BackfillTriggered { get; init; }

    public required int BackfilledHeadersCount { get; init; }

    public required int LocalVisibleCountBefore { get; init; }

    public required int LocalVisibleCountAfter { get; init; }

    public required int ServerVisibleCount { get; init; }
}
