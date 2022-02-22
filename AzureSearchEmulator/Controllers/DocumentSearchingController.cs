using AzureSearchEmulator.Repositories;
using AzureSearchEmulator.Searching;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.OData.Query;
using Microsoft.AspNetCore.OData.Routing.Controllers;

namespace AzureSearchEmulator.Controllers;

public class DocumentSearchingController : ODataController
{
    private readonly IIndexSearcher _indexSearcher;
    private readonly ISearchIndexRepository _searchIndexRepository;

    public DocumentSearchingController(IIndexSearcher indexSearcher, 
        ISearchIndexRepository searchIndexRepository)
    {
        _indexSearcher = indexSearcher;
        _searchIndexRepository = searchIndexRepository;
    }

    [HttpGet]
    [Route("indexes/{indexKey}/docs/$count")]
    [Route("indexes/({indexKey})/docs/$count")]
    public async Task<IActionResult> GetDocumentCount(string indexKey)
    {
        var index = await _searchIndexRepository.Get(indexKey);

        if (index == null)
        {
            return NotFound();
        }

        var count = await _indexSearcher.GetDocCount(index);
        
        return Ok(count);
    }

    [HttpGet]
    [Route("indexes/{indexKey}/docs/{key}")]
    [Route("indexes/({indexKey})/docs/({key})")]
    [EnableQuery]
    public async Task<IActionResult> GetDocument(string indexKey, string key)
    {
        var index = await _searchIndexRepository.Get(indexKey);

        if (index == null)
        {
            return NotFound();
        }

        var doc = await _indexSearcher.GetDoc(index, key);

        if (doc == null)
        {
            return NotFound();
        }

        return Ok(doc);
    }
}