using Microsoft.AspNetCore.Http;

namespace SmartRoutingAPI.Services.Navigation;

/// <summary>
/// Wird geworfen, wenn eine Navigationsanfrage nicht beantwortet werden kann. Trägt den
/// Statuscode, mit dem der Controller antworten soll, damit die Zuordnung „Ursache → HTTP-Code“
/// an <em>einer</em> Stelle liegt und nicht in jeder Ausnahmebehandlung erneut entschieden wird.
/// </summary>
/// <remarks>
/// Die Meldung ist für den Aufrufer bestimmt und enthält deshalb keine internen Angaben. Technische
/// Ursachen bleiben in der <see cref="Exception.InnerException"/> und im Serverprotokoll.
/// </remarks>
public sealed class NavigationException : Exception
{
    /// <summary>Der HTTP-Statuscode, mit dem der Controller antworten sollte.</summary>
    public int SuggestedStatusCode { get; }

    /// <summary>Erzeugt eine neue <see cref="NavigationException"/>.</summary>
    /// <param name="message">Für den Aufrufer geeignete, nicht sensible Meldung.</param>
    /// <param name="suggestedStatusCode">Vorgeschlagener HTTP-Statuscode.</param>
    /// <param name="innerException">Die zugrunde liegende Ausnahme; bleibt serverseitig.</param>
    public NavigationException(
        string message,
        int suggestedStatusCode = StatusCodes.Status400BadRequest,
        Exception? innerException = null)
        : base(message, innerException)
    {
        SuggestedStatusCode = suggestedStatusCode;
    }
}
