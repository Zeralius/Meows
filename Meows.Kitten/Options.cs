using System.Text.RegularExpressions;

namespace Meows.Kitten;

/// <summary>What was asked for on the command line, with the defaults filled in.</summary>
public sealed record Options(
    string Name,
    string Plain,
    string PlainDe,
    string Description,
    string DescriptionDe,
    string Icon,
    string Category,
    bool Verify,
    bool DryRun)
{
    /// <summary>Lower case, which is what the plugin id and every string key start with.</summary>
    public string Key => Name.ToLowerInvariant();

    public string Id => $"meows.{Key}";

    public string Project => $"Meows.Plugins.{Name}";

    public const string Usage = """
        Kitten writes a new Meows plugin from a name.

          dotnet run --project Meows.Kitten -- <Name> [options]

          <Name>                 PascalCase, one word, the cat's name: Whiskers, Larder, Prowl.
          --plain <text>         What it is in plain words, for the paw switch. Default: the name.
          --plain-de <text>      The same in German. Default: the English one.
          --description <text>   One sentence for the card. Default: a placeholder.
          --description-de <text>
          --icon <emoji>         Default: 🐾
          --category <key>       group.everyday (default), group.disk or group.bot.
          --no-verify            Write and edit but do not build or test.
          --dry-run              Say what would be written and edited, and stop.
        """;

    public static Options? Parse(string[] args)
    {
        if (args.Length == 0)
            return null;

        string? name = null;
        string? plain = null, plainDe = null, description = null, descriptionDe = null;
        var icon = "🐾";
        var category = "group.everyday";
        var verify = true;
        var dryRun = false;

        for (var i = 0; i < args.Length; i++)
        {
            var arg = args[i];
            string Next()
            {
                if (i + 1 >= args.Length)
                    throw new ArgumentException($"{arg} needs a value");
                return args[++i];
            }

            try
            {
                switch (arg)
                {
                    case "--plain": plain = Next(); break;
                    case "--plain-de": plainDe = Next(); break;
                    case "--description": description = Next(); break;
                    case "--description-de": descriptionDe = Next(); break;
                    case "--icon": icon = Next(); break;
                    case "--category": category = Next(); break;
                    case "--no-verify": verify = false; break;
                    case "--dry-run": dryRun = true; break;
                    case "-h" or "--help": return null;
                    default:
                        if (arg.StartsWith('-') || name is not null)
                            return null;
                        name = arg;
                        break;
                }
            }
            catch (ArgumentException)
            {
                return null;
            }
        }

        if (name is null)
            return null;

        return new Options(
            name,
            plain ?? name,
            plainDe ?? plain ?? name,
            description ?? $"What {name} does, in one sentence.",
            descriptionDe ?? description ?? $"Was {name} tut, in einem Satz.",
            icon,
            category,
            verify,
            dryRun);
    }

    /// <summary>
    /// One PascalCase word. It becomes a namespace, a class prefix, a folder and a key, and
    /// every one of those objects to a space or a hyphen.
    /// </summary>
    public static bool IsGoodName(string name) => Regex.IsMatch(name, "^[A-Z][A-Za-z0-9]{1,30}$");
}
