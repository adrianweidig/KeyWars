using System.Diagnostics;
using FlaUI.Core.AutomationElements;
using FlaUI.Core.Definitions;
using FlaUI.Core.Input;
using FlaUI.Core.Tools;
using OpenCvSharp;
using Window = FlaUI.Core.AutomationElements.Window;

namespace KeyWars.WindowsUiTests;

[TestFixture]
[NonParallelizable]
[Category("WindowsUI")]
public sealed class WindowsUiSmokeTests
{
    private WindowsUiTestEnvironment? environment;

    [OneTimeSetUp]
    public async Task StartEnvironment()
    {
        if (!System.OperatingSystem.IsWindows())
        {
            Assert.Ignore("FlaUI- und OpenCV-Laufzeitprüfungen benötigen Windows.");
        }

        environment = await WindowsUiTestEnvironment.StartAsync();
    }

    [OneTimeTearDown]
    public async Task StopEnvironment()
    {
        if (environment is not null)
        {
            await environment.DisposeAsync();
        }
    }

    [Test]
    [Order(1)]
    public void FlaUiFindsTheKeyWarsWindow()
    {
        var window = RequiredEnvironment.MainWindow;

        Assert.Multiple(() =>
        {
            Assert.That(window, Is.Not.Null);
            Assert.That(window!.Title, Does.Contain("KeyWars").IgnoreCase);
            Assert.That(window.BoundingRectangle.Width, Is.GreaterThanOrEqualTo(600));
            Assert.That(window.BoundingRectangle.Height, Is.GreaterThanOrEqualTo(400));
        });
    }

    [Test]
    [Order(2)]
    public void Uia3ExposesTheLoginControls()
    {
        var window = RequiredEnvironment.MainWindow!;
        var document = window.FindFirstDescendant(factory => factory.ByControlType(ControlType.Document));
        var inputFields = WaitForLoginInputs(window);
        var loginButton = WaitForVisibleElement(window, ControlType.Button, "Anmelden");

        Assert.Multiple(() =>
        {
            Assert.That(document, Is.Not.Null, "Der Browser muss seinen Dokumentbereich über UIA3 bereitstellen.");
            Assert.That(inputFields, Has.Length.GreaterThanOrEqualTo(2), "Die Anmeldemaske muss zwei Eingabefelder anbieten.");
            Assert.That(loginButton, Is.Not.Null, "Die Anmeldemaske muss die Schaltfläche 'Anmelden' anbieten.");
            Assert.That(loginButton?.IsEnabled, Is.True, "Die Anmeldeschaltfläche muss bedienbar sein.");
        });
    }

    [Test]
    [Order(3)]
    public async Task OpenCvConfirmsStructuredRenderedContent()
    {
        var screenshot = await RequiredEnvironment.CaptureRenderedPageAsync("keywars-rendered-page.png");
        TestContext.AddTestAttachment(screenshot, "Deterministisch gerenderte KeyWars-Seite");

        var metrics = VisualAssertions.Analyze(screenshot);
        TestContext.Progress.WriteLine(
            $"OpenCV: {metrics.Width}x{metrics.Height}; Mittel={metrics.MeanLuminance:F2}; " +
            $"Standardabweichung={metrics.LuminanceStandardDeviation:F2}; Kantenpixel={metrics.EdgePixels}");

        Assert.Multiple(() =>
        {
            Assert.That(metrics.Width, Is.GreaterThanOrEqualTo(600));
            Assert.That(metrics.Height, Is.GreaterThanOrEqualTo(400));
            Assert.That(metrics.MeanLuminance, Is.InRange(5, 250));
            Assert.That(metrics.LuminanceStandardDeviation, Is.GreaterThan(8));
            Assert.That(metrics.EdgePixels, Is.GreaterThan(1_000));
        });
    }

    [Test]
    [Order(4)]
    public void DevelopmentLoginAndNavigationProduceVisibleChange()
    {
        var window = RequiredEnvironment.MainWindow!;
        var sessionName = $"ui.active.{Guid.NewGuid():N}";
        var inputs = WaitForLoginInputs(window);
        var beforePath = CaptureWindow(window, "active-login-before.png");
        TestContext.AddTestAttachment(beforePath, "Loginseite vor der aktiven Bedienung");

        EnterText(inputs[0], sessionName);
        EnterText(inputs[1], "ui-test-only");
        var enteredPath = CaptureWindow(window, "active-login-entered.png");
        TestContext.AddTestAttachment(enteredPath, "Ausgefüllte Loginmaske vor dem Absenden");
        WaitForVisibleElement(window, ControlType.Button, "Anmelden").Click();

        WaitForWindowTitle(window, "Start");
        WaitForVisibleElement(window, ControlType.Hyperlink, "Spielen").Click();
        WaitForWindowTitle(window, "Spielen");
        var heading = WaitForVisibleNamedElement(window, "Sofortrunde");

        var visualChange = WaitForVisualChange(window, beforePath, "active-playing-after.png");
        TestContext.AddTestAttachment(visualChange.Path, "Sichtbarer Zustand nach Login und Navigation");
        TestContext.Progress.WriteLine(
            $"OpenCV-Zustandswechsel: mittlere absolute Differenz={visualChange.Mean:F2}; " +
            $"geänderte Pixel={visualChange.ChangedRatio:P2}");

        Assert.Multiple(() =>
        {
            Assert.That(heading.IsOffscreen, Is.False, "Die Sofortrunde muss sichtbar sein.");
            Assert.That(visualChange.Mean, Is.GreaterThan(2.5));
            Assert.That(visualChange.ChangedRatio, Is.GreaterThan(0.03));
        });
    }

    private string CaptureWindow(Window window, string name)
    {
        var path = Path.Combine(RequiredEnvironment.ArtifactDirectory, name);
        window.Focus();
        window.CaptureToFile(path);
        return path;
    }

    private (string Path, double Mean, double ChangedRatio) WaitForVisualChange(
        Window window,
        string beforePath,
        string name)
    {
        var timeout = Stopwatch.StartNew();
        var path = string.Empty;
        var difference = (Mean: 0d, ChangedRatio: 0d);
        while (timeout.Elapsed < TimeSpan.FromSeconds(20))
        {
            path = CaptureWindow(window, name);
            difference = CompareScreenshots(beforePath, path);
            if (difference.Mean > 2.5 && difference.ChangedRatio > 0.03)
            {
                break;
            }

            Thread.Sleep(200);
        }

        return (path, difference.Mean, difference.ChangedRatio);
    }

    private static AutomationElement[] WaitForLoginInputs(Window window)
    {
        var result = Retry.WhileNull(
            () =>
            {
                var username = FindVisibleElement(window, ControlType.Edit, "Benutzername");
                var password = FindVisibleElement(window, ControlType.Edit, "Passwort");
                if (username is not null && password is not null)
                {
                    return [username, password];
                }

                var candidates = window.FindAllDescendants(factory => factory.ByControlType(ControlType.Edit))
                    .Where(element => element.IsEnabled && !element.IsOffscreen)
                    .OrderBy(element => element.BoundingRectangle.Top)
                    .ToArray();
                var inputs = new List<AutomationElement>();
                foreach (var candidate in candidates)
                {
                    var bounds = candidate.BoundingRectangle;
                    if (inputs.Any(existing =>
                            bounds.Top < existing.BoundingRectangle.Bottom &&
                            bounds.Bottom > existing.BoundingRectangle.Top))
                    {
                        continue;
                    }

                    inputs.Add(candidate);
                }

                return inputs.Count >= 2 ? inputs.ToArray() : null;
            },
            TimeSpan.FromSeconds(20),
            TimeSpan.FromMilliseconds(200),
            throwOnTimeout: false,
            ignoreException: true);
        return result.Result ?? throw new AssertionException("Die sichtbaren Loginfelder wurden nicht rechtzeitig gefunden.");
    }

    private static void EnterText(AutomationElement input, string value)
    {
        input.Click();
        Keyboard.Type(value);
    }

    private static AutomationElement WaitForVisibleElement(Window window, ControlType controlType, string name)
    {
        var result = Retry.WhileNull(
            () => FindVisibleElement(window, controlType, name),
            TimeSpan.FromSeconds(20),
            TimeSpan.FromMilliseconds(200),
            throwOnTimeout: false,
            ignoreException: true);
        return result.Result ?? throw new AssertionException($"Das sichtbare UI-Element '{name}' wurde nicht rechtzeitig gefunden.");
    }

    private static AutomationElement WaitForVisibleNamedElement(Window window, string name)
    {
        var result = Retry.WhileNull(
            () => window.FindAllDescendants()
                .FirstOrDefault(element =>
                    element.IsEnabled &&
                    !element.IsOffscreen &&
                    element.Name.Equals(name, StringComparison.OrdinalIgnoreCase)),
            TimeSpan.FromSeconds(20),
            TimeSpan.FromMilliseconds(200),
            throwOnTimeout: false,
            ignoreException: true);
        return result.Result ?? throw new AssertionException($"Der sichtbare Text '{name}' wurde nicht rechtzeitig gefunden.");
    }

    private static void WaitForWindowTitle(Window window, string title)
    {
        var result = Retry.WhileNull(
            () => window.Title.StartsWith(title, StringComparison.OrdinalIgnoreCase) ? window : null,
            TimeSpan.FromSeconds(20),
            TimeSpan.FromMilliseconds(200),
            throwOnTimeout: false,
            ignoreException: true);
        if (result.Result is null)
        {
            throw new AssertionException($"Der Fenstertitel '{title}' wurde nicht rechtzeitig sichtbar.");
        }
    }

    private static AutomationElement? FindVisibleElement(Window window, ControlType controlType, string name) =>
        window.FindAllDescendants(factory => factory.ByControlType(controlType))
            .FirstOrDefault(element =>
                element.IsEnabled &&
                !element.IsOffscreen &&
                element.Name.Equals(name, StringComparison.OrdinalIgnoreCase));

    private static (double Mean, double ChangedRatio) CompareScreenshots(string beforePath, string afterPath)
    {
        using var before = Cv2.ImRead(beforePath, ImreadModes.Color);
        using var after = Cv2.ImRead(afterPath, ImreadModes.Color);
        Assert.That(before.Empty() || after.Empty(), Is.False, "OpenCV konnte die Vergleichsaufnahmen nicht lesen.");
        Assert.That(after.Size(), Is.EqualTo(before.Size()), "Die Vergleichsaufnahmen müssen gleich groß sein.");

        using var difference = new Mat();
        Cv2.Absdiff(before, after, difference);
        using var grayscale = new Mat();
        Cv2.CvtColor(difference, grayscale, ColorConversionCodes.BGR2GRAY);
        using var changed = new Mat();
        Cv2.Threshold(grayscale, changed, 12, 255, ThresholdTypes.Binary);
        return (Cv2.Mean(grayscale).Val0, Cv2.CountNonZero(changed) / (double)before.Total());
    }

    private WindowsUiTestEnvironment RequiredEnvironment =>
        environment ?? throw new InvalidOperationException("Die Windows-UI-Testumgebung wurde nicht gestartet.");
}
