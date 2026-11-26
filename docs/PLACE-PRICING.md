# Place pricing — approved release

Status: release authorised for both racing and greyhounds on 26 November 2026. Each application selects `DiscountedHarville` for its relevant sport. The code-level fallback remains `Legacy`. Deployment and runtime verification are recorded separately.

## Calculation

The candidate is discounted Harville. Keep the existing Race Winner ladder and its downstream win-probability conversion. First-place weights use those win probabilities. For each later finishing position, raise the original win weights to that position's exponent, exclude runners already selected and normalise over the remaining runners. Sum the joint finishing-order probabilities into cumulative Top 2/3/4 probabilities.

The implementation computes all four position books in one traversal per race build and reuses them for place and insure markets. Scratch/zero-price placeholders receive exactly zero weight. Unrounded Top K probabilities sum to K (or the eligible field size if smaller); the existing final place odds ladder remains in use, so displayed books need not sum exactly to K.

Initial review parameters are second **0.81**, third **0.65**, fourth **0.52**. The first two reproduce the Benter example discussed in the earlier place-model chat. **0.52 is an extrapolated fourth-place review parameter, not a published fitted coefficient. All greyhound parameters are provisional and have not been calibrated to dog results.** Horse and greyhound settings are separate objects.

Sources and evidence: [Benter's report](https://gwern.net/doc/statistics/decision/1994-benter.pdf), and the local chat “Find published place betting models” (`01a1ee58-7ea3-7fb3-89f8-7f9448e8db76`). In that saved six-race/71-runner Kempton comparison, discounted Harville averaged 0.91 probability percentage points from Betfair versus 2.05 for the app's fitted curve; Henery averaged 0.84. This is limited market-agreement evidence, not a prediction/profit guarantee or a greyhound validation.

## Scope

Top 2/3/4 and the place probabilities used by Insure Bet 2/3/4 use the selected place model. Existing market eligibility, greyhound exclusion of fourth-place products, IDs, selection identity, flags, final odds rounding and SP-only suspension behaviour remain intact. Winner, forecast, betting-without, match-bet, index, distance and meeting-market calculation rules remain unchanged.

The old fitted curve remains selectable for rollback and comparison. There is no live toggle endpoint. The approved release selects `DiscountedHarville` for the relevant sport in each application. Set that sport to `Legacy` to revert the model. The example below shows rollback settings.

```json
{
  "RacingTrader": {
    "PlacePricing": {
      "Horses": {
        "Model": "Legacy",
        "SecondExponent": 0.81,
        "ThirdExponent": 0.65,
        "FourthExponent": 0.52
      },
      "Greyhounds": {
        "Model": "Legacy",
        "SecondExponent": 0.81,
        "ThirdExponent": 0.65,
        "FourthExponent": 0.52
      }
    }
  }
}
```

Unknown model names and non-finite/out-of-range exponents fail validation at startup. The standard `/api/status` response will expose the chosen place settings after the approved build is deployed.

## Offline checks

Run `dotnet run --project PlacePricingTests/PlacePricingTests.csproj -c Release` in either repository. These tests open no provider, database or broker connections. Golden probability fixtures were generated independently using a subset-state dynamic programme, rather than the production finishing-order traversal. Checks cover conservation, monotonic cumulative probabilities, scratch exclusion, unit-exponent equivalence with ordinary Harville, symmetry, permutation/scale invariance, extreme favourites, separate sport settings, unchanged legacy defaults, market identities and eligibility, and existing non-place pricing.

The tests also report local timing for all Top 1–4 probability books in 6/12/24-runner fields. Initial warm-JIT measurements were approximately 0.03 / 0.4 / 6 ms per race. These are local observations, not a production throughput guarantee. Existing horse result/Rule 4 and greyhound distance suites are also required. Future release packaging runs the new checks automatically; release actions require explicit user authorisation, subsequently given for both applications.
