using System;
using System.Collections.Generic;

namespace AutoBlinkCaravenSpeed;

/// <summary>Pure integer/eligibility policy, kept free of game state so it can be tested directly.</summary>
public static class Policy
{
    /// <summary>Travel-time multiplier applied when every walking member carries an AutoBlink source.</summary>
    public const double Multiplier = 0.001;

    /// <summary>
    /// True only when at least one member walks and every walker is <paramref name="capable"/>.
    /// Carried passengers are excluded because they do not use the caravan's movement speed.
    /// An all-carried/empty group never grants the bonus (nothing walks).
    /// </summary>
    public static bool Eligible<T>(IEnumerable<T> pawns, Func<T, bool> carried, Func<T, bool> capable)
    {
        if (pawns == null || carried == null || capable == null) return false;
        bool any = false;
        foreach (T pawn in pawns)
        {
            if (carried(pawn)) continue;
            any = true;
            if (!capable(pawn)) return false;
        }
        return any;
    }

    /// <summary>
    /// 1000x faster, rounded to whole ticks, never below one tick.
    /// Non-positive input is treated as one tick; this mirror only ever returns
    /// 1..(ticks) for ticks &gt;= 1, so it can never make travel slower.
    /// </summary>
    public static int Scale(int ticks)
    {
        if (ticks <= 1) return 1;
        int scaled = (int)Math.Round(ticks * Multiplier, MidpointRounding.AwayFromZero);
        if (scaled < 1) return 1;
        return scaled > ticks ? ticks : scaled;
    }
}