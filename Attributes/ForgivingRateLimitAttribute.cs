using System;

/// <summary>
/// Markiert einen (typischerweise paginierten, lesenden) Endpunkt als „nachsichtig
/// ratenbegrenzt“. Der globale Rate-Limiter (<see cref="SmartRoutingAPI.Services.RateLimitingPolicy"/>)
/// erkennt dieses Marker-Attribut über die Endpoint-Metadaten und weist dem Aufrufer für
/// diese Endpunkte deutlich großzügigere Kontingente pro Sekunde/Minute/Stunde zu, damit das
/// Frontend Listen schnell (in Bursts) laden kann.
/// </summary>
/// <remarks>
/// <para>
/// <b>Warum ein Marker-Attribut und keine benannte Rate-Limit-Policy?</b>
/// In ASP.NET Core wird der <c>GlobalLimiter</c> mit jedem endpunktspezifischen Limiter
/// KOMBINIERT — es gilt stets der STRENGSTE Grenzwert. Eine großzügigere Endpunkt-Policy
/// (etwa via <c>[EnableRateLimiting]</c>) könnte den global konfigurierten, strengeren
/// Grenzwert daher NICHT lockern; der globale Limiter würde weiterhin blockieren. Der einzig
/// korrekte Weg, einzelne Endpunkte großzügiger zu behandeln, besteht folglich darin, den
/// globalen Limiter diese Endpunkte selbst erkennen zu lassen. Genau das ermöglicht dieser Marker.
/// </para>
/// <para>
/// Das Attribut trägt bewusst KEINE Zahlenwerte. Sämtliche Kontingente sind zentral in
/// <see cref="SmartRoutingAPI.Services.RateLimitingPolicy"/> gepflegt, damit keine „magic numbers“
/// über die Controller verstreut werden (Single Source of Truth).
/// </para>
/// <para>
/// Sicherheit: Der Marker sollte ausschließlich an authentifizierte, lesende (GET) Endpunkte
/// vergeben werden. Schreib-, Login- und anonyme Pfade behalten ihre strengen Standard-Kontingente.
/// </para>
/// </remarks>
[AttributeUsage(AttributeTargets.Method | AttributeTargets.Class, AllowMultiple = false, Inherited = true)]
public sealed class ForgivingRateLimitAttribute : Attribute
{
    // Reiner Marker: keine Logik, kein Zustand. Es gibt daher nichts, das eine Ausnahme werfen
    // könnte (logisch unmöglich) — folglich ist hier kein try/catch erforderlich.
}
