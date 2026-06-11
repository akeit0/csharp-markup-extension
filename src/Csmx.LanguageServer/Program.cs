namespace Csmx.LanguageServer;

internal static class Program
{
    public static async Task Main()
    {
        var server = new LspServer(Console.OpenStandardInput(), Console.OpenStandardOutput());
        await server.RunAsync();
    }
}
