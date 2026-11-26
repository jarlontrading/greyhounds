# Place pricing release — 26 November 2026

Released discounted Harville for greyhounds following explicit approval to release both traders. Source `683710d329f4e69d060084f8327587ef2ae2c115`; [successful CI run 45857695667](https://github.com/jarlontrading/greyhounds/actions/runs/45857695667). Verified CI application DLL/PDB checksums against the installed files.

Runtime status confirms `DiscountedHarville` for the relevant sport, with exponents 0.81 / 0.65 / 0.52. Only that sport's `RacingTrader.PlacePricing` configuration subtree changed; original settings were backed up with Windows DPAPI. Code defaults remain Legacy for rollback. The fourth-place exponent is extrapolated and dog parameters remain provisional.

PROD and UAT publishing are enabled. Betfred browser pricing and the separate OFF service reconnected without reported errors. Cancellation monitoring/delivery also reports no errors or pending work. Active-card repricing was requested after restart (24 horse events and 15 greyhound events queued).

Verified priced winner/place grids on Dunstall Park 16:41. The new model changes enabled Top 2/3/4 calculations and the place probabilities used by insure bets. Existing win processing, other pricing models, settlement rules and market eligibility remain unchanged; greyhounds still do not offer Top 4.

Validation before release: 1,676 racing and 1,670 greyhound place-pricing checks, 77 racing result tests, 88 racing arithmetic/consistency tests, and 3,815 greyhound distance checks. Release CI additionally passed its isolated SQL cancellation/recovery job. Local all-position calculation timing was approximately 0.03 ms for six runners and 6 ms for 24 runners.

Binaries, encrypted configuration backup, deployment receipt and live verification are retained in `D:\TraderReleases\places-20261126-greyhounds-683710d`. [Calculation and rollback settings](PLACE-PRICING.md).
