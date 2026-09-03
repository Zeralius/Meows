using Meows.Plugins.Abstractions;
using Meows.Plugins.Kibble.Services;

namespace Meows.Plugins.Kibble.ViewModels;

/// <summary>An IntakeStamp with words on it, so the dropdown does not show an enum name.</summary>
public sealed record StampOption(IntakeStamp Value, string Key) : ILabelledOption
{
    public TranslatedString Label { get; } = MeowsText.Entry(Key);

    public override string ToString() => Label.Value;
}
