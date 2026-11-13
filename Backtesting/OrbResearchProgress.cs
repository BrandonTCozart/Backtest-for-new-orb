using System.Diagnostics;
using System.Globalization;

namespace Backtest_for_new_orb;

// Console-only stage progress for the opt-in ORB pattern experiment.
// Uses newline updates (rather than carriage-return redraws) so both
// Visual Studio and redirected logs remain legible and copyable.
internal sealed class OrbResearchProgress
{
    private const int Stages = 7;
    private const int BarWidth = 20;
    private readonly TextWriter _output;
    private readonly Stopwatch _clock = new();
    private int _stage;
    private string _label = "";
    private int _total;
    private int _done;
    private int _lastPrintedBucket = -1;

    internal OrbResearchProgress(TextWriter? output = null) =>
        _output = output ?? Console.Out;

    internal void Start(int stage, string label, int total)
    {
        if (stage is < 1 or > Stages || total < 1)
            throw new ArgumentOutOfRangeException(nameof(stage));
        _stage = stage;
        _label = label;
        _total = total;
        _done = 0;
        _lastPrintedBucket = -1;
        _clock.Restart();
        Print(0);
    }

    internal void Advance() => Update(_done + 1);

    internal void Update(int completed)
    {
        if (_total < 1)
            throw new InvalidOperationException("Start a progress stage first.");
        if (completed < _done || completed > _total)
            throw new ArgumentOutOfRangeException(nameof(completed));
        _done = completed;
        var percent = (int)(100L * _done / _total);
        // Print at start, each 5% milestone, and at 100%. This avoids
        // flooding redirected logs with 41,472 configuration updates.
        var bucket = percent / 5;
        if (bucket > _lastPrintedBucket || _done == _total && _lastPrintedBucket < 20)
            Print(percent);
    }

    internal void Complete() => Update(_total);

    private void Print(int percent)
    {
        _lastPrintedBucket = percent / 5;
        var filled = (int)(BarWidth * (long)_done / _total);
        var bar = new string('#', filled) +
            new string('-', BarWidth - filled);
        _output.WriteLine("ORB PROGRESS [" + _stage + "/" + Stages +
            "] " + _label + " [" + bar + "] " +
            percent.ToString("D3", CultureInfo.InvariantCulture) + "% (" +
            _done.ToString("N0", CultureInfo.InvariantCulture) + "/" +
            _total.ToString("N0", CultureInfo.InvariantCulture) +
            ") elapsed " + _clock.Elapsed.ToString(@"hh\:mm\:ss",
                CultureInfo.InvariantCulture));
        _output.Flush();
    }
}
