namespace Nevolution.Core.Models;

public sealed class FolderLoadStats
{
    public required string AccountId { get; init; }

    public required string Folder { get; init; }

    public required int TotalCount { get; init; }

    public required int VisibleCount { get; init; }

    public required int SoftDeletedCount { get; init; }

    public required int BodyUnavailableCount { get; init; }
}
