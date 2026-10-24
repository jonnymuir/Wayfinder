using System.Text.RegularExpressions;
using YamlDotNet.Core;

namespace Wayfinder.Serialization;

/// <summary>
/// A blueprint's YAML could not be read. <see cref="Line"/> and <see cref="Column"/> (both
/// 1-based) point at the problem; <see cref="Exception.Message"/> says what it is and where.
/// </summary>
public sealed class ServiceBlueprintYamlException : Exception
{
    public ServiceBlueprintYamlException(string reason, int line, int column)
        : base($"Line {line}, column {column}: {reason}")
    {
        Reason = reason;
        Line = line;
        Column = column;
    }

    public ServiceBlueprintYamlException()
        : this("The YAML could not be read.", 1, 1)
    {
    }

    public ServiceBlueprintYamlException(string message)
        : this(message, 1, 1)
    {
    }

    public ServiceBlueprintYamlException(string message, Exception innerException)
        : base(message, innerException)
    {
        Reason = message;
        Line = 1;
        Column = 1;
    }

    /// <summary>What is wrong, without the position.</summary>
    public string Reason { get; }

    public int Line { get; }

    public int Column { get; }

    /// <summary>
    /// YamlDotNet wraps the real problem ("Property 'lable' not found …") in a generic
    /// "Exception during deserialization"; report the innermost YAML error, at its own position.
    /// </summary>
    internal static ServiceBlueprintYamlException From(YamlException exception)
    {
        var innermost = exception;
        for (var inner = exception.InnerException; inner is not null; inner = inner.InnerException)
        {
            if (inner is YamlException yaml)
            {
                innermost = yaml;
            }
        }

        var reason = ShortTypeNames(innermost.InnerException is { } cause and not YamlException ? cause.Message : CleanReason(innermost));
        return new ServiceBlueprintYamlException(reason, (int)innermost.Start.Line, (int)innermost.Start.Column);
    }

    // "on type 'Wayfinder.Models.ServiceDesign.StageDefinition'" reads better as "on type 'StageDefinition'".
    private static string ShortTypeNames(string reason) =>
        Regex.Replace(reason, @"type '(?:[\w`]+\.)+([\w`]+)'", "type '$1'", RegexOptions.None, TimeSpan.FromSeconds(1));

    // YamlException.Message repeats its own "(Line: …)" prefix; Wayfinder states the position once.
    private static string CleanReason(YamlException exception)
    {
        var message = exception.Message;
        var close = message.IndexOf("): ", StringComparison.Ordinal);
        return message.StartsWith("(Line:", StringComparison.Ordinal) && close >= 0 ? message[(close + 3)..] : message;
    }
}
