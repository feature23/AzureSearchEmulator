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
using Microsoft.AspNetCore.OData.Formatter;
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

// Take request bodies away from OData and give them to System.Text.Json (issue #41).
//
// AddOData registers an ODataInputFormatter that claims application/json, and because
// SearchIndex is an EDM entity type it wins the body of POST/PUT /indexes. That formatter
// validates against the EDM model and rejects any property the model does not declare, so a
// definition carrying corsOptions, scoringProfiles, a field-level normalizer, or a
// similarity with an @odata.type came back as 400 "The input was not valid." It also has no
// notion of [JsonExtensionData], so it could never populate SearchIndex.AdditionalProperties —
// preserving unmodelled properties is unimplementable while it handles the body.
//
// This is why both actions used to read the body off the stream by hand: bypassing the
// formatter was the only way to accept a realistic index definition. The cost was that
// ModelState stayed empty, leaving the [Required] attributes on SearchField unenforced and
// the !ModelState.IsValid guard dead. Removing the formatter lets [FromBody] bind, which
// restores validation as well.
//
// Only the INPUT formatter goes. ODataOutputFormatter and every query option stay registered,
// so responses keep their @odata.context envelope and [EnableQuery] is unaffected. The output
// side needs its own accommodation for the extension data — see GetEdmModel below.
builder.Services.Configure<MvcOptions>(options =>
{
    foreach (var formatter in options.InputFormatters.OfType<ODataInputFormatter>().ToList())
    {
        options.InputFormatters.Remove(formatter);
    }
});

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
// Singleton so writers stay open across requests; the container disposes it on shutdown,
// committing any pending changes.
builder.Services.AddSingleton<ILuceneIndexWriterFactory, LuceneNetIndexWriterFactory>();
builder.Services.AddTransient<IIndexSearcher, LuceneNetIndexSearcher>();
builder.Services.AddSingleton<ISearchIndexer, LuceneNetSearchIndexer>();

var app = builder.Build();

if (builder.Environment.IsDevelopment())
{
    app.UseDeveloperExceptionPage();
    // Route debug lists every registered endpoint at /$odata, which is a development aid and
    // not something a deployed emulator should expose.
    app.UseODataRouteDebug();
}

app.UseCors(CorsDefaultPolicyName);
app.UseODataQueryRequest();

var requestLogger = app.Services.GetRequiredService<ILoggerFactory>().CreateLogger("AzureSearchEmulator.Requests");

// Registered BEFORE UseODataBatching so the sub-requests an OData $batch dispatches also
// pass through here. After it, a batched indexing call — a documented Azure Search usage
// pattern — produced no log line at all.
app.Use(async (context, next) =>
{
    var method = context.Request.Method;
    var path = context.Request.Path;
    var queryString = context.Request.QueryString.ToString();
    // Method, path and query string are all caller-controlled; strip control characters so
    // an embedded newline cannot forge additional log lines.
    var fullPath = SanitizeForLog(string.IsNullOrEmpty(queryString) ? path.ToString() : $"{path}{queryString}");
    method = SanitizeForLog(method);
    try
    {
        await next();

        // Status code logged AFTER the pipeline so a rejected request is visible — a
        // silent 4xx/5xx is what makes a dropped index merge hard to diagnose. Non-success
        // logs at Warning so it surfaces without Information enabled.
        if (context.Response.StatusCode >= 400)
        {
            requestLogger.LogWarning("[HTTP {Method} {StatusCode}] {Path}", method, context.Response.StatusCode, fullPath);
        }
        else
        {
            requestLogger.LogInformation("[HTTP {Method} {StatusCode}] {Path}", method, context.Response.StatusCode, fullPath);
        }
    }
    catch (Exception ex)
    {
        requestLogger.LogError(ex, "[HTTP {Method} EXCEPTION] {Path}", method, fullPath);
        throw;
    }
});

app.UseODataBatching();

app.UseRouting();

app.MapControllers();

await app.RunAsync();
return;

static string SanitizeForLog(string value)
{
    if (string.IsNullOrEmpty(value))
    {
        return string.Empty;
    }

    return string.Create(value.Length, value, static (span, source) =>
    {
        for (var i = 0; i < source.Length; i++)
        {
            var c = source[i];
            span[i] = char.IsControl(c) ? ' ' : c;
        }
    });
}

static IEdmModel GetEdmModel()
{
    var builder = new ODataConventionModelBuilder();
    builder.EnableLowerCamelCase();

    var index = builder.EntitySet<SearchIndex>("indexes").EntityType;
    index.HasKey(i => i.Name);

    // Keep the extension-data bags out of the EDM model (issue #41). The convention builder
    // would otherwise treat them as ordinary structural properties, and ODataOutputFormatter
    // would serialize each captured property as an empty object under an "additionalProperties"
    // key — turning corsOptions and scoringProfiles into "additionalProperties": [{}, {}] in the
    // response while the values sat correctly on disk.
    //
    // Ignoring them keeps that corruption out of the collection endpoint, which stays on OData.
    // It does not make OData emit them: the single-index responses serialize with
    // System.Text.Json instead — see IndexesController.IndexJson.
    index.Ignore(i => i.AdditionalProperties);
    builder.ComplexType<SearchField>().Ignore(i => i.AdditionalProperties);

    // The vector search types carry bags of their own (issue #46), for the same reason and with
    // the same consequence if they reach the EDM model.
    builder.ComplexType<VectorSearch>().Ignore(i => i.AdditionalProperties);
    builder.ComplexType<VectorSearchAlgorithm>().Ignore(i => i.AdditionalProperties);
    builder.ComplexType<VectorSearchProfile>().Ignore(i => i.AdditionalProperties);
    builder.ComplexType<HnswParameters>().Ignore(i => i.AdditionalProperties);
    builder.ComplexType<ExhaustiveKnnParameters>().Ignore(i => i.AdditionalProperties);

    return builder.GetEdmModel();
}
