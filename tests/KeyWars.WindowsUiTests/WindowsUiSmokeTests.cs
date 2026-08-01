using FlaUI.Core.Definitions;

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
        if (!OperatingSystem.IsWindows())
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
    public void Uia3ExposesTheLoginControls()
    {
        var window = RequiredEnvironment.MainWindow!;
        var document = window.FindFirstDescendant(factory => factory.ByControlType(ControlType.Document));
        var inputFields = window.FindAllDescendants(factory => factory.ByControlType(ControlType.Edit));
        var loginButton = window.FindAllDescendants(factory => factory.ByControlType(ControlType.Button))
            .FirstOrDefault(element => element.Name.Equals("Anmelden", StringComparison.OrdinalIgnoreCase));

        Assert.Multiple(() =>
        {
            Assert.That(document, Is.Not.Null, "Der Browser muss seinen Dokumentbereich über UIA3 bereitstellen.");
            Assert.That(inputFields, Has.Length.GreaterThanOrEqualTo(2), "Die Anmeldemaske muss zwei Eingabefelder anbieten.");
            Assert.That(loginButton, Is.Not.Null, "Die Anmeldemaske muss die Schaltfläche 'Anmelden' anbieten.");
            Assert.That(loginButton?.IsEnabled, Is.True, "Die Anmeldeschaltfläche muss bedienbar sein.");
        });
    }

    [Test]
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

    private WindowsUiTestEnvironment RequiredEnvironment =>
        environment ?? throw new InvalidOperationException("Die Windows-UI-Testumgebung wurde nicht gestartet.");
}
