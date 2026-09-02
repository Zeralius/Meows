using Mews.Services;

namespace Mews.Tests;

public sealed class CrashLogTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "crash-" + Guid.NewGuid().ToString("N")[..10]);

    private string File_ => Path.Combine(_root, "nested", "mews.log");

    [Fact]
    public void Watching_makes_the_folder_it_will_need()
    {
        CrashLog.Watch(File_);

        // Nothing has created the settings folder at the point this is armed, and a crash while
        // starting up is one of the ones worth catching.
        Assert.True(Directory.Exists(Path.GetDirectoryName(File_)));
    }

    [Fact]
    public void A_crash_is_written_down_with_its_stack()
    {
        CrashLog.Watch(File_);

        try
        {
            throw new InvalidOperationException("something specific");
        }
        catch (Exception ex)
        {
            CrashLog.Write("test", ex);
        }

        var written = System.IO.File.ReadAllText(File_);
        Assert.Contains("something specific", written);
        Assert.Contains("test exception", written);
        // The stack is the entire point. A message alone says nothing about where it came from.
        Assert.Contains("CrashLogTests", written);
    }

    [Fact]
    public void The_same_crash_is_not_written_twice()
    {
        CrashLog.Watch(File_);
        var error = new InvalidOperationException("once only");

        // Main records and rethrows, and the rethrow reaches the handler, so one crash arrives
        // here twice.
        CrashLog.Write("fatal", error);
        CrashLog.Write("unhandled", error);

        var records = System.IO.File.ReadAllLines(File_).Count(l => l.StartsWith("--- "));
        Assert.Equal(1, records);
    }

    [Fact]
    public void Failing_to_record_a_crash_does_not_cause_another_one()
    {
        // A path that cannot be written. Throwing from the crash handler would replace a useful
        // report with a confusing one.
        CrashLog.Watch(Path.Combine(_root, "nul", "impossible", "mews.log"));

        var caught = Record.Exception(() => CrashLog.Write("test", new Exception("boom")));

        Assert.Null(caught);
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch (Exception)
        {
        }
    }
}
