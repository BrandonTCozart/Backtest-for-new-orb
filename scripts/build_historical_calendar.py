#!/usr/bin/env python3
"""Regenerate the checked-in historical U.S. release calendar from official archives.

This script runs for maintenance, NEVER while executing a backtest.
Data is checked into the repository so a replay needs no network.
Source HTML calendars:
  https://www.bls.gov/schedule/YYYY/ (2020..2025)
  https://www.federalreserve.gov/monetarypolicy/fomccalendars.htm
  https://www.bea.gov/news/archive?field_related_product_target_id=721&page=N
  https://www.bea.gov/news/archive?field_related_product_target_id=451&page=N
  https://www.census.gov/economic-indicators/calendar-listview-YYYY.html
  https://www.ismworld.org/supply-management-news-and-reports/reports/rob-report-calendar

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
    # Fed 2020 FOMC archive statements use a different URL structure.
    # These three regular meetings cover the historical dataset's 2020 portion.
    for text in ("2020-09-16", "2020-11-05", "2020-12-16"):
        day = dt.date.fromisoformat(text)
        add(day, "FOMC_DECISION", "FOMC rate decision", "14:00", url, True)
    print(f"FOMC statements: {dict(by_year)}; 2020 late-year decisions=3")
    # The official FOMC meeting archive labels minutes with their ACTUAL
    # release dates; do not treat meeting dates as minutes-release dates.
    minutes = 0
    pattern = r"Released\\s+([A-Za-z]+)\\s+(\\d{1,2}),?\\s+(20\\d{2})"
    for match in re.finditer(pattern.replace("\\\\", "\\"), soup.get_text(" ", strip=True)):
        date = parsedate(" ".join(match.groups()))
        if date:
            add(date, "FOMC_MINUTES", "FOMC meeting minutes", "14:00", url, False)
            minutes += 1
    if minutes < 25:
        raise RuntimeError(f"Too few official FOMC minutes release dates: {minutes}")
    print(f"Fed minutes: {minutes}")

def bea():
    # National GDP and personal income/PCE monthly releases appear in BEA's
    # published news archive. The national BEA economic release time is 08:30 ET.
    products = [(721, "PCE"), (451, "GDP")]
    for product_id, code in products:
        count = Counter()
        newest = None
        for page in range(12):
            url = (
                "https://www.bea.gov/news/archive?created_1=All&"
                f"field_related_product_target_id={product_id}&title=&page={page}"
            )
            soup = fetch(url)
            rows = 0
            earliest = None
            for tr in soup.select("tr"):
                cols = [c.get_text(" ", strip=True) for c in tr.find_all(["td"], recursive=False)]
                if len(cols) < 2:
                    continue
                title, date_text = cols[:2]
                published = parsedate(date_text)
                if published is None:
                    continue
                rows += 1
                earliest = min(earliest, published) if earliest else published
                if code == "PCE":
                    valid = title.startswith("Personal Income and Outlays,") and (
                        "Data Update" not in title)
                    desc = "Personal Income and Outlays / PCE"
                else:
                    valid = title.startswith("GDP (") or title.startswith(
                        "Gross Domestic Product,")
                    desc = "Gross Domestic Product"
                if valid:
                    add(published, code, desc, "08:30", url, True)
                    count[published.year] += 1
            if rows == 0:
                raise RuntimeError(f"BEA archive unexpectedly empty: {url}")
            if earliest is not None and earliest.year < 2020:
                break
        for year in range(2021, 2025):
            if count[year] < (9 if code=="PCE" else 8):
                raise RuntimeError(f"Missing BEA {code} {year} releases: {dict(count)}")
        print(f"BEA {code}: {dict(count)}")

def census():
    for year in range(2020,2026):
        url = f"https://www.census.gov/economic-indicators/calendar-listview-{year}.html"
        soup = fetch(url)
        count = 0
        for tr in soup.select("tr"):
            cols = [c.get_text(" ", strip=True) for c in tr.find_all(["td"], recursive=False)]
            if len(cols) < 3 or not cols[0].startswith(
                    "Advance Monthly Sales for Retail and Food Services"):
                continue
            date = parsedate(cols[1])
            if date is None or date.year != year:
                continue
            time_match = re.search(r"(\d{1,2}):(\d{2})\s*AM", cols[2], re.I)
            if not time_match or (int(time_match.group(1)),int(time_match.group(2)))!=(8,30):
                raise RuntimeError(f"Unexpected Census retail release time: {cols[:3]}")
            add(date, "RETAIL_SALES", "Advance retail sales", "08:30", url, True)
            count += 1
        if count < (7 if year==2025 else 10):
            raise RuntimeError(f"Census calendar incomplete {year}: {count}")
        print(f"Census retail {year}: {count}")

def ism():
    # ISM explicitly publishes Manufacturing PMI on the first BUSINESS day
    # and Services PMI on the third BUSINESS day at 10:00 ET. Because ISM's
    # Jan 2 holiday differs from federal calendars, skip Jan 1 and Jan 2.
    # This is a calendar-RULE derivation, not a per-date official archive.
    url = "https://www.ismworld.org/supply-management-news-and-reports/reports/rob-report-calendar/"
    for year in range(2020,2026):
        for month in range(1,13):
            if year==2025 and month>=10: continue
            dates = []
            for day in range(1,10):
                date = dt.date(year, month, day)
                if date.weekday() >= 5: continue
                if month == 1 and day <= 2: continue
                if month == 7:
                    observed = dt.date(year,7,4)
                    if observed.weekday() == 5: observed -= dt.timedelta(days=1)
                    elif observed.weekday() == 6: observed += dt.timedelta(days=1)
                    if date == observed: continue
                if month == 9 and day <= 7 and date.weekday() == 0: continue
                dates.append(date)
                if len(dates)==3: break
            add(dates[0],"ISM_MANUFACTURING","ISM Manufacturing PMI","10:00",url,True)
            add(dates[2],"ISM_SERVICES","ISM Services PMI","10:00",url,True)
    print("ISM: 1st and 3rd business-day rules; exception dates must be audited.")

def esc(x):
    return x.replace("\\", "\\\\").replace('"','\\"')

def output():
    years = Counter()
    for day,time_et,code,desc,src,major in EVENTS.values():
        years[day[:4]] += 1
    print("Events by year:",dict(years))
    if len(EVENTS) < 550:
        raise RuntimeError(f"Only {len(EVENTS)} historical events; refusing incomplete update.")
    lines = [
        "// AUTO-GENERATED 2020-2025 historical U.S. economic release calendar.",
        "// Sources: official BLS, Federal Reserve, BEA, Census archives;",
        "// ISM dates are derived from published 1st/3rd-business-day rules.",
        "// See scripts/build_historical_calendar.py. Checked in for offline backtests.",
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
    bea()
    census()
    ism()
    output()
