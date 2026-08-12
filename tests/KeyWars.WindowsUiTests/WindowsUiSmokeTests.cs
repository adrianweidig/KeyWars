using FlaUI.Core.AutomationElements;
using FlaUI.Core.Definitions;
using FlaUI.Core.Tools;
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

        EnterText(inputs[0], sessionName);
        EnterText(inputs[1], "ui-test-only");
        WaitForVisibleElement(window, ControlType.Button, "Anmelden").AsButton().Invoke();

        Invoke(WaitForVisibleElement(window, ControlType.Hyperlink, "Spielen"));
        WaitForVisibleNamedElement(window, "Sofortrunde");
        WaitForVisibleElement(window, ControlType.Edit, "Eingabe");
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
        Assert.That(input.Patterns.Value.IsSupported, Is.True, "Das Eingabefeld muss das UIA-Value-Pattern unterstützen.");
        input.Patterns.Value.Pattern.SetValue(value);
    }

    private static void Invoke(AutomationElement element)
    {
        Assert.That(element.Patterns.Invoke.IsSupported, Is.True, $"'{element.Name}' muss das UIA-Invoke-Pattern unterstützen.");
        element.Patterns.Invoke.Pattern.Invoke();
    }

    private static AutomationElement WaitForVisibleElement(Window window, ControlType controlType, string name)
    {
        var result = Retry.WhileNull(
            () => FindVisibleElement(window, controlType, name),
            TimeSpan.FromSeconds(20),
            TimeSpan.FromMilliseconds(200),
            throwOnTimeout: false,
            ignoreException: true);
        return result.Result ?? throw new AssertionException(
            $"Das sichtbare UI-Element '{name}' wurde nicht rechtzeitig gefunden. " +
            $"Fenster: '{window.Title}'. Sichtbarer UIA-Baum: {DescribeVisibleElements(window)}");
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
        return result.Result ?? throw new AssertionException(
            $"Der sichtbare Text '{name}' wurde nicht rechtzeitig gefunden. " +
            $"Fenster: '{window.Title}'. Sichtbarer UIA-Baum: {DescribeVisibleElements(window)}");
    }

    private static string DescribeVisibleElements(Window window) =>
        string.Join(
            " | ",
            window.FindAllDescendants()
                .Where(element => !element.IsOffscreen && !string.IsNullOrWhiteSpace(element.Name))
                .Take(60)
                .Select(element => $"{element.ControlType}:{element.Name}"));

    private static AutomationElement? FindVisibleElement(Window window, ControlType controlType, string name) =>
        window.FindAllDescendants(factory => factory.ByControlType(controlType))
            .FirstOrDefault(element =>
                element.IsEnabled &&
                !element.IsOffscreen &&
                element.Name.Equals(name, StringComparison.OrdinalIgnoreCase));

    private WindowsUiTestEnvironment RequiredEnvironment =>
        environment ?? throw new InvalidOperationException("Die Windows-UI-Testumgebung wurde nicht gestartet.");
}
