# Lab Search Crawler

Projeto local de busca e cache de páginas. O SearXNG encontra URLs, o worker de busca publica tarefas via Rebus/RabbitMQ e os workers de crawl baixam as páginas e guardam o texto extraído no Redis.

## Fluxo

```text
SearXNG → search-worker → RabbitMQ (CrawlUrlCommand) → crawler-worker → Redis
```

- `search-worker` executa as buscas de `SEARCH_QUERIES`, até três resultados por busca, e envia `CrawlUrlCommand` para a fila `mini-search-crawler-worker`.
- `crawler-worker` consome essa fila, ignora URLs já presentes no cache, extrai texto e grava um hash Redis com TTL.
- RabbitMQ persiste as tarefas pendentes; falhas no handler são tratadas pelo mecanismo de retry e pela fila de erro do Rebus.
- É possível aumentar consumidores de crawl com `docker compose up -d --build --scale crawler-worker=4`.

## Executar

```sh
cp .env.example .env
```

Edite `.env` e defina um valor aleatório para `SEARXNG_SECRET`. Depois suba a stack:

```sh
docker compose up -d --build
```

- SearXNG: http://localhost:3335
- Redis: `localhost:6369`
- RabbitMQ AMQP: `localhost:6670`
- RabbitMQ Management: http://localhost:6671

As buscas, filas, TTL, workers e concorrência podem ser ajustados nas variáveis do serviço em `docker-compose.yml`. Para trocar as buscas iniciais, edite `SEARCH_QUERIES` no serviço `search-worker`.

O arquivo `searxng/settings.yml` habilita o formato JSON exigido pelo cliente de busca. O secret padrão do Compose é apenas para desenvolvimento local; configure `SEARXNG_SECRET` no `.env` para uso compartilhado.
