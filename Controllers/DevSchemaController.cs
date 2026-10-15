#if DEBUG
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.OpenApi;
using Microsoft.OpenApi.Extensions;
using Swashbuckle.AspNetCore.Swagger;

namespace SmartRoutingAPI.Controllers;

/// <summary>
/// DEBUG-only tooling endpoint. Emits the generated OpenAPI 3.0 document so it can be handed to the
/// frontend team (together with docs/API_CHANGES_FOR_FRONTEND.md) to regenerate their typed client.
///
/// This entire controller is compiled out of Release builds (<c>#if DEBUG</c>), so it can never be
/// reached in production. It is intentionally <see cref="AllowAnonymousAttribute"/> for local tooling.
/// </summary>
[ApiController]
[AllowAnonymous]
[Route("api/dev/schema")]
[ApiExplorerSettings(IgnoreApi = true)] // keep the tooling endpoint out of the public schema itself
public sealed class DevSchemaController : ControllerBase
{
    private readonly ISwaggerProvider _swaggerProvider;
    private readonly IWebHostEnvironment _environment;
    private readonly ILogger<DevSchemaController> _logger;

    public DevSchemaController(
        ISwaggerProvider swaggerProvider,
        IWebHostEnvironment environment,
        ILogger<DevSchemaController> logger)
    {
        _swaggerProvider = swaggerProvider;
        _environment = environment;
        _logger = logger;
    }

    /// <summary>Returns the current OpenAPI 3.0 document as JSON.</summary>
    [HttpGet]
    [Produces("application/json")]
    public IActionResult GetSchema([FromQuery] string documentName = "v1")
    {
        var json = Serialize(documentName);
        return Content(json, "application/json");
    }

    /// <summary>
    /// Writes the OpenAPI 3.0 document to <c>docs/openapi.v1.json</c> (under the content root) and
    /// returns the absolute path. Convenient for committing the schema next to the changelog.
    /// </summary>
    [HttpPost("export")]
    public async Task<IActionResult> Export([FromQuery] string documentName = "v1", CancellationToken ct = default)
    {
        var json = Serialize(documentName);

        var docsDir = Path.Combine(_environment.ContentRootPath, "docs");
        Directory.CreateDirectory(docsDir);

        var path = Path.Combine(docsDir, $"openapi.{documentName}.json");
        await System.IO.File.WriteAllTextAsync(path, json, ct);

        _logger.LogInformation("OpenAPI schema exported to {Path}.", path);
        return Ok(new { documentName, path, bytes = System.Text.Encoding.UTF8.GetByteCount(json) });
    }

    private string Serialize(string documentName)
    {
        var document = _swaggerProvider.GetSwagger(documentName);
        return document.SerializeAsJson(OpenApiSpecVersion.OpenApi3_0);
    }
}
#endif
