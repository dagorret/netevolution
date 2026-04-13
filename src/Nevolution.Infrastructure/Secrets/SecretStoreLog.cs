namespace Nevolution.Infrastructure.Secrets;

internal static class SecretStoreLog
{
    public static void Info(string message)
    {
        Console.WriteLine($"[Secrets] {message}");
    }
}
