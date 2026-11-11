"""The download window for the VPS options pulls (user 2026-11-11).

BLOCKED on a trading day (a weekday that is not a market holiday) from --block-start (07:00 ET: the live te-oms.timer
fires at 07:30 ET, so 08:00 would overlap its start-up) until 16:30 ET — 13:30 ET on an early-close day (13:00 close).
ALLOWED at any hour on weekends and market holidays.
The holidays and early closes are parsed from TradingEdge.Orb/Timezone.fs (the single source; it covers 2016 → 2028 and
fails past 2028 — so does this module).
"""
import datetime as dt
import re
from zoneinfo import ZoneInfo

ET = ZoneInfo('America/New_York')


def _dates(block):
    return {dt.date(int(y), int(m), int(d)) for y, m, d in re.findall(r'DateOnly\((\d{4}),\s*(\d+),\s*(\d+)\)', block)}


class MarketCalendar:
    def __init__(self, timezone_fs, block_start='07:00', block_end='16:30', early_block_end='13:30'):
        src = open(timezone_fs).read()
        ec = src[src.index('let early_closes'):src.index('let holidays')]
        ho = src[src.index('let holidays'):src.index('let isTradingDay')]
        self.early = _dates(ec)
        self.holidays = _dates(ho)
        assert len(self.early) > 20 and len(self.holidays) > 100, (len(self.early), len(self.holidays))
        last = max(self.holidays)
        if dt.date.today() > last:
            raise RuntimeError(f"Timezone.fs holidays end {last}: update them")
        hm = lambda s: dt.time(*map(int, s.split(':')))
        self.bs, self.be, self.ebe = hm(block_start), hm(block_end), hm(early_block_end)

    def is_trading_day(self, d):
        return d.weekday() < 5 and d not in self.holidays

    def block(self, d):
        """(start, end) of day d's blocked interval as aware ET datetimes, or None (weekend / holiday)."""
        if not self.is_trading_day(d):
            return None
        end = self.ebe if d in self.early else self.be
        return dt.datetime.combine(d, self.bs, ET), dt.datetime.combine(d, end, ET)

    def blocked_until(self, now):
        """If `now` (aware) is inside a blocked interval -> its end; else None."""
        b = self.block(now.astimezone(ET).date())
        return b[1] if b and b[0] <= now < b[1] else None

    def next_block_start(self, now):
        """The start of the next blocked interval at or after `now`."""
        d = now.astimezone(ET).date()
        for i in range(30):
            b = self.block(d + dt.timedelta(days=i))
            if b and b[1] > now:
                return max(b[0], now) if b[0] <= now else b[0]
        raise RuntimeError('no trading day within 30 days?')


if __name__ == '__main__':                            # a quick self-check: python3 market_calendar.py Timezone.fs
    import sys
    c = MarketCalendar(sys.argv[1])
    now = dt.datetime.now(ET)
    print(f"{len(c.holidays)} holidays, {len(c.early)} early closes; now {now:%Y-%m-%d %H:%M %Z}; "
          f"blocked until {c.blocked_until(now)}; next block {c.next_block_start(now)}")
    for s in ['2026-11-26 10:00', '2026-11-27 12:00', '2026-11-27 13:45', '2026-11-28 10:00', '2026-11-30 07:30',
              '2026-11-30 16:29', '2026-11-30 16:31', '2026-12-24 13:00']:
        t = dt.datetime.fromisoformat(s).replace(tzinfo=ET)
        print(f"  {s} ET  blocked_until={c.blocked_until(t)}")
