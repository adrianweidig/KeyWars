using Microsoft.Extensions.Hosting;

namespace KeyWars.Data;

public static class DataPaths
{
    public static string Resolve(IConfiguration configuration, IHostEnvironment environment)
    {
        var configured = configuration["KEYWARS:DATA:DIRECTORY"];
        if (!string.IsNullOrWhiteSpace(configured))
        {
            return configured;
        }

        return environment.IsDevelopment()
            ? Path.Combine(environment.ContentRootPath, "App_Data")
            : "/data";
    }

    public static void EnsureWritable(string dataDirectory)
    {
        Directory.CreateDirectory(dataDirectory);
        Directory.CreateDirectory(Path.Combine(dataDirectory, "dataprotection-keys"));
        Directory.CreateDirectory(Path.Combine(dataDirectory, "backups"));

        var probePath = Path.Combine(dataDirectory, ".write-test");
        File.WriteAllText(probePath, DateTimeOffset.UtcNow.ToString("O"));
        File.Delete(probePath);
    }

    public static string DatabasePath(string dataDirectory) => Path.Combine(dataDirectory, "keywars.db");
}
