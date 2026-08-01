using OpenCvSharp;

namespace KeyWars.WindowsUiTests;

internal static class VisualAssertions
{
    public static VisualMetrics Analyze(string screenshotPath)
    {
        using var source = Cv2.ImRead(screenshotPath, ImreadModes.Color);
        Assert.That(source.Empty(), Is.False, "OpenCV konnte die gerenderte Browseraufnahme nicht lesen.");

        using var grayscale = new Mat();
        Cv2.CvtColor(source, grayscale, ColorConversionCodes.BGR2GRAY);
        Cv2.MeanStdDev(grayscale, out var mean, out var standardDeviation);

        using var edges = new Mat();
        Cv2.Canny(grayscale, edges, 70, 160);

        return new VisualMetrics(
            source.Width,
            source.Height,
            mean.Val0,
            standardDeviation.Val0,
            Cv2.CountNonZero(edges));
    }
}

internal sealed record VisualMetrics(
    int Width,
    int Height,
    double MeanLuminance,
    double LuminanceStandardDeviation,
    int EdgePixels);
