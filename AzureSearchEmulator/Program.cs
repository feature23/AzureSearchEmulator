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
// Singleton so writers stay open across requests; the container disposes it on shutdown,
// committing any pending changes.
builder.Services.AddSingleton<ILuceneIndexWriterFactory, LuceneNetIndexWriterFactory>();
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

    return builder.GetEdmModel();
}
