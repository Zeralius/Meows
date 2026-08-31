namespace Mews.Plugins.TelegramPoster.Model;

/// <summary>What we remember between runs.</summary>
public sealed class TelegramPosterSettings
{
    public string? BotRoot { get; set; }

    public string? PythonPath { get; set; }

    /// <summary>Where it was cloned from. Saved per machine once you change it.</summary>
    public string? RepositoryUrl { get; set; }
}
