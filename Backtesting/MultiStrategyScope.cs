namespace Backtest_for_new_orb;

// Focused is a predeclared RESEARCH SHORTLIST, selected after inspecting
// earlier research, not a clean unbiased live strategy-selection result.
// Full keeps all 19 families for periodic comparisons and validation.
public enum MultiStrategyScope { Focused, Full }

public static class MultiStrategyScopeSelection
{
    private static readonly MultiStrategyKind[] Focused =
    [
        MultiStrategyKind.Orb,
        MultiStrategyKind.MorningVwapReversion,
        MultiStrategyKind.AfternoonMomentum,
        MultiStrategyKind.AfternoonVwapReversion,
        MultiStrategyKind.MorningLiquiditySweep
    ];

    public static IReadOnlyList<MultiStrategyKind> FocusedKinds => Focused;

    public static bool Contains(MultiStrategyKind kind) => kind is
        MultiStrategyKind.Orb or
        MultiStrategyKind.MorningVwapReversion or
        MultiStrategyKind.AfternoonMomentum or
        MultiStrategyKind.AfternoonVwapReversion or
        MultiStrategyKind.MorningLiquiditySweep;

    public static IReadOnlyList<MultiStrategyVariant> Filter(
        IReadOnlyList<MultiStrategyVariant> allVariants, MultiStrategyScope scope)
    {
        if (scope == MultiStrategyScope.Full)
            return allVariants;
        if (scope != MultiStrategyScope.Focused)
            throw new ArgumentOutOfRangeException(nameof(scope));

        // Construct the full enumeration first, then filter: this preserves
        // every existing Deep/Smoke recipe ID and its exit-mode mapping.
        var selected = allVariants.Where(v => Contains(v.Kind)).ToArray();
        var actual = selected.Select(v => v.Kind).Distinct().ToHashSet();
        if (!actual.SetEquals(Focused))
            throw new InvalidOperationException(
                "Focused search grid is missing a selected strategy family.");
        return selected;
    }

    public static MultiStrategyKind[] Kinds(
        IReadOnlyList<MultiStrategyVariant> variants) =>
        variants.Select(v => v.Kind).Distinct().OrderBy(k => k).ToArray();
}
