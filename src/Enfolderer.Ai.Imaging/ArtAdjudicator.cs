namespace Enfolderer.Ai.Imaging;

/// <summary>What comparing the photograph against a candidate's catalogue image showed.</summary>
public enum ArtVerdict
{
    /// <summary>
    /// No comparison was made: no candidate images were available, none could be fetched, or the
    /// photograph itself could not be hashed. The identification is untouched and unendorsed.
    /// </summary>
    NotChecked,

    /// <summary>The printing that was identified is the one the photograph looks like.</summary>
    Agrees,

    /// <summary>
    /// A different printing of the same card looks markedly more like the photograph, and the
    /// card has been moved to it.
    /// </summary>
    Moved,

    /// <summary>
    /// The candidates could not be told apart by their art. This is the ordinary outcome for a
    /// plain reprint and is not a failure; see <see cref="ArtComparison"/>.
    /// </summary>
    Inconclusive
}

/// <summary>One candidate printing and how far its catalogue image is from the photograph.</summary>
/// <param name="Set">Set code of the candidate printing.</param>
/// <param name="CollectorNumber">Collector number of the candidate printing.</param>
/// <param name="Distance">
/// Normalised hash distance, 0 (identical) to about 0.5 (unrelated pictures).
/// </param>
public sealed record ArtCandidate(string Set, string CollectorNumber, double Distance);

/// <summary>
/// Decides which of a card's printings a photograph actually shows, from the distance between the
/// photograph and each candidate's catalogue image.
/// <para>
/// The decision is deliberately conservative, and the case it most needs to get right is the one
/// where it must say nothing. Most reprints share their illustration: a card printed in four sets
/// with the same picture gives four candidates whose images are nearly identical, and whichever
/// comes out a few bits ahead is ahead by noise. Picking it would be worse than useless — it would
/// overwrite a set code that was read off the card with one chosen by a coin toss, while
/// presenting the result as evidence.
/// </para>
/// <para>
/// So a candidate has to win on two counts, not one. It must be close to the photograph in
/// absolute terms (<see cref="MaxAgreeingDistance"/>), which rules out a field where every
/// candidate is wrong — the card was misidentified outright, or the crop is of something else.
/// And it must be clear of its nearest rival (<see cref="MinSeparation"/>), which rules out the
/// shared-art field, where being closest means nothing. Failing either gives
/// <see cref="ArtVerdict.Inconclusive"/>, which changes nothing and claims nothing.
/// </para>
/// </summary>
public static class ArtAdjudicator
{
    /// <summary>
    /// How close a candidate's image must be to the photograph before it is treated as the same
    /// picture at all.
    /// <para>
    /// Generous, because the two images are never alike as files: one is a catalogue scan, the
    /// other is a photograph through a sleeve at an angle, rectified from a quadrilateral whose
    /// corners were measured to within a few pixels. A matching pair typically lands well below
    /// this; two unrelated pictures sit near 0.5 and cannot approach it.
    /// </para>
    /// </summary>
    public const double MaxAgreeingDistance = 0.30;

    /// <summary>
    /// How much nearer the winner must be than the runner-up before the gap counts as evidence.
    /// <para>
    /// This is the threshold that protects the shared-art case, and it is the reason this code is
    /// safe to run on every card. Printings of one illustration land within a bit or two of each
    /// other, far inside this; a genuinely different illustration is a different picture and lands
    /// a long way outside it.
    /// </para>
    /// </summary>
    public const double MinSeparation = 0.06;

    /// <summary>
    /// Picks the printing the photograph shows, or declines.
    /// </summary>
    /// <param name="identified">
    /// The printing the identification settled on, as <c>set</c> and <c>collectorNumber</c>, or
    /// null when the comparison is being made without one.
    /// </param>
    /// <param name="candidates">Every printing considered, with its distance from the photograph.</param>
    public static ArtComparison Adjudicate(
        (string Set, string CollectorNumber)? identified,
        IReadOnlyList<ArtCandidate> candidates)
    {
        ArgumentNullException.ThrowIfNull(candidates);

        if (candidates.Count == 0) return ArtComparison.NotChecked;

        var ranked = candidates.OrderBy(c => c.Distance).ToList();
        var best = ranked[0];

        // Nothing here looks like the photograph. That is not a printing question — it says the
        // card was named wrongly, or the crop is not of this card — and it is not this code's
        // place to answer it, so it declines rather than moving the card to the least bad of a bad
        // field.
        if (best.Distance > MaxAgreeingDistance)
            return new ArtComparison(ArtVerdict.Inconclusive, best, Separation(ranked), ranked);

        var separation = Separation(ranked);

        // One candidate only: there is nothing to separate it from, so closeness is the whole
        // test. A single printing is also the case where moving is impossible anyway.
        if (ranked.Count == 1)
            return new ArtComparison(ArtVerdict.Agrees, best, separation, ranked);

        if (separation < MinSeparation)
            return new ArtComparison(ArtVerdict.Inconclusive, best, separation, ranked);

        var agrees = identified is not null
                  && string.Equals(identified.Value.Set, best.Set, StringComparison.OrdinalIgnoreCase)
                  && string.Equals(identified.Value.CollectorNumber, best.CollectorNumber, StringComparison.OrdinalIgnoreCase);

        return new ArtComparison(agrees ? ArtVerdict.Agrees : ArtVerdict.Moved, best, separation, ranked);
    }

    /// <summary>Gap between the closest candidate and the next closest; 0 when there is only one.</summary>
    private static double Separation(IReadOnlyList<ArtCandidate> ranked)
        => ranked.Count < 2 ? 0d : ranked[1].Distance - ranked[0].Distance;
}

/// <summary>
/// The outcome of comparing a photograph against a card's candidate printings.
/// </summary>
/// <param name="Verdict">What the comparison showed.</param>
/// <param name="Best">The closest candidate, or null when nothing was compared.</param>
/// <param name="Separation">
/// How far the closest candidate is ahead of the next. This is the number worth reporting, more
/// than the distance: "closest by a wide margin" and "closest by a hair" are different claims, and
/// only the first is evidence.
/// </param>
/// <param name="Ranked">Every candidate, nearest first.</param>
public sealed record ArtComparison(
    ArtVerdict Verdict,
    ArtCandidate? Best,
    double Separation,
    IReadOnlyList<ArtCandidate> Ranked)
{
    public static readonly ArtComparison NotChecked =
        new(ArtVerdict.NotChecked, null, 0d, []);
}
