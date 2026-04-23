using System.Text.Json.Nodes;
using AzureSearchEmulator.Models;
using Lucene.Net.Documents;
using Lucene.Net.Index;

namespace AzureSearchEmulator.Indexing;

public static class SearchFieldExtensions
{
    public static IEnumerable<IIndexableField> CreateFields(this SearchField field, JsonNode value)
    {
        var stored = field.Retrievable ? Field.Store.YES : Field.Store.NO;

        if (field.Type == "Edm.String")
        {
            var str = value.GetValue<string>();
            var searchable = field.Searchable.GetValueOrDefault(true);
            var filterable = field.Filterable;

            if (searchable)
            {
                yield return new TextField(field.Name, str, stored);
                // Filter/sort/facet require a non-analyzed copy under the same field name
                // so TermQuery-based filters match the raw literal (matches Azure semantics).
                if (filterable || field.Sortable.GetValueOrDefault() || field.Facetable.GetValueOrDefault())
                {
                    yield return new StringField(field.Name, str, Field.Store.NO);
                }
            }
            else
            {
                yield return new StringField(field.Name, str, stored);
            }
            yield break;
        }

        yield return field.Type switch
        {
            "Edm.Int32" => new Int32Field(field.Name, value.GetValue<int>(), stored),
            "Edm.Int64" => new Int64Field(field.Name, value.GetValue<long>(), stored),
            "Edm.Double" => new DoubleField(field.Name, value.GetValue<double>(), stored),
            "Edm.Boolean" => new Int32Field(field.Name, value.GetValue<bool>() ? 1 : 0, stored),
            "Edm.DateTimeOffset" => new Int64Field(field.Name, value.GetValue<DateTimeOffset>().ToUnixTimeMilliseconds(), stored),
            "Edm.GeographyPoint" => throw new NotImplementedException(),
            "Edm.ComplexType" => throw new NotImplementedException(),
            "Collection(Edm.String)" => throw new NotImplementedException(),
            "Collection(Edm.Int32)" => throw new NotImplementedException(),
            "Collection(Edm.Int64)" => throw new NotImplementedException(),
            "Collection(Edm.Double)" => throw new NotImplementedException(),
            "Collection(Edm.Boolean)" => throw new NotImplementedException(),
            "Collection(Edm.DateTimeOffset)" => throw new NotImplementedException(),
            "Collection(Edm.GeographyPoint)" => throw new NotImplementedException(),
            "Collection(Edm.ComplexType)" => throw new NotImplementedException(),
            _ => throw new InvalidOperationException($"Unsupported field type {field.Type}")
        };
    }
}