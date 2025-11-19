using System.Text.Json;
using AzureSearchEmulator;
using AzureSearchEmulator.Indexing;
using AzureSearchEmulator.Models;
using AzureSearchEmulator.Repositories;
using AzureSearchEmulator.SearchData;
using AzureSearchEmulator.Searching;
using Microsoft.AspNetCore.Http.Extensions;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.OData;
using Microsoft.Extensions.Options;
using Microsoft.OData.Edm;
using Microsoft.OData.ModelBuilder;

var builder = WebApplication.CreateBuilder(args);

var model = GetEdmModel();

builder.Services.Configure<EmulatorOptions>(builder.Configuration.GetSection("Emulator"));

const string CorsDefaultPolicyName = "AllowAllOrigins";

builder.Services.AddCors(options =>
{
    options.AddPolicy(CorsDefaultPolicyName,
        cors =>
        {
            cors.AllowAnyOrigin()
                .AllowAnyMethod()
                .AllowAnyHeader();
        });
});

builder.Services.AddControllers()
    .AddJsonOptions(options =>
    {
        options.JsonSerializerOptions.PropertyNamingPolicy = JsonNamingPolicy.CamelCase;
        options.JsonSerializerOptions.DictionaryKeyPolicy = JsonNamingPolicy.CamelCase;
    })
    .AddOData(options =>
        options.Count().Filter().Expand().Select().OrderBy().SetMaxTop(1000)
            .AddRouteComponents("", model));

builder.Services.AddTransient(sp =>
{
    var jsonOptions = sp.GetService<IOptions<JsonOptions>>();

    if (jsonOptions == null)
    {
        throw new InvalidOperationException("JsonOptions not registered properly");
    }

    return jsonOptions.Value.JsonSerializerOptions;
});

builder.Services.AddTransient<ISearchIndexRepository, FileSearchIndexRepository>();
builder.Services.AddSingleton<ILuceneDirectoryFactory, SimpleFSDirectoryFactory>();
builder.Services.AddSingleton<ILuceneIndexReaderFactory, LuceneDirectoryReaderFactory>();
builder.Services.AddTransient<IIndexSearcher, LuceneNetIndexSearcher>();
builder.Services.AddSingleton<ISearchIndexer, LuceneNetSearchIndexer>();

var app = builder.Build();

if (builder.Environment.IsDevelopment())
{
    app.UseDeveloperExceptionPage();
}

app.UseCors(CorsDefaultPolicyName);
app.UseODataRouteDebug();
app.UseODataQueryRequest();
app.UseODataBatching();

app.UseRouting();

app.Use((context, next) =>
{
    Console.WriteLine($"{context.Request.Method} {context.Request.GetDisplayUrl()}");
    return next();
});

app.MapControllers();

await app.RunAsync();
return;

static IEdmModel GetEdmModel()
{
    var builder = new ODataConventionModelBuilder();
    builder.EnableLowerCamelCase();

    var index = builder.EntitySet<SearchIndex>("indexes").EntityType;
    index.HasKey(i => i.Name);

    return builder.GetEdmModel();
}
