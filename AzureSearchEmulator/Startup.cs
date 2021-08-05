using System;
using System.Text.Json;
using Azure.Search.Documents.Indexes.Models;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http.Extensions;
using Microsoft.AspNetCore.OData;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.OData.Edm;
using Microsoft.OData.ModelBuilder;

namespace AzureSearchEmulator
{
    public class Startup
    {
        // This method gets called by the runtime. Use this method to add services to the container.
        // For more information on how to configure your application, visit https://go.microsoft.com/fwlink/?LinkID=398940
        public void ConfigureServices(IServiceCollection services)
        {
            var model = GetEdmModel();

            services.AddControllers()
                .AddJsonOptions(options => options.JsonSerializerOptions.PropertyNamingPolicy = JsonNamingPolicy.CamelCase)
                .AddOData(options => 
                    options.Count().Filter().Expand().Select().OrderBy().SetMaxTop(5)
                    .AddRouteComponents("", model));

        }

        // This method gets called by the runtime. Use this method to configure the HTTP request pipeline.
        public void Configure(IApplicationBuilder app, IWebHostEnvironment env)
        {
            if (env.IsDevelopment())
            {
                app.UseDeveloperExceptionPage();
            }

            app.UseODataRouteDebug();
            app.UseODataQueryRequest();
            app.UseODataBatching();

            app.UseRouting();

            app.Use((context, next) =>
            {
                Console.WriteLine(context.Request.GetDisplayUrl());
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

            var index = builder.EntitySet<SearchIndex>("indexes").EntityType;
            index.HasKey(i => i.Name);

            return builder.GetEdmModel();
        }
    }
}