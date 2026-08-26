using System.Diagnostics;

namespace IndexedLargeDiskData;

/// <summary>
/// The default progress sink for <see cref="DataRoot.Maintain()"/>: a console meter.
/// </summary>
/// <remarks>
/// <para>
/// A merge is the one operation here with no natural feedback — it can run for minutes writing
/// nothing the caller can see — so the parameterless overload reports rather than sitting silent.
/// Pass an explicit callback, or set <see cref="StoreOptions.ReportMaintenanceProgress"/> to false,
/// to take the console out of it.
/// </para>
/// <para>
/// Output adapts to where it is going. On a terminal the line is redrawn in place with a carriage
/// return every half percent; when stdout is redirected — a pipe, a log file, a test runner —
/// carriage returns would collapse the whole run onto one unreadable line, so it prints a fresh
/// line every ten percent instead.
/// </para>
/// </remarks>
internal sealed class ConsoleMaintenanceReporter
{
    private readonly Stopwatch _clock = Stopwatch.StartNew();
    private readonly bool _redirected = Console.IsOutputRedirected;
    private readonly double _step;
    private double _lastPrinted = -1;

    internal ConsoleMaintenanceReporter(long plannedEntries)
    {
        if (_redirected)
        {
            _step = 10d;
        }
        else
        {
            _step = 0.5d;
        }
        Console.WriteLine($"maintain: merging {plannedEntries:N0} index entries");
    }

    /// <summary>Announces that a pass would do nothing, so silence is not mistaken for a hang.</summary>
    internal static void ReportNothingToDo() =>
        Console.WriteLine("maintain: nothing to merge, every level is already under the fanout");

    internal void Report(MaintenanceProgress progress)
    {
        if (progress.Percentage - _lastPrinted < _step && progress.Percentage < 100d)
        {
            return;
        }

        _lastPrinted = progress.Percentage;

        string line = $"maintain: {progress.Percentage,5:F1}%  {progress.EntriesWritten,14:N0}" +
                      $" / {progress.TotalEntries:N0}  [{progress.Stage}]  {_clock.Elapsed.TotalSeconds,6:F1}s";

        if (_redirected)
        {
            Console.WriteLine(line);
        }
        else
        {
            Console.Write("\r" + line + "   ");
        }
    }

    internal void Finish(long plannedEntries)
    {
        if (!_redirected)
        {
            Console.WriteLine();
        }

        double seconds = Math.Max(_clock.Elapsed.TotalSeconds, 0.001);
        Console.WriteLine($"maintain: merged {plannedEntries:N0} entries in {seconds:F2}s " +
                          $"({plannedEntries / seconds:N0} entries/s)");
    }
}
