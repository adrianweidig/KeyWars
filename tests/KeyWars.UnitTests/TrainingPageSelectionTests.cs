using KeyWars.Domain;
using KeyWars.Pages.Spielen;

namespace KeyWars.UnitTests;

public sealed class TrainingPageSelectionTests
{
    [Theory]
    [InlineData(15, TrainingMode.Sprint15)]
    [InlineData(30, TrainingMode.Sprint30)]
    [InlineData(60, TrainingMode.Sprint60)]
    [InlineData(120, TrainingMode.Sprint120)]
    public void SprintPageSelectsSupportedDuration(int seconds, TrainingMode expectedMode)
    {
        var page = new SprintModel();

        page.OnGet(seconds);

        Assert.Equal(seconds, page.SelectedSeconds);
        Assert.Equal(expectedMode, page.SelectedMode);
    }

    [Fact]
    public void SprintPageNormalizesMissingAndUnsupportedDuration()
    {
        var page = new SprintModel();

        page.OnGet(null);
        Assert.Equal(60, page.SelectedSeconds);
        Assert.Equal(TrainingMode.Sprint60, page.SelectedMode);

        page.OnGet(999);
        Assert.Equal(60, page.SelectedSeconds);
        Assert.Equal(TrainingMode.Sprint60, page.SelectedMode);
    }

    [Theory]
    [InlineData(10, TrainingMode.Words10)]
    [InlineData(25, TrainingMode.Words25)]
    [InlineData(50, TrainingMode.Words50)]
    [InlineData(100, TrainingMode.Words100)]
    public void WordsPageSelectsSupportedWordCount(int words, TrainingMode expectedMode)
    {
        var page = new WoerterModel();

        page.OnGet(words);

        Assert.Equal(words, page.SelectedWords);
        Assert.Equal(expectedMode, page.SelectedMode);
    }

    [Fact]
    public void WordsPageNormalizesMissingAndUnsupportedWordCount()
    {
        var page = new WoerterModel();

        page.OnGet(null);
        Assert.Equal(25, page.SelectedWords);
        Assert.Equal(TrainingMode.Words25, page.SelectedMode);

        page.OnGet(-1);
        Assert.Equal(25, page.SelectedWords);
        Assert.Equal(TrainingMode.Words25, page.SelectedMode);
    }
}
