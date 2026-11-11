using System;
using Tools;

namespace Data;

// What a contract pays in. The value goes into every instrument header, so it is part of the shared-memory contract:
// rename freely, never renumber. USD is 0 so an unstamped header reads dollars, which is what every header held before
// currencies existed, and so what an unported CmeServer keeps producing (see Spec.md).
[RegisterJson]
public enum Currency : byte
{
    USD = 0,
    USc = 1,   // US cents: the grains quote in them, so the rate is a flat 0.01 and never an FX rate
    JPY = 2,
    EUR = 3,
    CNH = 4,
    SEK = 5,
    NOK = 6,
}

// One day's dollars for one unit of a currency, a line in <server>/ExchangeRates/<currency>.exchangerate.
[RegisterJson]
public struct ExchangeRate()
{
    public Currency Currency = Currency.USD;
    public Timestamp Timestamp = Timestamp.MinValue;
    public double UsdRate = 0;

    public override string ToString() => Json.SerializeToLine(this);
}

// The Currency enum's shared-memory side: how many rate rows there are, and how a catalog string and a file become one.
public static class Currencies
{
    // The rate array's size, not the enum's count: a currency added later must not change the Mirror's layout hash and
    // make every desk reinstall the same day (see Spec.md).
    public const int s_capacity = 16;

    // The catalog's Currency string, which is the enum's own name. Throws on an unknown one: falling back to USD would
    // misprice a whole product silently. Startup only.
    public static Currency FromString(string currency) =>
        Enum.TryParse(currency, out Currency parsedCurrency)
            ? parsedCurrency
            : throw new ArgumentOutOfRangeException(nameof(currency), $"Currencies.FromString: '{currency}' is not a known currency; add it to the Currency enum and give it an .exchangerate file");
}
