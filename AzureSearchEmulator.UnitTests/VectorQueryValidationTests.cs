using AzureSearchEmulator.Models;
using AzureSearchEmulator.Searching;
using Xunit;

namespace AzureSearchEmulator.UnitTests;

/// <summary>
/// Unit tests for the checks a vector query is put through before any scan runs (issue #46).
/// </summary>
/// <remarks>
/// These faults are all reported rather than absorbed, because the alternative is an empty
/// result set that looks exactly like a genuine absence of near neighbours — the failure that is
/// hardest to notice and hardest to debug.
/// </remarks>
public class VectorQueryValidationTests
{
    private static SearchIndex CreateIndex() => new()
    {
        Name = "vectors",
        Fields =
        [
            new SearchField { Name = "Id", Type = "Edm.String", Key = true, Filterable = true },
            new SearchField { Name = "Title", Type = "Edm.String", Searchable = true },
            new SearchField
            {
                Name = "Embedding",
                Type = "Collection(Edm.Single)",
                Filterable = false,
                Dimensions = 3,
                VectorSearchProfile = "vp"
            },
        ],
        VectorSearch = new VectorSearch
        {
            Algorithms = [new VectorSearchAlgorithm { Name = "algo" }],
            Profiles = [new VectorSearchProfile { Name = "vp", Algorithm = "algo" }]
        }
    };

    private static SearchRequest Request(VectorQuery query) => new() { VectorQueries = [query] };

    private static string Validate(SearchIndex index, SearchRequest request)
        => Assert.Throws<InvalidOperationException>(
            () => VectorQuerySupport.Validate(index, request)).Message;

    [Fact]
    public void ValidQuery_IsAccepted()
    {
        var request = Request(new VectorQuery
        {
            Kind = "vector",
            Vector = [1f, 2f, 3f],
            Fields = "Embedding"
        });

        VectorQuerySupport.Validate(CreateIndex(), request);
    }

    /// <summary>
    /// A request with no vector queries is untouched, which is every request predating this
    /// feature.
    /// </summary>
    [Fact]
    public void RequestWithoutVectorQueries_IsAccepted()
    {
        VectorQuerySupport.Validate(CreateIndex(), new SearchRequest { Search = "hello" });
    }

    [Fact]
    public void QueryVectorOfWrongLength_IsRejected()
    {
        var message = Validate(CreateIndex(), Request(new VectorQuery
        {
            Kind = "vector",
            Vector = [1f, 2f],
            Fields = "Embedding"
        }));

        Assert.Contains("2", message);
        Assert.Contains("3", message);
        Assert.Contains("Embedding", message);
    }

    [Fact]
    public void EmptyVector_IsRejected()
    {
        var message = Validate(CreateIndex(), Request(new VectorQuery
        {
            Kind = "vector",
            Vector = [],
            Fields = "Embedding"
        }));

        Assert.Contains("vector", message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void MissingVector_IsRejected()
    {
        var message = Validate(CreateIndex(), Request(new VectorQuery
        {
            Kind = "vector",
            Fields = "Embedding"
        }));

        Assert.Contains("vector", message, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Naming a field that is not a vector field is a different mistake from naming one that
    /// does not exist, and the message says which.
    /// </summary>
    [Fact]
    public void NonVectorField_IsRejectedAsSuch()
    {
        var message = Validate(CreateIndex(), Request(new VectorQuery
        {
            Kind = "vector",
            Vector = [1f, 2f, 3f],
            Fields = "Title"
        }));

        Assert.Contains("not a vector field", message);
    }

    [Fact]
    public void UnknownField_IsRejectedAsNotExisting()
    {
        var message = Validate(CreateIndex(), Request(new VectorQuery
        {
            Kind = "vector",
            Vector = [1f, 2f, 3f],
            Fields = "Nope"
        }));

        Assert.Contains("does not exist", message);
    }

    [Fact]
    public void TextKind_IsRejected()
    {
        var message = Validate(CreateIndex(), Request(new VectorQuery
        {
            Kind = "text",
            Text = "a query",
            Fields = "Embedding"
        }));

        Assert.Contains("embedding model", message);
    }

    [Fact]
    public void UnknownKind_IsRejected()
    {
        var message = Validate(CreateIndex(), Request(new VectorQuery
        {
            Kind = "imageUrl",
            Vector = [1f, 2f, 3f],
            Fields = "Embedding"
        }));

        Assert.Contains("imageUrl", message);
    }

    /// <summary>
    /// Azure's own default kind is <c>vector</c>, so omitting it is not an error.
    /// </summary>
    [Fact]
    public void MissingKind_IsTreatedAsVector()
    {
        VectorQuerySupport.Validate(CreateIndex(), Request(new VectorQuery
        {
            Vector = [1f, 2f, 3f],
            Fields = "Embedding"
        }));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void NonPositiveK_IsRejected(int k)
    {
        var message = Validate(CreateIndex(), Request(new VectorQuery
        {
            Kind = "vector",
            Vector = [1f, 2f, 3f],
            Fields = "Embedding",
            KNearestNeighborsCount = k
        }));

        Assert.Contains("k", message);
    }

    /// <summary>
    /// An index with no vector fields cannot answer a vector query, and saying so is more useful
    /// than an empty result.
    /// </summary>
    [Fact]
    public void IndexWithoutVectorFields_IsRejected()
    {
        var index = new SearchIndex
        {
            Name = "hotels",
            Fields = [new SearchField { Name = "Id", Type = "Edm.String", Key = true }]
        };

        var message = Validate(index, Request(new VectorQuery
        {
            Kind = "vector",
            Vector = [1f, 2f, 3f]
        }));

        Assert.Contains("no vector fields", message);
    }

    /// <summary>
    /// Hybrid search needs Reciprocal Rank Fusion, which is not implemented. A union of the two
    /// arms would not approximate it — their scores are on unrelated scales — so the request is
    /// refused rather than answered with a ranking Azure would not produce.
    /// </summary>
    [Fact]
    public void HybridSearch_IsRejected()
    {
        var request = Request(new VectorQuery
        {
            Kind = "vector",
            Vector = [1f, 2f, 3f],
            Fields = "Embedding"
        });
        request.Search = "hello";

        var message = Validate(CreateIndex(), request);

        Assert.Contains("Reciprocal Rank Fusion", message);
    }

    /// <summary>
    /// Fields searched together must share a metric, because one query produces one ranking and
    /// scores from different metrics are not comparable.
    /// </summary>
    [Fact]
    public void FieldsWithDifferentMetrics_AreRejected()
    {
        var index = CreateIndex();

        index.VectorSearch!.Algorithms.Add(new VectorSearchAlgorithm
        {
            Name = "euclideanAlgo",
            HnswParameters = new HnswParameters { Metric = VectorSearchMetric.Euclidean }
        });
        index.VectorSearch.Profiles.Add(new VectorSearchProfile
        {
            Name = "euclideanProfile",
            Algorithm = "euclideanAlgo"
        });
        index.Fields.Add(new SearchField
        {
            Name = "Other",
            Type = "Collection(Edm.Single)",
            Filterable = false,
            Dimensions = 3,
            VectorSearchProfile = "euclideanProfile"
        });

        var message = Validate(index, Request(new VectorQuery
        {
            Kind = "vector",
            Vector = [1f, 2f, 3f],
            Fields = "Embedding,Other"
        }));

        Assert.Contains("metric", message);
    }

    /// <summary>
    /// Field names are matched the way the rest of the emulator matches them.
    /// </summary>
    [Fact]
    public void FieldNames_AreMatchedCaseInsensitively()
    {
        VectorQuerySupport.Validate(CreateIndex(), Request(new VectorQuery
        {
            Kind = "vector",
            Vector = [1f, 2f, 3f],
            Fields = "embedding"
        }));
    }

    /// <summary>
    /// The Azure SDK writes <c>kNearestNeighborsCount</c>, while the REST reference writes
    /// <c>k</c>. Both have to bind, or a request written from the documentation silently gets
    /// the default number of neighbours instead of the one it asked for.
    /// </summary>
    [Theory]
    [InlineData("\"kNearestNeighborsCount\": 7")]
    [InlineData("\"k\": 7")]
    public void BothSpellingsOfK_Bind(string property)
    {
        var json =
            $$"""
            {
              "vectorQueries": [
                { "kind": "vector", "vector": [1, 2, 3], "fields": "Embedding", {{property}} }
              ]
            }
            """;

        var request = System.Text.Json.JsonSerializer.Deserialize<SearchRequest>(
            json,
            new System.Text.Json.JsonSerializerOptions
            {
                PropertyNamingPolicy = System.Text.Json.JsonNamingPolicy.CamelCase
            })!;

        Assert.Equal(7, request.VectorQueries![0].KNearestNeighborsCount);
    }
}
