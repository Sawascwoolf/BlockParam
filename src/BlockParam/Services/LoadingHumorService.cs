using System;
using System.Collections.Generic;

namespace BlockParam.Services;

/// <summary>
/// Picks a single random splash quip key from a finite catalog (#127).
///
/// The catalog holds only <see cref="BlockParam.Localization.Res"/> keys —
/// never the text. The caller (TIA thread) localizes the chosen key and hands
/// the resulting string to <c>LoadingSplashController</c>; the splash render
/// thread never touches <c>Res</c> (the #125 render-only rule).
///
/// Constraints from the issue, encoded here so they can't silently drift:
/// - Catalog is tight (≤15 lines) — every line is a translation cost.
/// - One quip per splash session: callers invoke <see cref="PickKey"/> exactly
///   once when the splash is created, not on a timer.
/// - No line names TIA, Siemens, Openness, SimaticML, or any competitor — the
///   joke is about loading screens in general (Marketplace-published).
/// </summary>
public static class LoadingHumorService
{
    /// <summary>Resource keys for the rotating splash quips. See Strings.resx.</summary>
    public static readonly IReadOnlyList<string> Keys = new[]
    {
        "Quip_EstimatingTime",
        "Quip_AligningUdt",
        "Quip_CountingDbs",
        "Quip_NegotiatingTree",
        "Quip_ReticulatingStartValues",
        "Quip_LongerThanEstimate",
        "Quip_BigProjectBigPatience",
        "Quip_LunchBreak",
    };

    // System.Random is not thread-safe; the lock keeps PickKey callable from
    // any thread (today it's only the single TIA caller thread, but the lock
    // costs nothing and removes a footgun).
    private static readonly Random Rng = new Random();

    /// <summary>
    /// Returns a random key from <see cref="Keys"/>. Call once per splash
    /// session — the returned key stays fixed for the rest of that session.
    /// </summary>
    public static string PickKey()
    {
        lock (Rng)
        {
            return Keys[Rng.Next(Keys.Count)];
        }
    }
}
