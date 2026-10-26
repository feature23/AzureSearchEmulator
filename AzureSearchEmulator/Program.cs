using System.Text.Json;
using AzureSearchEmulator;
using AzureSearchEmulator.Components;
using AzureSearchEmulator.ErrorHandling;
using AzureSearchEmulator.Health;
using AzureSearchEmulator.Indexing;
using AzureSearchEmulator.Models;
using AzureSearchEmulator.Repositories;
using AzureSearchEmulator.Routing;
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

// Default to plain HTTP on a fixed port (issue #67).
//
// Kestrel's own default binds 5000 and 5001, and 5001 is HTTPS — which needs a development
// certificate the tool does not ship and cannot assume the user has trusted. As a `dotnet
// tool` that produces a startup failure rather than a working emulator, so bind HTTP only.
//
// This is a default, not an override: it is only applied when the user has said nothing.
// Docker and the Aspire integration both set ASPNETCORE_URLS explicitly and are unaffected,
// as is `dotnet run`, whose launchSettings.json supplies applicationUrl.
if (string.IsNullOrEmpty(builder.Configuration["urls"]))
{
    builder.WebHost.UseUrls("http://localhost:5123");
}

var model = GetEdmModel();

builder.Services.Configure<EmulatorOptions>(builder.Configuration.GetSection("Emulator"));

// Resolve IndexesDirectory to an absolute path up front (issue #67).
//
// The setting defaults to the relative path "indexes". Under `dotnet run` from the project
// folder, and in the container where WORKDIR is /app, relative resolution lands somewhere
// sensible. Installed as a `dotnet tool` it does not: the tool is launched from whatever
// directory the user happens to be in, so a relative path would scatter index folders across
// the filesystem and make an index created in one shell invisible from another.
//
// Anchoring to the current directory keeps that per-directory behaviour explicit and
// documented rather than incidental, and leaves Docker unchanged — /app/indexes is what
// "indexes" already resolved to there. An absolute value configured by the user passes
// through Path.GetFullPath untouched.
builder.Services.PostConfigure<EmulatorOptions>(options =>
{
    options.IndexesDirectory = Path.GetFullPath(
        string.IsNullOrWhiteSpace(options.IndexesDirectory) ? "indexes" : options.IndexesDirectory);
});

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

builder.Services.AddControllers(options =>
    {
        // Rewrites the controllers' own 4xx results into Azure's error envelope. Registered
        // as a filter rather than applied at each call site so every existing rejection —
        // and any added later — carries the shape the SDK reads (issue #40).
        options.Filters.Add<SearchErrorResultFilter>();

        // Frees "/" for the dashboard; see the convention for why the service document is the
        // endpoint that gives way (issue #90).
        options.Conventions.Add(new ODataServiceDocumentConvention());
    })
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
builder.Services.AddTransient<ISynonymMapRepository, FileSynonymMapRepository>();
builder.Services.AddSingleton<ILuceneDirectoryFactory, SimpleFSDirectoryFactory>();
builder.Services.AddSingleton<ILuceneIndexReaderFactory, LuceneDirectoryReaderFactory>();
// Singleton so writers stay open across requests; the container disposes it on shutdown,
// committing any pending changes.
builder.Services.AddSingleton<ILuceneIndexWriterFactory, LuceneNetIndexWriterFactory>();
builder.Services.AddTransient<IIndexSearcher, LuceneNetIndexSearcher>();
builder.Services.AddSingleton<ISearchIndexer, LuceneNetSearchIndexer>();

// Health checks, surfaced both at /health for a monitor and on the dashboard for a human
// (issue #90). The names are the identifiers the dashboard maps to prose.
builder.Services.AddHealthChecks()
    .AddCheck<IndexStorageHealthCheck>("index-storage")
    .AddCheck<IndexDefinitionsHealthCheck>("index-definitions");

builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddScoped<EmulatorStatusService>();

// The dashboard (issue #90). Blazor rather than a separate SPA so the whole UI ships inside the
// same assembly as the emulator — no second build step, no static bundle to keep in sync, and
// nothing extra for `dotnet tool install` to pull down. The interactive server render mode means
// the status panel refreshes over the existing circuit instead of the page needing to know its
// own externally reachable URL to poll itself.
builder.Services.AddRazorComponents()
    .AddInteractiveServerComponents();

var app = builder.Build();

// Outermost, so it also covers the model binding and OData formatter work that runs before a
// controller action is reached (issue #40).
//
// This deliberately replaces UseDeveloperExceptionPage rather than sitting behind it. That
// page renders HTML, so with it in front every fault in Development came back as a stack-trace
// document instead of the error envelope — meaning the shape a client saw depended on which
// environment the emulator happened to run in, and the tests here exercise a container that
// does not run in Development. An emulator is only useful if it answers the same way
// everywhere; the stack trace is still written to the log by the handler itself.
app.UseMiddleware<SearchErrorMiddleware>();

if (builder.Environment.IsDevelopment())
{
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

// Serves wwwroot, which holds the dashboard's stylesheet and nothing else. Registered after
// UseRouting so these requests still pass through the request logging above rather than being
// short-circuited ahead of it.
app.UseStaticFiles();

// Required by MapRazorComponents, which stamps anti-forgery metadata onto its endpoints. It only
// validates tokens on the form posts and interactive requests that carry them, so the emulator's
// own API routes are unaffected — a client posting a document batch has no token and is not asked
// for one.
app.UseAntiforgery();

app.MapControllers();

app.MapRazorComponents<App>()
    .AddInteractiveServerRenderMode();

// The machine-readable half of the same checks the dashboard renders. Kept separate from
// /servicestats, which Aspire already probes: that route exists to imitate Azure's API surface and
// answers 200 as long as the process is up, whereas this one answers for the emulator's actual
// ability to serve — a read-only indexes volume is a 200 there and Unhealthy here.
app.MapHealthChecks("/health");

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
