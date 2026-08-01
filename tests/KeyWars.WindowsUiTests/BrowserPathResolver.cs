namespace KeyWars.WindowsUiTests;

internal static class BrowserPathResolver
{
    public static string Find()
    {
        var configured = Environment.GetEnvironmentVariable("KEYWARS_WINDOWS_UI_BROWSER");
        if (!string.IsNullOrWhiteSpace(configured))
        {
            return File.Exists(configured)
                ? configured
                : throw new FileNotFoundException("KEYWARS_WINDOWS_UI_BROWSER verweist auf keine vorhandene Datei.", configured);
        }

        var candidates = new[]
        {
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86),
                "Microsoft", "Edge", "Application", "msedge.exe"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
                "Microsoft", "Edge", "Application", "msedge.exe"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
                "Google", "Chrome", "Application", "chrome.exe"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "Google", "Chrome", "Application", "chrome.exe")
        };

        return candidates.FirstOrDefault(File.Exists)
            ?? throw new FileNotFoundException("Für die Windows-UI-Tests wurde weder Microsoft Edge noch Google Chrome gefunden.");
    }
}
