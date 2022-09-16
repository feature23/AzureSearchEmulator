using System.Text.Json.Nodes;
using AzureSearchEmulator.Models;
using Lucene.Net.Documents;
using Lucene.Net.Index;

namespace AzureSearchEmulator.Indexing;

public static class SearchFieldExtensions
{
    public static IEnumerable<IIndexableField> CreateField(this SearchField field, JsonNode value)
    {
        var stored = field.Retrievable ? Field.Store.YES : Field.Store.NO;

        return field.Type switch
        {
            "Edm.String" when field.Searchable.GetValueOrDefault(true) => new TextField(field.Name, value.GetValue<string>(), stored).AsEnumerable(),
            "Edm.String" when !field.Searchable.GetValueOrDefault(true) => new StringField(field.Name, value.GetValue<string>(), stored).AsEnumerable(),
            "Edm.Int32" => new Int32Field(field.Name, value.GetValue<int>(), stored).AsEnumerable(),
            "Edm.Int64" => new Int64Field(field.Name, value.GetValue<long>(), stored).AsEnumerable(),
            "Edm.Double" => new DoubleField(field.Name, value.GetValue<double>(), stored).AsEnumerable(),
            "Edm.Boolean" => new Int32Field(field.Name, value.GetValue<bool>() ? 1 : 0, stored).AsEnumerable(),
            "Edm.DateTimeOffset" => new Int64Field(field.Name, value.GetValue<DateTimeOffset>().ToUnixTimeMilliseconds(), stored).AsEnumerable(),
            "Edm.GeographyPoint" => throw new NotImplementedException(),
            "Edm.ComplexType" => throw new NotImplementedException(),
            "Collection(Edm.String)" when field.Searchable.GetValueOrDefault(true) => 
                Collection(field.Type, value, i => new TextField(field.Name, i.GetValue<string>(), stored)),
            "Collection(Edm.String)" when !field.Searchable.GetValueOrDefault(true) => 
                Collection(field.Type, value, i => new StringField(field.Name, i.GetValue<string>(), stored)),
            "Collection(Edm.Int32)" => Collection(field.Type, value, i => new Int32Field(field.Name, i.GetValue<int>(), stored)),
            "Collection(Edm.Int64)" => Collection(field.Type, value, i => new Int64Field(field.Name, i.GetValue<long>(), stored)),
            "Collection(Edm.Double)" => Collection(field.Type, value, i => new DoubleField(field.Name, i.GetValue<double>(), stored)),
            "Collection(Edm.Boolean)" => Collection(field.Type, value, i => new Int32Field(field.Name, i.GetValue<bool>() ? 1 : 0, stored)),
            "Collection(Edm.DateTimeOffset)" => Collection(field.Type, value, i => new Int64Field(field.Name, i.GetValue<DateTimeOffset>().ToUnixTimeMilliseconds(), stored)),
            "Collection(Edm.GeographyPoint)" => throw new NotImplementedException(),
            "Collection(Edm.ComplexType)" => throw new NotImplementedException(),
            _ => throw new InvalidOperationException($"Unsupported field type {field.Type}")
        };
    }

    private static IEnumerable<IIndexableField> Collection(string edmTypeName, JsonNode node, Func<JsonNode, IIndexableField> factory)
    {
        if (node is not JsonArray jsonArray)
        {
            throw new InvalidOperationException($"Type specified as \"{edmTypeName}\" but value is not a JSON array.");
        }

        return jsonArray.OfType<JsonNode>().Select(factory);
    }

    private static IEnumerable<IIndexableField> AsEnumerable(this IIndexableField field)
    {
        yield return field;
    }
}