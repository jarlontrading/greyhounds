using RacingTrader.Web.Services;

internal static class OffSignalIdentityChecks
{
    internal static void Run(Action<bool, string> check)
    {
        var now = new DateTime(2026, 11, 26, 17, 0, 30, DateTimeKind.Utc);
        var start = new DateTimeOffset(now.AddSeconds(-30)).ToUnixTimeSeconds();
        foreach (var sport in new[] { 2, 4 })
        {
            var meeting = sport == 2 ? "Chelmsford City" : "Towcester";
            var providerName = meeting;
            var race = new PricingEvent(123, sport, meeting, meeting + " 17:00", start, "0", 0);
            var metadata = new BetfredOffRaceIdentity(456, sport, providerName, start);
            bool Match(BetfredOffRaceIdentity? m, string card = "RUNNING 17:00", long id = 456,
                DateTime? observed = null, PricingEvent? target = null)
                => BetfredOffIdentity.Matches(target ?? race, m, id, card, observed ?? now, now);
            check(Match(metadata), "OFF joins exact provider ID without a course heading");
            check(Match(metadata, "Race is Running 17:00"), "Existing running label accepted");
            check(Match(metadata, "IN-PLAY 17:00"), "In-play label accepted");
            check(!Match(null), "Missing dated metadata never matches OFF");
            check(!Match(metadata, id: 457), "Different provider event ID rejected");
            check(!Match(metadata with { Meeting = "Another Course" }), "Same clock time at another course rejected");
            check(!Match(metadata with { SportId = sport == 2 ? 4 : 2 }), "Other sport rejected");
            check(!Match(metadata, "RUNNING 17:01"), "Wrong displayed start rejected");
            check(!Match(metadata, "RUNNING 17:000"), "Partial clock token rejected");
            check(!Match(metadata, "17:00"), "A scheduled clock time is not an OFF signal");
            check(!Match(metadata, observed: now.AddMinutes(-11)), "Expired OFF signal rejected");
            check(!Match(metadata, observed: now.AddMinutes(1)), "Future observation rejected");
            check(!Match(metadata with { StartTimeUnix = start + 86400 }), "Tomorrow's metadata rejected");
            check(!Match(metadata, target: race with { StartTimeUnix = start - 86400 }), "Yesterday's local card rejected");
            check(!Match(metadata, target: race with { Meeting = "" }), "Unknown meeting rejected");
            check(!Match(metadata with { StartTimeUnix = 0 }), "Missing provider start rejected");
        }
    }
}
