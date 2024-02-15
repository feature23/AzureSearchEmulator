using System.Text.Json;
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

namespace AzureSearchEmulator;

public class Startup(IConfiguration configuration)
{
    private const string CorsDefaultPolicyName = "AllowAllOrigins";
    
    public IConfiguration Configuration { get; } = configuration;

    // This method gets called by the runtime. Use this method to add services to the container.
    // For more information on how to configure your application, visit https://go.microsoft.com/fwlink/?LinkID=398940
    public void ConfigureServices(IServiceCollection services)
    {
        var model = GetEdmModel();

        services.Configure<EmulatorOptions>(Configuration.GetSection("Emulator"));

        services.AddCors(options =>
        {
            options.AddPolicy(CorsDefaultPolicyName,
                builder =>
                {
                    builder
                    .AllowAnyOrigin()
                    .AllowAnyMethod()
                    .AllowAnyHeader();
                });
        });

        services.AddControllers()
            .AddJsonOptions(options =>
            {
                options.JsonSerializerOptions.PropertyNamingPolicy = JsonNamingPolicy.CamelCase;
                options.JsonSerializerOptions.DictionaryKeyPolicy = JsonNamingPolicy.CamelCase;
            })
            .AddOData(options =>
                options.Count().Filter().Expand().Select().OrderBy().SetMaxTop(1000)
                    .AddRouteComponents("", model));

        services.AddTransient(sp =>
        {
            var jsonOptions = sp.GetService<IOptions<JsonOptions>>();

            if (jsonOptions == null)
            {
                throw new InvalidOperationException("JsonOptions not registered properly");
            }

            return jsonOptions.Value.JsonSerializerOptions;
        });

        services.AddTransient<ISearchIndexRepository, FileSearchIndexRepository>();
        services.AddSingleton<ILuceneDirectoryFactory, SimpleFSDirectoryFactory>();
        services.AddSingleton<ILuceneIndexReaderFactory, LuceneDirectoryReaderFactory>();
        services.AddTransient<IIndexSearcher, LuceneNetIndexSearcher>();
        services.AddSingleton<ISearchIndexer, LuceneNetSearchIndexer>();
    }

    // This method gets called by the runtime. Use this method to configure the HTTP request pipeline.
    public void Configure(IApplicationBuilder app, IWebHostEnvironment env)
    {
        if (env.IsDevelopment())
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

        app.UseEndpoints(endpoints =>
        {
            endpoints.MapControllers();
        });
    }

    private static IEdmModel GetEdmModel()
    {
        var builder = new ODataConventionModelBuilder();
        builder.EnableLowerCamelCase();

        var index = builder.EntitySet<SearchIndex>("indexes").EntityType;
        index.HasKey(i => i.Name);

        return builder.GetEdmModel();
    }
}
