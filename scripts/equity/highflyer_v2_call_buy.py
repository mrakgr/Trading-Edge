"""Run 39 — HighFlyerV2 as a BOUGHT CALL instead of the stock (user 2026-11-06: "since puts don't work that well, how would
buying calls do?"). Long volatility + long direction: the right tail kept, the loss capped at the premium.
(Built from highflyer_v2_call_buy.py, Run 38: the same trips, contracts at the same strikes/expiry, calls instead of puts.)
Trips: Run 37's (v2_options.parquet) with a call chain on the previous session — 1,838, the proxy book's 417 among them.
Contracts, from D's call chain (EOD, expiration=*): ATM = the strike nearest the 10:00 price (raw_close_1000); OTM = nearest
1.10 x it (above); both at the first expiry ON OR AFTER the trip's exit date (alive at the exit).
ENTRY  D's 10:00:00 1-minute quote (history/quote, interval 1m): BOUGHT at the ask / ask - 25 % of the spread / the mid.
EXIT A the trip's exit date's 10:00:00 quote: SOLD at the bid / bid + 25 % / the mid (⚠ the stock trip exits at that day's
       OPEN: a 30-minute mismatch).
EXIT B held to EXPIRY: intrinsic against the stock's raw close on the expiration day (or the session before it); the stock
       held to the same close beside it. Trips with a split between D and the expiry are dropped.
Stub filter: an ask above the stock, a spread > 50 % of the mid, or no quote. P&L per share in % of the 10:00 price, and on
the premium paid (ROP).
Stock rows: Run 37's `ret` (10:00 -> the exit day's open) for A, raw close to close for B. PF = plain; the stock also clip PF.
Needs the Theta Terminal running (127.0.0.1:25503).
    cd research && python3 -u scripts/equity/highflyer_v2_call_buy.py > data/equity/highflyer_repro/v2_call_buy.md
writes data/equity/highflyer_repro/v2_call_buy.parquet
"""
import concurrent.futures as cf, json, sys, urllib.error, urllib.parse, urllib.request
import duckdb, numpy as np, pandas as pd
import data_v2

BASE = 'http://127.0.0.1:25503/v3/option'


def log(*a):
    print(*a, file=sys.stderr, flush=True)


def get(path, **q):
    url = f"{BASE}/{path}?" + urllib.parse.urlencode({**q, 'format': 'ndjson'})
    try:
        with urllib.request.urlopen(url, timeout=180) as r:
            return [json.loads(l) for l in r.read().decode().splitlines() if l.strip()]
    except (urllib.error.HTTPError, json.JSONDecodeError):
        return []


T = pd.read_parquet("data/equity/highflyer_repro/v2_options.parquet")
T = T[T.has_chain].reset_index(drop=True)
for c in ("entry_date", "exit_date"):
    T[c] = pd.to_datetime(T[c])
con = data_v2.attach(duckdb.connect(), 'main'); con.execute("SET memory_limit='3GB'")
F = lambda d: d.strftime('%Y%m%d')


def quote10(sym, exp, k, day):
    rows = get('history/quote', symbol=sym, expiration=F(exp), strike=f"{k:.3f}", right='call', start_date=F(day), end_date=F(day), interval='1m')
    q = [r for r in rows if r['timestamp'].endswith('T10:00:00.000')]
    return (q[0]['bid'], q[0]['ask']) if q else (np.nan, np.nan)


def close_on(sym, day):
    v = con.cursor().execute("SELECT close FROM daily_adjusted WHERE ticker = ? AND date <= ? ORDER BY date DESC LIMIT 1", [sym, day.date()]).fetchone()
    return v[0] if v else np.nan


DONE = [0]


def sim(r):
    try:
        o = dict(symbol=r.symbol, entry_date=r.entry_date)
        ch = get('history/eod', symbol=r.symbol, expiration='*', right='call', start_date=F(r.entry_date), end_date=F(r.entry_date))
        if not ch:
            return {**o, 'err': 'no calls on D'}
        e = pd.DataFrame(ch); e['expiration'] = pd.to_datetime(e.expiration)
        later = sorted(x for x in e.expiration.unique() if x >= r.exit_date)
        if not later:
            return {**o, 'err': 'no expiry after the exit'}
        exp = later[0]; ks = np.sort(e[e.expiration == exp].strike.unique()); p = r.raw_close_1000
        ka = float(ks[np.argmin(np.abs(ks - p))]); above = ks[ks > p]
        ko = float(above[np.argmin(np.abs(above - 1.1 * p))]) if len(above) else np.nan
        o.update(expiration=exp, dte=(exp - r.entry_date).days, k_atm=ka, k_otm=ko)
        for lab, k in (('atm', ka), ('otm', ko)):
            if np.isnan(k):
                continue
            o[f'{lab}_b0'], o[f'{lab}_a0'] = quote10(r.symbol, exp, k, r.entry_date)
            o[f'{lab}_b1'], o[f'{lab}_a1'] = quote10(r.symbol, exp, k, r.exit_date)
        o['close_exp'] = close_on(r.symbol, exp)
        return o
    finally:
        DONE[0] += 1
        if DONE[0] % 100 == 0:
            log(f"  {DONE[0]} / {len(T)}")


log(f"{len(T)} trips with a chain")
with cf.ThreadPoolExecutor(4) as ex:
    res = pd.DataFrame(list(ex.map(sim, T.itertuples())))
d = T.merge(res, on=['symbol', 'entry_date'])
d['expiration'] = pd.to_datetime(d.expiration)
con.register('d', d[['symbol', 'entry_date', 'expiration']].dropna())
spl = con.execute("SELECT DISTINCT d.symbol, d.entry_date FROM d JOIN splits s ON s.ticker = d.symbol AND s.execution_date > d.entry_date "
                  "AND s.execution_date <= d.expiration").df()
d['split_to_expiry'] = d.set_index(['symbol', 'entry_date']).index.isin(spl.set_index(['symbol', 'entry_date']).index)
P = d.raw_close_1000
FILL = {'ask': 0.0, 'ask-25%': 0.25, 'mid': 0.5}
for lab in ('atm', 'otm'):
    K = d[f'k_{lab}']; b0, a0, b1, a1 = d[f'{lab}_b0'], d[f'{lab}_a0'], d[f'{lab}_b1'], d[f'{lab}_a1']
    for fl, f in FILL.items():
        buy = a0 - f * (a0 - b0); sell = b1 + f * (a1 - b1)
        d[f'{lab}_A_{fl}'] = (sell - buy) / P * 100
        d[f'{lab}_B_{fl}'] = (np.maximum(0, d.close_exp - K) - buy) / P * 100
        d[f'{lab}_rop_A_{fl}'] = (sell - buy) / buy * 100
        d[f'{lab}_rop_B_{fl}'] = (np.maximum(0, d.close_exp - K) - buy) / buy * 100
    mid0 = (b0 + a0) / 2
    d[f'{lab}_stub'] = b0.isna() | (a0 > P) | ((a0 - b0) / mid0 > 0.5) | b1.isna() | (mid0 <= 0) | (a0 <= 0)
d['stock_A'] = d.ret * 100
d['stock_B'] = (d.close_exp / P - 1) * 100
d.to_parquet("data/equity/highflyer_repro/v2_call_buy.parquet", index=False)


def pf(r, clip=False):
    r = np.asarray(r, float); r = r[np.isfinite(r)]; l = -r[r < 0].sum()
    g = (np.minimum(r, 50.0) if clip else r)[r > 0].sum()
    return np.inf if l == 0 else g / l


def f2(x): return "inf" if np.isinf(x) else f"{x:.2f}"


def line(lab, x):
    x = np.asarray(x, float); x = x[np.isfinite(x)]
    return f"| {lab} | {len(x)} | {(x > 0).mean() * 100:.0f}% | {x.mean():+.2f} | {np.median(x):+.2f} | **{f2(pf(x))}** | {x.min():+.1f} | {np.percentile(x, 5):+.1f} |"


print("# Run 39 — HighFlyerV2 as a BOUGHT CALL (scripts/equity/highflyer_v2_call_buy.py)\n")
errs = d.err.value_counts().to_dict() if 'err' in d else {}
print(f"Trips with a chain: {len(T):,}; no usable contract: {errs}; split before the expiry (dropped): {int(d.split_to_expiry.sum())}; "
      f"median dte {d.dte.median():.0f} days; ATM stub/missing quotes {int(d.atm_stub.sum())}, OTM {int(d.otm_stub.sum())}. "
      "P&L per share, % of the 10:00 price.\n")
for book, sel0 in (("all chain trips", d.index == d.index), ("the proxy book (dv60_med < $2M, CS)", d.proxy)):
    for lab in ('atm', 'otm'):
        u = d[sel0 & ~d[f'{lab}_stub'] & ~d.split_to_expiry]
        print(f"\n## {book} — {'ATM' if lab == 'atm' else '+10 % OTM'} call, clean quotes ({len(u)})\n")
        print(f"premium at the ask: median {(u[f'{lab}_a0'] / u.raw_close_1000 * 100).median():.2f} % of the stock; spread median "
              f"{((u[f'{lab}_a0'] - u[f'{lab}_b0']) / ((u[f'{lab}_a0'] + u[f'{lab}_b0']) / 2) * 100).median():.0f} % of the mid\n")
        print("| structure (same trips) | n | win% | mean % | median % | PF | worst % | p5 % |\n|---|---|---|---|---|---|---|---|")
        print(line(f"stock, 10:00 -> the exit day's open (clip PF {f2(pf(u.stock_A, True))})", u.stock_A))
        for fl in FILL:
            print(line(f"bought call, out on the exit day at 10:00, {fl}", u[f'{lab}_A_{fl}']))
        print(line("stock held to the expiry's close", u.stock_B))
        for fl in FILL:
            print(line(f"bought call held to expiry, {fl}", u[f'{lab}_B_{fl}']))
        r = u[f'{lab}_rop_A_ask-25%']; print(f"\nreturn on the premium (out at the exit, ask - 25 %): mean {r.mean():+.1f} %, median {r.median():+.1f} %, "
              f"lost > 90 % of it in {(r < -90).mean() * 100:.0f} % of trips, doubled in {(r > 100).mean() * 100:.0f} %")
print("\n## by year — ATM call, clean, ask − 25 %: PF (n) — stock to the exit / call out at the exit / call to expiry / stock to expiry\n")
print("| year | all chain trips | proxy book |\n|---|---|---|")
u = d[~d.atm_stub & ~d.split_to_expiry].copy(); u['yr'] = u.entry_date.dt.year
for y, g in u.groupby('yr'):
    cells = []
    for G in (g, g[g.proxy]):
        cells.append(f"{f2(pf(G.stock_A))} / {f2(pf(G['atm_A_ask-25%']))} / {f2(pf(G['atm_B_ask-25%']))} / {f2(pf(G.stock_B))} ({len(G)})" if len(G) else "—")
    print(f"| {y} | {cells[0]} | {cells[1]} |")
print("\n## the payoff by the stock's move to the exit (ATM, clean, ask − 25 %, mean %)\n")
u['move'] = pd.cut(u.stock_A, [-1e9, -20, -10, 0, 10, 20, 1e9], labels=['< −20 %', '−20..−10', '−10..0', '0..+10', '+10..+20', '> +20 %'])
print(u.groupby('move', observed=True)[['stock_A', 'atm_A_ask-25%']].agg(['size', 'mean']).round(2)
      .loc[:, [('stock_A', 'size'), ('stock_A', 'mean'), ('atm_A_ask-25%', 'mean')]].to_string())
