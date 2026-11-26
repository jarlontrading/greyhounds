namespace RacingTrader.Web.Services;

// Mirrored in both traders. Defaults retain the deployed model until approval.
internal sealed class PlacePricingOptions
{
    public PlacePricingParameters Horses { get; set; } = new();
    public PlacePricingParameters Greyhounds { get; set; } = new();

    public PlacePricingParameters ForSport(int sportId) => sportId switch
    {
        2 => Horses,
        4 => Greyhounds,
        _ => throw new ArgumentOutOfRangeException(nameof(sportId))
    };

    public PlacePricingOptions Validate()
    {
        if (Horses is null || Greyhounds is null)
            throw new ArgumentException("Both horse and greyhound place settings are required.");
        Horses.Validate();
        Greyhounds.Validate();
        return this;
    }
}

internal sealed class PlacePricingParameters
{
    public string Model { get; set; } = "Legacy";
    public double SecondExponent { get; set; } = .81;
    public double ThirdExponent { get; set; } = .65;
    // Review parameter, extrapolated in the earlier comparison; not published/fitted.
    public double FourthExponent { get; set; } = .52;

    public bool UsesDiscountedHarville => string.Equals(Model, "DiscountedHarville", StringComparison.OrdinalIgnoreCase);

    public void Validate()
    {
        if (!UsesDiscountedHarville && !string.Equals(Model, "Legacy", StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("Place model must be Legacy or DiscountedHarville.");
        foreach (var exponent in new[] { SecondExponent, ThirdExponent, FourthExponent })
            if (!double.IsFinite(exponent) || exponent <= 0 || exponent > 1)
                throw new ArgumentOutOfRangeException(nameof(exponent), "Place exponents must be finite and in (0, 1].");
    }
}

internal static class DiscountedHarville
{
    // First-place weights stay unchanged. At subsequent positions apply the
    // relevant power to the ORIGINAL win weights, excluding previous finishers.
    // One traversal supplies cumulative top-1/2/3/4 probabilities together.
    internal static double[][] Calculate(IReadOnlyList<double> winWeights, PlacePricingParameters parameters)
    {
        parameters.Validate();
        var count = winWeights.Count;
        var result = Enumerable.Range(0, 5).Select(_ => new double[count]).ToArray();
        if (winWeights.Any(p => !double.IsFinite(p) || p < 0))
            throw new ArgumentException("Win weights must be finite and nonnegative.", nameof(winWeights));
        var maximum = winWeights.DefaultIfEmpty(0).Max();
        if (maximum == 0) return result;
        // Scaling by the largest weight avoids overflow and changes no conditional odds.
        var wins = winWeights.Select(p => p / maximum).ToArray();
        var exponents = new[] { 1d, parameters.SecondExponent, parameters.ThirdExponent, parameters.FourthExponent };
        var weights = exponents.Select(e => wins.Select(p => p == 0 ? 0 : Math.Pow(p, e)).ToArray()).ToArray();
        var positions = Math.Min(4, wins.Count(p => p > 0));
        var used = new bool[count];
        var ranks = Enumerable.Range(0, 4).Select(_ => new double[count]).ToArray();

        void Walk(int position, double path)
        {
            double denominator = 0;
            for (var i = 0; i < count; i++) if (!used[i]) denominator += weights[position][i];
            if (denominator <= 0) return;
            for (var i = 0; i < count; i++)
            {
                if (used[i] || weights[position][i] == 0) continue;
                var probability = path * weights[position][i] / denominator;
                ranks[position][i] += probability;
                if (position + 1 >= positions) continue;
                used[i] = true;
                Walk(position + 1, probability);
                used[i] = false;
            }
        }

        Walk(0, 1);
        for (var place = 1; place <= 4; place++)
            for (var i = 0; i < count; i++)
                result[place][i] = Math.Clamp(result[place - 1][i] + ranks[place - 1][i], 0, 1);
        return result;
    }
}
