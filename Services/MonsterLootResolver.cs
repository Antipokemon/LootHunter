using System.Net;
using System.Text.Json;
using System.Text.RegularExpressions;
using HtmlAgilityPack;

namespace LootHunter.Services;

public sealed record MonsterLootFallbackRecord(
    string MobName,
    int? MobLevel,
    string TerritoryName,
    float MapX,
    float MapY);

/// <summary>
/// On-demand monster-drop enrichment modeled after MonsterLootHunter's data strategy.
/// This is an internal LootHunter service; MonsterLootHunter is not a runtime dependency.
/// </summary>
public sealed partial class MonsterLootResolver : IDisposable
{
    private const string WikiApi = "https://ffxiv.consolegameswiki.com/mediawiki/api.php?action=parse&page={0}&format=json";

    private readonly HttpClient httpClient;
    private readonly Dictionary<uint, IReadOnlyList<MonsterLootFallbackRecord>> cache = [];
    private readonly IPluginLogAdapter log;

    public MonsterLootResolver(Dalamud.Plugin.Services.IPluginLog pluginLog)
    {
        log = new PluginLogAdapter(pluginLog);
        httpClient = new HttpClient
        {
            Timeout = TimeSpan.FromSeconds(20),
        };
        httpClient.DefaultRequestHeaders.UserAgent.ParseAdd("LootHunter/0.1 (+https://github.com/Antipokemon/LootHunter)");
    }

    public async Task<IReadOnlyList<MonsterLootFallbackRecord>> ResolveAsync(
        uint itemId,
        string itemName,
        CancellationToken cancellationToken)
    {
        if (cache.TryGetValue(itemId, out var cached))
            return cached;

        if (string.IsNullOrWhiteSpace(itemName))
            return [];

        try
        {
            var pageName = Uri.EscapeDataString(itemName.Trim().Replace(' ', '_'));
            var response = await httpClient.GetAsync(string.Format(WikiApi, pageName), cancellationToken).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                log.Warning($"MonsterLoot fallback lookup for {itemName} returned HTTP {(int)response.StatusCode}.");
                cache[itemId] = [];
                return [];
            }

            await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
            using var document = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken).ConfigureAwait(false);
            if (!document.RootElement.TryGetProperty("parse", out var parse) ||
                !parse.TryGetProperty("text", out var text) ||
                !text.TryGetProperty("*", out var htmlElement))
            {
                cache[itemId] = [];
                return [];
            }

            var html = htmlElement.GetString();
            if (string.IsNullOrWhiteSpace(html))
            {
                cache[itemId] = [];
                return [];
            }

            var results = ParseMonsterDrops(html);
            cache[itemId] = results;
            if (results.Count > 0)
                log.Information($"MonsterLoot fallback resolved {results.Count} monster location(s) for {itemName} ({itemId}).");
            return results;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            log.Warning($"MonsterLoot fallback lookup failed for {itemName} ({itemId}): {ex.Message}");
            cache[itemId] = [];
            return [];
        }
    }

    private static IReadOnlyList<MonsterLootFallbackRecord> ParseMonsterDrops(string html)
    {
        var document = new HtmlDocument();
        document.LoadHtml(html);

        var droppedBy = document.DocumentNode.SelectSingleNode("//*[@id='Dropped_By']");
        var heading = droppedBy is not null && IsHeading(droppedBy)
            ? droppedBy
            : droppedBy?.Ancestors().FirstOrDefault(IsHeading);

        var table = heading?.SelectSingleNode("following-sibling::table[1]")
            ?? document.DocumentNode.SelectSingleNode("//table[contains(@class,'item')]");
        if (table is null)
            return [];

        var rows = table.SelectNodes(".//tr");
        if (rows is null || rows.Count <= 1)
            return [];

        var results = new List<MonsterLootFallbackRecord>();
        foreach (var row in rows.Skip(1))
        {
            var cells = row.SelectNodes("./td");
            if (cells is null || cells.Count < 3)
                continue;

            var mobName = Clean(cells[0].InnerText);
            var levelText = Clean(cells[1].InnerText);
            var locationText = Clean(cells[cells.Count - 1].InnerText);
            if (string.IsNullOrWhiteSpace(mobName) || string.IsNullOrWhiteSpace(locationText))
                continue;

            var location = LocationRegex().Match(locationText);
            if (!location.Success ||
                !float.TryParse(location.Groups["x"].Value, System.Globalization.CultureInfo.InvariantCulture, out var mapX) ||
                !float.TryParse(location.Groups["y"].Value, System.Globalization.CultureInfo.InvariantCulture, out var mapY))
                continue;

            int? mobLevel = null;
            var level = LevelRegex().Match(levelText);
            if (level.Success && int.TryParse(level.Value, out var parsedLevel))
                mobLevel = parsedLevel;

            results.Add(new MonsterLootFallbackRecord(
                mobName,
                mobLevel,
                location.Groups["zone"].Value.Trim(),
                mapX,
                mapY));
        }

        return results
            .DistinctBy(x => (x.MobName.ToUpperInvariant(), x.TerritoryName.ToUpperInvariant(), x.MapX, x.MapY))
            .ToList();
    }

    private static bool IsHeading(HtmlNode node)
        => node.Name.Equals("h2", StringComparison.OrdinalIgnoreCase) ||
           node.Name.Equals("h3", StringComparison.OrdinalIgnoreCase) ||
           node.Name.Equals("h4", StringComparison.OrdinalIgnoreCase);

    private static string Clean(string value)
        => WebUtility.HtmlDecode(value)
            .Replace('\u00A0', ' ')
            .Replace("\r", string.Empty)
            .Replace("\n", " ")
            .Trim();

    [GeneratedRegex(@"^(?<zone>.+?)\s*\(\s*(?:X\s*:\s*)?(?<x>\d+(?:\.\d+)?)\s*,\s*(?:Y\s*:\s*)?(?<y>\d+(?:\.\d+)?)\s*\)", RegexOptions.IgnoreCase | RegexOptions.Compiled)]
    private static partial Regex LocationRegex();

    [GeneratedRegex(@"\d+", RegexOptions.Compiled)]
    private static partial Regex LevelRegex();

    public void Dispose() => httpClient.Dispose();

    private interface IPluginLogAdapter
    {
        void Information(string message);
        void Warning(string message);
    }

    private sealed class PluginLogAdapter(Dalamud.Plugin.Services.IPluginLog pluginLog) : IPluginLogAdapter
    {
        public void Information(string message) => pluginLog.Information(message);
        public void Warning(string message) => pluginLog.Warning(message);
    }
}
