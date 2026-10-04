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
}
