using RazorConsole.Core;
using SuperCoolApp;

var appBase = AppContext.BaseDirectory;
string? found = null;
var cwd = Directory.GetCurrentDirectory();
var dir = new DirectoryInfo(cwd);
for (var i = 0; i < 6 && dir != null; i++)
{
    var candidate = Path.Combine(dir.FullName, "ApiCredential.json");
    if (File.Exists(candidate))
    {
        found = candidate;
        break;
    }
    dir = dir.Parent;
}

if (found != null)
{
    try
    {
        var dest = Path.Combine(appBase, "ApiCredential.json");
        if (!File.Exists(dest)) File.Copy(found, dest);
    }
    catch (Exception ex)
    {
        Console.WriteLine($"Warning: failed to copy ApiCredential.json to app base: {ex.Message}");
    }
}

await AppHost.RunAsync<CalendarDashboard>();
