using System.Diagnostics;
using System.Text.Json;
using CaptureRack.DirectShow.Windows.Capture;

namespace CaptureRack.App.Views;

internal static class DirectShowInspectionClient
{
    internal static async Task<T> RunAsync<T>(DirectShowInspectionRequest request, CancellationToken cancellation)
    {
        var basePath = AppContext.BaseDirectory;
        var executable = new[]
        {
            Path.Combine(basePath, "CaptureRack.Probe.exe"),
            Path.GetFullPath(Path.Combine(basePath, "..", "Probe", "CaptureRack.Probe.exe"))
        }.FirstOrDefault(File.Exists) ?? throw new FileNotFoundException();

        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
        deadline.CancelAfter(TimeSpan.FromSeconds(15));
        var temporary = Directory.CreateTempSubdirectory("capturerack-directshow-");
        try
        {
            var requestPath = Path.Combine(temporary.FullName, "request.json");
            var resultPath = Path.Combine(temporary.FullName, "result.json");
            await File.WriteAllTextAsync(requestPath, JsonSerializer.Serialize(request), deadline.Token).ConfigureAwait(false);

            var start = new ProcessStartInfo(executable)
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            };
            foreach (var argument in new[] { "directshow", "inspect-worker", requestPath, resultPath })
                start.ArgumentList.Add(argument);
            using var process = Process.Start(start) ?? throw new InvalidOperationException();
            var stdout = process.StandardOutput.BaseStream.CopyToAsync(Stream.Null);
            var stderr = process.StandardError.ReadToEndAsync();

            try
            {
                await process.WaitForExitAsync(deadline.Token).ConfigureAwait(false);
                await stdout.ConfigureAwait(false);
                var detail = (await stderr.ConfigureAwait(false)).Trim();
                if (process.ExitCode != 0)
                    throw new InvalidOperationException(detail);
                var json = await File.ReadAllTextAsync(resultPath, deadline.Token).ConfigureAwait(false);
                return JsonSerializer.Deserialize<T>(json) ?? throw new InvalidDataException();
            }
            catch (OperationCanceledException)
            {
                if (!process.HasExited)
                {
                    try { process.Kill(); }
                    catch (InvalidOperationException) when (process.HasExited) { }
                    await process.WaitForExitAsync().ConfigureAwait(false);
                }
                if (!cancellation.IsCancellationRequested)
                    throw new TimeoutException();
                throw;
            }
        }
        finally
        {
            foreach (var file in temporary.EnumerateFiles())
                file.Delete();
            temporary.Delete();
        }
    }
}
