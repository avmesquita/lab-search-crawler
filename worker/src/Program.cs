using MiniSearchWorker;
using Rebus.Config;
using Rebus.Routing.TypeBased;
using Rebus.ServiceProvider;
using StackExchange.Redis;

var builder = Host.CreateApplicationBuilder(args);
var configuration = builder.Configuration;

var workerRole = (configuration["WORKER_ROLE"] ?? "crawler").Trim().ToLowerInvariant();
if (workerRole is not ("search" or "crawler"))
{
    throw new InvalidOperationException("WORKER_ROLE precisa ser 'search' ou 'crawler'.");
}

var rabbitConnectionString = configuration["RABBITMQ_CONNECTION_STRING"]
    ?? "amqp://mini_search:mini_search_dev@localhost:5672";
var searchQueue = configuration["SEARCH_WORKER_QUEUE"] ?? "mini-search-search-worker";
var crawlerQueue = configuration["CRAWLER_WORKER_QUEUE"] ?? "mini-search-crawler-worker";
var inputQueue = workerRole == "search" ? searchQueue : crawlerQueue;

builder.Services.AddRebus((configure, _) => configure
    .Transport(transport => transport.UseRabbitMq(rabbitConnectionString, inputQueue))
    .Routing(route => route.TypeBased().Map<CrawlUrlCommand>(crawlerQueue))
    .Options(options =>
    {
        var workers = Math.Max(1, configuration.GetValue("REBUS_WORKERS", 1));
        var maxParallelism = Math.Max(1, configuration.GetValue("REBUS_MAX_PARALLELISM", workers));
        options.SetNumberOfWorkers(workers);
        options.SetMaxParallelism(maxParallelism);
    }));

if (workerRole == "search")
{
    builder.Services.AddHttpClient("SearxngClient", client =>
    {
        client.BaseAddress = new Uri(configuration["SEARXNG_BASE_URL"] ?? "http://searxng/");
        client.Timeout = TimeSpan.FromSeconds(20);
    });
    builder.Services.AddHostedService<SearchWorker>();
}
else
{
    var redisConnectionString = configuration.GetConnectionString("Redis")
        ?? configuration["REDIS_HOST"]
        ?? "localhost:6379";

    var redisOptions = ConfigurationOptions.Parse(redisConnectionString);
    redisOptions.AbortOnConnectFail = false;
    builder.Services.AddSingleton<IConnectionMultiplexer>(
        ConnectionMultiplexer.Connect(redisOptions));

    builder.Services.AddHttpClient("CrawlerClient", client =>
    {
        client.Timeout = TimeSpan.FromSeconds(30);
        client.DefaultRequestHeaders.UserAgent.ParseAdd("MiniSearchCrawler/1.0 (+https://github.com/avmesquita/lab-search-crawler)");
    });

    builder.Services.AutoRegisterHandlersFromAssemblyOf<CrawlerWorker>();
}

await builder.Build().RunAsync();
