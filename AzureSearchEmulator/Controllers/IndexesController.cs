using System.Collections.Generic;
using Azure.Search.Documents.Indexes.Models;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.OData.Query;
using Microsoft.AspNetCore.OData.Routing.Controllers;

namespace AzureSearchEmulator.Controllers
{
    public class IndexesController : ODataController
    {
        [HttpGet]
        [EnableQuery]
        [Route("indexes")]
        public IEnumerable<SearchIndex> Get(ODataQueryOptions<SearchIndex> options) => new List<SearchIndex>();

        [HttpGet]
        [Route("indexes({key})")]
        [Route("indexes/{key}")]
        public IActionResult Get(string key) => NotFound();

        [HttpPost]
        [Route("indexes")]
        public SearchIndex Post([FromBody] SearchIndex index) => index;
    }
}
