using System.Reflection;
using Microsoft.AspNetCore.Mvc.ApplicationModels;
using Microsoft.AspNetCore.OData.Routing.Controllers;

namespace AzureSearchEmulator.Routing;

/// <summary>
/// Drops OData's service-document action so the dashboard can own <c>/</c> (issue #90).
/// </summary>
/// <remarks>
/// <c>AddRouteComponents("", model)</c> registers OData's built-in <see cref="MetadataController"/>
/// at the route prefix, which puts <see cref="MetadataController.GetServiceDocument"/> on <c>/</c>.
/// The dashboard's <c>@page "/"</c> lands on the same address, and routing refuses to choose between
/// them — the request fails with an AmbiguousMatchException rather than either endpoint winning.
/// <para>
/// The service document is the one to give up. Azure AI Search does not serve one: its root answers
/// 404, so nothing written against the real service can be relying on it, and the Azure SDK never
/// requests it. It exists here only because registering the OData route components at the root
/// prefix brings it along. <c>/$metadata</c> is a separate action on the same controller and stays
/// registered.
/// </para>
/// </remarks>
public class ODataServiceDocumentConvention : IApplicationModelConvention
{
    public void Apply(ApplicationModel application)
    {
        foreach (var controller in application.Controllers
                     .Where(i => i.ControllerType == typeof(MetadataController).GetTypeInfo()))
        {
            foreach (var action in controller.Actions
                         .Where(i => i.ActionName == nameof(MetadataController.GetServiceDocument))
                         .ToList())
            {
                controller.Actions.Remove(action);
            }
        }
    }
}
