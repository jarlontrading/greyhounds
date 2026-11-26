using System.Text.RegularExpressions;

namespace RacingTrader.Web.Services;

internal sealed record BetfredOffRaceIdentity(long EventId, int SportId, string Meeting, long StartTimeUnix);

// Mirrored in both traders. Never infer OFF from clock time alone.
internal static class BetfredOffIdentity
{
    private static readonly TimeZoneInfo Zone = TimeZoneInfo.FindSystemTimeZoneById(OperatingSystem.IsWindows() ? "GMT Standard Time" : "Europe/London");
    internal static bool Matches(PricingEvent race, BetfredOffRaceIdentity? provider,
        long signalEventId, string cardText, DateTime observedAtUtc, DateTime nowUtc)
    {
        if (provider is null || signalEventId <= 0 || provider.EventId != signalEventId ||
            race.SportId != provider.SportId || race.StartTimeUnix <= 0 || provider.StartTimeUnix <= 0 ||
            string.IsNullOrWhiteSpace(race.Meeting) || string.IsNullOrWhiteSpace(provider.Meeting)) return false;
        var age = nowUtc - observedAtUtc;
        if (age > TimeSpan.FromMinutes(10) || age < TimeSpan.FromSeconds(-30)) return false;
        if (Math.Abs((double)race.StartTimeUnix - provider.StartTimeUnix) > 180 ||
            RacingEventNames.MeetingKey(race.Meeting) != RacingEventNames.MeetingKey(provider.Meeting)) return false;
        var start = DateTimeOffset.FromUnixTimeSeconds(provider.StartTimeUnix);
        var target = DateTimeOffset.FromUnixTimeSeconds(race.StartTimeUnix);
        var zone = Zone;
        if (TimeZoneInfo.ConvertTime(start, zone).Date != TimeZoneInfo.ConvertTime(target, zone).Date ||
            Math.Abs((new DateTimeOffset(DateTime.SpecifyKind(observedAtUtc, DateTimeKind.Utc)) - start).TotalHours) > 2) return false;
        if (!Regex.IsMatch(cardText, @"\b(?:RACE IS RUNNING|RUNNING|IN[- ]?PLAY)\b", RegexOptions.IgnoreCase)) return false;
        var time = TimeZoneInfo.ConvertTime(start, zone).ToString("HH:mm", System.Globalization.CultureInfo.InvariantCulture);
        return Regex.IsMatch(cardText, @"(?<!\d)" + Regex.Escape(time) + @"(?!\d)");
    }
}
