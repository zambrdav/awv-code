using Microsoft.OpenApi.Models;

namespace SmartRoutingAPI.Configuration;

/// <summary>
/// Operator metadata of the published OpenAPI document (Swagger UI), bound from the
/// <c>"OpenApi"</c> configuration section.
/// </summary>
/// <remarks>
/// Kept in configuration so that every installation presents its own operator, contact and legal
/// links instead of those of the original developer. All fields except <see cref="Title"/> are
/// optional. An unset field is omitted from the document rather than filled with a placeholder,
/// because API consumers cannot tell a placeholder link from a real one.
/// </remarks>
public sealed class OpenApiDocumentOptions
{
    /// <summary>The configuration section this class is bound from.</summary>
    public const string SectionName = "OpenApi";

    /// <summary>Title used when <see cref="Title"/> is left blank.</summary>
    private const string DefaultTitle = "SmartRouting API";

    /// <summary>Document title shown in Swagger UI.</summary>
    public string Title { get; init; } = DefaultTitle;

    /// <summary>Free-text description of the API and its operator; omitted when blank.</summary>
    public string? Description { get; init; }

    /// <summary>Absolute URL of the terms of service; omitted when blank.</summary>
    public string? TermsOfServiceUrl { get; init; }

    /// <summary>Name of the responsible contact; omitted when blank.</summary>
    public string? ContactName { get; init; }

    /// <summary>E-mail address of the responsible contact; omitted when blank.</summary>
    public string? ContactEmail { get; init; }

    /// <summary>Absolute URL of a contact page; omitted when blank.</summary>
    public string? ContactUrl { get; init; }

    /// <summary>
    /// Name of the license the API is offered under. The OpenAPI specification requires a name on
    /// every license object, so no license is emitted while this is blank.
    /// </summary>
    public string? LicenseName { get; init; }

    /// <summary>Absolute URL of the license text; only valid together with <see cref="LicenseName"/>.</summary>
    public string? LicenseUrl { get; init; }

    /// <summary>
    /// Builds the OpenAPI info object for one document version from the configured values.
    /// </summary>
    /// <param name="version">The document version label, e.g. <c>v1</c>.</param>
    /// <returns>An info object containing only the configured, non-blank fields.</returns>
    /// <exception cref="InvalidOperationException">
    /// A configured link is not an absolute http(s) URL, or <see cref="LicenseUrl"/> is set
    /// without <see cref="LicenseName"/>. Raised during startup so that a broken link never
    /// reaches the published document.
    /// </exception>
    public OpenApiInfo ToOpenApiInfo(string version)
    {
        var info = new OpenApiInfo
        {
            Version = version,
            Title = string.IsNullOrWhiteSpace(Title) ? DefaultTitle : Title.Trim(),
            Description = NullIfBlank(Description),
            TermsOfService = DeploymentSettings.OptionalLink(TermsOfServiceUrl, Key(nameof(TermsOfServiceUrl)))
        };

        var contactName = NullIfBlank(ContactName);
        var contactEmail = NullIfBlank(ContactEmail);
        var contactUrl = DeploymentSettings.OptionalLink(ContactUrl, Key(nameof(ContactUrl)));
        if (contactName is not null || contactEmail is not null || contactUrl is not null)
        {
            info.Contact = new OpenApiContact { Name = contactName, Email = contactEmail, Url = contactUrl };
        }

        var licenseName = NullIfBlank(LicenseName);
        var licenseUrl = DeploymentSettings.OptionalLink(LicenseUrl, Key(nameof(LicenseUrl)));
        if (licenseName is not null)
        {
            info.License = new OpenApiLicense { Name = licenseName, Url = licenseUrl };
        }
        else if (licenseUrl is not null)
        {
            throw new InvalidOperationException(
                $"{Key(nameof(LicenseUrl))} ist gesetzt, {Key(nameof(LicenseName))} jedoch nicht. "
                + "Die OpenAPI-Spezifikation verlangt zu jeder Lizenz einen Namen.");
        }

        return info;
    }

    /// <summary>Normalises blank configuration values to <c>null</c> so they are omitted.</summary>
    /// <param name="value">The configured value.</param>
    /// <returns>The trimmed value, or <c>null</c> when blank.</returns>
    private static string? NullIfBlank(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    /// <summary>Builds the full configuration key of a property for error messages.</summary>
    /// <param name="property">The property name.</param>
    /// <returns>The key in <c>Section:Property</c> form.</returns>
    private static string Key(string property) => $"{SectionName}:{property}";
}
