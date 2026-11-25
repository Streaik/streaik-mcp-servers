using System.ComponentModel;
using System.Text.Json.Serialization;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using RESTCountries.NET.Models;
using RESTCountries.NET.Services;

namespace Streaik.Mcp.Servers;

internal static class RestCountries
{
    private const string Icon = "https://api.nuget.org/v3-flatcontainer/restcountries.net/3.5.0/icon";

    internal static McpServerPrimitiveCollection<McpServerTool> CreateTools() => LocalToolSupport.CreateTools(typeof(RestCountries));

    [Description("Search country codes and names")]
    [McpServerTool(Title = "Search country codes and names", Name = "github_rest_countries_search_codes",
        IconSource = Icon, UseStructuredContent = true, OutputSchemaType = typeof(CountryList), ReadOnly = true, OpenWorld = false)]
    public static CallToolResult SearchCodes([Description("Search query by name (contains)")] string name) =>
        LocalToolSupport.Structured(new CountryList
        {
            Countries = string.IsNullOrEmpty(name) ? RestCountriesService.GetAllCountries() : RestCountriesService.GetCountriesByNameContains(name),
        });

    [Description("Get all country details by the alpha-2 code")]
    [McpServerTool(Title = "Get all country details by the alpha-2 code", Name = "github_rest_countries_get_detail",
        IconSource = Icon, UseStructuredContent = true, OutputSchemaType = typeof(Country), ReadOnly = true, OpenWorld = false)]
    public static CallToolResult GetDetail([Description("The alpha-2 code of the country")] string cca) =>
        RestCountriesService.GetCountryByCode(cca) is { } country
            ? LocalToolSupport.Structured(country) : LocalToolSupport.Text($"{cca} not found", error: true);

    [Description("Get countries by region")]
    [McpServerTool(Title = "Get countries by region", Name = "github_rest_countries_get_by_region",
        IconSource = Icon, UseStructuredContent = true, OutputSchemaType = typeof(CountryList), ReadOnly = true, OpenWorld = false)]
    public static CallToolResult GetByRegion([Description("The region to filter on (e.g. Europe, Asia, Africa).")] string region) =>
        LocalToolSupport.Structured(new CountryList
        {
            Countries = RestCountriesService.GetAllCountries().Where(country => string.Equals(country.Region, region, StringComparison.OrdinalIgnoreCase)),
        });

    internal static ValueTask<CompleteResult> Complete(RequestContext<CompleteRequestParams> request, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var input = request.Params;
        if (input?.Argument is not { Name: { } name, Value: { } value })
            return ValueTask.FromResult(new CompleteResult { Completion = new() { Values = [] } });

        var comparisonArgument = name == "countryA" ? "countryB" : name == "countryB" ? "countryA" : null;
        var exclude = comparisonArgument is not null && input.Context?.Arguments?.TryGetValue(comparisonArgument, out var other) == true ? other : null;
        IEnumerable<string> values = name switch
        {
            "countryA" or "countryB" or "countryName" => (RestCountriesService.GetCountriesByNameContains(value) ?? [])
                .Select(country => country.Name.Official).OfType<string>()
                .Where(country => !string.Equals(country, exclude, StringComparison.OrdinalIgnoreCase)),
            "region" => RestCountriesService.GetAllCountries().Select(country => country.Region).OfType<string>().Distinct(),
            "capital" => RestCountriesService.GetAllCountries().SelectMany(country => country.Capital ?? []).OfType<string>().Distinct(),
            "currency" => RestCountriesService.GetAllCountries().SelectMany(country => country.Currencies?.Keys.AsEnumerable() ?? []).Distinct(),
            _ => [],
        };

        return ValueTask.FromResult(new CompleteResult { Completion = new() { Values = values.Take(100).Order().ToArray() } });
    }

    public sealed class CountryList
    {
        [JsonPropertyName("countries")]
        public IEnumerable<Country>? Countries { get; set; }
    }
}
