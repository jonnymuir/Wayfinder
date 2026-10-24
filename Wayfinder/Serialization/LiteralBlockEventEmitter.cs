using YamlDotNet.Core;
using YamlDotNet.Serialization;
using YamlDotNet.Serialization.EventEmitters;

namespace Wayfinder.Serialization;

/// <summary>Writes multi-line text (guidance, HTML content, long expressions) as a readable <c>|</c> block.</summary>
internal sealed class LiteralBlockEventEmitter(IEventEmitter nextEmitter) : ChainedEventEmitter(nextEmitter)
{
    public override void Emit(ScalarEventInfo eventInfo, IEmitter emitter)
    {
        if (eventInfo.Source.Value is string text && text.Contains('\n', StringComparison.Ordinal))
        {
            eventInfo.Style = ScalarStyle.Literal;
        }

        base.Emit(eventInfo, emitter);
    }
}
