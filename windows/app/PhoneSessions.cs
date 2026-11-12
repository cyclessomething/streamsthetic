namespace PhoneCam;

public sealed record PhoneSlotAssignment(int Slot, PhoneEndpoint Endpoint, DateTime? LastSeen = null);

public sealed class PhoneSession
{
    public int Slot { get; }
    public ReceiverController Receiver { get; } = new();
    public PhoneEndpoint? Endpoint { get; internal set; }
    public string Name => Endpoint?.Name ?? $"Camera {Slot}";
    public IReadOnlyDictionary<string, string> LatestSettings { get; internal set; } = new Dictionary<string, string>();
    public DateTime? LastSeen { get; internal set; }
    public bool Running => Receiver.Status.State is not (LinkState.Stopped or LinkState.Error);

    internal PhoneSession(int slot) => Slot = slot;

    public void Start(string code, string extraArguments = "")
    {
        var phone = Endpoint ?? throw new InvalidOperationException("Choose a phone first.");
        if (code.Length != 6 || code.Any(c => c is < '0' or > '9')) throw new ArgumentException("Use the six-digit pairing code.", nameof(code));
        if (!System.Net.IPAddress.TryParse(phone.Host, out var address) || address.AddressFamily != System.Net.Sockets.AddressFamily.InterNetwork
            || phone.Port is < 1 or > 65535) throw new ArgumentException("Invalid phone address.");
        Receiver.PhoneHost = phone.Host;
        Receiver.PhonePort = phone.Port;
        Receiver.Start($"--slot {Slot} --host {address} --port {phone.Port} --code {code} {extraArguments}", phone.Name);
    }
}

/// <summary>Four independently controlled receivers. Assignments are remembered even while a phone is offline.</summary>
public sealed class PhoneSessions : IDisposable
{
    private readonly PhoneSession[] _sessions = Enumerable.Range(1, 4).Select(slot => new PhoneSession(slot)).ToArray();
    public IReadOnlyList<PhoneSession> All => _sessions;
    public IReadOnlyList<PhoneSlotAssignment> Assignments => _sessions.Where(s => s.Endpoint != null).Select(s => new PhoneSlotAssignment(s.Slot, s.Endpoint!, s.LastSeen)).ToArray();
    public event Action<PhoneSession, ReceiverStatus>? StatusChanged;
    public event Action<PhoneSession, IReadOnlyDictionary<string, string>>? SettingsChanged;

    public PhoneSessions()
    {
        foreach (var session in _sessions)
        {
            session.Receiver.StatusChanged += status =>
            {
                if (status.State is LinkState.Starting or LinkState.Stopped) session.LatestSettings = new Dictionary<string, string>();
                if (status.State is LinkState.Live or LinkState.WaitingForApp) session.LastSeen = DateTime.Now;
                StatusChanged?.Invoke(session, status);
            };
            session.Receiver.PhoneSettingsChanged += settings =>
            {
                session.LatestSettings = new Dictionary<string, string>(settings);
                SettingsChanged?.Invoke(session, session.LatestSettings);
            };
        }
    }

    public PhoneSession Find(int slot) => slot is >= 1 and <= 4 ? _sessions[slot - 1] : throw new ArgumentOutOfRangeException(nameof(slot));

    public PhoneSession? Assign(PhoneEndpoint phone)
    {
        if (!Valid(phone)) return null;
        var existing = _sessions.FirstOrDefault(s => s.Endpoint?.Id == phone.Id);
        // Upgrade an older endpoint-only record when a modern phone advertises its stable installation ID.
        existing ??= _sessions.FirstOrDefault(s => s.Endpoint is { } old && old.Id.StartsWith("legacy:") && old.Host == phone.Host && old.Port == phone.Port);
        existing ??= _sessions.FirstOrDefault(s => s.Endpoint == null && s.Running && s.Receiver.PhoneHost == phone.Host && s.Receiver.PhonePort == phone.Port);
        // A new identity at an occupied address must not create a second receiver for the same phone.
        if (_sessions.Any(s => s != existing && s.Endpoint is { } other && other.Host == phone.Host && other.Port == phone.Port)) return null;
        var session = existing ?? _sessions.FirstOrDefault(s => s.Endpoint == null && !s.Running);
        if (session == null) return null;
        session.Endpoint = phone;
        return session;
    }

    /// <summary>
    /// Refresh remembered identities from one complete discovery scan. Apply address changes together so
    /// two phones can swap DHCP addresses without losing their camera slots. Returned sessions were
    /// stopped because their running receiver used an old address; the caller may reconnect them.
    /// Unknown phones still require an explicit Assign/Connect action.
    /// </summary>
    public IReadOnlyList<PhoneSession> Reconcile(IEnumerable<PhoneEndpoint> discovered)
    {
        // An ambiguous advertisement must never move a remembered camera to an arbitrary endpoint.
        var phones = discovered.Where(Valid).Distinct().ToArray();
        var candidates = phones.Where(p => phones.Count(other => other.Id == p.Id) == 1
            && phones.Count(other => SameAddress(other, p)) == 1).ToArray();
        var updates = new Dictionary<PhoneSession, PhoneEndpoint>();
        foreach (var phone in candidates)
        {
            var session = _sessions.FirstOrDefault(s => s.Endpoint?.Id == phone.Id);
            session ??= _sessions.FirstOrDefault(s => s.Endpoint is { } old && old.Id.StartsWith("legacy:") && SameAddress(old, phone));
            session ??= _sessions.FirstOrDefault(s => s.Endpoint == null && s.Running
                && s.Receiver.PhoneHost == phone.Host && s.Receiver.PhonePort == phone.Port);
            if (session != null && !updates.ContainsKey(session)) updates.Add(session, phone);
        }

        // Keep claims made by phones absent from this scan. Repeat because rejecting one move can
        // make another move unsafe (A moves into B's old address but B's move was rejected).
        bool rejected;
        do
        {
            rejected = false;
            foreach (var update in updates.ToArray())
            {
                if (_sessions.Any(other => other != update.Key
                    && (other.Endpoint is { } old && SameAddress(old, update.Value) && !updates.ContainsKey(other)
                        || other.Endpoint == null && other.Running && other.Receiver.PhoneHost == update.Value.Host
                            && other.Receiver.PhonePort == update.Value.Port)))
                {
                    updates.Remove(update.Key);
                    rejected = true;
                }
            }
        } while (rejected);

        var reconnect = updates.Where(update => update.Key.Running
            && (update.Key.Receiver.PhoneHost != update.Value.Host || update.Key.Receiver.PhonePort != update.Value.Port))
            .Select(update => update.Key).ToArray();
        // Stop every old receiver before publishing any new endpoint; otherwise a swap can connect
        // two receivers to one phone while its previous receiver is still alive.
        foreach (var session in reconnect) session.Receiver.Stop();
        foreach (var update in updates) update.Key.Endpoint = update.Value;
        return reconnect;
    }

    private static bool SameAddress(PhoneEndpoint first, PhoneEndpoint second) =>
        first.Host == second.Host && first.Port == second.Port;

    public void Restore(IEnumerable<PhoneSlotAssignment>? assignments)
    {
        if (assignments == null) return;
        foreach (var assignment in assignments)
        {
            if (assignment == null || assignment.Slot is < 1 or > 4 || !Valid(assignment.Endpoint)) continue;
            if (_sessions.Any(s => s.Endpoint?.Id == assignment.Endpoint.Id)) continue;
            if (_sessions.Any(s => s.Endpoint is { } other && other.Host == assignment.Endpoint.Host && other.Port == assignment.Endpoint.Port)) continue;
            var session = Find(assignment.Slot);
            if (session.Endpoint == null && !session.Running)
            {
                session.Endpoint = assignment.Endpoint;
                session.LastSeen = assignment.LastSeen;
            }
        }
    }

    public void Forget(int slot)
    {
        var session = Find(slot);
        session.Receiver.Stop();
        session.Receiver.Preview = false;
        session.Receiver.PhoneHost = null;
        session.Endpoint = null;
        session.LatestSettings = new Dictionary<string, string>();
        session.LastSeen = null;
    }

    public void Dispose()
    {
        foreach (var session in _sessions) session.Receiver.Dispose();
    }

    private static bool Valid(PhoneEndpoint? phone) => phone != null && !string.IsNullOrWhiteSpace(phone.Id)
        && !string.IsNullOrWhiteSpace(phone.Name) && !phone.Name.Any(char.IsControl)
        && System.Net.IPAddress.TryParse(phone.Host, out var address)
        && address.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork && phone.Port is >= 1 and <= 65535;
}
