// TODO(integration): delete when Cli.cs lands. The real class must expose
// `Run(string[] args)`: a `Main(string[])` here would be a second entry point
// (CS0017) unless the csproj sets <StartupObject>Tramoya.App.Program</StartupObject>.
namespace Tramoya.App;

internal static class Cli
{
    public static int Run(string[] args)
    {
        Console.Error.WriteLine("CLI pendiente de integración");
        return 2;
    }
}
