using System;

namespace BlockParam.Services;

/// <summary>
/// Recognises TIA Openness "block/UDT is inconsistent" exceptions across locales.
/// Openness throws <c>EngineeringTargetInvocationException</c> for many unrelated
/// failures too (read-only library, create not possible, ...) so type alone is not
/// a reliable signal — we still need to inspect the message. The message itself is
/// localized to the TIA UI language, so a pure English substring match misses
/// non-English installs (#51).
///
/// Strategy: walk the full inner-exception chain and look for any locale-specific
/// "inconsistent" marker. Markers are intentionally narrow (full words, not "consistent")
/// to avoid false positives from messages that mention consistency in passing.
/// </summary>
internal static class InconsistencyDetector
{
    // Add a locale here only after confirming on a real TIA install of that language.
    // The strings are matched case-insensitively, so a single form per language is enough
    // to catch noun/adjective variants ("Inconsistent" / "inconsistent" / "is inconsistent").
    private static readonly string[] Markers =
    {
        "inconsistent",   // English  (e.g. "The block is inconsistent")
        "inkonsistent",   // German   (e.g. "Der Baustein ist inkonsistent")
    };

    public static bool Matches(Exception? ex)
    {
        for (var current = ex; current != null; current = current.InnerException)
        {
            if (ContainsMarker(current.Message)) return true;
        }
        return false;
    }

    private static bool ContainsMarker(string? message)
    {
        if (string.IsNullOrEmpty(message)) return false;
        foreach (var marker in Markers)
        {
            if (message!.IndexOf(marker, StringComparison.OrdinalIgnoreCase) >= 0)
                return true;
        }
        return false;
    }
}
