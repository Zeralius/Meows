using Meows.Plugins.Kibble.Services;

namespace Meows.Plugins.Kibble.ViewModels;

/// <summary>An IntakeStamp with words on it, so the dropdown does not show an enum name.</summary>
public sealed record StampOption(IntakeStamp Value, string Label)
{
    public override string ToString() => Label;
}
