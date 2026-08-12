using System.Net;
using System.Text.Json;
using System.Text.RegularExpressions;
using KeyWars.Data;
using KeyWars.Domain;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace KeyWars.E2ETests;

public sealed class PeopleSearchPagingTests
{
    [Fact]
    public async Task SearchReturnsBoundedStructuredDepartmentPageForChallengePurpose()
    {
        using var factory = new KeyWarsWebFactory();
        var client = factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
        await LoginAsync(client);
        const string department = "Datenplattform";

        await using (var scope = factory.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<KeyWarsDbContext>();
            db.UserProfiles.AddRange(Enumerable.Range(1, 15).Select(index => new UserProfile
            {
                DisplayName = $"Paging Person {index:00}",
                SamAccountName = $"paging.person.{index:00}",
                UserPrincipalName = $"paging.person.{index:00}@test.invalid",
                DirectoryObjectGuid = Guid.CreateVersion7().ToString(),
                DirectorySid = $"S-1-5-21-{Guid.CreateVersion7():N}",
                Department = department,
                ChallengesEnabled = index <= 12
            }));
            await db.SaveChangesAsync();
        }

        using var response = await client.GetAsync(
            $"/api/personen/suche?department={Uri.EscapeDataString(department)}&purpose=challenge&page=2&pageSize=5");
        response.EnsureSuccessStatusCode();
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var root = document.RootElement;

        Assert.Equal(2, root.GetProperty("page").GetInt32());
        Assert.Equal(5, root.GetProperty("pageSize").GetInt32());
        Assert.Equal(12, root.GetProperty("totalCount").GetInt32());
        Assert.Equal(3, root.GetProperty("totalPages").GetInt32());
        var items = root.GetProperty("items").EnumerateArray().ToArray();
        Assert.Equal(5, items.Length);
        Assert.All(items, item => Assert.Equal(department, item.GetProperty("department").GetString()));
        Assert.All(items, item => Assert.False(string.IsNullOrWhiteSpace(item.GetProperty("label").GetString())));
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
