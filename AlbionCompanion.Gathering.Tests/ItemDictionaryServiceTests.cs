using System.Net;
using System.Text;
using AlbionCompanion.Core.Data;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace AlbionCompanion.Gathering.Tests;

public class ItemDictionaryServiceTests
{
    private sealed class FakeHttpMessageHandler : HttpMessageHandler
    {
        private readonly string _responseJson;

        public FakeHttpMessageHandler(string responseJson) => _responseJson = responseJson;

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(_responseJson, Encoding.UTF8, "application/json"),
            });
    }

    private const string SampleItemsJson = """
        [
            {
                "UniqueName": "T4_ORE",
                "LocalizedNames": { "EN-US": "Iron Ore", "PL-PL": "Ruda Żelaza" }
            },
            {
                "UniqueName": "MAIN_SWORD",
                "LocalizedNames": { "EN-US": "Sword", "PL-PL": "Miecz" }
            },
            {
                "LocalizedNames": { "EN-US": "No unique name, should be skipped" }
            }
        ]
        """;

    private static (ItemDictionaryService Service, AppDbContext Context) CreateService(SqliteConnection connection, string json)
    {
        var options = new DbContextOptionsBuilder<AppDbContext>().UseSqlite(connection).Options;
        var context = new AppDbContext(options);
        context.Database.EnsureCreated();
        var httpClient = new HttpClient(new FakeHttpMessageHandler(json));
        return (new ItemDictionaryService(context, httpClient), context);
    }

    [Fact]
    public async Task SeedFromJsonAsync_ParsesTierAndGroupFromUniqueName()
    {
        using var connection = new SqliteConnection("DataSource=:memory:");
        connection.Open();
        var (service, context) = CreateService(connection, SampleItemsJson);

        await service.SeedFromJsonAsync();

        var ore = await service.GetItemByIdAsync("T4_ORE");
        Assert.NotNull(ore);
        Assert.Equal(4, ore!.Tier);
        Assert.Equal("ORE", ore.ItemGroup);
        Assert.Equal("Ruda Żelaza", ore.DisplayNamePL);
        Assert.Equal("Iron Ore", ore.DisplayNameEN);
    }

    [Fact]
    public async Task SeedFromJsonAsync_ItemWithoutTierPrefix_GetsTierZeroAndFullNameAsGroup()
    {
        using var connection = new SqliteConnection("DataSource=:memory:");
        connection.Open();
        var (service, context) = CreateService(connection, SampleItemsJson);

        await service.SeedFromJsonAsync();

        var sword = await service.GetItemByIdAsync("MAIN_SWORD");
        Assert.NotNull(sword);
        Assert.Equal(0, sword!.Tier);
        Assert.Equal("MAIN_SWORD", sword.ItemGroup);
    }

    [Fact]
    public async Task SeedFromJsonAsync_EntryWithoutUniqueName_IsSkipped()
    {
        using var connection = new SqliteConnection("DataSource=:memory:");
        connection.Open();
        var (service, context) = CreateService(connection, SampleItemsJson);

        await service.SeedFromJsonAsync();

        Assert.Equal(2, await context.ItemDictionaries.CountAsync());
    }

    [Fact]
    public async Task SeedFromJsonAsync_WhenAlreadyPopulated_DoesNotFetchOrDuplicate()
    {
        using var connection = new SqliteConnection("DataSource=:memory:");
        connection.Open();
        var (service, context) = CreateService(connection, SampleItemsJson);
        await service.SeedFromJsonAsync();

        // A second call with a handler that would throw if invoked confirms no re-fetch happens.
        var options = new DbContextOptionsBuilder<AppDbContext>().UseSqlite(connection).Options;
        var secondContext = new AppDbContext(options);
        var throwingClient = new HttpClient(new ThrowingHttpMessageHandler());
        var secondService = new ItemDictionaryService(secondContext, throwingClient);

        await secondService.SeedFromJsonAsync();

        Assert.Equal(2, await context.ItemDictionaries.CountAsync());
    }

    private sealed class ThrowingHttpMessageHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            throw new InvalidOperationException("Should not fetch when already seeded.");
    }

    [Fact]
    public async Task GetItemByIdAsync_UnknownId_ReturnsNull()
    {
        using var connection = new SqliteConnection("DataSource=:memory:");
        connection.Open();
        var (service, context) = CreateService(connection, SampleItemsJson);
        await service.SeedFromJsonAsync();

        Assert.Null(await service.GetItemByIdAsync("T99_DOES_NOT_EXIST"));
    }

    [Fact]
    public async Task SearchItemsAsync_MatchesByPolishDisplayName()
    {
        using var connection = new SqliteConnection("DataSource=:memory:");
        connection.Open();
        var (service, context) = CreateService(connection, SampleItemsJson);
        await service.SeedFromJsonAsync();

        var results = (await service.SearchItemsAsync("Żelaza")).ToList();

        var result = Assert.Single(results);
        Assert.Equal("T4_ORE", result.UniqueName);
    }
}
