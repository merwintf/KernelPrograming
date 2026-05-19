using Elastic.Clients.Elasticsearch;
using System.Text.Json;

public async Task ReadBugSeenChangesAsync(
    ElasticsearchClient client,
    DateTime fromUtc,
    DateTime toUtc,
    CancellationToken cancellationToken = default)
{
    var response = await client.SearchAsync<JsonDocument>(s => s
        .Index("your-index-name")
        .Size(1000)
        .Query(q => q
            .Bool(b => b
                .Must(m => m
                    .MatchPhrase(mp => mp
                        .Field("message")
                        .Query("seen count changed")
                    )
                )
                .Filter(f => f
                    .DateRange(r => r
                        .Field("timestamp")
                        .Gte(fromUtc)
                        .Lt(toUtc)
                    )
                )
            )
        ),
        cancellationToken);

    if (!response.IsValidResponse)
        throw new Exception(response.ElasticsearchServerError?.Error?.Reason);

    foreach (var hit in response.Hits)
    {
        var source = hit.Source;

        if (source is null)
            continue;

        var root = source.RootElement;

        var bugId = root.GetProperty("bugId").GetInt64();
        var updatedByUserId = root.GetProperty("updatedByUserId").GetInt64();
        var occurredAt = root.GetProperty("timestamp").GetDateTime();

        Console.WriteLine($"{bugId} - {updatedByUserId} - {occurredAt}");
    }
}
