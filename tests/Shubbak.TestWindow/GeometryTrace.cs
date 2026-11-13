using System.Diagnostics;
using Shubbak.Core.Geometry;
using Shubbak.Native;

namespace Shubbak.TestWindow;

/// <summary>Records only this test window's applied geometry; writes after the timed run.</summary>
internal sealed class GeometryTrace(string path)
{
    private readonly Sample[] _samples = new Sample[32_768];
    private int _count, _dropped;
    private Rect _previous;
    private readonly record struct Sample(long Qpc, Rect Rect);

    internal void Record(nint window)
    {
        Rect rect = Win32Window.GetBounds(window);
        if (rect == _previous) return;
        _previous = rect;
        if (_count < _samples.Length) _samples[_count++] = new Sample(Stopwatch.GetTimestamp(), rect);
        else _dropped++;
    }

    internal void Save()
    {
        using var output = new StreamWriter(new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.Read)) { NewLine = "\n" };
        output.WriteLine("qpc,x,y,width,height");
        for (int i = 0; i < _count; i++)
        {
            Sample sample = _samples[i];
            output.WriteLine(FormattableString.Invariant($"{sample.Qpc},{sample.Rect.X},{sample.Rect.Y},{sample.Rect.Width},{sample.Rect.Height}"));
        }
        if (_dropped != 0) throw new InvalidOperationException($"Geometry recording overflowed by {_dropped} samples.");
    }
}
