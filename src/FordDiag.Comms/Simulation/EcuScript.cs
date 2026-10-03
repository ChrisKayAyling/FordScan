namespace FordDiag.Comms.Simulation;

/// <summary>One diagnostic message an ECU emits in answer to a request.</summary>
/// <param name="Payload">Diagnostic payload (service id first).</param>
/// <param name="Delay">Virtual delay before it appears on the bus; delays larger than the adapter timeout (ATST) make the message only visible to a later monitor (ATMA).</param>
public readonly record struct EcuReply(byte[] Payload, TimeSpan Delay = default)
{
    public static EcuReply Of(string hex, TimeSpan delay = default) => new(Hex.Parse(hex), delay);
}

/// <summary>
/// Transport-independent scripted ECU behaviour: request prefix -> reply sequence. Used by the simulated CAN/K-line ECUs
/// and by <see cref="SimulatedTransport"/>.
/// </summary>
public sealed class EcuScript
{
    private sealed record Rule(byte[] Prefix, Func<byte[], int, IReadOnlyList<EcuReply>> Handler);

    private readonly List<Rule> _rules = new();
    private readonly Dictionary<int, int> _hits = new();
    private readonly object _lock = new();

    /// <summary>Reply used when no rule matches (default: negative response 0x11 serviceNotSupported). Null: stay silent.</summary>
    public byte? DefaultNegativeResponse { get; set; } = 0x11;

    /// <summary>Every request received, in order.</summary>
    public List<byte[]> Received { get; } = new();

    public int RequestCount { get { lock (_lock) return Received.Count; } }

    /// <summary>When the request starts with <paramref name="requestPrefixHex"/> answer with <paramref name="responseHex"/>.</summary>
    public EcuScript On(string requestPrefixHex, string responseHex) =>
        On(requestPrefixHex, _ => new[] { EcuReply.Of(responseHex) });

    public EcuScript On(string requestPrefixHex, Func<byte[], IReadOnlyList<EcuReply>> handler) =>
        Add(requestPrefixHex, (req, _) => handler(req));

    /// <summary>Answers with a fixed sequence, cycling the last element (e.g. changing live values: pass several responses).</summary>
    public EcuScript OnSequence(string requestPrefixHex, params string[] responsesHex) =>
        Add(requestPrefixHex, (_, n) => new[] { EcuReply.Of(responsesHex[Math.Min(n, responsesHex.Length - 1)]) });

    /// <summary>Negative response 7F sid nrc.</summary>
    public EcuScript Negative(string requestPrefixHex, byte nrc) =>
        On(requestPrefixHex, req => new[] { new EcuReply(new byte[] { 0x7F, req[0], nrc }) });

    /// <summary>Sends <paramref name="pendingCount"/> x "7F sid 78" and then the final response after <paramref name="finalDelay"/>.</summary>
    public EcuScript Pending(string requestPrefixHex, int pendingCount, string finalResponseHex, TimeSpan? finalDelay = null) =>
        On(requestPrefixHex, req =>
        {
            var list = new List<EcuReply>();
            for (int i = 0; i < pendingCount; i++) list.Add(new EcuReply(new byte[] { 0x7F, req[0], 0x78 }));
            list.Add(new EcuReply(Hex.Parse(finalResponseHex), finalDelay ?? TimeSpan.FromMilliseconds(1500)));
            return list;
        });

    /// <summary>The ECU does not answer.</summary>
    public EcuScript Silent(string requestPrefixHex) => On(requestPrefixHex, _ => Array.Empty<EcuReply>());

    private EcuScript Add(string prefixHex, Func<byte[], int, IReadOnlyList<EcuReply>> handler)
    {
        var prefix = Hex.Parse(prefixHex);
        lock (_lock)
        {
            _rules.Add(new Rule(prefix, handler));
            _rules.Sort((a, b) => b.Prefix.Length.CompareTo(a.Prefix.Length)); // longest prefix wins (stable enough for scripts)
        }
        return this;
    }

    /// <summary>Computes the replies for a request.</summary>
    public IReadOnlyList<EcuReply> Handle(byte[] request)
    {
        lock (_lock)
        {
            Received.Add(request);
            for (int i = 0; i < _rules.Count; i++)
            {
                var r = _rules[i];
                if (request.Length < r.Prefix.Length || !request.AsSpan(0, r.Prefix.Length).SequenceEqual(r.Prefix)) continue;
                int n = _hits.GetValueOrDefault(i);
                _hits[i] = n + 1;
                return r.Handler(request, n);
            }
        }
        if (DefaultNegativeResponse is byte nrc && request.Length > 0)
            return new[] { new EcuReply(new byte[] { 0x7F, request[0], nrc }) };
        return Array.Empty<EcuReply>();
    }
}
