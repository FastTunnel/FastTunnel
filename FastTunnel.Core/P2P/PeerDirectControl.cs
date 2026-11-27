using System.Globalization;
using System.Threading.Channels;

namespace FastTunnel.Core.P2P;

// A generation prevents delayed address candidates from a failed attempt being
// applied to the next connection. The visitor initiates each attempt.
internal sealed class PeerDirectControl(bool visitor)
{
    private readonly object gate = new();
    private readonly Channel<Attempt> starts = Channel.CreateBounded<Attempt>(
        new BoundedChannelOptions(1) { FullMode = BoundedChannelFullMode.DropOldest });
    private Attempt? current;

    internal Attempt Start()
    {
        lock (gate)
            return Replace((current?.Generation ?? 0) + 1);
    }

    internal void Receive(string signal)
    {
        var separator = signal.IndexOf('\n');
        if (separator is < 1 or > 10 || !int.TryParse(signal.AsSpan(0, separator), NumberStyles.None,
            CultureInfo.InvariantCulture, out var generation) || generation <= 0)
            return;

        lock (gate)
        {
            if (!visitor && separator == signal.Length - 1 && generation > (current?.Generation ?? 0))
                starts.Writer.TryWrite(Replace(generation));
            else if (current?.Generation == generation && separator < signal.Length - 1
                && !current.Signals.Writer.TryWrite(signal[(separator + 1)..]))
                current.Signals.Writer.TryComplete();
        }
    }

    internal IAsyncEnumerable<Attempt> ReadAttemptsAsync(CancellationToken token)
        => starts.Reader.ReadAllAsync(token);

    private Attempt Replace(int generation)
    {
        current?.Signals.Writer.TryComplete();
        return current = new Attempt(generation);
    }

    internal sealed class Attempt(int generation)
    {
        internal int Generation { get; } = generation;
        internal Channel<string> Signals { get; } = Channel.CreateBounded<string>(130);
        internal string Prefix { get; } = generation.ToString(CultureInfo.InvariantCulture) + "\n";
    }
}
