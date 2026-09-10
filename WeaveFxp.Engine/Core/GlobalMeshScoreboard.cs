namespace WeaveFxp.Engine.Core;

// Snapshots contain no callbacks into a mesh's state lock. Resource callbacks must
// be synchronous, nonblocking permit operations; connection I/O happens after claim.
internal sealed class GlobalMeshScoreboard<T>
{
    internal sealed record Candidate(T Value, long Score, string DestinationKey,
        object SourceResource, object DestinationResource, Func<bool> CanStart, Func<IDisposable?> TryReserve,
        Func<int> SourceFree, Func<int> DestinationFree);

    internal sealed class Registration : IDisposable
    {
        private readonly GlobalMeshScoreboard<T> _board;
        internal readonly string Owner;
        internal readonly int Capacity;
        internal readonly Action Wake;
        internal readonly long Order;
        internal List<Candidate> Candidates = new();
        internal int Active;
        internal long LastServed;
        internal bool Paused;

        internal Registration(GlobalMeshScoreboard<T> board, string owner, int capacity, Action wake, long order)
        { _board = board; Owner = owner; Capacity = capacity; Wake = wake; Order = order; }

        public void Dispose() => _board.Remove(this);
    }

    internal sealed class Claim : IDisposable
    {
        private GlobalMeshScoreboard<T>? _board;
        internal readonly Registration Registration;
        public Candidate Candidate { get; }
        public IDisposable Reservation { get; }

        internal Claim(GlobalMeshScoreboard<T> board, Registration registration, Candidate candidate, IDisposable reservation)
        { _board = board; Registration = registration; Candidate = candidate; Reservation = reservation; }

        public void Dispose()
        {
            var board = Interlocked.Exchange(ref _board, null);
            if (board is null) return;
            try { Reservation.Dispose(); }
            finally { board.Finish(this); }
        }
    }

    private readonly object _sync = new();
    private readonly Dictionary<string, Registration> _owners = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _activeDestinations = new(StringComparer.OrdinalIgnoreCase);
    private long _sequence;

    public Registration Register(string owner, int capacity, Action wake)
    {
        lock (_sync)
        {
            var registration = new Registration(this, owner, Math.Max(1, capacity), wake, ++_sequence);
            _owners[owner] = registration;
            return registration;
        }
    }

    public void Publish(Registration owner, IEnumerable<Candidate> candidates)
    {
        var ordered = candidates.OrderByDescending(x => x.Score).ToList();
        lock (_sync)
            if (IsCurrent(owner)) owner.Candidates = ordered;
    }

    public void SetPaused(string owner, bool paused)
    {
        Action[] wakes;
        lock (_sync)
        {
            if (_owners.TryGetValue(owner, out var registration)) registration.Paused = paused;
            wakes = _owners.Values.Select(x => x.Wake).ToArray();
        }
        foreach (var wake in wakes) wake();
    }

    public Claim? TryTake(Registration caller)
    {
        var wakes = new HashSet<Action>();
        Claim? claim = null;
        lock (_sync)
        {
            if (!IsCurrent(caller) || caller.Paused || caller.Active >= caller.Capacity) return null;
            // Merge each race's sorted snapshot. Equal scores rotate between races.
            var queue = new PriorityQueue<(Registration Owner, int Index), (long Score, long Served, long Order)>();
            void Enqueue(Registration owner, int index)
            {
                if (index < owner.Candidates.Count)
                    queue.Enqueue((owner, index), (-owner.Candidates[index].Score, owner.LastServed, owner.Order));
            }
            foreach (var owner in _owners.Values)
                if (!owner.Paused && owner.Active < owner.Capacity) Enqueue(owner, 0);

            // Reserve slots per free capacity, not per whole pool. A higher-priority race
            // only "uses up" ONE slot on each side for its next pick; the rest of the pool
            // stays available to lower races. This keeps every free slot busy (cbftp) while
            // still guaranteeing priority wins the scarce last slots. The blanket per-pool
            // reservation this replaces left up to 19/20 slots idle behind one pending pick.
            var projectedFree = new Dictionary<object, int>();
            int Free(object resource, Func<int> probe) =>
                projectedFree.TryGetValue(resource, out var v) ? v : (projectedFree[resource] = probe());
            while (queue.TryDequeue(out var entry, out _))
            {
                var candidate = entry.Owner.Candidates[entry.Index];
                Enqueue(entry.Owner, entry.Index + 1);
                if (_activeDestinations.Contains(candidate.DestinationKey) || !candidate.CanStart()) continue;
                var srcFree = Free(candidate.SourceResource, candidate.SourceFree);
                var dstFree = Free(candidate.DestinationResource, candidate.DestinationFree);
                if (srcFree <= 0 || dstFree <= 0) continue; // higher races already committed every free slot here
                if (!ReferenceEquals(entry.Owner, caller))
                {
                    // A higher/equal race will take one slot on each side. Reserve exactly
                    // that one, wake it, and leave the remaining slots open to the caller.
                    projectedFree[candidate.SourceResource] = srcFree - 1;
                    projectedFree[candidate.DestinationResource] = dstFree - 1;
                    wakes.Add(entry.Owner.Wake);
                    continue;
                }
                var reservation = candidate.TryReserve();
                if (reservation is null) continue;
                caller.Active++;
                caller.LastServed = ++_sequence;
                _activeDestinations.Add(candidate.DestinationKey);
                caller.Candidates.RemoveAll(x => x.DestinationKey.Equals(candidate.DestinationKey, StringComparison.OrdinalIgnoreCase));
                claim = new Claim(this, caller, candidate, reservation);
                break;
            }
        }
        foreach (var wake in wakes) wake();
        return claim;
    }

    private bool IsCurrent(Registration owner) =>
        _owners.TryGetValue(owner.Owner, out var current) && ReferenceEquals(owner, current);

    private void Remove(Registration owner)
    {
        Action[] wakes;
        lock (_sync)
        {
            if (!IsCurrent(owner)) return;
            _owners.Remove(owner.Owner);
            wakes = _owners.Values.Select(x => x.Wake).ToArray();
        }
        foreach (var wake in wakes) wake();
    }

    private void Finish(Claim claim)
    {
        Action[] wakes;
        lock (_sync)
        {
            claim.Registration.Active--;
            _activeDestinations.Remove(claim.Candidate.DestinationKey);
            wakes = _owners.Values.Select(x => x.Wake).ToArray();
        }
        foreach (var wake in wakes) wake();
    }
}
