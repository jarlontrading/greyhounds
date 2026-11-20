namespace RacingTrader.Web.Services;

public static class Rule4Calculator
{
    /// <summary>
    /// Tattersalls / bet365 cap on the TOTAL deduction for a market when two or
    /// more runners are withdrawn before coming under starter's orders.
    /// </summary>
    public const double MaximumTotalDeduction = .90;

    // Tattersalls Rule 4(c), expressed as decimal-odds upper bounds.
    // Source: https://www.thejockeyclub.co.uk/globalassets/racecourses/sandown-park/plan-your-day/tatts_rules_each_way_betting_terms_jan_2015.pdf
    private static readonly (double MaximumDecimalOdds, double Deduction)[] Scale =
    [
        (1d + 1d / 9d, .90),
        (1d + 2d / 11d, .85),
        (1.25, .80),
        (1.30, .75),
        (1.40, .70),
        (1d + 8d / 15d, .65),
        (1d + 8d / 13d, .60),
        (1.80, .55),
        (1d + 20d / 21d, .50),
        (2.20, .45),
        (2.50, .40),
        (2.75, .35),
        (3.25, .30),
        (4.00, .25),
        (5.00, .20),
        (6.50, .15),
        (10.00, .10),
        (15.00, .05)
    ];

    public static double FromDecimalOdds(double decimalOdds)
    {
        if (!double.IsFinite(decimalOdds) || decimalOdds <= 1d)
            return 0d;

        foreach (var band in Scale)
        {
            if (decimalOdds <= band.MaximumDecimalOdds + 0.000001)
                return band.Deduction;
        }

        return 0d;
    }

    /// <summary>
    /// Deduction for a market whose book does not total 100% - Top 2/3/4 and the
    /// Insure Bet markets. The Tattersalls scale above is a lookup on WIN
    /// probability and is only valid where the book totals 1.0; applying it to a
    /// price drawn from a 200/300/400% book treats a share of several places as
    /// though it were a share of one, and over-deducts by roughly the number of
    /// places.
    ///
    /// bet365 publish a separate To Place deductions table keyed by the withdrawn
    /// runner's To Place price and the number of places offered
    /// (help.bet365.com/s/en-gb/sportsrules/horse-racing/place-betting). Rather
    /// than hard-code that table, derive the same quantity from the book: the
    /// withdrawn runner's share of it. Removing a runner whose share is s
    /// shortens every survivor by the factor (1 - s), so s is the deduction, and
    /// for a 100% book this reduces to 1/price - the basis of the scale above.
    /// </summary>
    /// <param name="decimalOdds">The withdrawn runner's price in that market at the time of withdrawal.</param>
    /// <param name="marketBook">That market's total book at the time of withdrawal (sum of 1/price over live runners).</param>
    public static double FromMarketBookShare(double decimalOdds, double marketBook)
    {
        if (!double.IsFinite(decimalOdds) || decimalOdds <= 1d) return 0d;
        if (!double.IsFinite(marketBook) || marketBook <= 0d) return 0d;

        var share = 1d / decimalOdds / marketBook;
        if (!double.IsFinite(share) || share <= 0d) return 0d;

        // Rule 4 is quoted in whole pence in the pound.
        return Math.Min(MaximumTotalDeduction, Math.Round(share, 2, MidpointRounding.AwayFromZero));
    }
}
