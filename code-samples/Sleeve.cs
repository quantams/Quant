namespace Quant.Core;

/// <summary>
/// 한 계좌를 여러 전략이 같이 쓸 때 **한 전략의 몫**(sleeve) — 넣은 원금, 이 몫의 현금, 이 몫이 가진 자산.
///
/// 왜 필요한가: 전략들이 "지갑 잔고 = 내 돈" 으로 계산하면 같은 지갑의 남의 돈을 제 것으로 센다.
/// 2026-09-28 데모에서 델타 중립용으로 넣은 현물 $5,000 를 10분 뒤 추세추종이 BTC 로 사 버렸고,
/// 델타 중립은 그 뒤로 한 번도 진입하지 못했다. 서브계정이 없는 데모에서는 거래소 대신 이 장부가
/// 몫을 나눈다 — 전략은 지갑이 아니라 자기 몫만 보고, 몫은 **자기 체결로만** 움직인다.
///
/// 실전에서 전략 묶음마다 서브계정을 쓰면 지갑 전체가 곧 그 몫이라 장부가 필요 없다(쓰지 않으면 예전과 같다).
/// </summary>
public sealed class Sleeve
{
    public string Id { get; set; } = "";

    /// <summary>어느 지갑의 몫인가 — 현물(spot) 또는 USDⓈ-M 선물(usdm). 두 지갑을 다 쓰는 전략(델타 중립)은 몫이 둘이다.</summary>
    public string Wallet { get; set; } = "spot";

    /// <summary>현금 자산(USDT). 몫의 현금은 이 자산으로만 센다.</summary>
    public string Quote { get; set; } = "USDT";

    /// <summary>넣은 원금 누계(USDT). 수익률의 분모다 — 평가액이 아니다.</summary>
    public double Capital { get; set; }

    /// <summary>
    /// 이 몫의 현금. **음수가 될 수 있다** — 몫보다 더 써서 남의 돈을 빌린 상태이고, 전략은 그만큼 팔아 갚는다
    /// (2026-09-28 에 추세추종이 쓴 델타 중립의 돈이 이렇게 적힌다).
    /// </summary>
    public double Cash { get; set; }

    /// <summary>
    /// 이 몫이 가진 자산 수량(자산 → 수량). 수수료를 BNB 로 내면 그 자산이 **음수**로 남는다 —
    /// 계좌의 BNB 를 빌려 수수료를 낸 것이고, 평가액에서 그만큼 빠진다.
    /// </summary>
    public Dictionary<string, double> Holdings { get; set; } = new(StringComparer.Ordinal);

    public DateTimeOffset SeededAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }

    /// <summary>
    /// 결과를 아직 장부에 못 적은 주문. 있으면 **새 주문을 내지 않는다** — 체결됐는지 모르는 채 또 사면
    /// 몫이 조용히 틀어진다. 다음 판단 때 거래소에 물어 정리한다.
    /// </summary>
    public SleevePending? Pending { get; set; }

    /// <summary>최근 장부 기록(감사용). 오래된 것부터 버린다.</summary>
    public List<SleeveEntry> History { get; set; } = new();

    /// <summary>
    /// 선물 몫: 손익 내역을 여기까지 읽어 적었다. 선물은 체결마다 장부를 적는 대신 **거래소 손익 내역**(실현손익·수수료·펀딩)을
    /// 종목으로 갈라 적는다 — 지갑이 실제로 움직인 기록이라 체결 회계와 독립인 제3자다.
    /// </summary>
    public DateTimeOffset? IncomeSyncedUntil { get; set; }

    /// <summary>이미 적은 손익 내역 번호 → 시각. 겹쳐 받은 줄을 두 번 적지 않는다(오래된 것은 버린다).</summary>
    public Dictionary<string, DateTimeOffset> SeenIncome { get; set; } = new(StringComparer.Ordinal);

    /// <summary>
    /// 선물 몫: **종목을 가진 기간**. 종목이 날마다 바뀌는 전략(델타 중립)은 설정의 고정 종목 대신 이것으로 손익 내역을 가른다 —
    /// 그 종목을 들고 있던 동안의 줄만 이 몫이다. 닫은 종목을 나중에 다른 봇이 잡아도 그 봇의 손익이 여기로 새지 않는다.
    /// 고정 종목으로 도는 몫(V7)은 비어 있다.
    /// </summary>
    public List<SleeveSpan> Spans { get; set; } = new();

    public double Holding(string asset) => Holdings.TryGetValue(asset, out double q) ? q : 0;

    /// <summary>
    /// 몫의 평가액 = 현금 + 자산 × 가격. 가격을 모르는 자산이 있으면 **예외다** — 그 자산을 0 으로 치면
    /// 평가액이 조용히 틀린다.
    /// </summary>
    public double Equity(Func<string, double?> priceOf)
    {
        double total = Cash;
        foreach (var (asset, qty) in Holdings)
        {
            if (Math.Abs(qty) < 1e-15) continue;
            if (priceOf(asset) is not { } px || !(px > 0) || !double.IsFinite(px))
                throw new InvalidOperationException($"몫 '{Id}' 의 {asset} 가격을 몰라 평가액을 셀 수 없다");
            total += qty * px;
        }
        return total;
    }
}

/// <summary>결과를 아직 장부에 못 적은 주문 — clientOrderId 로 거래소에 되물을 수 있다.</summary>
public sealed class SleevePending
{
    public string Symbol { get; set; } = "";
    public OrderSide Side { get; set; }
    public double Amount { get; set; }
    public string ClientOrderId { get; set; } = "";

    /// <summary>응답을 받았으면 거래소 주문 ID. 응답 전에 끊겼으면 null(clientOrderId 로 찾는다).</summary>
    public string? OrderId { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
}

/// <summary>선물 몫이 한 종목을 가진 기간. <see cref="Until"/> 이 null 이면 아직 가지고 있다.</summary>
public sealed class SleeveSpan
{
    public string Symbol { get; set; } = "";
    public DateTimeOffset From { get; set; }
    public DateTimeOffset? Until { get; set; }
}

/// <summary>장부 기록 한 줄. 무엇이 몫을 얼마나 움직였는지 사람이 거꾸로 따라갈 수 있게 남긴다.</summary>
public sealed class SleeveEntry
{
    public DateTimeOffset Time { get; set; }

    /// <summary>
    /// seed(시작) · contribute(원금 추가·회수) · fill(체결) · clear(체결 없이 정리) · income(선물 손익 내역) ·
    /// transfer(같은 전략의 두 지갑 몫 사이 이체) · span(선물 종목을 갖기 시작함·놓음).
    /// </summary>
    public string Kind { get; set; } = "";
    public string? Symbol { get; set; }
    public string? OrderId { get; set; }
    public double CashDelta { get; set; }
    public Dictionary<string, double> AssetDelta { get; set; } = new(StringComparer.Ordinal);
    public string Note { get; set; } = "";
}

/// <summary>한 봇 runtime 의 몫 장부 파일 전체(sleeves.&lt;모드&gt;.json).</summary>
public sealed class SleeveBook
{
    public Dictionary<string, Sleeve> Sleeves { get; set; } = new(StringComparer.Ordinal);
}

/// <summary>현물 체결 조각 하나(`/api/v3/myTrades`). 수수료 자산은 조각마다 다를 수 있다(BNB).</summary>
public sealed record SpotTradeFill(double Quantity, double QuoteQuantity, double Commission, string CommissionAsset);

/// <summary>
/// 현물 주문 하나를 거래소에 되물은 결과. <paramref name="Found"/>=false 면 거래소가 그 주문을 모른다 —
/// 나가지 않은 주문이다. <paramref name="Final"/>=false 면 아직 끝나지 않았다(체결 합계를 확정할 수 없다).
/// </summary>
public sealed record SpotOrderReport(bool Found, bool Final, string? OrderId, IReadOnlyList<SpotTradeFill> Fills)
{
    public static readonly SpotOrderReport Missing = new(false, true, null, Array.Empty<SpotTradeFill>());
}

/// <summary>
/// 몫 장부 계산 — 순수 규칙. 체결 한 건이 현금·자산을 어떻게 움직이는지만 정한다(파일·거래소는 모른다).
/// </summary>
public static class SleeveBooking
{
    /// <summary>기록은 이만큼만 남긴다 — 파일이 끝없이 커지지 않게.</summary>
    public const int HistoryLimit = 200;

    /// <summary>
    /// 체결을 적는다. 매수는 자산 +수량 · 현금 −대금, 매도는 반대. 수수료는 **낸 자산에서** 뺀다 —
    /// 매수 수수료는 보통 산 코인에서 빠지고(BTC), 매도는 USDT, BNB 차감이면 BNB 가 음수로 남는다.
    /// </summary>
    public static void ApplySpotFills(Sleeve s, OrderSide side, string baseAsset, IReadOnlyList<SpotTradeFill> fills,
                                      string? orderId, string symbol, DateTimeOffset at)
    {
        var entry = new SleeveEntry { Time = at, Kind = "fill", Symbol = symbol, OrderId = orderId, Note = side.Wire() };
        foreach (SpotTradeFill f in fills)
        {
            if (!(f.Quantity >= 0) || !(f.QuoteQuantity >= 0) || !(f.Commission >= 0))
                throw new ArgumentException($"체결 조각 값이 이상하다: {f}");
            double sign = side == OrderSide.Buy ? 1 : -1;
            Move(s, entry, baseAsset, sign * f.Quantity);
            Move(s, entry, s.Quote, -sign * f.QuoteQuantity);
            if (f.Commission > 0) Move(s, entry, f.CommissionAsset, -f.Commission);
        }
        Record(s, entry);
    }

    /// <summary>
    /// 선물 손익 내역을 적는다 — 이 몫의 종목(<paramref name="symbols"/>)만, 몫을 시작한 뒤의 것만, 처음 보는 번호만.
    /// USDT 는 현금으로, 다른 자산(BNB 수수료 등)은 그 자산 수량으로. 적은 줄 수를 돌려준다(0 이면 기록을 안 남긴다).
    /// </summary>
    public static int ApplyIncome(Sleeve s, IEnumerable<IncomeEntry> entries, IReadOnlySet<string> symbols,
                                  DateTimeOffset syncedUntil, DateTimeOffset at)
        => ApplyIncome(s, entries, e => symbols.Contains(e.Symbol), syncedUntil, at);

    /// <summary>
    /// 선물 손익 내역을 적는다 — <paramref name="owns"/> 가 이 몫이라고 하는 줄만(고정 종목이면 종목으로, 기간으로 도는 몫이면
    /// <see cref="Owns"/> 로). 종목 없는 줄(지갑 이체)은 언제나 뺀다 — 이체는 부르는 쪽이 <see cref="Transfer"/> 로 적는다.
    /// </summary>
    public static int ApplyIncome(Sleeve s, IEnumerable<IncomeEntry> entries, Func<IncomeEntry, bool> owns,
                                  DateTimeOffset syncedUntil, DateTimeOffset at)
    {
        var entry = new SleeveEntry { Time = at, Kind = "income" };
        var byType = new SortedDictionary<string, double>(StringComparer.Ordinal);
        int applied = 0;
        foreach (IncomeEntry e in entries.OrderBy(e => e.Time))
        {
            if (e.Symbol.Length == 0 || !owns(e)) continue;   // 남의 종목·이체는 이 몫이 아니다
            if (e.Time < s.SeededAt || s.SeenIncome.ContainsKey(e.TranId)) continue;
            if (!double.IsFinite(e.Amount)) throw new ArgumentException($"손익 내역 금액이 이상하다: {e}");
            Move(s, entry, e.Asset, e.Amount);
            s.SeenIncome[e.TranId] = e.Time;
            byType[e.Type] = (byType.TryGetValue(e.Type, out double t) ? t : 0) + e.Amount;
            applied++;
        }
        s.IncomeSyncedUntil = syncedUntil;
        // 겹쳐 받는 창(1시간)보다 충분히 오래된 번호·닫힌 기간은 버린다 — 파일이 끝없이 커지지 않게.
        DateTimeOffset stale = syncedUntil - TimeSpan.FromDays(2);
        foreach (string id in s.SeenIncome.Where(kv => kv.Value < stale).Select(kv => kv.Key).ToList())
            s.SeenIncome.Remove(id);
        s.Spans.RemoveAll(p => p.Until is { } u && u < stale);
        if (applied > 0)
        {
            entry.Note = $"{applied}줄 — " + string.Join(" · ", byType.Select(kv => $"{kv.Key} {kv.Value:+0.####;-0.####}"));
            Record(s, entry);
        }
        return applied;
    }

    /// <summary>
    /// 기간 경계에 두는 여유. 손익 내역 시각은 거래소 시계, 기간은 봇 시계로 적는다. 종목 주인 목록이 한 종목을 한 봇에만
    /// 주므로 경계 1분 안에 남의 줄이 끼어들 일은 없다.
    /// </summary>
    public static readonly TimeSpan SpanSkew = TimeSpan.FromMinutes(1);

    /// <summary>이 몫이 <paramref name="symbol"/> 을 <paramref name="time"/> 에 가지고 있었나(기간으로 도는 선물 몫).</summary>
    public static bool Owns(Sleeve s, string symbol, DateTimeOffset time)
        => s.Spans.Any(p => p.Symbol == symbol && time >= p.From - SpanSkew && (p.Until is not { } u || time <= u + SpanSkew));

    /// <summary>지금 가지고 있는 종목(열린 기간).</summary>
    public static IReadOnlySet<string> OpenSymbols(Sleeve s)
        => s.Spans.Where(p => p.Until is null).Select(p => p.Symbol).ToHashSet(StringComparer.Ordinal);

    /// <summary>
    /// 종목을 갖기 시작한다. 이미 열려 있으면 아무 일도 없다(거짓). 시작 시각은 <paramref name="from"/> — 주문 **전**의 시각이어야
    /// 첫 체결의 수수료부터 이 몫이 된다.
    /// </summary>
    public static bool OpenSpan(Sleeve s, string symbol, DateTimeOffset from, DateTimeOffset at, string note)
    {
        if (s.Spans.Any(p => p.Symbol == symbol && p.Until is null)) return false;
        s.Spans.Add(new SleeveSpan { Symbol = symbol, From = from });
        Record(s, new SleeveEntry { Time = at, Kind = "span", Symbol = symbol, Note = $"가짐 — {note}" });
        return true;
    }

    /// <summary>종목을 놓는다. 열린 기간이 없으면 아무 일도 없다(거짓). 닫힌 기간은 손익 내역을 다 받은 뒤(이틀) 버린다.</summary>
    public static bool CloseSpan(Sleeve s, string symbol, DateTimeOffset at, string note)
    {
        SleeveSpan? open = s.Spans.FirstOrDefault(p => p.Symbol == symbol && p.Until is null);
        if (open is null) return false;
        open.Until = at;
        Record(s, new SleeveEntry { Time = at, Kind = "span", Symbol = symbol, Note = $"놓음 — {note}" });
        return true;
    }

    /// <summary>
    /// 한 전략의 두 지갑 몫 사이로 현금을 옮긴다 — 거래소 이체가 **된 뒤에**. 선물 손익 내역의 이체 줄은 종목이 없어 몫이 세지 않으므로
    /// 여기서 적는다(안 적으면 옮긴 돈이 한쪽 몫에서 사라지고 다른 지갑의 '몫 밖' 에 생긴다).
    /// 원금은 그대로 둔다 — 원금은 밖에서 넣은 돈이다. 이런 전략의 수익률은 두 몫을 합쳐서 본다.
    /// </summary>
    public static void Transfer(Sleeve from, Sleeve to, double amount, DateTimeOffset at, string note)
    {
        if (!double.IsFinite(amount) || !(amount > 0)) throw new ArgumentException($"이체 금액이 이상하다: {amount}");
        if (from.Quote != to.Quote) throw new ArgumentException($"현금 자산이 다른 몫끼리는 옮길 수 없다: {from.Quote} → {to.Quote}");
        from.Cash -= amount;
        to.Cash += amount;
        Record(from, new SleeveEntry { Time = at, Kind = "transfer", CashDelta = -amount, Note = $"→ {to.Id} {note}".TrimEnd() });
        Record(to, new SleeveEntry { Time = at, Kind = "transfer", CashDelta = amount, Note = $"← {from.Id} {note}".TrimEnd() });
    }

    /// <summary>원금을 넣거나(+) 뺀다(−). 현금과 원금이 같이 움직인다 — 수익률이 입출금에 흔들리지 않게.</summary>
    public static void Contribute(Sleeve s, double amount, DateTimeOffset at, string note)
    {
        if (!double.IsFinite(amount) || amount == 0) throw new ArgumentException($"원금 변동이 이상하다: {amount}");
        s.Capital += amount;
        s.Cash += amount;
        Record(s, new SleeveEntry { Time = at, Kind = "contribute", CashDelta = amount, Note = note });
    }

    public static void Record(Sleeve s, SleeveEntry entry)
    {
        s.History.Add(entry);
        if (s.History.Count > HistoryLimit) s.History.RemoveRange(0, s.History.Count - HistoryLimit);
        s.UpdatedAt = entry.Time;
    }

    static void Move(Sleeve s, SleeveEntry entry, string asset, double delta)
    {
        if (delta == 0) return;
        if (asset == s.Quote)
        {
            s.Cash += delta;
            entry.CashDelta += delta;
            return;
        }
        s.Holdings[asset] = s.Holding(asset) + delta;
        entry.AssetDelta[asset] = (entry.AssetDelta.TryGetValue(asset, out double d) ? d : 0) + delta;
    }
}
