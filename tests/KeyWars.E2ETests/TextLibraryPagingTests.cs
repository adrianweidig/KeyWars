using System.Net;
using System.Text.RegularExpressions;
using KeyWars.Data;
using KeyWars.Domain;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace KeyWars.E2ETests;

public sealed class TextLibraryPagingTests
{
    [Fact]
    public async Task TextLibraryShowsExactCountAndKeepsFiltersAcrossBoundedPages()
    {
        using var factory = new KeyWarsWebFactory();
        var client = factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
        await LoginAsync(client);

        await using (var scope = factory.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<KeyWarsDbContext>();
            var profileId = await db.UserProfiles
                .Where(profile => profile.SamAccountName == "max.mustermann" && !profile.Deleted)
                .Select(profile => profile.Id)
                .SingleAsync();
            db.TrainingTexts.AddRange(Enumerable.Range(1, 55).Select(index => new TrainingText
            {
                OwnerProfileId = profileId,
                Title = $"Pager {index:000}",
                SourceKey = $"pager-{index:000}",
                Body = $"Pager-Inhalt {index:000}",
                Visibility = TrainingTextVisibility.Private,
                CharacterCount = 17
            }));
            await db.SaveChangesAsync();
        }

        var firstPage = WebUtility.HtmlDecode(await client.GetStringAsync(
            "/texte?Suche=Pager&Sichtbarkeit=Private&Seite=1"));
        var nextLink = PaginationLink(firstPage, "next");

        Assert.Contains("<strong>55</strong> sichtbar", firstPage);
        Assert.Contains("Treffer 1–48 von 55", firstPage);
        Assert.Contains("data-current-page=\"1\"", firstPage);
        Assert.Contains("data-total-pages=\"2\"", firstPage);
        Assert.Contains("Suche=Pager", nextLink);
        Assert.Contains("Sichtbarkeit=Private", nextLink);
        Assert.Contains("Seite=2", nextLink);
        Assert.DoesNotContain("data-page-direction=\"previous\"", firstPage);

        var boundedLastPage = WebUtility.HtmlDecode(await client.GetStringAsync(
            "/texte?Suche=Pager&Sichtbarkeit=Private&Seite=2147483647"));
        var previousLink = PaginationLink(boundedLastPage, "previous");

        Assert.Contains("Treffer 49–55 von 55", boundedLastPage);
        Assert.Contains("data-current-page=\"2\"", boundedLastPage);
        Assert.Contains("Pager 049", boundedLastPage);
        Assert.Contains("Pager 055", boundedLastPage);
        Assert.DoesNotContain("Pager 048", boundedLastPage);
        Assert.Contains("Suche=Pager", previousLink);
        Assert.Contains("Sichtbarkeit=Private", previousLink);
        Assert.Contains("Seite=1", previousLink);
        Assert.DoesNotContain("data-page-direction=\"next\"", boundedLastPage);
    }

    private static string PaginationLink(string html, string direction)
    {
        var match = Regex.Match(
            html,
            $"<a(?=[^>]*data-page-direction=\"{Regex.Escape(direction)}\")[^>]*>",
            RegexOptions.CultureInvariant);
        Assert.True(match.Success, $"Pagination-Link '{direction}' fehlt.");
        return match.Value;
    }

    private static async Task LoginAsync(HttpClient client)
    {
        var login = await client.GetStringAsync("/anmelden");
        var token = Regex.Match(
            login,
            "name=\"__RequestVerificationToken\" type=\"hidden\" value=\"(?<token>[^\"]+)\"",
            RegexOptions.CultureInvariant).Groups["token"].Value;
        Assert.NotEmpty(token);

        var response = await client.PostAsync("/anmelden", new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["Input.Username"] = "max.mustermann",
            ["Input.Password"] = "lokales-test-passwort",
            ["__RequestVerificationToken"] = token
        }));
        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
    }
}
