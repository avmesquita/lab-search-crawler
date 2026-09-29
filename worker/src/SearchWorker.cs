using System.Net.Http.Json;
using Rebus.Bus;

namespace MiniSearchWorker;

public sealed class SearchWorker : BackgroundService
{
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly IBus _bus;
    private readonly IConfiguration _configuration;
    private readonly ILogger<SearchWorker> _logger;

    public SearchWorker(
        IHttpClientFactory httpClientFactory,
        IBus bus,
        IConfiguration configuration,
        ILogger<SearchWorker> logger)
    {
        _httpClientFactory = httpClientFactory;
        _bus = bus;
        _configuration = configuration;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var queries = (_configuration["SEARCH_QUERIES"] ?? "")
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var intervalSeconds = Math.Max(1, _configuration.GetValue("SEARCH_INTERVAL_SECONDS", 3600));
        var retrySeconds = Math.Max(1, _configuration.GetValue("SEARCH_RETRY_INTERVAL_SECONDS", 60));

        if (queries.Length == 0)
        {
            _logger.LogWarning("Nenhuma busca configurada em SEARCH_QUERIES; worker permanecerá ocioso.");
            return;
        }

        _logger.LogInformation("Search worker iniciado com {QueryCount} buscas configuradas.", queries.Length);

        while (!stoppingToken.IsCancellationRequested)
        {
            var hadFailures = false;
            foreach (var query in queries)
            {
                if (stoppingToken.IsCancellationRequested) break;
                hadFailures |= !await SearchAndEnqueueAsync(query, stoppingToken);
            }

            try
            {
                var delay = TimeSpan.FromSeconds(hadFailures ? retrySeconds : intervalSeconds);
                await Task.Delay(delay, stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
        }
    }

    private async Task<bool> SearchAndEnqueueAsync(string query, CancellationToken cancellationToken)
    {
        try
        {
            var client = _httpClientFactory.CreateClient("SearxngClient");
            var endpoint = $"search?q={Uri.EscapeDataString(query)}&format=json";
            var response = await client.GetFromJsonAsync<SearxngResponse>(endpoint, cancellationToken);

            foreach (var result in response?.Results?.Take(3) ?? Enumerable.Empty<SearxngResult>())
            {
                if (!Uri.TryCreate(result.Url, UriKind.Absolute, out var uri) ||
                    (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
                {
                    continue;
                }

                await _bus.Send(new CrawlUrlCommand(uri.ToString(), query, result.Title));
                _logger.LogInformation("URL enviada para crawl: {Url} (busca: {Query})", uri, query);
            }

            return true;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Falha ao consultar SearXNG para a busca {Query}.", query);
            return false;
        }
    }

    private sealed record SearxngResponse(List<SearxngResult>? Results);
    private sealed record SearxngResult(string Url, string? Title);
}
