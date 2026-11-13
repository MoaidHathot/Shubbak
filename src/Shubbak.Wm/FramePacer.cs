namespace Shubbak.Wm;

/// <summary>A monotonic deadline that absorbs small wake errors without accumulating drift.</summary>
internal struct FramePacer
{
    private bool _started;
    private double _deadline, _last, _period;

    internal double Remaining(double nowMilliseconds, double periodMilliseconds)
    {
        if (!_started) return 0;
        if (_period != periodMilliseconds)
        {
            _period = periodMilliseconds;
            _deadline = _last + _period;
        }
        return Math.Max(0, _deadline - nowMilliseconds);
    }

    internal void Commit(double nowMilliseconds, double periodMilliseconds)
    {
        double next = _started && _period == periodMilliseconds
            ? _deadline + periodMilliseconds
            : nowMilliseconds + periodMilliseconds;
        // Recover sub-millisecond timer error, but never replay a backlog or post
        // near-adjacent frames after a long application/scheduler stall.
        double recovery = Math.Min(1, periodMilliseconds / 10);
        _deadline = next - nowMilliseconds >= periodMilliseconds - recovery
            ? next : nowMilliseconds + periodMilliseconds;
        _last = nowMilliseconds;
        _period = periodMilliseconds;
        _started = true;
    }

    internal void Reset() => this = default;
}
