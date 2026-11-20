using System.Diagnostics;
using System.Text.Json;

namespace GreyhoundTrader.Monitor;

internal static class Program
{
    [STAThread]
    private static void Main()
    {
        ApplicationConfiguration.Initialize();
        Application.Run(new MonitorForm());
    }
}

internal sealed class MonitorForm : Form
{
    private const string BaseUrl = "http://127.0.0.1:5287";
    private const string BridgeStatusUrl = "http://127.0.0.1:5278/api/betfred/status";
    private const string PublicStatusUrl = "https://greyhounds.jarlontrading.co.uk/api/status";
    private const string BetfredServiceUrl = "http://127.0.0.1:5388/api/status";
    private static readonly Color Healthy = Color.FromArgb(72, 211, 160);
    private static readonly Color Unhealthy = Color.FromArgb(255, 102, 113);
    private readonly HttpClient _http = new() { Timeout = TimeSpan.FromSeconds(6) };
    private readonly Label _service = StatusLabel("CONNECTING", Color.FromArgb(242, 190, 66));
    private readonly Label _webHealth = HealthLabel("WEB / API");
    private readonly Label _feedHealth = HealthLabel("LIVE FEED");
    private readonly Label _resultHealth = HealthLabel("RESULTS");
    private readonly Label _distanceHealth = HealthLabel("DISTANCES");
    private readonly Label _bridgeHealth = HealthLabel("BETFRED BRIDGE");
    private readonly Label _tunnelHealth = HealthLabel("PUBLIC URL");
    private readonly Label _suspensionHealth = HealthLabel("BETFRED SERVICE");
    private readonly Label _environment = ValueLabel("—");
    private readonly Label _feed = ValueLabel("—");
    private readonly Label _results = ValueLabel("—");
    private readonly Label _off = ValueLabel("—");
    private readonly Label _cards = ValueLabel("—");
    private readonly Label _updated = ValueLabel("—");
    private readonly Label _lateCount = new()
    {
        Text = "CHECKING", AutoSize = true, ForeColor = Color.FromArgb(242, 190, 66),
        Font = new Font("Segoe UI Semibold", 8.5F, FontStyle.Bold)
    };
    private readonly ListBox _lateRaces = new();
    private readonly ListBox _activity = new();
    private readonly System.Windows.Forms.Timer _timer = new() { Interval = 5000 };
    private readonly NotifyIcon _notify = new()
    {
        Icon = SystemIcons.Warning,
        Visible = true,
        Text = "GreyhoundTrader Health"
    };
    private bool _refreshing;
    private bool? _lastOverallHealthy;
    private int _consecutiveWebFailures;
    private DateTime _lastRestartAttemptUtc = DateTime.MinValue;

    public MonitorForm()
    {
        Text = "GreyhoundTrader Health";
        Size = new Size(560, 660);
        MinimumSize = new Size(500, 575);
        StartPosition = FormStartPosition.CenterScreen;
        BackColor = Color.FromArgb(12, 18, 29);
        ForeColor = Color.FromArgb(230, 238, 247);
        Font = new Font("Segoe UI", 9F);

        var title = new Label
        {
            Text = "GREYHOUND TRADER",
            Font = new Font("Segoe UI Semibold", 17F, FontStyle.Bold),
            ForeColor = Color.FromArgb(117, 208, 255),
            AutoSize = true,
            Location = new Point(18, 15)
        };
        _service.Location = new Point(365, 18);

        var health = new TableLayoutPanel
        {
            Location = new Point(18, 55), Width = 505, Height = 58,
            ColumnCount = 4, RowCount = 2, Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right
        };
        for (var i = 0; i < 4; i++) health.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 25F));
        health.RowStyles.Add(new RowStyle(SizeType.Percent, 50));
        health.RowStyles.Add(new RowStyle(SizeType.Percent, 50));
        health.Controls.Add(_webHealth, 0, 0);
        health.Controls.Add(_feedHealth, 1, 0);
        health.Controls.Add(_resultHealth, 2, 0);
        health.Controls.Add(_suspensionHealth, 3, 0);
        health.Controls.Add(_distanceHealth, 0, 1);
        health.Controls.Add(_bridgeHealth, 1, 1);
        health.Controls.Add(_tunnelHealth, 2, 1);

        var lateTitle = new Label
        {
            Text = "LATE RACES — PAST SCHEDULED TIME, NOT OFF", AutoSize = true, Location = new Point(18, 126),
            ForeColor = Color.FromArgb(143, 161, 180), Font = new Font("Segoe UI Semibold", 8.5F)
        };
        _lateCount.Location = new Point(465, 126);
        _lateRaces.Location = new Point(18, 148);
        _lateRaces.Size = new Size(505, 76);
        _lateRaces.Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right;
        _lateRaces.BackColor = Color.FromArgb(18, 27, 42);
        _lateRaces.ForeColor = Color.FromArgb(220, 230, 240);
        _lateRaces.BorderStyle = BorderStyle.FixedSingle;
        _lateRaces.Font = new Font("Consolas", 8.5F);

        var grid = new TableLayoutPanel
        {
            Location = new Point(18, 235), Width = 505, Height = 112,
            ColumnCount = 4, RowCount = 3, Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right
        };
        grid.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 22));
        grid.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 28));
        grid.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 22));
        grid.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 28));
        AddMetric(grid, 0, "ENVIRONMENT", _environment);
        AddMetric(grid, 1, "FEED WORKER", _feed);
        AddMetric(grid, 2, "RESULTS", _results);
        AddMetric(grid, 3, "BETFRED OFF", _off);
        AddMetric(grid, 4, "RACE CARDS", _cards);
        AddMetric(grid, 5, "LAST CHECK", _updated);

        var activityTitle = new Label
        {
            Text = "RECENT ACTIVITY", AutoSize = true, Location = new Point(18, 360),
            ForeColor = Color.FromArgb(143, 161, 180), Font = new Font("Segoe UI Semibold", 9F)
        };
        _activity.Location = new Point(18, 384);
        _activity.Size = new Size(505, 188);
        _activity.Anchor = AnchorStyles.Top | AnchorStyles.Bottom | AnchorStyles.Left | AnchorStyles.Right;
        _activity.BackColor = Color.FromArgb(18, 27, 42);
        _activity.ForeColor = Color.FromArgb(220, 230, 240);
        _activity.BorderStyle = BorderStyle.FixedSingle;
        _activity.Font = new Font("Consolas", 8.5F);

        var open = Button("Open Trader", new Point(18, 582));
        open.Click += (_, _) => Process.Start(new ProcessStartInfo(BaseUrl) { UseShellExecute = true });
        var refresh = Button("Refresh now", new Point(132, 582));
        refresh.Click += async (_, _) => await RefreshAsync();

        Controls.AddRange([title, _service, health, lateTitle, _lateCount, _lateRaces, grid,
            activityTitle, _activity, open, refresh]);
        _timer.Tick += async (_, _) => await RefreshAsync();
        Shown += async (_, _) => { _timer.Start(); await RefreshAsync(); };
        FormClosed += (_, _) => _notify.Dispose();
    }

    private async Task RefreshAsync()
    {
        if (_refreshing) return;
        _refreshing = true;
        try
        {
            var statusTask = TryGetJsonAsync(BaseUrl + "/api/status");
            var offTask = TryGetJsonAsync(BaseUrl + "/api/betfred-off/status");
            var activityTask = TryGetJsonAsync(BaseUrl + "/api/activity?limit=18");
            var distanceTask = TryGetJsonAsync(BaseUrl + "/api/distance-review");
            var bridgeTask = TryGetJsonAsync(BridgeStatusUrl);
            var tunnelTask = TryGetJsonAsync(PublicStatusUrl);
            var suspensionTask = TryGetJsonAsync(BetfredServiceUrl);
            var eventsTask = TryGetJsonAsync(BaseUrl + "/api/events/chronological?sportId=4&subscribedOnly=true");
            await Task.WhenAll(statusTask, offTask, activityTask, distanceTask, bridgeTask, tunnelTask, suspensionTask, eventsTask);

            using var status = await statusTask;
            using var off = await offTask;
            using var activity = await activityTask;
            using var distance = await distanceTask;
            using var bridge = await bridgeTask;
            using var tunnel = await tunnelTask;
            using var suspension = await suspensionTask;
            using var events = await eventsTask;

            var webOk = status is not null;
            var feedOk = false;
            var resultsOk = false;
            if (status is not null)
            {
                var root = status.RootElement;
                var hosted = root.GetProperty("hostedFeed");
                feedOk = hosted.GetProperty("enabled").GetBoolean();
                resultsOk = hosted.GetProperty("hostedResults").GetBoolean();
                _environment.Text = root.GetProperty("environment").GetString() ?? "—";
                _environment.ForeColor = Color.FromArgb(242, 190, 66);
                _feed.Text = feedOk ? "LIVE" : "OFF";
                _feed.ForeColor = feedOk ? Healthy : Unhealthy;
                _results.Text = resultsOk ? "POLLING" : "OFF";
                _results.ForeColor = resultsOk ? Healthy : Unhealthy;
            }
            else
            {
                _environment.Text = "NO CONNECTION";
                _feed.Text = "NO CONNECTION";
                _results.Text = "NO CONNECTION";
            }

            var distanceOk = distance is not null && DistanceIsHealthy(distance.RootElement);
            var bridgeOk = bridge is not null && bridge.RootElement.TryGetProperty("connected", out var connected) && connected.GetBoolean();
            var tunnelOk = tunnel is not null;
            var suspensionOk = suspension is not null && suspension.RootElement.TryGetProperty("healthy", out var serviceHealthy) && serviceHealthy.GetBoolean();
            SetHealth(_webHealth, "WEB / API", webOk);
            SetHealth(_feedHealth, "LIVE FEED", feedOk);
            SetHealth(_resultHealth, "RESULTS", resultsOk);
            SetHealth(_distanceHealth, "DISTANCES", distanceOk);
            SetHealth(_bridgeHealth, "BETFRED BRIDGE", bridgeOk);
            SetHealth(_tunnelHealth, "PUBLIC URL", tunnelOk);
            SetHealth(_suspensionHealth, "BETFRED SERVICE", suspensionOk);
            var overallHealthy = webOk && feedOk && resultsOk && distanceOk && bridgeOk && tunnelOk && suspensionOk;
            SetService(overallHealthy ? "ALL HEALTHY" : "ATTENTION", overallHealthy ? Healthy : Unhealthy);
            await HandleHealthTransitionAsync(webOk, overallHealthy);

            if (off is not null)
            {
                var offRoot = off.RootElement;
                var available = offRoot.GetProperty("pageAvailable").GetBoolean();
                _off.Text = available ? "LIVE" : "BETSAPI FALLBACK";
                _off.ForeColor = available ? Healthy : Color.FromArgb(242, 190, 66);
                _cards.Text = offRoot.GetProperty("raceCardsObserved").GetInt32().ToString("N0");
            }
            else
            {
                _off.Text = "NO CONNECTION";
                _off.ForeColor = Unhealthy;
                _cards.Text = "—";
            }
            _updated.Text = DateTime.Now.ToString("HH:mm:ss");
            UpdateLateRaces(events);

            if (activity is not null)
            {
                _activity.BeginUpdate();
                _activity.Items.Clear();
                foreach (var item in activity.RootElement.EnumerateArray())
                {
                    var at = item.GetProperty("createdAtUtc").GetDateTime().ToLocalTime().ToString("HH:mm:ss");
                    var type = item.GetProperty("activityType").GetString()?.ToUpperInvariant() ?? "EVENT";
                    var meeting = item.TryGetProperty("meeting", out var m) ? m.GetString() : "";
                    var name = item.TryGetProperty("eventName", out var n) ? n.GetString() : "";
                    var message = item.TryGetProperty("message", out var msg) ? msg.GetString() : "";
                    _activity.Items.Add($"{at}  {type,-13} {meeting} {name} — {message}");
                }
                _activity.EndUpdate();
            }
        }
        finally { _refreshing = false; }
    }

    private async Task HandleHealthTransitionAsync(bool webOk, bool overallHealthy)
    {
        _consecutiveWebFailures = webOk ? 0 : _consecutiveWebFailures + 1;
        if (_lastOverallHealthy == true && !overallHealthy)
        {
            _notify.BalloonTipTitle = "GreyhoundTrader needs attention";
            _notify.BalloonTipText = webOk
                ? "One or more pricing services are unhealthy. Open the health monitor for details."
                : "The web service is down. Automatic restart is being attempted.";
            _notify.ShowBalloonTip(10000);
        }
        if (_lastOverallHealthy == false && overallHealthy)
        {
            _notify.BalloonTipTitle = "GreyhoundTrader recovered";
            _notify.BalloonTipText = "All monitored services are healthy again.";
            _notify.ShowBalloonTip(6000);
        }
        _lastOverallHealthy = overallHealthy;

        if (_consecutiveWebFailures < 2 || DateTime.UtcNow - _lastRestartAttemptUtc < TimeSpan.FromMinutes(2)) return;
        _lastRestartAttemptUtc = DateTime.UtcNow;
        var script = Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "Start-GreyhoundTrader-Web.ps1");
        script = Path.GetFullPath(script);
        if (!File.Exists(script)) return;
        Process.Start(new ProcessStartInfo("powershell.exe",
            $"-NoProfile -ExecutionPolicy Bypass -File \"{script}\"")
        {
            UseShellExecute = true,
            WindowStyle = ProcessWindowStyle.Hidden
        });
        await Task.CompletedTask;
    }

    private async Task<JsonDocument?> TryGetJsonAsync(string url)
    {
        try
        {
            using var response = await _http.GetAsync(url);
            response.EnsureSuccessStatusCode();
            return JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        }
        catch { return null; }
    }

    private static bool DistanceIsHealthy(JsonElement root)
    {
        var errorFree = !root.TryGetProperty("lastError", out var error) ||
            error.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined ||
            string.IsNullOrWhiteSpace(error.GetString());
        if (!errorFree) return false;

        if (root.TryGetProperty("lastPollUtc", out var lastPoll) && lastPoll.ValueKind == JsonValueKind.String &&
            lastPoll.TryGetDateTime(out var checkedUtc))
            return DateTime.UtcNow - checkedUtc.ToUniversalTime() <= TimeSpan.FromMinutes(3);

        // Historical distance polling is intentionally outside the live host.
        // An error-free loaded table set is healthy in that mode.
        return root.TryGetProperty("tableCount", out var tableCount) && tableCount.GetInt32() > 0;
    }

    private void UpdateLateRaces(JsonDocument? events)
    {
        _lateRaces.BeginUpdate();
        _lateRaces.Items.Clear();
        if(events is null)
        {
            _lateCount.Text = "UNAVAILABLE";
            _lateCount.ForeColor = Unhealthy;
            _lateRaces.Items.Add("Schedule unavailable");
            _lateRaces.EndUpdate();
            return;
        }
        var now = DateTimeOffset.UtcNow;
        var late = events.RootElement.EnumerateArray()
            .Where(item => item.GetProperty("startTimeUnix").GetInt64() < now.ToUnixTimeSeconds() &&
                           item.GetProperty("status").GetString() is "0" or "not started" &&
                           (!item.TryGetProperty("inProgress", out var inProgress) || !inProgress.GetBoolean()))
            .OrderBy(item => item.GetProperty("startTimeUnix").GetInt64()).ToList();
        _lateCount.Text = late.Count == 0 ? "0 LATE" : $"{late.Count} LATE";
        _lateCount.ForeColor = late.Count == 0 ? Healthy : Unhealthy;
        if(late.Count == 0) _lateRaces.Items.Add("No late races");
        foreach(var item in late.Take(8))
        {
            var scheduled = DateTimeOffset.FromUnixTimeSeconds(item.GetProperty("startTimeUnix").GetInt64());
            var minutes = Math.Max(1, (int)Math.Floor((now - scheduled).TotalMinutes));
            var localTime = scheduled.ToLocalTime().ToString("HH:mm");
            var meeting = item.GetProperty("meeting").GetString() ?? "Unknown";
            _lateRaces.Items.Add($"{localTime}  {meeting,-22} {minutes,3}m late");
        }
        if(late.Count > 8) _lateRaces.Items.Add($"… and {late.Count - 8} more");
        _lateRaces.EndUpdate();
    }

    private void SetService(string text, Color colour)
    {
        _service.Text = "● " + text;
        _service.ForeColor = colour;
    }

    private static void SetHealth(Label label, string name, bool healthy)
    {
        label.Text = $"● {name}  {(healthy ? "OK" : "DOWN")}";
        label.ForeColor = healthy ? Healthy : Unhealthy;
    }

    private static void AddMetric(TableLayoutPanel grid, int index, string caption, Label value)
    {
        var cell = new Panel { Dock = DockStyle.Fill, Padding = new Padding(4) };
        cell.Controls.Add(new Label { Text = caption, Dock = DockStyle.Top, Height = 17, ForeColor = Color.FromArgb(113, 132, 152), Font = new Font("Segoe UI Semibold", 7.5F) });
        value.Dock = DockStyle.Bottom;
        cell.Controls.Add(value);
        grid.Controls.Add(cell, (index % 2) * 2, index / 2);
        grid.SetColumnSpan(cell, 2);
    }

    private static Label StatusLabel(string text, Color colour) => new()
    {
        Text = "● " + text, ForeColor = colour, AutoSize = true,
        Font = new Font("Segoe UI Semibold", 10F, FontStyle.Bold)
    };

    private static Label HealthLabel(string text) => new()
    {
        Text = "● " + text + "  CHECKING", ForeColor = Color.FromArgb(242, 190, 66), Dock = DockStyle.Fill,
        TextAlign = ContentAlignment.MiddleLeft, Font = new Font("Segoe UI Semibold", 8.5F), AutoEllipsis = true
    };

    private static Label ValueLabel(string text) => new()
    {
        Text = text, Height = 22, ForeColor = Color.FromArgb(220, 230, 240),
        Font = new Font("Segoe UI Semibold", 9.5F, FontStyle.Bold)
    };

    private static Button Button(string text, Point location) => new()
    {
        Text = text, Location = location, Size = new Size(105, 30),
        FlatStyle = FlatStyle.Flat, BackColor = Color.FromArgb(28, 46, 67),
        ForeColor = Color.White, Anchor = AnchorStyles.Bottom | AnchorStyles.Left
    };
}
