using System.Diagnostics;

namespace RacingTrader.Web.Services;

internal sealed class OnDemandBet365Capture(
    IHttpClientFactory clients,
    ILogger<OnDemandBet365Capture> logger)
{
    private const string HealthUrl = "http://127.0.0.1:5378/healthz";
    private readonly SemaphoreSlim _gate = new(1, 1);
    private Process? _launcher;

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            if (await IsHealthyAsync(cancellationToken))
            {
                await WarmRacingMeetingsAsync(cancellationToken);
                return;
            }
            var captureRoot = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory,
                "..", "..", "..", "..", "GreyhoundTrader.Bet365Capture"));
            var captureDll = Path.Combine(captureRoot, "bin", "Debug", "net8.0",
                "GreyhoundTrader.Bet365Capture.dll");
            if (!File.Exists(captureDll))
                throw new FileNotFoundException("The on-demand Bet365 capture executable was not found.", captureDll);

            var start = new ProcessStartInfo("dotnet.exe")
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                WindowStyle = ProcessWindowStyle.Hidden,
                WorkingDirectory = captureRoot
            };
            start.ArgumentList.Add(captureDll);
            _launcher = Process.Start(start)
                ?? throw new InvalidOperationException("The Bet365 capture process could not be started.");

            var deadline = DateTime.UtcNow.AddSeconds(60);
            while (DateTime.UtcNow < deadline)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (await IsHealthyAsync(cancellationToken))
                {
                    await WarmRacingMeetingsAsync(cancellationToken);
                    return;
                }
                await Task.Delay(500, cancellationToken);
            }
            await StopCoreAsync();
            throw new TimeoutException("The on-demand Bet365 capture did not become ready within 60 seconds.");
        }
        catch
        {
            await StopCoreAsync();
            throw;
        }
        finally { _gate.Release(); }
    }

    public async Task StopAsync()
    {
        await _gate.WaitAsync();
        try { await StopCoreAsync(); }
        finally { _gate.Release(); }
    }

    private async Task<bool> IsHealthyAsync(CancellationToken cancellationToken)
    {
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(2));
            using var response = await clients.CreateClient("Bet365Capture").GetAsync(HealthUrl, timeout.Token);
            return response.IsSuccessStatusCode;
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested) { return false; }
        catch (HttpRequestException) { return false; }
    }

    private async Task WarmRacingMeetingsAsync(CancellationToken cancellationToken)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(90));
        using var response = await clients.CreateClient("Bet365Capture")
            .PostAsync("http://127.0.0.1:5378/api/racing/refresh", null, timeout.Token);
        response.EnsureSuccessStatusCode();
    }

    private Task StopCoreAsync()
    {
        var launcher = _launcher;
        _launcher = null;
        try
        {
            var pid = launcher is not null && !launcher.HasExited ? launcher.Id : FindListenerPid(5378);
            if (pid > 0)
                Process.Start(new ProcessStartInfo("taskkill.exe", $"/PID {pid} /T /F")
                {
                    UseShellExecute = false,
                    CreateNoWindow = true
                })?.WaitForExit(10000);
            // Chromium can detach a crash-handler child from the original tree.
            // Remove only the process carrying this app's dedicated profile.
            Process.Start(new ProcessStartInfo("powershell.exe",
                "-NoProfile -Command \"Get-CimInstance Win32_Process | Where-Object { $_.Name -eq 'chrome.exe' -and $_.CommandLine -match 'greyhoundtrader\\\\bet365-greyhound-browser-profile' } | ForEach-Object { Stop-Process -Id $_.ProcessId -Force -ErrorAction SilentlyContinue }\"")
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                WindowStyle = ProcessWindowStyle.Hidden
            })?.WaitForExit(10000);
        }
        catch (Exception ex) { logger.LogWarning(ex, "Could not stop the on-demand Bet365 capture process tree."); }
        launcher?.Dispose();
        return Task.CompletedTask;
    }

    private static int FindListenerPid(int port)
    {
        using var process = Process.Start(new ProcessStartInfo("netstat.exe", "-ano -p tcp")
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true
        });
        if (process is null) return 0;
        var output = process.StandardOutput.ReadToEnd();
        process.WaitForExit(5000);
        foreach (var line in output.Split('\n'))
        {
            var parts = line.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length >= 5 && parts[1].EndsWith($":{port}", StringComparison.Ordinal) &&
                parts[3].Equals("LISTENING", StringComparison.OrdinalIgnoreCase) &&
                int.TryParse(parts[4], out var pid)) return pid;
        }
        return 0;
    }
}
