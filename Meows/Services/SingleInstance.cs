using System.IO.Pipes;

namespace Meows.Services;

/// <summary>
/// One Meows per user. A tray application started twice is two icons, two sets of watches and
/// two writers to one settings folder, none of which anyone wanted; the second start should
/// mean "show me the one that is running". A named mutex says who was first, and a named
/// pipe carries the newcomer's arguments to the one that stays, so a shortcut that opens a
/// plugin works whether Meows was running or not.
/// </summary>
public sealed class SingleInstance : IDisposable
{
    private const string MutexName = @"Local\Meows.Single";
    private const string PipeName = "Meows.Instance";

    private readonly Mutex _mutex;
    private CancellationTokenSource? _listening;

    private SingleInstance(Mutex mutex) => _mutex = mutex;

    /// <summary>
    /// The one that stays, or null when another Meows already holds the name. A portable
    /// copy and a profile copy are both "Meows" to the mutex; two at once is still two.
    /// </summary>
    public static SingleInstance? TryClaim()
    {
        try
        {
            var mutex = new Mutex(initiallyOwned: true, MutexName, out var createdNew);
            if (createdNew)
                return new SingleInstance(mutex);
            mutex.Dispose();
            return null;
        }
        catch (Exception)
        {
            // A mutex that cannot be made is not a reason to refuse to start.
            return new SingleInstance(new Mutex());
        }
    }

    /// <summary>
    /// Tells the running Meows what this one was started with, and says whether it listened.
    /// One line, the arguments joined by tabs, which is what the listener splits on.
    /// </summary>
    public static bool Signal(IEnumerable<string> args)
    {
        try
        {
            using var pipe = new NamedPipeClientStream(".", PipeName, PipeDirection.Out);
            pipe.Connect(2000);
            using var writer = new StreamWriter(pipe) { AutoFlush = true };
            writer.WriteLine(string.Join('\t', args));
            return true;
        }
        catch (Exception)
        {
            return false;
        }
    }

    /// <summary>
    /// Starts taking signals, on a thread of its own, and hands each one's arguments to
    /// <paramref name="onSignal"/> from that thread; the caller posts to the UI thread.
    /// </summary>
    public void Listen(Action<string[]> onSignal)
    {
        _listening = new CancellationTokenSource();
        var token = _listening.Token;

        var thread = new Thread(() =>
        {
            while (!token.IsCancellationRequested)
            {
                try
                {
                    using var server = new NamedPipeServerStream(PipeName, PipeDirection.In, 1,
                        PipeTransmissionMode.Byte, PipeOptions.Asynchronous);
                    server.WaitForConnectionAsync(token).GetAwaiter().GetResult();
                    using var reader = new StreamReader(server);
                    var line = reader.ReadLine() ?? "";
                    var args = line.Length == 0 ? [] : line.Split('\t');
                    onSignal(args);
                }
                catch (OperationCanceledException)
                {
                    return;
                }
                catch (Exception)
                {
                    // A broken pipe from a client that gave up; listen again.
                    if (token.IsCancellationRequested)
                        return;
                    Thread.Sleep(200);
                }
            }
        })
        {
            IsBackground = true,
            Name = "Meows single-instance listener",
        };
        thread.Start();
    }

    public void Dispose()
    {
        try
        {
            _listening?.Cancel();
            _mutex.ReleaseMutex();
        }
        catch (Exception)
        {
            // Released with the process either way.
        }
        _mutex.Dispose();
    }
}
