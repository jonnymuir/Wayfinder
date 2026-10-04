namespace Wayfinder.Engine.Services.Validation;

/// <summary>The diagnostic codes for one kind of statically evaluated expression: the error, and the downgrade used when a known gap explains the failure.</summary>
internal readonly record struct EvaluationCodes(string Error, string Unverified);
