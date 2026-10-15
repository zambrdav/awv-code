using System.Net;

namespace SmartRoutingAPI.Configuration;

/// <summary>
/// Reads and validates the configuration values that differ per installation: service endpoints,
/// CORS origins and reverse-proxy addresses.
/// </summary>
/// <remarks>
/// <para>
/// None of these values has a safe default in source. Hosts, domains and IP addresses of one
/// operator must never be compiled into the application, otherwise a second operator silently
/// talks to the first operator's infrastructure. They are therefore read exclusively from
/// configuration (appsettings / environment variables) and checked while the host is built.
/// </para>
/// <para>
/// Validation is deliberately strict because every malformed value here fails silently at
/// runtime: a CORS origin without scheme never matches and only blocks the frontend, an
/// unparsable proxy address collapses all anonymous callers into a single rate-limit partition,
/// and a Valhalla URL with a path prefix loses that prefix on every request.
/// </para>
/// <para>
/// Exception messages are German to stay consistent with the remaining startup checks in
/// <see cref="Program"/>, which operators read side by side in the same log.
/// </para>
/// </remarks>
public static class DeploymentSettings
{
    /// <summary>Configuration key of the reverse-proxy allowlist for forwarded headers.</summary>
    public const string TrustedProxyAddressesKey = "Security:TrustedProxyAddresses";

    /// <summary>Configuration key of the allowed CORS origins.</summary>
    public const string CorsAllowedOriginsKey = "Cors:AllowedOrigins";

    /// <summary>
    /// Parses a mandatory service root URL such as the Valhalla endpoint.
    /// </summary>
    /// <param name="value">The configured value.</param>
    /// <param name="key">The configuration key, used in the error message.</param>
    /// <returns>The parsed absolute root URL.</returns>
    /// <exception cref="InvalidOperationException">
    /// The value is missing, not an absolute http(s) URL, or carries a path, query or fragment.
    /// </exception>
    /// <remarks>
    /// A path is rejected because the typed HTTP clients address the service with absolute paths
    /// (<c>"/route"</c>, <c>"/status"</c>). <see cref="HttpClient.BaseAddress"/> then replaces the
    /// configured path instead of appending to it, so a reverse-proxy prefix such as
    /// <c>https://proxy/valhalla</c> would route every call to the proxy root.
    /// </remarks>
    public static Uri RequireServiceRootUrl(string? value, string key)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            throw new InvalidOperationException(
                $"{key} ist nicht gesetzt. Die Adresse ist installationsspezifisch und hat keinen "
                + $"Vorgabewert im Code. Ablage über appsettings oder die Umgebungsvariable "
                + $"{ToEnvironmentVariableName(key)}.");
        }

        if (!TryParseHttpRoot(value.Trim(), out var uri))
        {
            throw new InvalidOperationException(
                $"{key} = '{value}' ist ungültig. Erwartet wird eine absolute http(s)-Adresse ohne "
                + "Pfad, z. B. 'http://valhalla.example.internal:8002'.");
        }

        return uri;
    }

    /// <summary>
    /// Parses an optional service root URL that overrides another endpoint when set.
    /// </summary>
    /// <param name="value">The configured value; blank means "not configured".</param>
    /// <param name="key">The configuration key, used in the error message.</param>
    /// <returns>The parsed root URL, or <c>null</c> when the value is blank.</returns>
    /// <exception cref="InvalidOperationException">The value is set but malformed.</exception>
    public static Uri? OptionalServiceRootUrl(string? value, string key)
        => string.IsNullOrWhiteSpace(value) ? null : RequireServiceRootUrl(value, key);

    /// <summary>
    /// Parses an optional absolute http(s) link, e.g. a contact or license page.
    /// </summary>
    /// <param name="value">The configured value; blank means "not configured".</param>
    /// <param name="key">The configuration key, used in the error message.</param>
    /// <returns>The parsed URL, or <c>null</c> when the value is blank.</returns>
    /// <exception cref="InvalidOperationException">The value is set but not an absolute http(s) URL.</exception>
    public static Uri? OptionalLink(string? value, string key)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        if (!Uri.TryCreate(value.Trim(), UriKind.Absolute, out var uri) || !IsHttpScheme(uri))
        {
            throw new InvalidOperationException(
                $"{key} = '{value}' ist ungültig. Erwartet wird eine absolute http(s)-Adresse.");
        }

        return uri;
    }

    /// <summary>
    /// Rejects CORS origins that can never match a browser <c>Origin</c> header.
    /// </summary>
    /// <param name="origins">The configured origins.</param>
    /// <exception cref="InvalidOperationException">An entry is not of the form scheme://host[:port].</exception>
    /// <remarks>
    /// Browsers send the origin as scheme, host and optional port without a trailing slash. An entry
    /// with a path, a trailing slash or without scheme is accepted by the CORS middleware but never
    /// matches, so the misconfiguration would only show up as blocked frontend requests. A
    /// wildcard is rejected as well, because <c>"*"</c> in the origin list re-enables the
    /// allow-any-origin behaviour this API intentionally dropped.
    /// </remarks>
    public static void ValidateCorsOrigins(IEnumerable<string> origins)
    {
        foreach (var origin in origins)
        {
            if (string.IsNullOrWhiteSpace(origin)
                || origin.EndsWith('/')
                || !TryParseHttpRoot(origin, out _))
            {
                throw new InvalidOperationException(
                    $"{CorsAllowedOriginsKey} enthält den ungültigen Ursprung '{origin}'. Erwartet "
                    + "werden Schema, Host und optional Port ohne Pfad und ohne abschließenden "
                    + "Schrägstrich, z. B. 'https://routing.example.com'.");
            }
        }
    }

    /// <summary>
    /// Reads the IP addresses of reverse proxies that are allowed to set
    /// <c>X-Forwarded-For</c> and <c>X-Forwarded-Proto</c>.
    /// </summary>
    /// <param name="configuration">The application configuration.</param>
    /// <returns>The parsed addresses; empty when the API is reached without a proxy.</returns>
    /// <exception cref="InvalidOperationException">An entry is not a valid IP address.</exception>
    /// <remarks>
    /// Invalid entries fail the start instead of being skipped: a skipped proxy address means the
    /// forwarded client address is ignored, the rate limiter partitions every anonymous caller by
    /// the proxy address, and a single caller can exhaust the login quota for everyone.
    /// </remarks>
    public static IReadOnlyList<IPAddress> ReadTrustedProxyAddresses(IConfiguration configuration)
    {
        var entries = configuration.GetSection(TrustedProxyAddressesKey).Get<string[]>()
            ?? Array.Empty<string>();

        var addresses = new List<IPAddress>(entries.Length);
        foreach (var entry in entries)
        {
            if (!IPAddress.TryParse(entry?.Trim(), out var address))
            {
                throw new InvalidOperationException(
                    $"{TrustedProxyAddressesKey} enthält den ungültigen Eintrag '{entry}'. Erwartet "
                    + "wird eine einzelne IPv4- oder IPv6-Adresse des Reverse Proxys.");
            }

            addresses.Add(address);
        }

        return addresses;
    }

    /// <summary>
    /// Checks for an absolute http(s) URL that consists only of scheme, host and optional port.
    /// </summary>
    /// <param name="value">The candidate URL.</param>
    /// <param name="uri">The parsed URL when the check succeeds.</param>
    /// <returns><c>true</c> when the value is such a root URL.</returns>
    private static bool TryParseHttpRoot(string value, out Uri uri)
    {
        if (Uri.TryCreate(value, UriKind.Absolute, out var parsed)
            && IsHttpScheme(parsed)
            && parsed.AbsolutePath == "/"
            && string.IsNullOrEmpty(parsed.Query)
            && string.IsNullOrEmpty(parsed.Fragment)
            && string.IsNullOrEmpty(parsed.UserInfo))
        {
            uri = parsed;
            return true;
        }

        uri = null!;
        return false;
    }

    /// <summary>Restricts accepted URLs to plain HTTP and HTTPS.</summary>
    /// <param name="uri">The parsed URL.</param>
    /// <returns><c>true</c> for the http and https schemes.</returns>
    private static bool IsHttpScheme(Uri uri)
        => uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps;

    /// <summary>
    /// Maps a configuration key to the environment variable name the default configuration
    /// providers read it from, so the error message names the exact variable to set.
    /// </summary>
    /// <param name="key">A colon-separated configuration key.</param>
    /// <returns>The key with every colon replaced by a double underscore.</returns>
    private static string ToEnvironmentVariableName(string key) => key.Replace(":", "__", StringComparison.Ordinal);
}
