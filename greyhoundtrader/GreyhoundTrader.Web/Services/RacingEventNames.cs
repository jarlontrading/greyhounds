namespace RacingTrader.Web.Services;

internal static class RacingEventNames
{
    private static readonly TimeZoneInfo UkTimeZone = FindUkTimeZone();

    public static string CanonicalMeeting(string meeting)
    {
        var venue = (meeting ?? "").Trim();
        foreach (var suffix in new[] { " EVE", " AM", " PM" })
            if (venue.EndsWith(suffix, StringComparison.OrdinalIgnoreCase))
            {
                venue = venue[..^suffix.Length].Trim();
                break;
            }
        foreach (var suffix in new[]
                 {
                     " (Mon)", " (Monday)", " (Tue)", " (Tuesday)",
                     " (Wed)", " (Wednesday)", " (Thu)", " (Thursday)",
                     " (Fri)", " (Friday)", " (Sat)", " (Saturday)",
                     " (Sun)", " (Sunday)"
                 })
            if (venue.EndsWith(suffix, StringComparison.OrdinalIgnoreCase))
            {
                venue = venue[..^suffix.Length].Trim();
                break;
            }

        // Spreadex labels the course "Epsom"; Bet365, Sporting Life and the
        // existing Sportuno events use "Epsom Downs".
        if (venue.Equals("Epsom", StringComparison.OrdinalIgnoreCase))
            return "Epsom Downs";
        return string.IsNullOrWhiteSpace(venue) ? "Race" : venue;
    }

    public static string MeetingKey(string meeting) =>
        new(CanonicalMeeting(meeting).Where(char.IsLetterOrDigit)
            .Select(char.ToLowerInvariant).ToArray());

    public static string Canonical(string meeting, long startTimeUnix)
    {
        var venue = CanonicalMeeting(meeting);
        if (startTimeUnix <= 0) return venue;
        var localStart = TimeZoneInfo.ConvertTime(DateTimeOffset.FromUnixTimeSeconds(startTimeUnix), UkTimeZone);
        return $"{venue} {localStart:HH:mm}";
    }

    private static TimeZoneInfo FindUkTimeZone()
    {
        foreach (var id in new[] { "GMT Standard Time", "Europe/London" })
            try { return TimeZoneInfo.FindSystemTimeZoneById(id); }
            catch (TimeZoneNotFoundException) { }
            catch (InvalidTimeZoneException) { }
        return TimeZoneInfo.Utc;
    }
}
