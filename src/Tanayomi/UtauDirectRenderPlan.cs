namespace Tanayomi;

// F0 is a finite 5 ms grid in Hz on Plan's final sample timeline. It does not
// decide source voicing: each renderer preserves the raw source's noise/closure.
internal sealed record UtauDirectRenderPlan(UtauDirectPlan Plan, IReadOnlyList<double> F0);
