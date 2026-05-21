using Elastic.Clients.Elasticsearch;
using Elastic.Clients.Elasticsearch.QueryDsl;
using Elastic.Transport;

var settings = new ElasticsearchClientSettings(new Uri("https://YOUR_ES_URL"))
    .Authentication(new ApiKey("YOUR_API_KEY"));

var client = new ElasticsearchClient(settings);

const string indexName = "rumba";
const int pageSize = 1000;

object[]? searchAfter = null;

while (true)
{
    var response = await client.SearchAsync<object>(s => s
        .Index(indexName)
        .Size(pageSize)
        .Sort(sort => sort
            .Field("@timestamp", new FieldSort { Order = SortOrder.Asc })
            .Field("_id", new FieldSort { Order = SortOrder.Asc })
        )
        .SearchAfter(searchAfter)
        .Query(q => q
            .Bool(b => b
                .Filter(
                    f => f.MatchPhrase(mp => mp
                        .Field("message")
                        .Query("your exact phrase here")
                    ),
                    f => f.DateRange(dr => dr
                        .Field("@timestamp")
                        .Gte("2026-05-01T00:00:00Z")
                        .Lte("2026-05-20T23:59:59Z")
                    )
                )
            )
        )
    );

    if (!response.IsValidResponse)
    {
        Console.WriteLine(response.DebugInformation);
        break;
    }

    if (response.Hits.Count == 0)
        break;

    foreach (var hit in response.Hits)
    {
        Console.WriteLine(hit.Source);
    }

    searchAfter = response.Hits.Last().Sorts?.ToArray();

    if (searchAfter == null || searchAfter.Length == 0)
        break;
}
