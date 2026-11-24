"""Build an offline Yomitan/Hachidori dictionary from pinned FFXIV game tables,
community player lingo (AkhMorning, XIV JP Raids, curated glossaries), and
character/location illustrations.

Python 3.11+; Pillow is used to verify and optimize embedded image files.
Raw downloads are cached. No browser, game, or Hachidori settings are changed.
"""
from __future__ import annotations

import argparse
import csv
import hashlib
import html
import io
import json
import re
import sys
import time
import urllib.parse
import urllib.request
import zipfile
from collections import Counter
from html.parser import HTMLParser
from pathlib import Path

ROOT = Path(__file__).resolve().parent
COMMIT = "a9f3a282a0e6070fdc0469b6d4e3e7ea17e16edf"  # 7.58, 2026-11-18
GAME_SNAPSHOT = "7.58"
REVISION = "2026.11.24.1"
AKHMORNING_URL = "https://www.akhmorning.com/resources/ffxiv-en-jp-dictionary/"
MAX_IMAGE_DIM = 360

# (csv_field, category_tag, full_label)
SHEETS = {
    "ENpcResident": ("Singular", "character", "Character / NPC"),
    "BNpcName": ("Singular", "enemy", "Enemy / boss"),
    "PlaceName": ("Name", "location", "Location"),
    "ClassJob": ("Name", "job", "Job / class"),
    "ContentFinderCondition": ("Name", "duty", "Dungeon / trial / raid / duty"),
    "ContentRoulette": ("Name", "duty", "Duty Roulette"),
    "Fate": ("Name", "fate", "FATE"),
    "Race": ("Masculine", "race", "Playable race"),
    "Tribe": ("Masculine", "clan", "Playable clan"),
    "GrandCompany": ("Singular", "organization", "Grand Company"),
    "BeastTribe": ("Name", "society", "Allied society"),
    "GuardianDeity": ("Name", "deity", "The Twelve"),
    "Mount": ("Singular", "mount", "Mount"),
    "Companion": ("Singular", "minion", "Minion"),
    "Action": ("Name", "action", "Player action"),
    "Trait": ("Name", "trait", "Job trait"),
    "Status": ("Name", "status", "Status effect"),
    "Completion": ("Text", "autotranslate", "Auto-Translate"),
    "Emote": ("Name", "emote", "Emote"),
    "Weather": ("Name", "weather", "Weather"),
    "OnlineStatus": ("Name", "status", "Online status"),
    "Orchestrion": ("Name", "music", "Orchestrion roll"),
}

LABELS = {
    "character": "Character / NPC",
    "enemy": "Enemy / boss",
    "location": "Location",
    "job": "Job / class",
    "job_abbr": "Job abbreviation",
    "duty": "Duty",
    "fate": "FATE",
    "race": "Playable race",
    "clan": "Playable clan",
    "organization": "Organization",
    "society": "Allied society",
    "deity": "The Twelve",
    "mount": "Mount",
    "minion": "Minion",
    "action": "Action",
    "trait": "Trait",
    "status": "Status",
    "autotranslate": "Auto-Translate",
    "emote": "Emote",
    "weather": "Weather",
    "title": "Title",
    "music": "Orchestrion",
    "event": "Seasonal event",
    "expansion": "Expansion",
    "term": "Game term",
    "alias": "Alias",
    "pf": "Party Finder",
    "slang": "Player lingo",
    "lingo": "Community lingo",
    "acronym": "Acronym",
    "materia": "Materia",
    "world": "World / Server",
}

# Badge colors (WCAG-readable white text on rich dark hues, works in dark & light themes)
BADGE_STYLE = {
    "character": ("Character", "#6d28d9"),
    "enemy": ("Enemy", "#b91c1c"),
    "location": ("Location", "#0f766e"),
    "job": ("Job", "#b45309"),
    "job_abbr": ("Job Abbr", "#c2410c"),
    "duty": ("Duty", "#4338ca"),
    "fate": ("FATE", "#7c3aed"),
    "race": ("Race", "#0369a1"),
    "clan": ("Clan", "#0284c7"),
    "organization": ("Org", "#475569"),
    "society": ("Society", "#0d9488"),
    "deity": ("The Twelve", "#a16207"),
    "mount": ("Mount", "#15803d"),
    "minion": ("Minion", "#16a34a"),
    "action": ("Action", "#1d4ed8"),
    "trait": ("Trait", "#2563eb"),
    "status": ("Status", "#9333ea"),
    "autotranslate": ("Auto-Translate", "#334155"),
    "emote": ("Emote", "#be185d"),
    "weather": ("Weather", "#0284c7"),
    "title": ("Title", "#64748b"),
    "music": ("Orchestrion", "#7e22ce"),
    "event": ("Event", "#db2777"),
    "expansion": ("Expansion", "#4f46e5"),
    "term": ("Term", "#0f766e"),
    "alias": ("Alias", "#059669"),
    "pf": ("PF / Raid", "#047857"),
    "slang": ("Player Lingo", "#059669"),
    "lingo": ("JP–EN Lingo", "#0d9488"),
    "acronym": ("Acronym", "#0369a1"),
    "materia": ("Materia", "#b45309"),
    "world": ("World", "#374151"),
}


def json_bytes(value):
    return json.dumps(value, ensure_ascii=False, separators=(",", ":")).encode("utf-8")


def json_pretty_bytes(value):
    return json.dumps(value, ensure_ascii=False, indent=2).encode("utf-8")


def fetch(url, path):
    if path.exists():
        return path.read_bytes()
    for attempt in range(3):
        try:
            request = urllib.request.Request(url, headers={"User-Agent": "HachidoriFfxiv-dictionary-builder/2.0"})
            with urllib.request.urlopen(request, timeout=60) as response:
                data = response.read()
            path.parent.mkdir(parents=True, exist_ok=True)
            path.write_bytes(data)
            return data
        except Exception:
            if attempt == 2:
                raise
            time.sleep(1 + attempt)


def clean(value):
    # Strip formatting payloads, never expose game markup as a lookup headword.
    value = re.sub(r"<[^>]*>", "", value or "")
    return re.sub(r"\s+", " ", html.unescape(value)).strip()


def clean_desc(value, max_len=240):
    text = clean(value)
    if len(text) > max_len:
        text = text[: max_len - 1].rsplit(" ", 1)[0].rstrip(".,;:") + "…"
    return text


def usable(japanese, english, allow_ascii=False, curated=False):
    if not japanese or not english:
        return False
    if re.search(r"[\x00-\x1f\ufffd]", japanese + english):
        return False
    if not curated and re.search(r"\b(dummy|test|debug)\b|未使用|ダミー|テスト|無効値|仮名称", japanese + english, re.I):
        return False
    if re.search(r"[\u3040-\u30ff\u3400-\u9fff〆]", japanese):
        return True
    if re.fullmatch(r"[A-Z]+-\d{2,}", japanese):
        return True
    if allow_ascii and re.fullmatch(r"[A-Za-z0-9+/.-]{1,16}", japanese):
        return True
    return False


def kana_reading(word):
    # Kana-only names have a mechanically known reading. Do not guess kanji readings.
    if not re.fullmatch(r"[\u3041-\u3096\u30a1-\u30faー・\s]+", word):
        return ""
    return "".join(chr(ord(c) - 0x60) if "ァ" <= c <= "ヶ" else c for c in word)


class Images(HTMLParser):
    def __init__(self):
        super().__init__()
        self.images = []

    def handle_starttag(self, tag, attrs):
        if tag == "img":
            self.images.append(dict(attrs))


def optimize_image_bytes(data: bytes, suffix: str) -> tuple[bytes, int, int, str]:
    """Resize large portraits cleanly to <= MAX_IMAGE_DIM while preserving quality."""
    from PIL import Image

    with Image.open(io.BytesIO(data)) as im:
        im.load()
        width, height = im.size
        has_alpha = im.mode in ("RGBA", "LA") or (im.mode == "P" and "transparency" in im.info)
        if max(width, height) > MAX_IMAGE_DIM:
            im.thumbnail((MAX_IMAGE_DIM, MAX_IMAGE_DIM), Image.Resampling.LANCZOS)
            width, height = im.size
            out = io.BytesIO()
            if has_alpha:
                im.save(out, format="PNG", optimize=True)
                return out.getvalue(), width, height, ".png"
            else:
                if im.mode != "RGB":
                    im = im.convert("RGB")
                im.save(out, format="JPEG", quality=88, optimize=True)
                return out.getvalue(), width, height, ".jpg"
        # Even if not resized, re-save huge uncompressed PNGs if opaque and > 180 KB
        if len(data) > 180_000 and not has_alpha:
            out = io.BytesIO()
            rgb = im.convert("RGB") if im.mode != "RGB" else im
            rgb.save(out, format="JPEG", quality=88, optimize=True)
            if out.tell() < len(data):
                return out.getvalue(), width, height, ".jpg"
        return data, width, height, suffix


def acquire_images():
    from PIL import Image

    targets = json.loads((ROOT / "image_targets.json").read_text(encoding="utf-8"))
    records, failures = [], []
    for target in targets:
        title = target["page"]
        page = target.get("source_page") or "https://ffxiv.gamerescape.com/wiki/" + urllib.parse.quote(title.replace(" ", "_"), safe="()")
        slug = re.sub(r"[^a-z0-9]+", "-", title.lower()).strip("-")
        try:
            if target.get("image_url"):
                candidate = {"src": target["image_url"], "alt": target.get("image_alt", title)}
            else:
                parser = Images()
                cache_slug = slug + "-" + hashlib.sha256(page.encode()).hexdigest()[:8] if target.get("source_page") else slug
                parser.feed(fetch(page, ROOT / ".cache" / "pages" / (cache_slug + ".html")).decode("utf-8"))
                candidates = [i for i in parser.images if re.search(target["image_pattern"], i.get("alt", ""), re.I)]
                if not candidates:
                    raise ValueError("No matching portrait / location artwork")
                candidate = candidates[0]
            url = urllib.parse.urljoin(page, candidate["src"])
            suffix = Path(urllib.parse.urlsplit(url).path).suffix.lower()
            if suffix not in (".png", ".jpg", ".jpeg", ".webp"):
                raise ValueError(f"Unsupported image format: {suffix}")
            raw_path = ROOT / ".cache" / "raw_images" / (slug + suffix)
            existing_img = ROOT / "images" / (slug + suffix)
            if raw_path.exists():
                raw_data = raw_path.read_bytes()
            elif existing_img.exists():
                raw_data = existing_img.read_bytes()
                raw_path.parent.mkdir(parents=True, exist_ok=True)
                raw_path.write_bytes(raw_data)
            else:
                raw_data = fetch(url, raw_path)

            with Image.open(io.BytesIO(raw_data)) as im:
                orig_w, orig_h = im.size
                im.verify()
            minimum = target.get("minimum_dimension", 100)
            if orig_w < minimum or orig_h < minimum:
                raise ValueError("Image too small to identify subject")

            opt_data, width, height, out_suffix = optimize_image_bytes(raw_data, suffix)
            path = ROOT / "images" / (slug + out_suffix)
            path.parent.mkdir(parents=True, exist_ok=True)
            path.write_bytes(opt_data)

            record = target | {
                "path": "images/" + path.name,
                "page_url": page,
                "image_url": url,
                "file_page_url": target.get("file_page_url") or (
                    page if target.get("image_url")
                    else "https://ffxiv.gamerescape.com/wiki/File:" + urllib.parse.quote(candidate["alt"].replace(" ", "_"))
                ),
                "source_name": target.get("source_name", "Gamer Escape"),
                "width": width,
                "height": height,
                "sha256": hashlib.sha256(opt_data).hexdigest(),
                "credit": f"FFXIV game imagery / artwork © SQUARE ENIX. Source: {target.get('source_name', 'Gamer Escape')}; see linked source page for contributor details.",
            }
            records.append(record)
            print(f"Image: {title} ({width}x{height})", flush=True)
        except Exception as error:
            failures.append({"page": title, "error": str(error)})
            print(f"Image unavailable: {title}: {error}", flush=True)
    (ROOT / "image_manifest.json").write_bytes(json_pretty_bytes({"images": records, "unavailable": failures}))
    return records


class AkhMorningParser(HTMLParser):
    def __init__(self):
        super().__init__()
        self.tables = []
        self.cur_table = None
        self.cur_row = None
        self.cur_cell = None
        self.headings = []
        self.in_h = False
        self.h_text = ""

    def handle_starttag(self, tag, attrs):
        if tag in ("h1", "h2", "h3", "h4"):
            self.in_h = True
            self.h_text = ""
        elif tag == "table":
            self.cur_table = {"heading": self.headings[-1] if self.headings else "", "rows": []}
        elif tag == "tr" and self.cur_table is not None:
            self.cur_row = []
        elif tag in ("td", "th") and self.cur_row is not None:
            self.cur_cell = ""

    def handle_endtag(self, tag):
        if tag in ("h1", "h2", "h3", "h4") and self.in_h:
            self.in_h = False
            t = re.sub(r"\s+", " ", self.h_text).strip()
            if t:
                self.headings.append(t)
        elif tag in ("td", "th") and self.cur_cell is not None:
            self.cur_row.append(re.sub(r"\s+", " ", self.cur_cell).strip())
            self.cur_cell = None
        elif tag == "tr" and self.cur_row is not None:
            if self.cur_row:
                self.cur_table["rows"].append(self.cur_row)
            self.cur_row = None
        elif tag == "table" and self.cur_table is not None:
            self.tables.append(self.cur_table)
            self.cur_table = None

    def handle_data(self, data):
        if self.in_h:
            self.h_text += data
        if self.cur_cell is not None:
            self.cur_cell += data


def parse_akhmorning():
    """Extract JP–EN player lingo, races, materia, worlds, and acronyms from AkhMorning."""
    raw = fetch(AKHMORNING_URL, ROOT / ".cache" / "pages" / "akhmorning-dict.html").decode("utf-8", errors="replace")
    parser = AkhMorningParser()
    parser.feed(raw)
    results = []
    for table in parser.tables:
        heading = table["heading"]
        rows = table["rows"]
        if len(rows) < 2:
            continue
        header = [h.casefold() for h in rows[0]]
        for row in rows[1:]:
            cells = [clean(c) for c in row]
            if not any(cells):
                continue
            # Worlds table: ['World', 'Server', 'Japanese']
            if heading == "Worlds" and len(cells) >= 3:
                dc_raw, en_server, jp_server = cells[0], cells[1], cells[2]
                dc = dc_raw.split()[0] if dc_raw else "JP"
                if jp_server and en_server and usable(jp_server, en_server):
                    results.append((jp_server, "", en_server, "world", dc, AKHMORNING_URL, f"{dc} Data Center world (server)."))
                continue
            # Acronyms table: ['Acronym', 'Meaning']
            if len(header) >= 2 and header[0] == "acronym":
                acr, meaning = cells[0], cells[1]
                if acr and meaning and usable(acr, meaning, allow_ascii=True):
                    results.append((acr, "", meaning, "acronym", "AkhMorning", AKHMORNING_URL, ""))
                continue
            # Standard 3-column tables: [Japanese, Romaji/Pronounced, English/Meaning]
            if len(cells) >= 3:
                jp_raw, romaji, en_raw = cells[0], cells[1], cells[2]
                if not jp_raw or not en_raw:
                    continue
                # Skip template sentences with placeholder x
                if re.search(r"(^|\s)[xX](が|を|は|の|\s|$)", jp_raw):
                    continue
                # Split primary English gloss vs longer explanation
                parts = re.split(r"(?<=[.!?])\s+", en_raw, maxsplit=1)
                primary = parts[0].rstrip(".") if len(parts[0]) <= 80 else en_raw[:78].rsplit(" ", 1)[0] + "…"
                note = parts[1] if len(parts) > 1 and len(parts[0]) <= 80 else (en_raw if len(parts[0]) > 80 else "")
                if romaji and not kana_reading(jp_raw):
                    note = f"Romaji: {romaji}" + (f" · {note}" if note else "")

                if heading == "Races":
                    cat, sub = "slang", "Race"
                elif heading == "Materia":
                    cat, sub = "materia", "Materia"
                elif heading == "Greetings":
                    cat, sub = "lingo", "Chat"
                elif heading == "Raiding":
                    cat, sub = "pf", "Raiding"
                else:
                    cat, sub = "lingo", "AkhMorning"

                # Handle slash-separated variants like "おつ / お疲れ"
                variants = [v.strip() for v in re.split(r"\s*[/／]\s*", jp_raw) if v.strip()]
                for v in variants:
                    # Strip trailing punctuation on expressions for cleaner lookup matching
                    expr = v.rstrip("！!？?。")
                    if usable(expr, primary, allow_ascii=False):
                        results.append((expr, "", primary, cat, sub, AKHMORNING_URL, clean_desc(note, 280)))
    return results


def collect():
    entries, sources = {}, []

    def add(japanese, english, category, source, reading="", note="", priority=0, subtag=""):
        key = (japanese, english.casefold(), category)
        entry = entries.setdefault(key, {
            "expression": japanese,
            "reading": reading or kana_reading(japanese),
            "english": english,
            "category": category,
            "subtag": subtag,
            "sources": [],
            "note": note,
            "priority": priority,
        })
        if source not in entry["sources"]:
            entry["sources"].append(source)
        if reading:
            entry["reading"] = reading
        if note and (priority >= entry["priority"] or not entry["note"]):
            entry["note"] = note
        if subtag and (priority >= entry["priority"] or not entry["subtag"]):
            entry["subtag"] = subtag
        entry["priority"] = max(priority, entry["priority"])

    # Load ClassJob first so Action/Trait can look up Job abbreviations
    job_abbr_by_id = {}
    sheet_order = ["ClassJob"] + [s for s in SHEETS if s != "ClassJob"]

    for sheet in sheet_order:
        field, category, label = SHEETS[sheet]
        tables = {}
        for language in ("ja", "en"):
            url = f"https://raw.githubusercontent.com/xivapi/ffxiv-datamining/{COMMIT}/csv/{language}/{sheet}.csv"
            data = fetch(url, ROOT / ".cache" / COMMIT / language / (sheet + ".csv"))
            rows = list(csv.DictReader(io.StringIO(data.decode("utf-8-sig"))))
            if not rows or field not in rows[0] or "#" not in rows[0]:
                raise ValueError(f"Unexpected columns in {sheet}/{language}")
            tables[language] = {r["#"]: r for r in rows}
            sources.append({
                "sheet": sheet,
                "language": language,
                "url": url,
                "sha256": hashlib.sha256(data).hexdigest(),
                "rows": len(rows),
            })

        if sheet == "ClassJob":
            for row_id, en in tables["en"].items():
                abbr = clean(en.get("Abbreviation", ""))
                if abbr:
                    job_abbr_by_id[row_id] = abbr

        count = 0
        for row_id, ja in tables["ja"].items():
            en = tables["en"].get(row_id)
            if en is None or (sheet == "GrandCompany" and row_id == "0"):
                continue

            # Filter Action to actual player actions (avoids 45,000+ enemy/internal dummy casts)
            if sheet == "Action" and en.get("IsPlayerAction") != "True":
                continue

            japanese, english = clean(ja[field]), clean(en[field])
            # Strip 【...】 category headers from Completion (Auto-Translate)
            if sheet == "Completion":
                if japanese.startswith("【") and japanese.endswith("】"):
                    continue
                if japanese == english:
                    continue
            if not usable(japanese, english):
                continue

            note = ""
            subtag = ""
            if sheet == "ClassJob":
                abbr = clean(en.get("Abbreviation", ""))
                if abbr:
                    subtag = abbr
                    note = f"Job abbreviation: {abbr}"
            elif sheet == "ENpcResident":
                en_title = clean(en.get("Title", ""))
                if en_title and en_title.lower() not in ("shop", "none"):
                    subtag = en_title if len(en_title) <= 28 else ""
                    if not subtag:
                        note = f"Title: {en_title}"
            elif sheet == "ContentRoulette":
                subtag = "Roulette"
            elif sheet == "OnlineStatus":
                subtag = "Online Status"
            elif sheet == "GuardianDeity":
                subtag = "The Twelve"
                note = clean_desc(en.get("Description", ""), 200)
            elif sheet in ("Action", "Trait"):
                cid = en.get("ClassJob", "")
                if cid in job_abbr_by_id:
                    subtag = job_abbr_by_id[cid]
            elif sheet == "Status":
                en_desc = clean_desc(en.get("Description", ""), 200)
                if not en_desc:
                    continue
                note = en_desc
            elif sheet == "Orchestrion":
                en_desc = clean_desc(en.get("Description", ""), 180)
                if en_desc:
                    note = en_desc

            add(japanese, english, category, f"{sheet}:{row_id}", note=note, subtag=subtag)
            count += 1

            # Explicit game-authored short duty name
            if sheet == "ContentFinderCondition":
                short = clean(ja.get("NameShort"))
                if short and short != japanese and usable(short, english):
                    add(short, english, category, f"{sheet}:{row_id}:NameShort", note=f"Short name for {japanese}")

            # Feminine title variant when distinct
            if sheet == "Title":
                ja_fem = clean(ja.get("Feminine", ""))
                en_fem = clean(en.get("Feminine", ""))
                if ja_fem and en_fem and (ja_fem != japanese or en_fem != english) and usable(ja_fem, en_fem):
                    add(ja_fem, en_fem, category, f"{sheet}:{row_id}:Feminine")
        print(f"{sheet}: {count} aligned rows", flush=True)

    # AkhMorning JP-EN community dictionary (priority 50)
    akh_rows = parse_akhmorning()
    akh_added = 0
    for jp, reading, en, cat, sub, src, note in akh_rows:
        add(jp, en, cat, src, reading=reading, note=note, priority=50, subtag=sub)
        akh_added += 1
    print(f"AkhMorning JP-EN dictionary: {akh_added} parsed entries", flush=True)

    # Original curated.tsv (priority 100)
    with (ROOT / "curated.tsv").open(encoding="utf-8", newline="") as f:
        for row in csv.DictReader(f, delimiter="\t"):
            if not usable(row["expression"], row["english"], allow_ascii=True, curated=True):
                raise ValueError(f"Invalid curated entry: {row}")
            add(row["expression"], row["english"], row["category"], row["source"],
                row["reading"], row["note"], 100)

    # Curated character & location variants (priority 105)
    char_extra = ROOT / "characters_extra.tsv"
    if char_extra.exists():
        c_count = 0
        with char_extra.open(encoding="utf-8", newline="") as f:
            for row in csv.DictReader(f, delimiter="\t"):
                if not usable(row["expression"], row["english"], allow_ascii=True, curated=True):
                    raise ValueError(f"Invalid character extra entry: {row}")
                add(row["expression"], row["english"], row["category"], row["source"],
                    row["reading"], row["note"], 105, subtag=row.get("subtag", ""))
                c_count += 1
        print(f"characters_extra.tsv: {c_count} entries", flush=True)

    # Curated modern PF / player lingo / job abbreviations (priority 110)
    lingo_file = ROOT / "lingo.tsv"
    if lingo_file.exists():
        l_count = 0
        with lingo_file.open(encoding="utf-8", newline="") as f:
            for row in csv.DictReader(f, delimiter="\t"):
                if not usable(row["expression"], row["english"], allow_ascii=True, curated=True):
                    raise ValueError(f"Invalid lingo entry: {row}")
                add(row["expression"], row["english"], row["category"], row["source"],
                    row["reading"], row["note"], 110, subtag=row.get("subtag", ""))
                l_count += 1
        print(f"lingo.tsv: {l_count} entries", flush=True)

    # Deduplication:
    # 1) Merge identical (expression, english) enemy entries into character/minion/mount
    # 2) Merge identical (expression, english) autotranslate entries into more specific game categories
    # 3) Merge identical (expression, english) lower-priority AkhMorning lingo into curated/game entries
    merged = []
    grouped = {}
    for entry in entries.values():
        grouped.setdefault((entry["expression"], entry["english"].casefold()), []).append(entry)

    duplicate_merges = 0
    for group in grouped.values():
        enemy = next((e for e in group if e["category"] == "enemy"), None)
        preferred = next((e for cat in ("character", "minion", "mount") for e in group if e["category"] == cat), None)
        if enemy is not None and preferred is not None:
            preferred["sources"] = list(dict.fromkeys(preferred["sources"] + enemy["sources"]))
            preferred.setdefault("merged_categories", []).append("enemy")
            group.remove(enemy)
            duplicate_merges += 1

        auto = next((e for e in group if e["category"] == "autotranslate"), None)
        if auto is not None and len(group) > 1:
            keeper = next(e for e in group if e is not auto)
            keeper["sources"] = list(dict.fromkeys(keeper["sources"] + auto["sources"]))
            keeper.setdefault("merged_categories", []).append("autotranslate")
            group.remove(auto)
            duplicate_merges += 1

        akh = next((e for e in group if e["category"] == "lingo" and AKHMORNING_URL in e["sources"]), None)
        if akh is not None and len(group) > 1:
            keeper = next(e for e in group if e is not akh)
            keeper["sources"] = list(dict.fromkeys(keeper["sources"] + akh["sources"]))
            if not keeper["note"] and akh["note"]:
                keeper["note"] = akh["note"]
            keeper.setdefault("merged_categories", []).append("lingo")
            group.remove(akh)
            duplicate_merges += 1

        merged.extend(group)

    return sorted(merged, key=lambda e: (e["expression"], -e["priority"], e["category"], e["english"])), sources, duplicate_merges


def format_entry_glossary(entry, image):
    """Build a single clean Yomitan v3 structured-content card (no bogus 1. 2. 3. numbering)."""
    badge_label, badge_bg = BADGE_STYLE.get(entry["category"], (LABELS.get(entry["category"], entry["category"]), "#475569"))

    header_spans = [
        {
            "tag": "span",
            "style": {
                "backgroundColor": badge_bg,
                "color": "#ffffff",
                "fontSize": "0.78em",
                "fontWeight": "bold",
                "paddingLeft": "0.4em",
                "paddingRight": "0.4em",
                "borderRadius": "0.25em",
                "marginRight": "0.35em",
            },
            "content": badge_label,
        }
    ]

    subtag = entry.get("subtag") or (image.get("expansion") if image else "")
    if subtag:
        header_spans.append({
            "tag": "span",
            "style": {
                "backgroundColor": "#334155",
                "color": "#e2e8f0",
                "fontSize": "0.75em",
                "paddingLeft": "0.35em",
                "paddingRight": "0.35em",
                "borderRadius": "0.25em",
                "marginRight": "0.4em",
            },
            "content": subtag,
        })

    header_spans.append({
        "tag": "span",
        "style": {"fontWeight": "bold", "fontSize": "1.05em"},
        "content": entry["english"],
    })

    blocks = [{"tag": "div", "content": header_spans}]

    if entry["note"]:
        blocks.append({
            "tag": "div",
            "style": {"fontSize": "0.9em", "marginTop": "0.2em"},
            "content": entry["note"],
        })

    if image:
        # Display at a crisp, comfortable popup size (max height 220px)
        w, h = image["width"], image["height"]
        if h > 220:
            scale = 220 / h
            disp_w, disp_h = max(1, round(w * scale)), 220
        else:
            disp_w, disp_h = w, h
        blocks.append({
            "tag": "div",
            "style": {"marginTop": "0.35em"},
            "content": {
                "tag": "img",
                "path": image["path"],
                "width": disp_w,
                "height": disp_h,
                "sizeUnits": "px",
                "collapsible": False,
                "collapsed": False,
                "background": False,
                "borderRadius": "0.3em",
                "title": image["page"],
                "alt": image["page"],
            },
        })
        blocks.append({
            "tag": "div",
            "data": {"content": "attribution"},
            "style": {"fontSize": "0.75em", "marginTop": "0.15em"},
            "content": [
                {"tag": "a", "href": image["file_page_url"], "content": "Image: " + image.get("source_name", "Gamer Escape")},
                " · © SQUARE ENIX",
            ],
        })

    return [{"type": "structured-content", "content": {"tag": "div", "content": blocks}}]


def apply_character_descriptions(entries, targets):
    expressions_by_character = {t["page"]: t["expressions"] for t in targets if t["category"] == "character"}
    characters_by_expression = {}
    for entry in entries:
        if entry["category"] == "character":
            characters_by_expression.setdefault(entry["expression"], []).append(entry)

    described = set()
    with (ROOT / "character_descriptions.tsv").open(encoding="utf-8", newline="") as f:
        for row in csv.DictReader(f, delimiter="\t"):
            name, description = row["character"], row["description"]
            if not name or not description or name in described:
                raise ValueError(f"Invalid or duplicate character description: {row}")
            japanese_names = row["japanese_names"]
            expressions = japanese_names.split(";") if japanese_names else expressions_by_character.get(name, [])
            matched = False
            for expression in expressions:
                matches = characters_by_expression.get(expression, [])
                if len(matches) > 1:
                    # Shared Japanese spellings can have different English meanings.
                    matches = [entry for entry in matches if entry["english"].casefold() == name.casefold()]
                for entry in matches:
                    entry["note"] = description
                    entry["sources"].append("curated-character-description")
                    matched = True
            if not matched:
                raise ValueError(f"Character description without a dictionary entry: {name}")
            described.add(name)


def build(images):
    entries, sources, duplicate_merges = collect()
    targets = json.loads((ROOT / "image_targets.json").read_text(encoding="utf-8"))
    apply_character_descriptions(entries, targets)
    image_map = {}
    for image in images:
        for expression in image["expressions"]:
            image_map[(expression, image["category"])] = image

    bank = []
    illustrated = 0
    for sequence, entry in enumerate(entries, 1):
        image = image_map.get((entry["expression"], entry["category"]))
        if image:
            illustrated += 1
        glossary = format_entry_glossary(entry, image)
        score = max(entry["priority"], 100 if image else 0)
        bank.append([entry["expression"], entry["reading"], entry["category"], "", score, glossary, sequence, "ffxiv"])

    index = {
        "title": "FFXIV Hachidori JP–EN",
        "format": 3,
        "revision": REVISION,
        "sequenced": True,
        "author": "Hachidori for FFXIV custom dictionary; localized game data by SQUARE ENIX; community lingo from AkhMorning & XIV JP Raids",
        "url": "https://github.com/xivapi/ffxiv-datamining",
        "description": (
            f"Comprehensive FFXIV Japanese-to-English dictionary (game data {GAME_SNAPSHOT}): "
            "characters/NPCs (with titles, aliases, portraits & selected introductions), enemies, locations, jobs & abbreviations, "
            "duties, FATEs, player actions, traits, statuses, Auto-Translate, emotes, weather, titles, "
            "orchestrion rolls, The Twelve, mounts, minions, plus Japanese Party Finder & player lingo "
            "(AkhMorning, XIV JP Raids, and curated glossaries). Contains names/portraits from all expansions."
        ),
        "sourceLanguage": "ja",
        "targetLanguage": "en",
        "attribution": (
            "FFXIV text and imagery © SQUARE ENIX. CSV extraction: xivapi/ffxiv-datamining contributors. "
            "Community JP–EN lingo: AkhMorning (akhmorning.com) & XIV JP Raids (xivjpraids.com). "
            "Selected character/location portraits hosted by Gamer Escape and credited per entry."
        ),
    }

    counts = dict(sorted(Counter(e["category"] for e in entries).items()))
    report = {
        "revision": REVISION,
        "source_commit": COMMIT,
        "game_data_snapshot": GAME_SNAPSHOT,
        "entries": len(entries),
        "categories": counts,
        "embedded_images": len(images),
        "duplicate_entries_merged": duplicate_merges,
        "images_by_category": dict(sorted(Counter(i["category"] for i in images).items())),
        "character_images_by_expansion": dict(sorted(Counter(i.get("expansion", "Earlier expansions") for i in images if i["category"] == "character").items())),
        "illustrated_entries": illustrated,
        "source_files": sources,
        "unmatched_image_targets": [
            i["page"] for i in images
            if not any((e["expression"], e["category"]) in {(x, i["category"]) for x in i["expressions"]} for e in entries)
        ],
    }
    (ROOT / "source_manifest.json").write_bytes(json_pretty_bytes(report))

    by_page = {i["page"]: i for i in images}
    with (ROOT / "character_coverage.tsv").open("w", encoding="utf-8", newline="") as f:
        writer = csv.writer(f, delimiter="\t")
        writer.writerow(["character", "focus_expansion", "japanese_names", "image_status", "image", "source"])
        for target in targets:
            if target["category"] != "character":
                continue
            image = by_page.get(target["page"])
            writer.writerow([
                target["page"],
                target.get("expansion", "Earlier expansions"),
                ";".join(target["expressions"]),
                "embedded" if image else "text only; image unavailable",
                image["path"] if image else "",
                image["file_page_url"] if image else "",
            ])

    with (ROOT / "vocabulary.tsv").open("w", encoding="utf-8", newline="") as f:
        writer = csv.writer(f, delimiter="\t")
        writer.writerow(["expression", "reading", "english", "category", "subtag", "sources", "note"])
        writer.writerows(
            [e["expression"], e["reading"], e["english"], e["category"], e.get("subtag", ""), ";".join(e["sources"]), e["note"]]
            for e in entries
        )

    archive = ROOT / "FFXIV-Hachidori-JP-EN.zip"
    with zipfile.ZipFile(archive, "w", zipfile.ZIP_DEFLATED, compresslevel=9) as z:
        def write(name, data):
            info = zipfile.ZipInfo(name, date_time=(2026, 11, 22, 0, 0, 0))
            info.compress_type = zipfile.ZIP_DEFLATED
            z.writestr(info, data)

        write("index.json", json_pretty_bytes(index))
        tag_bank = [[k, "partOfSpeech", 0, v, 0] for k, v in sorted(LABELS.items())] + [["ffxiv", "misc", 0, "Final Fantasy XIV", 0]]
        write("tag_bank_1.json", json_pretty_bytes(tag_bank))
        for start in range(0, len(bank), 5000):
            write(f"term_bank_{start // 5000 + 1}.json", json_bytes(bank[start:start + 5000]))
        for filename in ("source_manifest.json", "image_manifest.json", "character_coverage.tsv", "character_descriptions.tsv",
                         "curated.tsv", "lingo.tsv", "characters_extra.tsv", "README.md"):
            p = ROOT / filename
            if p.exists():
                write(filename, p.read_bytes())
        for image in images:
            write(image["path"], (ROOT / image["path"]).read_bytes())

    print(json.dumps({k: v for k, v in report.items() if k != "source_files"}, ensure_ascii=False, indent=2))
    print(f"Created {archive} ({archive.stat().st_size / 1024 / 1024:.2f} MiB)")


if __name__ == "__main__":
    sys.stdout.reconfigure(encoding="utf-8")
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--fetch-images", action="store_true", help="Fetch and optimize curated images (requires Pillow); otherwise use the existing manifest")
    args = parser.parse_args()
    images = acquire_images() if args.fetch_images else json.loads((ROOT / "image_manifest.json").read_text(encoding="utf-8"))["images"]
    build(images)
