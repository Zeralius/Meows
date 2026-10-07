using Microsoft.Data.Sqlite;

namespace Meows.Tests;

/// <summary>
/// The SQLite the store actually runs on, asked of SQLite itself.
///
/// Microsoft.Data.Sqlite 10.0.0 brings a native SQLite older than the 3.50.2 that fixed
/// CVE-2025-6965, so the shell pins the native package past it. A pin is a line in a csproj that
/// a later package bump can quietly make pointless or undo, and nothing would say so: the store
/// would open, every test would pass, and the old SQLite would ship. This asks the loaded library
/// for its version, which is the one answer the package graph cannot fake.
/// </summary>
public sealed class SqliteVersionTests
{
    /// <summary>The first SQLite release with the fix for CVE-2025-6965.</summary>
    private static readonly Version Fixed = new(3, 50, 2);

    [Fact]
    public void The_sqlite_the_store_loads_is_past_the_known_advisory()
    {
        using var connection = new SqliteConnection("Data Source=:memory:");
        connection.Open();

        using var command = connection.CreateCommand();
        command.CommandText = "select sqlite_version()";
        var reported = (string)command.ExecuteScalar()!;

        Assert.True(Version.TryParse(reported, out var loaded), $"SQLite reported a version that does not parse: {reported}");
        Assert.True(loaded >= Fixed,
            $"The store runs on SQLite {reported}, which is older than {Fixed} and has CVE-2025-6965. " +
            "Check the SQLitePCLRaw.lib.e_sqlite3 pin in Meows/Meows.csproj.");
    }
}
