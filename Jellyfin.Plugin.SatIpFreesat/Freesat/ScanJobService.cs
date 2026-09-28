namespace Jellyfin.Plugin.SatIpFreesat.Freesat;

/// <summary>
/// Singleton that tracks the state of the currently running (or most recent) channel scan.
/// Shared between the HTTP controller (manual scans) and the background scheduler (auto-scans)
/// so that both paths coordinate and the status endpoint always reflects real state.
/// </summary>
public sealed class ScanJobService
{
    private string _state = "idle";
    private string _message = "No scan run yet — click Scan to begin";
    private int _channelCount;
    private int? _percent;
    private readonly object _lock = new();

    public bool IsScanning { get { lock (_lock) return _state == "scanning"; } }

    public bool TryStart()
    {
        lock (_lock)
        {
            if (_state == "scanning") return false;
            _state = "scanning";
            _message = "Starting scan…";
            _channelCount = 0;
            _percent = null;
            return true;
        }
    }

    public void Update(string message, int? percent)
    {
        lock (_lock) { _message = message; _percent = percent; }
    }

    public void Complete(int count, string message)
    {
        lock (_lock) { _state = "done"; _channelCount = count; _message = message; _percent = 100; }
    }

    public void Fail(string message)
    {
        lock (_lock) { _state = "failed"; _message = message; _percent = null; }
    }

    public ScanProgressInfo GetProgress()
    {
        lock (_lock) { return new(_state, _message, _channelCount, _percent); }
    }

    public readonly record struct ScanProgressInfo(string State, string Message, int ChannelCount, int? Percent);
}
