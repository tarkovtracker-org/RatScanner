using System;
using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;
using Xunit;

namespace RatScanner.Tests;

public sealed class BenchmarkReportTests
{
    [Fact]
    public void P50_uses_nearest_rank_for_even_and_odd_samples()
    {
        Assert.Equal(5, BenchmarkReport.Percentile([10, 2, 8, 4, 6, 1, 9, 3, 7, 5], 0.5));
        Assert.Equal(2, BenchmarkReport.Percentile([3, 1, 2], 0.5));
        Assert.Equal(1, BenchmarkReport.Percentile([1], 0.5));
    }

    [Fact]
    public void Reports_share_one_timestamp_with_filename_and_never_overwrite()
    {
        string directory = Path.Combine(Path.GetTempPath(), "RatScanner-benchmark-" + Guid.NewGuid().ToString("N"));
        DateTimeOffset timestamp = new(2026, 10, 30, 12, 34, 56, TimeSpan.Zero);
        int clockReads = 0;
        DateTimeOffset Clock()
        {
            clockReads++;
            return timestamp;
        }
        try
        {
            string first = BenchmarkReport.Write(directory, t => new { TimestampUtc = t, Value = 1 }, Clock);
            string second = BenchmarkReport.Write(directory, t => new { TimestampUtc = t, Value = 2 }, Clock);
            Assert.Equal(2, clockReads);
            Assert.NotEqual(first, second);
            Assert.StartsWith("20261030-123456-icon-latency-", Path.GetFileName(first));
            Assert.Equal(timestamp, JObject.Parse(File.ReadAllText(first))["TimestampUtc"].ToObject<DateTimeOffset>());
            Guid fixedId = Guid.NewGuid();
            string fixedPath = BenchmarkReport.Write(directory, t => new { Value = 3 }, Clock, () => fixedId);
            Assert.Throws<IOException>(() =>
                BenchmarkReport.Write(directory, t => new { Value = 4 }, Clock, () => fixedId)
            );
            Assert.Equal(3, JObject.Parse(File.ReadAllText(fixedPath))["Value"].Value<int>());
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task Idle_gpu_and_report_helpers_pass_hermetic_regressions()
    {
        DirectoryInfo root = new(AppContext.BaseDirectory);
        while (root != null && !File.Exists(Path.Combine(root.FullName, "RatScanner.sln")))
            root = root.Parent;
        Assert.NotNull(root);
        ProcessStartInfo startInfo = new("powershell.exe")
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        foreach (
            string argument in new[]
            {
                "-NoProfile",
                "-ExecutionPolicy",
                "Bypass",
                "-File",
                Path.Combine(root.FullName, "scripts", "bench", "test-benchmark-helpers.ps1"),
            }
        )
            startInfo.ArgumentList.Add(argument);
        using Process process =
            Process.Start(startInfo) ?? throw new InvalidOperationException("Could not start PowerShell");
        CancellationToken cancellation = TestContext.Current.CancellationToken;
        Task<string> output = process.StandardOutput.ReadToEndAsync(cancellation);
        Task<string> error = process.StandardError.ReadToEndAsync(cancellation);
        using CancellationTokenSource timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
        timeout.CancelAfter(TimeSpan.FromSeconds(20));
        try
        {
            await process.WaitForExitAsync(timeout.Token);
        }
        catch (OperationCanceledException)
        {
            process.Kill(entireProcessTree: true);
            throw;
        }
        string standardOutput = await output;
        Assert.True(process.ExitCode == 0, $"{standardOutput} {await error}");
        Assert.Contains("Benchmark helper regressions passed", standardOutput);
    }
}
