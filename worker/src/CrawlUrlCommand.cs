namespace MiniSearchWorker;

public sealed record CrawlUrlCommand(string Url, string Query, string? Title);
