using AzureSearchEmulator.Models;
using AzureSearchEmulator.Repositories;
using Microsoft.AspNetCore.Mvc;

namespace AzureSearchEmulator.Controllers;

[ApiController]
[Route("servicestats")]
public class ServiceStatsController(ISearchIndexRepository searchIndexRepository) : ControllerBase
{
    [HttpGet]
    public async Task<IActionResult> Get()
    {
        var indexes = new List<SearchIndex>();
        await foreach (var index in searchIndexRepository.GetAll())
        {
            indexes.Add(index);
        }
        
        var stats = new Dictionary<string, object>
        {
            ["@odata.context"] = $"{Request.Scheme}://{Request.Host}/$metadata#Microsoft.Azure.Search.V2025_05_01_Preview.ServiceStatistics",
            ["counters"] = new
            {
                documentCount = new
                {
                    usage = 0,
                    quota = (long?)null
                },
                indexesCount = new
                {
                    usage = indexes.Count,
                    quota = 15
                },
                indexersCount = new
                {
                    usage = 0,
                    quota = 15
                },
                dataSourcesCount = new
                {
                    usage = 0,
                    quota = 15
                },
                storageSize = new
                {
                    usage = 0L,
                    quota = 16106127360L
                },
                synonymMaps = new
                {
                    usage = 0,
                    quota = 3
                },
                skillsetCount = new
                {
                    usage = 0,
                    quota = 15
                },
                aliasesCount = new
                {
                    usage = 0,
                    quota = 30
                },
                vectorIndexSize = new
                {
                    usage = 0L,
                    quota = 5368709120L
                }
            },
            ["limits"] = new
            {
                maxStoragePerIndex = 16106127360L,
                maxFieldsPerIndex = 1000,
                maxFieldNestingDepthPerIndex = 10,
                maxComplexCollectionFieldsPerIndex = 40,
                maxComplexObjectsInCollectionsPerDocument = 3000
            }
        };

        return Ok(stats);
    }
}

