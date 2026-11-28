using System;
using System.Collections.Generic;

using ACE.Server.Entity;

namespace ACE.Server.Avatars;

/// <summary>
/// Pure Allegiance Avatar meter and curve math. Every method takes an <see cref="AvatarTuning"/>
/// value and never reads PropertyManager. Unsigned results saturate and never wrap or underflow.
/// </summary>
public static class AvatarCurves
{
    // Absorbs the last-ulp error of products such as 1000 * 2.6 or 100 * 0.9 before flooring.
    private const double FloorEpsilon = 1e-6;

    /// <summary>
    /// Amount of <paramref name="incoming"/> actually credited to an account for the day. Linear up to
    /// the soft cap, then incoming * falloff, and never past soft * hardCapMultiple in total.
    /// </summary>
    public static long CreditAccountDay(long creditedSoFar, long incoming, AvatarTuning t)
    {
        if (incoming <= 0)
            return 0;
        if (creditedSoFar < 0)
            creditedSoFar = 0;

        var soft = Math.Max(0L, t.AccountDailySoftCap);
        var hard = SaturatingToLong(soft * Math.Max(1.0, t.AccountDailyHardCapMultiple));

        var linear = Math.Min(incoming, Math.Max(0L, soft - creditedSoFar));
        var excess = incoming - linear;

        var falloff = Math.Clamp(t.AccountDailyFalloff, 0.0, 1.0);
        var past = SaturatingToLong(Math.Floor(excess * falloff + FloorEpsilon));

        // Room left under the hard cap once the linear part is taken.
        var room = Math.Max(0L, hard - (creditedSoFar + linear));

        return linear + Math.Min(past, room);
    }

    /// <summary>
    /// The effective account count: R up to T = max(minActiveAccounts, thresholdFraction * S), and T + (R - T)^exponent
    /// beyond it. The numerator of <see cref="RosterScale"/>, and what the scaled prices multiply their per-account part by.
    /// A fractional threshold can push the result slightly above R; callers that must not amplify clamp it.
    /// </summary>
    public static double EffectiveAccounts(int allegianceActiveAccounts, int serverActiveAccounts, AvatarTuning t)
    {
        if (allegianceActiveAccounts <= 0)
            return 0.0;

        double r = allegianceActiveAccounts;
        var threshold = Math.Max((double)t.MinActiveAccounts, t.RosterThresholdFraction * Math.Max(0, serverActiveAccounts));
        if (r <= threshold)
            return r;

        return threshold + Math.Pow(r - threshold, t.RosterExcessExponent);
    }

    /// <summary>
    /// effective(R) / R, where effective = R up to T = max(minActiveAccounts, thresholdFraction * S)
    /// and T + (R - T)^exponent beyond it. Returns 1.0 when R is not positive.
    /// </summary>
    public static double RosterScale(int allegianceActiveAccounts, int serverActiveAccounts, AvatarTuning t)
    {
        if (allegianceActiveAccounts <= 0)
            return 1.0;

        var effective = EffectiveAccounts(allegianceActiveAccounts, serverActiveAccounts, t);
        // A fractional threshold can push effective slightly above R; the scale never amplifies.
        return Math.Min(1.0, effective / allegianceActiveAccounts);
    }

    /// <summary>
    /// max(floor, ceil(perAccount * EffectiveAccounts(R, S))), saturating and never negative. A negative floor or
    /// per-account part counts as 0.
    /// </summary>
    public static ulong ScaledPrice(long floor, long perAccount, int allegianceActiveAccounts, int serverActiveAccounts, AvatarTuning t)
    {
        var scaled = Math.Max(0L, perAccount) * EffectiveAccounts(allegianceActiveAccounts, serverActiveAccounts, t);
        var ceiled = SaturatingToUlong(Math.Ceiling(scaled - FloorEpsilon));
        return Math.Max((ulong)Math.Max(0L, floor), ceiled);
    }

    /// <summary>
    /// The per-Avatar Favor cap: max(favorCap, ceil(max(1, priceMultiple) * largestPrice)), saturating. The multiple is floored at 1.0
    /// (NaN, negative or below 1 read as 1.0) so the cap never sits below the largest price. <paramref name="largestPrice"/>
    /// is the largest of the Avatar's scaled prices (see <see cref="AvatarPricing"/>).
    /// </summary>
    public static ulong FavorCapFor(ulong largestPrice, AvatarTuning t)
    {
        var flat = (ulong)Math.Max(0L, t.FavorCap);
        var multiple = double.IsFinite(t.FavorCapPriceMultiple) ? Math.Max(1.0, t.FavorCapPriceMultiple) : 1.0;
        var scaled = SaturatingToUlong(Math.Ceiling(largestPrice * multiple - FloorEpsilon));
        return Math.Max(flat, scaled);
    }

    /// <summary>
    /// Test seam: the cumulative XP chart the Avatar level is read against. Production is the chart players use
    /// (EnlightenmentXpCurve.ExtendedTotals), which needs the client dat.
    /// </summary>
    internal static Func<IReadOnlyList<ulong>> ChartSource = () => EnlightenmentXpCurve.ExtendedTotals;

    internal static void ResetSeams() => ChartSource = () => EnlightenmentXpCurve.ExtendedTotals;

    /// <summary>
    /// The Avatar level for a pool total: the highest level L whose chart total chart[L] the pool reaches, clamped
    /// to [1, chart.Count - 1]. The chart is a cumulative-total list indexed by level (index 0 = 0 XP), the same
    /// shape players use. Throws ArgumentException for a chart too short to clamp into.
    /// </summary>
    public static int LevelForPool(ulong xp, IReadOnlyList<ulong> chart)
    {
        if (chart == null || chart.Count < 3)
            throw new ArgumentException("an XP chart needs at least three entries (levels 0..2)", nameof(chart));

        var lo = 1;
        var hi = chart.Count - 1;

        // Invariant: every level below lo is reached; the answer is in [lo, hi].
        while (lo < hi)
        {
            var mid = lo + (hi - lo + 1) / 2;
            if (chart[mid] <= xp)
                lo = mid;
            else
                hi = mid - 1;
        }

        return lo;
    }

    /// <summary>
    /// max(favor, min(cap, favor + contribution * favorRate)), saturating. <paramref name="cap"/> is the per-Avatar cap (<see cref="FavorCapFor"/>).
    /// The cap stops growth and never destroys Favor: an Avatar above a cap that shrank (its roster fell) keeps what it has, the same
    /// never-lower contract as AvatarManager.RefundFavor.
    /// </summary>
    public static ulong AddFavor(ulong favor, ulong contribution, ulong cap, AvatarTuning t)
    {
        var gained = SaturatingToUlong(Math.Floor(contribution * Math.Max(0.0, t.FavorRate) + FloorEpsilon));
        var total = favor > ulong.MaxValue - gained ? ulong.MaxValue : favor + gained;
        return Math.Max(favor, Math.Min(cap, total));
    }

    /// <summary>
    /// floor(available * (1 - fraction)) when the Avatar is inactive or the grace days since the last
    /// contribution have elapsed (a null last contribution counts as elapsed); otherwise unchanged.
    /// Callers pass Favor minus ReservedFavor, never Favor. The result never exceeds the input.
    /// </summary>
    public static ulong DecayFavor(ulong available, DateTime? lastContributionUtc, bool inactive, DateTime todayUtc, AvatarTuning t)
    {
        var graceElapsed = lastContributionUtc == null
            || (todayUtc.Date - lastContributionUtc.Value.Date).TotalDays >= t.FavorDecayGraceDays;
        if (!inactive && !graceElapsed)
            return available;

        var keep = 1.0 - Math.Clamp(t.FavorDecayDailyFraction, 0.0, 1.0);
        return Math.Min(available, SaturatingToUlong(Math.Floor(available * keep + FloorEpsilon)));
    }

    /// <summary>floor(source * absorbFraction), the XP a retiring Avatar passes to its absorber.</summary>
    public static ulong AbsorbXp(ulong sourceXp, AvatarTuning t)
    {
        var fraction = Math.Clamp(t.AbsorbFraction, 0.0, 1.0);
        return Math.Min(sourceXp, SaturatingToUlong(Math.Floor(sourceXp * fraction + FloorEpsilon)));
    }

    private static ulong SaturatingToUlong(double v)
    {
        if (double.IsNaN(v) || v <= 0)
            return 0;
        return v >= 18446744073709551615.0 ? ulong.MaxValue : (ulong)v;
    }

    private static long SaturatingToLong(double v)
    {
        if (double.IsNaN(v) || v <= 0)
            return 0;
        return v >= 9223372036854775807.0 ? long.MaxValue : (long)v;
    }
}
