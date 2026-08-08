using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text;
using FlaUI.Core;
using FlaUI.Core.AutomationElements;
using FlaUI.Core.Definitions;
using FlaUI.UIA3;

namespace KeyWars.WindowsUiTests;

internal sealed class WindowsUiTestEnvironment : IAsyncDisposable
{
    private readonly StringBuilder appOutput = new();
    private readonly StringBuilder appError = new();
    private readonly string appDataDirectory;
    private readonly string browserProfileDirectory;
    private Process? appProcess;
    private Process? browserProcess;

    private WindowsUiTestEnvironment(string repositoryRoot, Uri baseAddress, string artifactDirectory)
    {
        RepositoryRoot = repositoryRoot;
        BaseAddress = baseAddress;
        ArtifactDirectory = artifactDirectory;
        appDataDirectory = Path.Combine(Path.GetTempPath(), "keywars-windows-ui-data-" + Guid.NewGuid().ToString("N"));
        browserProfileDirectory = Path.Combine(Path.GetTempPath(), "keywars-windows-ui-browser-" + Guid.NewGuid().ToString("N"));
    }

    public string RepositoryRoot { get; }

    public Uri BaseAddress { get; }

    public string ArtifactDirectory { get; }

    public Application? BrowserApplication { get; private set; }

    public UIA3Automation? Automation { get; private set; }

    public Window? MainWindow { get; private set; }

    public static async Task<WindowsUiTestEnvironment> StartAsync()
    {
        var repositoryRoot = FindRepositoryRoot();
        var port = ReservePort();
        var artifactDirectory = ResolveArtifactDirectory();
        var environment = new WindowsUiTestEnvironment(
            repositoryRoot,
            new Uri($"http://127.0.0.1:{port}"),
            artifactDirectory);

        Directory.CreateDirectory(environment.ArtifactDirectory);
        Directory.CreateDirectory(environment.appDataDirectory);
        Directory.CreateDirectory(environment.browserProfileDirectory);
        PrepareBrowserProfile(environment.browserProfileDirectory);

        try
        {
            await environment.StartApplicationAsync();
            environment.StartBrowser();
            return environment;
        }
        catch
        {
            await environment.DisposeAsync();
            throw;
        }
    }

    public async Task<string> CaptureRenderedPageAsync(string name)
    {
        var path = Path.Combine(ArtifactDirectory, name);
        var captureProfileDirectory = Path.Combine(
            Path.GetTempPath(),
            "keywars-windows-ui-capture-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(captureProfileDirectory);

        try
        {
            var startInfo = new ProcessStartInfo(BrowserPathResolver.Find())
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            };
            startInfo.ArgumentList.Add("--headless=new");
            startInfo.ArgumentList.Add("--no-first-run");
            startInfo.ArgumentList.Add("--hide-scrollbars");
            startInfo.ArgumentList.Add("--window-size=1280,900");
            startInfo.ArgumentList.Add("--user-data-dir=" + captureProfileDirectory);
            startInfo.ArgumentList.Add("--screenshot=" + path);
            startInfo.ArgumentList.Add(BaseAddress.ToString());

            using var process = Process.Start(startInfo)
                ?? throw new InvalidOperationException("Der Browser für die OpenCV-Aufnahme konnte nicht gestartet werden.");
            var standardErrorTask = process.StandardError.ReadToEndAsync();
            var timeout = Stopwatch.StartNew();
            while (!File.Exists(path) && timeout.Elapsed < TimeSpan.FromSeconds(60))
            {
                if (process.HasExited && process.ExitCode != 0)
                {
                    break;
                }

                await Task.Delay(250);
            }

            if (!File.Exists(path))
            {
                if (!process.HasExited)
                {
                    process.Kill(entireProcessTree: true);
                    await process.WaitForExitAsync();
                }

                var standardError = await standardErrorTask;
                throw new InvalidOperationException(
                    $"Die OpenCV-Aufnahme wurde nicht erzeugt. Browser-ExitCode=" +
                    $"{(process.HasExited ? process.ExitCode.ToString() : "unbekannt")}{Environment.NewLine}{standardError}");
            }

            return path;
        }
        finally
        {
            TryDelete(captureProfileDirectory);
        }
    }

    public async ValueTask DisposeAsync()
    {
        try
        {
            BrowserApplication?.Close();
        }
        catch (InvalidOperationException)
        {
        }

        BrowserApplication?.Dispose();
        Automation?.Dispose();

        if (browserProcess is { HasExited: false })
        {
            browserProcess.Kill(entireProcessTree: true);
            await browserProcess.WaitForExitAsync();
        }

        if (appProcess is { HasExited: false })
        {
            appProcess.Kill(entireProcessTree: true);
            await appProcess.WaitForExitAsync();
        }

        await File.WriteAllTextAsync(Path.Combine(ArtifactDirectory, "keywars.stdout.log"), appOutput.ToString());
        await File.WriteAllTextAsync(Path.Combine(ArtifactDirectory, "keywars.stderr.log"), appError.ToString());

        appProcess?.Dispose();
        browserProcess?.Dispose();
        TryDelete(appDataDirectory);
        TryDelete(browserProfileDirectory);
    }

    private async Task StartApplicationAsync()
    {
        var applicationPath = Path.Combine(RepositoryRoot, "src", "KeyWars", "bin", "Release", "net10.0", "KeyWars.dll");
        if (!File.Exists(applicationPath))
        {
            throw new FileNotFoundException("Der Release-Build von KeyWars fehlt. Baue das Testprojekt vor dem Testlauf.", applicationPath);
        }

        var dotnetHost = Environment.GetEnvironmentVariable("DOTNET_HOST_PATH") ?? "dotnet";
        var startInfo = new ProcessStartInfo(dotnetHost)
        {
            WorkingDirectory = RepositoryRoot,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        };
        startInfo.ArgumentList.Add(applicationPath);
        startInfo.Environment["ASPNETCORE_ENVIRONMENT"] = "Development";
        startInfo.Environment["ASPNETCORE_URLS"] = BaseAddress.ToString().TrimEnd('/');
        startInfo.Environment["KEYWARS__AUTH__DEVELOPMENT_LOGIN"] = "true";
        startInfo.Environment["KEYWARS__DATA__DIRECTORY"] = appDataDirectory;
        startInfo.Environment["KEYWARS__LIVE__COUNTDOWN_SECONDS"] = "1";
        startInfo.Environment["Logging__LogLevel__Default"] = "Warning";
        startInfo.Environment["Logging__LogLevel__Microsoft"] = "Warning";

        appProcess = new Process { StartInfo = startInfo, EnableRaisingEvents = true };
        appProcess.OutputDataReceived += (_, args) => AppendLine(appOutput, args.Data);
        appProcess.ErrorDataReceived += (_, args) => AppendLine(appError, args.Data);
        if (!appProcess.Start())
        {
            throw new InvalidOperationException("Der KeyWars-Testprozess konnte nicht gestartet werden.");
        }

        appProcess.BeginOutputReadLine();
        appProcess.BeginErrorReadLine();

        using var client = new HttpClient { BaseAddress = BaseAddress, Timeout = TimeSpan.FromSeconds(5) };
        var timeout = Stopwatch.StartNew();
        while (timeout.Elapsed < TimeSpan.FromSeconds(90))
        {
            if (appProcess.HasExited)
            {
                throw new InvalidOperationException(
                    $"KeyWars wurde vor der Readiness-Prüfung beendet. ExitCode={appProcess.ExitCode}{Environment.NewLine}{appError}");
            }

            try
            {
                using var response = await client.GetAsync("/health/ready");
                if (response.IsSuccessStatusCode)
                {
                    return;
                }
            }
            catch (HttpRequestException)
            {
            }
            catch (TaskCanceledException)
            {
            }

            await Task.Delay(500);
        }

        throw new TimeoutException("KeyWars wurde innerhalb von 90 Sekunden nicht bereit.");
    }

    private void StartBrowser()
    {
        var browserPath = BrowserPathResolver.Find();
        var browserProcessName = Path.GetFileNameWithoutExtension(browserPath);
        var existingProcessIds = Process.GetProcessesByName(browserProcessName)
            .Select(process =>
            {
                using (process)
                {
                    return process.Id;
                }
            })
            .ToHashSet();
        var startInfo = new ProcessStartInfo(browserPath)
        {
            UseShellExecute = false
        };
        startInfo.ArgumentList.Add("--app=" + BaseAddress);
        startInfo.ArgumentList.Add("--new-window");
        startInfo.ArgumentList.Add("--no-first-run");
        startInfo.ArgumentList.Add("--disable-default-apps");
        startInfo.ArgumentList.Add("--disable-save-password-bubble");
        startInfo.ArgumentList.Add("--disable-features=msEdgeFirstRunExperience");
        startInfo.ArgumentList.Add("--force-renderer-accessibility");
        startInfo.ArgumentList.Add("--user-data-dir=" + browserProfileDirectory);

        using var launcherProcess = Process.Start(startInfo)
            ?? throw new InvalidOperationException("Der Browserprozess konnte nicht gestartet werden.");
        Automation = new UIA3Automation();

        var timeout = Stopwatch.StartNew();
        while (timeout.Elapsed < TimeSpan.FromSeconds(45))
        {
            foreach (var candidate in Process.GetProcessesByName(browserProcessName))
            {
                if (existingProcessIds.Contains(candidate.Id))
                {
                    candidate.Dispose();
                    continue;
                }

                try
                {
                    candidate.Refresh();
                    if (candidate.MainWindowHandle == IntPtr.Zero ||
                        !candidate.MainWindowTitle.Contains("KeyWars", StringComparison.OrdinalIgnoreCase))
                    {
                        candidate.Dispose();
                        continue;
                    }

                    browserProcess = candidate;
                    BrowserApplication = Application.Attach(candidate.Id);
                    MainWindow = BrowserApplication.GetMainWindow(Automation, TimeSpan.FromSeconds(10));
                    if (MainWindow is not null)
                    {
                        if (MainWindow.Patterns.Window.IsSupported)
                        {
                            MainWindow.Patterns.Window.Pattern.SetWindowVisualState(WindowVisualState.Normal);
                        }

                        MainWindow.Focus();
                        Thread.Sleep(1_000);
                        return;
                    }
                }
                catch (InvalidOperationException)
                {
                    candidate.Dispose();
                }
            }

            Thread.Sleep(250);
        }

        throw new TimeoutException("FlaUI fand innerhalb von 45 Sekunden kein KeyWars-Browserfenster.");
    }

    private static void PrepareBrowserProfile(string profileDirectory)
    {
        var defaultProfile = Path.Combine(profileDirectory, "Default");
        Directory.CreateDirectory(defaultProfile);
        File.WriteAllText(
            Path.Combine(defaultProfile, "Preferences"),
            """
            {
              "autofill": {
                "credit_card_enabled": false,
                "profile_enabled": false
              },
              "credentials_enable_service": false,
              "profile": {
                "password_manager_enabled": false
              }
            }
            """);
    }

    private static string FindRepositoryRoot()
    {
        var directory = new DirectoryInfo(TestContext.CurrentContext.TestDirectory);
        while (directory is not null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "KeyWars.slnx")))
            {
                return directory.FullName;
            }

            directory = directory.Parent;
        }

        throw new DirectoryNotFoundException("Das KeyWars-Repository konnte ausgehend vom Testverzeichnis nicht gefunden werden.");
    }

    private static string ResolveArtifactDirectory()
    {
        var configured = Environment.GetEnvironmentVariable("KEYWARS_WINDOWS_UI_ARTIFACTS");
        return string.IsNullOrWhiteSpace(configured)
            ? Path.Combine(Path.GetTempPath(), "keywars-windows-ui-artifacts-" + Guid.NewGuid().ToString("N"))
            : Path.GetFullPath(configured);
    }

    private static int ReservePort()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }

    private static void AppendLine(StringBuilder builder, string? value)
    {
        if (value is null)
        {
            return;
        }

        lock (builder)
        {
            builder.AppendLine(value);
        }
    }

    private static void TryDelete(string path)
    {
        try
        {
            Directory.Delete(path, recursive: true);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }
}
