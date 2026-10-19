package com.wirelessscanner.core.analytics

import java.util.Locale

/** Invariant number formatting matching .NET "F<n>" / "0.0" on the C# side (Locale.ROOT, half away from zero on the exact value). */
internal object Fmt {
    fun f(v: Double?, decimals: Int): String = if (v == null) "" else String.format(Locale.ROOT, "%.${decimals}f", v)
    fun f(v: Int?, decimals: Int): String = f(v?.toDouble(), decimals)
    fun s(v: Any?): String = v?.toString() ?: ""

    /** .NET custom format "0.#…#" (maxDecimals '#'): up to maxDecimals, trailing zeros dropped. */
    fun hash(v: Double?, maxDecimals: Int): String {
        if (v == null) return ""
        val df = java.text.DecimalFormat("0." + "#".repeat(maxDecimals), java.text.DecimalFormatSymbols(Locale.ROOT))
        df.roundingMode = java.math.RoundingMode.HALF_UP
        return df.format(v)
    }
}

internal fun Double.roundTo(decimals: Int): Double {
    // Same algorithm as .NET Math.Round(x, n): scale by 10^n, round half to even, scale back.
    val p = Math.pow(10.0, decimals.toDouble())
    return Math.rint(this * p) / p
}
