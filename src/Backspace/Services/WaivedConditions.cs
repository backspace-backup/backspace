using System.Collections.Generic;

namespace Backspace.Services;

/// <summary>
/// The conditions you chose to ignore for a run you started (U226, owner,
/// 13 November 2026): on battery, or over a metered connection, until the
/// run ends. Each is written in the run's log and record, so the run
/// explains itself afterwards.
/// </summary>
internal readonly record struct WaivedConditions(bool Battery, bool Metered)
{
    internal const string BatteryNote = "Started on battery at your request.";
    internal const string MeteredNote = "Started on a metered connection at your request.";

    internal IEnumerable<string> Notes()
    {
        if (Battery) yield return BatteryNote;
        if (Metered) yield return MeteredNote;
    }

    /// <summary>As the background service's worker receives it: "battery,metered".</summary>
    internal string Encode() => (Battery, Metered) switch
    {
        (true, true) => "battery,metered",
        (true, false) => "battery",
        (false, true) => "metered",
        _ => "",
    };

    internal static WaivedConditions Decode(string? text)
    {
        var parts = (text ?? "").Split(',');
        return new(System.Array.IndexOf(parts, "battery") >= 0, System.Array.IndexOf(parts, "metered") >= 0);
    }
}
