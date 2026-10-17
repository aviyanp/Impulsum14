namespace Impulsum14;
internal static class ServerDiagnostics
{
    [System.Diagnostics.Conditional("DEBUG")]
    internal static void Write(string message) => Console.WriteLine(message);
}
