using System;
using System.Collections.Generic;
using System.Text;
using Data;
using Execution;

public static class ConsoleGraphics
{
    /// <summary>
    /// Renders a ladder-style order book:
    /// [UBuy] [Bids] [Price] [Asks] [USell]
    /// Notes:
    /// - MarketByPrice64 already includes user orders → MBP columns are raw.
    /// - OrderProfile side is derived from sign of Quantity:
    ///     Quantity > 0 => buy (UBuy)
    ///     Quantity < 0 => sell (USell, stored as abs)
    /// </summary>
    public static string RenderOrderBook(MarketByPrice64 book, Span<OrderProfile> orderProfiles)
    {
        // ─────────────────────────────────────────────────────────────
        // 1. Aggregate user orders (by ticks, per side, ABS quantity)
        // ─────────────────────────────────────────────────────────────
        Dictionary<int, int> userBuys = new Dictionary<int, int>();
        Dictionary<int, int> userSells = new Dictionary<int, int>();

        for (int i = 0; i < orderProfiles.Length; i++)
        {
            OrderProfile profile = orderProfiles[i];
            int quantity = profile.Quantity;

            if (quantity == 0)
            {
                continue;
            }

            int ticks = profile.Ticks;

            if (quantity > 0)
            {
                AddToBucket(userBuys, ticks, quantity);
            }
            else // quantity < 0 → sell
            {
                AddToBucket(userSells, ticks, -quantity);
            }
        }

        // ─────────────────────────────────────────────────────────────
        // 2. Extract MBP bids/asks (non-zero levels)
        // ─────────────────────────────────────────────────────────────
        Dictionary<int, int> mbpBids = new Dictionary<int, int>();
        foreach (Level lvl in book.EnumerateBids())
        {
            if (lvl.Quantity > 0)
            {
                mbpBids[lvl.Ticks] = lvl.Quantity;
            }
        }

        Dictionary<int, int> mbpAsks = new Dictionary<int, int>();
        foreach (Level lvl in book.EnumerateAsks())
        {
            if (lvl.Quantity > 0)
            {
                mbpAsks[lvl.Ticks] = lvl.Quantity;
            }
        }

        // ─────────────────────────────────────────────────────────────
        // 3. Collect unique ticks (union of all relevant prices)
        // ─────────────────────────────────────────────────────────────
        HashSet<int> ticksSet = new HashSet<int>();

        AddKeys(ticksSet, userBuys);
        AddKeys(ticksSet, userSells);
        AddKeys(ticksSet, mbpBids);
        AddKeys(ticksSet, mbpAsks);

        if (ticksSet.Count == 0)
        {
            StringBuilder empty = new StringBuilder();
            empty.Append("Exchange: ");
            empty.Append(book.ExchangeTimestamp.ToString());
            empty.Append("    NIC: ");
            empty.Append(book.NicTimestamp.ToString());
            empty.AppendLine();
            empty.Append("[no book / orders]");
            return empty.ToString();
        }

        List<int> allTicks = new List<int>(ticksSet.Count);
        foreach (int t in ticksSet)
        {
            allTicks.Add(t);
        }
        allTicks.Sort();
        allTicks.Reverse(); // highest price at top

        // ─────────────────────────────────────────────────────────────
        // 4. Compute widths
        // ─────────────────────────────────────────────────────────────
        int maxUserBuy = MaxValue(userBuys);
        int maxUserSell = MaxValue(userSells);
        int maxBid = MaxValue(mbpBids);
        int maxAsk = MaxValue(mbpAsks);

        int maxTickAbs = 0;
        for (int i = 0; i < allTicks.Count; i++)
        {
            int v = allTicks[i];
            if (v < 0) v = -v;
            if (v > maxTickAbs) maxTickAbs = v;
        }

        int uBuyWidth = Math.Max("UBuy".Length, DigitCount(maxUserBuy));
        int bidsWidth = Math.Max("Bids".Length, DigitCount(maxBid));
        int priceWidth = Math.Max("Price".Length, DigitCount(maxTickAbs) + 1); // sign room
        int asksWidth = Math.Max("Asks".Length, DigitCount(maxAsk));
        int uSellWidth = Math.Max("USell".Length, DigitCount(maxUserSell));

        int totalWidth =
            uBuyWidth + 1 +
            bidsWidth + 1 +
            priceWidth + 1 +
            asksWidth + 1 +
            uSellWidth;

        // ─────────────────────────────────────────────────────────────
        // 5. Determine best bid / ask for spread separator
        // ─────────────────────────────────────────────────────────────
        bool hasBids = mbpBids.Count > 0;
        bool hasAsks = mbpAsks.Count > 0;

        int bestBid = hasBids ? MaxKey(mbpBids) : int.MinValue;
        int bestAsk = hasAsks ? MinKey(mbpAsks) : int.MaxValue;

        bool shouldInsertSpreadLine = hasBids && hasAsks;
        bool spreadLineInserted = false;

        // ─────────────────────────────────────────────────────────────
        // 6. Build output
        // ─────────────────────────────────────────────────────────────
        StringBuilder sb = new StringBuilder(totalWidth * (allTicks.Count + 4));

        // Header timestamps
        sb.Append("Exchange: ");
        sb.Append(book.ExchangeTimestamp.ToString());
        sb.Append("    NIC: ");
        sb.Append(book.NicTimestamp.ToString());
        sb.AppendLine();

        // Column headers
        AppendRightAligned(sb, "UBuy", uBuyWidth);
        sb.Append(' ');
        AppendRightAligned(sb, "Bids", bidsWidth);
        sb.Append(' ');
        AppendRightAligned(sb, "Price", priceWidth);
        sb.Append(' ');
        AppendRightAligned(sb, "Asks", asksWidth);
        sb.Append(' ');
        AppendRightAligned(sb, "USell", uSellWidth);
        sb.AppendLine();

        // Separator
        for (int i = 0; i < totalWidth; i++)
        {
            sb.Append('-');
        }
        sb.AppendLine();

        for (int i = 0; i < allTicks.Count; i++)
        {
            int ticks = allTicks[i];

            // Insert one blank line between best ask block and best bid block
            if (shouldInsertSpreadLine && !spreadLineInserted && i > 0)
            {
                int prevTicks = allTicks[i - 1];

                // We just crossed from >= bestAsk region into <= bestBid region
                if (prevTicks >= bestAsk && ticks <= bestBid)
                {
                    sb.AppendLine();
                    spreadLineInserted = true;
                }
            }

            int userBuyQty = GetBucket(userBuys, ticks);
            int bidQty = GetBucket(mbpBids, ticks);
            int askQty = GetBucket(mbpAsks, ticks);
            int userSellQty = GetBucket(userSells, ticks);

            if (userBuyQty == 0 && bidQty == 0 && askQty == 0 && userSellQty == 0)
            {
                continue;
            }

            // Column 1: user buys
            AppendRightAligned(sb,
                userBuyQty > 0 ? userBuyQty.ToString() : string.Empty,
                uBuyWidth);
            sb.Append(' ');

            // Column 2: MBP bids
            AppendRightAligned(sb,
                bidQty > 0 ? bidQty.ToString() : string.Empty,
                bidsWidth);
            sb.Append(' ');

            // Column 3: price
            AppendRightAligned(sb, ticks.ToString(), priceWidth);
            sb.Append(' ');

            // Column 4: MBP asks
            AppendRightAligned(sb,
                askQty > 0 ? askQty.ToString() : string.Empty,
                asksWidth);
            sb.Append(' ');

            // Column 5: user sells
            AppendRightAligned(sb,
                userSellQty > 0 ? userSellQty.ToString() : string.Empty,
                uSellWidth);

            sb.AppendLine();
        }

        return sb.ToString();
    }

    // ─────────────────────────────────────────────────────────────
    // Helpers
    // ─────────────────────────────────────────────────────────────

    private static void AddToBucket(Dictionary<int, int> bucket, int ticks, int quantity)
    {
        if (quantity == 0)
        {
            return;
        }

        if (bucket.TryGetValue(ticks, out int existing))
        {
            bucket[ticks] = existing + quantity;
        }
        else
        {
            bucket[ticks] = quantity;
        }
    }

    private static void AddKeys(HashSet<int> dst, Dictionary<int, int> src)
    {
        foreach (KeyValuePair<int, int> kvp in src)
        {
            if (kvp.Value != 0)
            {
                dst.Add(kvp.Key);
            }
        }
    }

    private static int GetBucket(Dictionary<int, int> bucket, int ticks)
    {
        if (bucket.TryGetValue(ticks, out int value))
        {
            return value;
        }

        return 0;
    }

    private static int MaxValue(Dictionary<int, int> dict)
    {
        int max = 0;
        foreach (KeyValuePair<int, int> kvp in dict)
        {
            int v = kvp.Value;
            if (v < 0)
            {
                v = -v;
            }

            if (v > max)
            {
                max = v;
            }
        }
        return max;
    }

    private static int DigitCount(int value)
    {
        if (value == 0)
        {
            return 1;
        }

        if (value < 0)
        {
            value = -value;
        }

        int count = 0;
        while (value > 0)
        {
            value /= 10;
            count++;
        }

        return count;
    }

    private static void AppendRightAligned(StringBuilder sb, string text, int width)
    {
        if (text == null)
        {
            text = string.Empty;
        }

        int pad = width - text.Length;
        for (int i = 0; i < pad; i++)
        {
            sb.Append(' ');
        }

        sb.Append(text);
    }

    private static int MaxKey(Dictionary<int, int> dict)
    {
        int max = int.MinValue;
        foreach (KeyValuePair<int, int> kvp in dict)
        {
            if (kvp.Key > max)
            {
                max = kvp.Key;
            }
        }

        return max;
    }

    private static int MinKey(Dictionary<int, int> dict)
    {
        int min = int.MaxValue;
        foreach (KeyValuePair<int, int> kvp in dict)
        {
            if (kvp.Key < min)
            {
                min = kvp.Key;
            }
        }

        return min;
    }
}
