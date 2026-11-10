#!/usr/bin/env python3
"""Regenerate the checked-in historical U.S. release calendar from official archives.

This script runs for maintenance, NEVER while executing a backtest.
Data is checked into the repository so a replay needs no network.
Source HTML calendars:
  https://www.bls.gov/schedule/YYYY/ (2020..2025)
  https://www.federalreserve.gov/monetarypolicy/fomccalendars.htm
  https://www.bea.gov/news/archive?field_related_product_target_id=721&page=N

Fails rather than fabricating release dates or silently generating a sparse file.
"""
import datetime as dt
import pathlib
import re
import sys
import time
from collections import Counter
from urllib.parse import urlencode

import requests
from bs4 import BeautifulSoup

DEST = pathlib.Path("Backtesting/HistoricalEconomicCalendar.g.cs")
START = dt.date(2020, 1, 1)
END = dt.date(2025, 10, 5)
SESSION = requests.Session()
SESSION.headers.update({"User-Agent": "Mozilla/5.0 (compatible; HistoricalCalendarResearch/1.0)"})
EVENTS = {}
BLS_NAMES = {
    "Employment Situation": ("NFP", "Employment Situation", True, "08:30"),
    "Consumer Price Index": ("CPI", "Consumer Price Index", True, "08:30"),
    "Producer Price Index": ("PPI", "Producer Price Index", True, "08:30"),
    "Job Openings and Labor Turnover Survey": ("JOLTS", "JOLTS job openings", True, "10:00"),
    "Employment Cost Index": ("ECI", "Employment Cost Index", True, "08:30"),
}
def fetch(url):
    errors = []
    for attempt in range(3):
        try:
            r = SESSION.get(url, timeout=35)
            r.raise_for_status()
            if len(r.content) < 1000:
                raise ValueError(f"Unexpectedly short body ({len(r.content)})")
            return BeautifulSoup(r.content, "html.parser")
        except (requests.RequestException, ValueError) as ex:
            errors.append(str(ex))
            time.sleep(attempt+1)
    raise RuntimeError(f"Unable to retrieve {url}: {errors}")

def add(date, code, desc, time_et, source, major=True):
    if date < START or date > END:
        return
    key = (date.isoformat(), code)
    prev = EVENTS.get(key)
    current = (date.isoformat(), time_et, code, desc, source, major)
    if prev and prev != current:
        raise RuntimeError(f"Contradictory release: {prev} vs {current}")
    EVENTS[key] = current

date_pattern = re.compile(r"([A-Za-z]+)\s+0?(\d{1,2}),?\s+(20\d{2})")
def parsedate(value):
    match = date_pattern.search(value)
    if not match:
        return None
    month, day, year = match.groups()
    try:
        return dt.date.strptime(f"{month} {day} {year}", "%B %d %Y")
    except AttributeError:
        return dt.datetime.strptime(f"{month} {day} {year}", "%B %d %Y").date()
    except ValueError:
        return None

def bls():
    for year in range(2020, 2026):
        url = f"https://www.bls.gov/schedule/{year}/"
        try:
            soup = fetch(url)
        except RuntimeError:
            url = f"https://blsmon1.bls.gov/schedule/{year}/"
            soup = fetch(url)
        count = Counter()
        for tr in soup.select("tr"):
            cols = [c.get_text(" ", strip=True) for c in tr.find_all(["th","td"], recursive=False)]
            if len(cols) < 3:
                continue
            date = parsedate(cols[0])
            if not date or date.year != year:
                continue
            title = cols[2]
            for prefix, (code, desc, major, expected) in BLS_NAMES.items():
                if title.startswith(prefix + " for "):
                    time_display = re.search(r"(\d{1,2}):(\d\d)\s*(AM|PM)", cols[1], re.I)
                    if not time_display:
                        raise RuntimeError(f"No recorded release time: {cols}")
                    h, m, period = time_display.groups()
                    hour = int(h) % 12 + (12 if period.upper() == "PM" else 0)
                    tm = f"{hour:02}:{int(m):02}"
                    if tm != expected:
                        raise RuntimeError(f"Unexpected BLS time {tm} on {date} for {code}")
                    add(date, code, desc, tm, url, major)
                    count[code] += 1
                    break
        # Each BLS year should contain roughly 12 monthly releases for the
        # three main indicators; 2025's last scheduled months may be delayed.
        for code in ("CPI", "NFP", "PPI"):
            min_count = 8 if year == 2025 else 11
            if count[code] < min_count:
                raise RuntimeError(f"BLS {year} calendar incomplete for {code}: {count}")
        print(f"BLS {year}: {dict(count)}")

def fed():
    url = "https://www.federalreserve.gov/monetarypolicy/fomccalendars.htm"
    soup = fetch(url)
    meetings = set()
    for anchor in soup.find_all("a", href=True):
        # Official FOMC statement release URLs include the meeting date.
        href = anchor["href"]
        m = re.search(r"/newsevents/pressreleases/monetary(20\d{6})a?\.htm", href)
        if not m:
            continue
        day = dt.datetime.strptime(m.group(1), "%Y%m%d").date()
        if START <= day <= END:
            add(day, "FOMC_DECISION", "FOMC rate decision", "14:00", url, True)
            meetings.add(day)
    by_year = Counter(x.year for x in meetings)
    for year in (2021, 2022, 2023, 2024):
        if by_year[year] < 7:
            raise RuntimeError(f"Federal Reserve {year} decisions incomplete: {by_year}")
    print(f"FOMC statements: {dict(by_year)}")
    # Minutes dates not encoded here: they're nonmajor but require exact
    # release dates, which should not be inferred from meeting dates.

def bea_pce():
    # BEA archive "Published" dates are historical news release dates.
    # Most 2020-25 Personal Income and Outlays releases were 08:30 ET.
    # Use a strict same-title parser; skip special "Data Update" releases.
    # To avoid inventing a time, only include when the release detail confirms
    # 08:30; BEA archive itself doesn't encode announcement time.
    print("BEA PCE historical dates not added until release times are verified.")

def esc(x):
    return x.replace("\\", "\\\\").replace('"','\\"')

def output():
    years = Counter()
    for day,time_et,code,desc,src,major in EVENTS.values():
        years[day[:4]] += 1
    print("Events by year:",dict(years))
    if len(EVENTS) < 170:
        raise RuntimeError(f"Only {len(EVENTS)} historical events; refusing incomplete update.")
    lines = [
        "// AUTO-GENERATED from official historical BLS and Federal Reserve calendars.",
        "// See scripts/build_historical_calendar.py. Checked in for offline backtests.",
        "// BEA/ISM/Census and Fed minutes are NOT covered by this generated file.",
        "namespace Backtest_for_new_orb;",
        "",
        "internal static class HistoricalEconomicCalendar",
        "{",
        "    internal static readonly EconomicEvent[] Scheduled =",
        "    ["
    ]
    for day, tm, code, desc, src, major in sorted(EVENTS.values()):
        lines.append(
            f'        new(DateOnly.Parse("{day}"), TimeSpan.Parse("{tm}"), '
            f'"{esc(code)}", "{esc(desc)}", "{esc(src)}", '
            f'{"true" if major else "false"}),')
    lines.extend(["    ];", "}", ""])
    DEST.write_text("\n".join(lines), encoding="utf-8")
    print(f"Wrote {DEST}: {len(EVENTS)} events")

if __name__ == "__main__":
    bls()
    fed()
    bea_pce()
    output()
