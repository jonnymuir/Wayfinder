using Wayfinder.Engine.Models;
using Wayfinder.Models.ServiceDesign;

namespace Wayfinder.Engine.Services;

/// <summary>What the engine's collaborators need from it to answer with a rendered stage.</summary>
internal interface IEnvelopeSource
{
    ServiceRequestResponseEnvelope BuildEnvelope(
        ServiceRequest instance,
        ServiceBlueprint definition,
        ActorProfile accessProfile,
        string userId);

    /// <summary>
    /// The answer to the actor whose move was just saved: the same as <see cref="BuildEnvelope"/>, except that a
    /// move which took the request out of everything they can see is a success (<c>moved_on</c>), not an access error.
    /// </summary>
    ServiceRequestResponseEnvelope BuildAfterMove(
        ServiceRequest saved,
        ServiceBlueprint definition,
        ActorProfile accessProfile,
        string userId);
}
