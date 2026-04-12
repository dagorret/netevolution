namespace Nevolution.Core.Models;

public sealed class FolderState
{
    public string AccountId { get; set; } = string.Empty;

    public string Folder { get; set; } = string.Empty;

    public uint LastUid { get; set; }

    public uint UidValidity { get; set; }
}
