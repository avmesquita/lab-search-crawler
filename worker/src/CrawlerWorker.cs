using System.Security.Cryptography;
using System.Text;
using HtmlAgilityPack;
using Rebus.Handlers;
using StackExchange.Redis;

namespace MiniSearchWorker;

public sealed class CrawlerWorker : IHandleMessages<CrawlUrlCommand>
{
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly IDatabase _redis;
    private readonly IConfiguration _configuration;
    private readonly ILogger<CrawlerWorker> _logger;

    public CrawlerWorker(
        IHttpClientFactory httpClientFactory,
        IConnectionMultiplexer redisConnection,
        IConfiguration configuration,
        ILogger<CrawlerWorker> logger)
    {
        _httpClientFactory = httpClientFactory;
        _redis = redisConnection.GetDatabase();
        _configuration = configuration;
        _logger = logger;
    }

    public async Task Handle(CrawlUrlCommand message)
    {
        if (!Uri.TryCreate(message.Url, UriKind.Absolute, out var uri) ||
            (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
        {
            _logger.LogWarning("Comando ignorado: URL inválida {Url}.", message.Url);
            return;
        }

        var normalizedUrl = new UriBuilder(uri) { Fragment = string.Empty }.Uri.ToString();
        var cacheKey = $"cache:page:{ComputeHash(normalizedUrl)}";

        if (await _redis.KeyExistsAsync(cacheKey))
        {
            _logger.LogInformation("URL já existe no cache: {Url}", normalizedUrl);
            return;
        }

        _logger.LogInformation("Baixando conteúdo de {Url} (busca: {Query})", normalizedUrl, message.Query);

        var client = _httpClientFactory.CreateClient("CrawlerClient");
        using var response = await client.GetAsync(normalizedUrl, HttpCompletionOption.ResponseHeadersRead);
        if (!response.IsSuccessStatusCode)
        {
            throw new HttpRequestException(
                $"Falha ao acessar {normalizedUrl}: HTTP {(int)response.StatusCode} ({response.StatusCode}).",
                null,
                response.StatusCode);
        }

        var htmlContent = await response.Content.ReadAsStringAsync();
        var rawText = ExtractTextFromHtml(htmlContent);
        var ttlSeconds = Math.Max(1, _configuration.GetValue("CACHE_TTL_SECONDS", 86400));

        await _redis.HashSetAsync(cacheKey, new HashEntry[]
        {
            new("url", normalizedUrl),
            new("title", message.Title ?? string.Empty),
            new("query", message.Query),
            new("content", rawText),
            new("fetched_at", DateTimeOffset.UtcNow.ToString("O"))
        });
        await _redis.KeyExpireAsync(cacheKey, TimeSpan.FromSeconds(ttlSeconds));

        _logger.LogInformation("Snapshot salvo no Redis: {Key}", cacheKey);
    }

    private static string ExtractTextFromHtml(string html)
    {
        var document = new HtmlDocument();
        document.LoadHtml(html);

        var nodesToRemove = document.DocumentNode.SelectNodes("//script|//style|//noscript|//head");
        if (nodesToRemove is not null)
        {
            foreach (var node in nodesToRemove)
            {
                node.Remove();
            }
        }

        return System.Text.RegularExpressions.Regex
            .Replace(document.DocumentNode.InnerText, @"\s+", " ")
            .Trim();
    }

    private static string ComputeHash(string input)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(input));
        return Convert.ToHexString(bytes)[..16];
    }
}
