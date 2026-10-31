"""Build an offline Yomitan/Hachidori dictionary from pinned FFXIV game tables.

Python 3.11+; Pillow is used only to verify downloaded image files.
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
COMMIT = "76d22160d687b0e0318071a098ab9eb9d531605f"  # 7.57, 2026-10-13
REVISION = "2026.10.14.3"
SHEETS = {
    "ENpcResident": ("Singular", "character", "Character / NPC"),
    "BNpcName": ("Singular", "enemy", "Enemy / boss"),
    "PlaceName": ("Name", "location", "Location"),
    "ClassJob": ("Name", "job", "Job / class"),
    "ContentFinderCondition": ("Name", "duty", "Dungeon / trial / raid / duty"),
    "Fate": ("Name", "fate", "FATE"),
    "Race": ("Masculine", "race", "Playable race"),
    "Tribe": ("Masculine", "clan", "Playable clan"),
    "GrandCompany": ("Singular", "organization", "Grand Company"),
    "BeastTribe": ("Name", "society", "Allied society"),
    "Mount": ("Singular", "mount", "Mount"),
    "Companion": ("Singular", "minion", "Minion"),
}
LABELS = {v[1]: v[2] for v in SHEETS.values()} | {
    "event": "Seasonal / special event", "term": "Game term",
    "organization": "Organization", "expansion": "Expansion", "alias": "Alias",
}


def json_bytes(value):
    return json.dumps(value, ensure_ascii=False, indent=2).encode("utf-8")


def fetch(url, path):
    if path.exists():
        return path.read_bytes()
    for attempt in range(3):
        try:
            request = urllib.request.Request(url, headers={"User-Agent": "Hachidori for FFXIV-dictionary-builder/1.0"})
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


def usable(japanese, english):
    return bool(japanese and english and (re.search(r"[\u3040-\u30ff\u3400-\u9fff]", japanese) or re.fullmatch(r"[A-Z]+-\d{2,}", japanese))
                and not re.search(r"[\x00-\x1f\ufffd]", japanese + english)
                and not re.search(r"dummy|test|debug|未使用|ダミー|テスト|無効値|仮名称", japanese + english, re.I))


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
                # A changed source page gets a different cache key.
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
            path = ROOT / "images" / (slug + suffix)
            data = fetch(url, path)
            with Image.open(io.BytesIO(data)) as im:
                width, height = im.size
                im.verify()
            minimum = target.get("minimum_dimension", 100)
            if width < minimum or height < minimum:
                raise ValueError("Image too small to identify subject")
            record = target | {
                "path": "images/" + path.name, "page_url": page, "image_url": url,
                "file_page_url": target.get("file_page_url") or (page if target.get("image_url") else "https://ffxiv.gamerescape.com/wiki/File:" + urllib.parse.quote(candidate["alt"].replace(" ", "_"))),
                "source_name": target.get("source_name", "Gamer Escape"),
                "width": width, "height": height, "sha256": hashlib.sha256(data).hexdigest(),
                "credit": f"FFXIV game imagery / artwork © SQUARE ENIX. Source: {target.get('source_name', 'Gamer Escape')}; see linked source page for contributor details.",
            }
            records.append(record)
            print(f"Image: {title}", flush=True)
        except Exception as error:
            failures.append({"page": title, "error": str(error)})
            print(f"Image unavailable: {title}: {error}", flush=True)
    (ROOT / "image_manifest.json").write_bytes(json_bytes({"images": records, "unavailable": failures}))
    return records


def collect():
    entries, sources = {}, []

    def add(japanese, english, category, source, reading="", note="", priority=0):
        key = (japanese, english.casefold(), category)
        entry = entries.setdefault(key, {"expression": japanese, "reading": reading or kana_reading(japanese),
                                        "english": english, "category": category, "sources": [], "note": note,
                                        "priority": priority})
        if source not in entry["sources"]:
            entry["sources"].append(source)
        if reading:
            entry["reading"] = reading
        if note:
            entry["note"] = note
        entry["priority"] = max(priority, entry["priority"])

    for sheet, (field, category, label) in SHEETS.items():
        tables = {}
        for language in ("ja", "en"):
            url = f"https://raw.githubusercontent.com/xivapi/ffxiv-datamining/{COMMIT}/csv/{language}/{sheet}.csv"
            data = fetch(url, ROOT / ".cache" / COMMIT / language / (sheet + ".csv"))
            rows = list(csv.DictReader(io.StringIO(data.decode("utf-8-sig"))))
            if not rows or field not in rows[0] or "#" not in rows[0]:
                raise ValueError(f"Unexpected columns in {sheet}/{language}")
            tables[language] = {r["#"]: r for r in rows}
            sources.append({"sheet": sheet, "language": language, "url": url,
                            "sha256": hashlib.sha256(data).hexdigest(), "rows": len(rows)})
        count = 0
        for row_id, ja in tables["ja"].items():
            en = tables["en"].get(row_id)
            if en is None or (sheet == "GrandCompany" and row_id == "0"):
                continue
            japanese, english = clean(ja[field]), clean(en[field])
            if not usable(japanese, english):
                continue
            note = f"Job abbreviation: {en['Abbreviation']}" if sheet == "ClassJob" else ""
            add(japanese, english, category, f"{sheet}:{row_id}", note=note)
            count += 1
            # The duty name usually has a Japanese descriptive prefix. NameShort
            # is an explicit game-authored short name, unlike a guessed split.
            if sheet == "ContentFinderCondition":
                short = clean(ja.get("NameShort"))
                if short and short != japanese and usable(short, english):
                    add(short, english, category, f"{sheet}:{row_id}:NameShort", note=f"Short name for {japanese}")
        print(f"{sheet}: {count} aligned rows", flush=True)
    with (ROOT / "curated.tsv").open(encoding="utf-8", newline="") as f:
        for row in csv.DictReader(f, delimiter="\t"):
            if not usable(row["expression"], row["english"]):
                raise ValueError(f"Invalid curated entry: {row}")
            add(row["expression"], row["english"], row["category"], row["source"],
                row["reading"], row["note"], 100)
    # Battle rows also contain allies, mounts, and minions. An identical bilingual
    # name is one lookup entry, with all source rows retained for auditing.
    merged = []
    grouped = {}
    for entry in entries.values():
        grouped.setdefault((entry["expression"], entry["english"].casefold()), []).append(entry)
    for group in grouped.values():
        enemy = next((e for e in group if e["category"] == "enemy"), None)
        preferred = next((e for category in ("character", "minion", "mount") for e in group if e["category"] == category), None)
        if enemy is not None and preferred is not None:
            preferred["sources"] = list(dict.fromkeys(preferred["sources"] + enemy["sources"]))
            preferred["merged_categories"] = ["enemy"]
            group.remove(enemy)
        merged.extend(group)
    return sorted(merged, key=lambda e: (e["expression"], e["category"], e["english"])), sources


def build(images):
    entries, sources = collect()
    image_map = {}
    for image in images:
        for expression in image["expressions"]:
            image_map[(expression, image["category"])] = image
    bank = []
    illustrated = 0
    for sequence, entry in enumerate(entries, 1):
        glossary = [f"{entry['english']} — {LABELS[entry['category']]}"]
        if entry["note"]:
            glossary.append(entry["note"])
        image = image_map.get((entry["expression"], entry["category"]))
        if image:
            if image.get("expansion"):
                glossary.append(f"Featured in {image['expansion']}.")
            glossary.extend([
                {"type": "image", "path": image["path"], "width": image["width"], "height": image["height"],
                 "title": image["page"], "description": image["page"], "pixelated": False},
                {"type": "structured-content", "content": {"tag": "div", "content": [
                    {"tag": "a", "href": image["file_page_url"], "content": "Image source: " + image.get("source_name", "Gamer Escape")},
                    " · © SQUARE ENIX",
                ]}},
            ])
            illustrated += 1
        bank.append([entry["expression"], entry["reading"], entry["category"], "", max(entry["priority"], 100 if image and image.get("expansion") else 0), glossary, sequence, "ffxiv"])
    index = {"title": "FFXIV Hachidori JP–EN", "format": 3, "revision": REVISION, "sequenced": True,
             "author": "Hachidori for FFXIV custom dictionary; localized game data by SQUARE ENIX",
             "url": "https://github.com/xivapi/ffxiv-datamining",
             "description": "FFXIV names and terminology, Japanese to official localized English. Game-data snapshot 7.57. Includes character/location illustrations for selected entries. Contains names from all expansions (possible spoilers). Kanji readings are omitted unless supplied in the curated supplement.",
             "sourceLanguage": "ja", "targetLanguage": "en", "attribution": "FFXIV text and imagery © SQUARE ENIX. CSV extraction: xivapi/ffxiv-datamining contributors. Selected images hosted by Gamer Escape. Source and image manifests included; no ownership or open license to game assets is claimed."}
    counts = dict(sorted(Counter(e["category"] for e in entries).items()))
    report = {"revision": REVISION, "source_commit": COMMIT, "game_data_snapshot": "7.57",
              "entries": len(entries), "categories": counts, "embedded_images": len(images),
              "duplicate_entries_merged": sum(len(e.get("merged_categories", [])) for e in entries),
              "images_by_category": dict(sorted(Counter(i["category"] for i in images).items())),
              "character_images_by_expansion": dict(sorted(Counter(i.get("expansion", "Earlier expansions") for i in images if i["category"] == "character").items())),
              "illustrated_entries": illustrated, "source_files": sources,
              "unmatched_image_targets": [i["page"] for i in images if not any((e["expression"], e["category"]) in {(x, i["category"]) for x in i["expressions"]} for e in entries)]}
    (ROOT / "source_manifest.json").write_bytes(json_bytes(report))
    by_page = {i["page"]: i for i in images}
    with (ROOT / "character_coverage.tsv").open("w", encoding="utf-8", newline="") as f:
        writer = csv.writer(f, delimiter="\t")
        writer.writerow(["character", "focus_expansion", "japanese_names", "image_status", "image", "source"])
        for target in json.loads((ROOT / "image_targets.json").read_text(encoding="utf-8")):
            if target["category"] != "character":
                continue
            image = by_page.get(target["page"])
            writer.writerow([target["page"], target.get("expansion", "Earlier expansions"),
                             ";".join(target["expressions"]), "embedded" if image else "text only; image unavailable",
                             image["path"] if image else "", image["file_page_url"] if image else ""])
    # Human-readable vocabulary and complete row provenance, alongside the import ZIP.
    with (ROOT / "vocabulary.tsv").open("w", encoding="utf-8", newline="") as f:
        writer = csv.writer(f, delimiter="\t")
        writer.writerow(["expression", "reading", "english", "category", "sources", "note"])
        writer.writerows([e["expression"], e["reading"], e["english"], e["category"], ";".join(e["sources"]), e["note"]] for e in entries)
    archive = ROOT / "FFXIV-Hachidori-JP-EN.zip"
    with zipfile.ZipFile(archive, "w", zipfile.ZIP_DEFLATED, compresslevel=9) as z:
        def write(name, data):
            info = zipfile.ZipInfo(name, date_time=(2026, 10, 14, 0, 0, 0))
            info.compress_type = zipfile.ZIP_DEFLATED
            z.writestr(info, data)
        write("index.json", json_bytes(index))
        write("tag_bank_1.json", json_bytes([[k, "", 0, v, 0] for k, v in sorted(LABELS.items())] + [["ffxiv", "", 0, "Final Fantasy XIV", 0]]))
        for start in range(0, len(bank), 5000):
            write(f"term_bank_{start // 5000 + 1}.json", json_bytes(bank[start:start + 5000]))
        for filename in ("source_manifest.json", "image_manifest.json", "character_coverage.tsv", "curated.tsv", "README.md"):
            write(filename, (ROOT / filename).read_bytes())
        for image in images:
            write(image["path"], (ROOT / image["path"]).read_bytes())
    print(json.dumps({k: v for k, v in report.items() if k != "source_files"}, ensure_ascii=False, indent=2))
    print(f"Created {archive} ({archive.stat().st_size / 1024 / 1024:.1f} MiB)")


if __name__ == "__main__":
    sys.stdout.reconfigure(encoding="utf-8")
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--fetch-images", action="store_true", help="Fetch curated images (requires Pillow); otherwise use the existing manifest")
    args = parser.parse_args()
    images = acquire_images() if args.fetch_images else json.loads((ROOT / "image_manifest.json").read_text(encoding="utf-8"))["images"]
    build(images)
