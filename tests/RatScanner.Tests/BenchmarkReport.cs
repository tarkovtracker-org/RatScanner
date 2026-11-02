using System;
using System.IO;
using System.Linq;
using Newtonsoft.Json;

namespace RatScanner.Tests;

internal static class BenchmarkReport
{
    internal static double Percentile(double[] values, double percentile)
    {
        double[] sorted = values.Order().ToArray();
        return sorted[Math.Max(0, (int)Math.Ceiling(percentile * sorted.Length) - 1)];
    }

    internal static string Write(
        string directory,
        Func<DateTimeOffset, object> createReport,
        Func<DateTimeOffset> clock = null,
        Func<Guid> newId = null
    )
    {
        DateTimeOffset timestamp = (clock ?? (() => DateTimeOffset.UtcNow))();
        Guid id = (newId ?? Guid.NewGuid)();
        Directory.CreateDirectory(directory);
        string path = Path.Combine(directory, $"{timestamp:yyyyMMdd-HHmmss}-icon-latency-{id:N}.json");
        string json = JsonConvert.SerializeObject(createReport(timestamp), Formatting.Indented);
        using FileStream stream = new(path, FileMode.CreateNew, FileAccess.Write);
        using StreamWriter writer = new(stream);
        writer.Write(json);
        return path;
    }
}
