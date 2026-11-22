"""Validate archive schemas, embedded images, provenance, and requested coverage.

Requires Pillow and jsonschema. Downloads and caches Yomitan's official schemas.
"""
import csv
import io
import json
import re
import sys
import zipfile

from PIL import Image
from jsonschema import Draft7Validator

from build_dictionary import ROOT, COMMIT, SHEETS, clean, fetch


def extract_plain_text(node):
    if isinstance(node, str):
        return node
    if isinstance(node, list):
        return " ".join(extract_plain_text(x) for x in node if x is not None)
    if isinstance(node, dict):
        if node.get("type") == "structured-content":
            return extract_plain_text(node.get("content"))
        return extract_plain_text(node.get("content"))
    return ""


def iter_images(node):
    if isinstance(node, list):
        for x in node:
            yield from iter_images(x)
    elif isinstance(node, dict):
        if node.get("type") == "image" or node.get("tag") == "img":
            yield node
        if "content" in node:
            yield from iter_images(node["content"])


def main():
    schemas = {}
    for kind, filename in {
        "index": "dictionary-index-schema.json",
        "term": "dictionary-term-bank-v3-schema.json",
        "tag": "dictionary-tag-bank-v3-schema.json",
    }.items():
        url = "https://raw.githubusercontent.com/yomidevs/yomitan/master/ext/data/schemas/" + filename
        schemas[kind] = Draft7Validator(json.loads(fetch(url, ROOT / ".cache" / "schemas" / filename)))

    with zipfile.ZipFile(ROOT / "FFXIV-Hachidori-JP-EN.zip") as z:
        assert z.testzip() is None, "Corrupt ZIP"
        schemas["index"].validate(json.loads(z.read("index.json")))
        terms = []
        for name in z.namelist():
            if name.startswith("term_bank_"):
                bank = json.loads(z.read(name))
                schemas["term"].validate(bank)
                terms.extend(bank)
            if name.startswith("tag_bank_"):
                schemas["tag"].validate(json.loads(z.read(name)))

        report = json.loads(z.read("source_manifest.json"))
        manifest = json.loads(z.read("image_manifest.json"))
        assert len(terms) == report["entries"]
        assert not report["unmatched_image_targets"], report["unmatched_image_targets"]
        assert len({t[6] for t in terms}) == len(terms), "Duplicate sequences"

        # Every entry uses single structured-content root (no bogus 1. 2. 3. numbering)
        for term in terms:
            assert len(term[5]) == 1 and isinstance(term[5][0], dict) and term[5][0].get("type") == "structured-content"
            assert term[0].strip() == term[0] and "\ufffd" not in term[0]

        assert len([t for t in terms if t[0] == "アルフィノ"]) == 1, "Alphinaud should be one lookup entry"

        images = {i["path"]: i for i in manifest["images"]}
        used = set()
        for term in terms:
            for img in iter_images(term[5]):
                path = img["path"]
                assert path in images and path in z.namelist(), f"Missing image: {path}"
                with Image.open(io.BytesIO(z.read(path))) as im:
                    im.verify()
                used.add(path)
        assert used == set(images), f"Embedded image not referenced: {set(images) - used}"

        # Spot-check core FFXIV names, character aliases, game systems, and player lingo
        by_expr = {}
        for t in terms:
            by_expr.setdefault(t[0], []).append(t)

        probes = {
            "ヤ・シュトラ": "Y'shtola",
            "ヤシュトラ": "Y'shtola",
            "ヤ・シュトラ・ルル": "Y'shtola Rhul",
            "アルフィノ": "Alphinaud",
            "アリゼー": "Alisaie",
            "ゼノス": "Zenos",
            "ラマチ": "Wuk Lamat",
            "ラハ": "G'raha Tia",
            "エメト": "Emet-Selch",
            "リムサ・ロミンサ": "Limsa Lominsa",
            "リムサ": "Limsa Lominsa",
            "オールド・シャーレアン": "Old Sharlayan",
            "ヴァイパー": "viper",
            "ピクトマンサー": "pictomancer",
            "竜騎士": "dragoon",
            "木工師": "carpenter",
            "星芒祭": "Starlight Celebration",
            "紅蓮祭": "Moonfire Faire",
            "新生祭": "The Rising",
            "守護天節": "All Saints' Wake",
            "降神祭": "Heavensturn",
            "ヒカセン": "Warrior of Light",
            "クリ目": "Aim to Clear",
            "消化": "Weekly reclear",
            "取り抜け": "Loot and scoot",
            "頭割り": "Stack marker",
            "シャキる": "Queue pop",
            "オスッテ": "Male Miqo'te",
            "メスッテ": "Female Miqo'te",
            "ミラプリ": "Glamour",
            "新式": "Crafted raid gear",
            "禁断": "Pentamelding",
            "剛力のマテリア": "Strength Materia",
            "被ダメージ上昇": "Vulnerability Up",
            "死の宣告": "Doom",
            "ハルオーネ": "Halone",
            "〆": "Closed",
            "MT": "Main Tank",
        }
        for japanese, expected in probes.items():
            hits = by_expr.get(japanese, [])
            assert any(expected.casefold() in extract_plain_text(t[5]).casefold() for t in hits), (japanese, expected)

        assert any(t[0] == "竜騎士" and t[1] == "りゅうきし" for t in by_expr["竜騎士"])

        # Check character portraits on both canonical names and aliases/new icons
        for japanese in [
            "ヤ・シュトラ", "ヤシュトラ", "リムサ・ロミンサ", "ラマチ", "ゼノス", "ラハ", "エメト",
            "コーナ", "ゾラージャ", "グルージャジャ", "バクージャジャ", "スフェーン", "カフキワ",
            "ヘルメス", "ヒュトロダエウス", "アテナ", "N-7000", "カリュクス", "ハルマルト",
            "パパリモ", "イゼル", "ヨツユ", "ヒルディブランド", "ネロ", "マトーヤ", "ムーンブリダ",
        ]:
            hits = by_expr.get(japanese, [])
            assert any(list(iter_images(t[5])) for t in hits), f"Expected image missing for: {japanese}"

        coverage = list(csv.DictReader(io.StringIO(z.read("character_coverage.tsv").decode("utf-8")), delimiter="\t"))
        assert sum(r["image_status"] == "embedded" for r in coverage) == report["images_by_category"]["character"]

        # Verify every game-table row provenance matches pinned 7.58 CSVs
        tables = {}
        aligned = 0
        with (ROOT / "vocabulary.tsv").open(encoding="utf-8", newline="") as f:
            vocabulary = list(csv.DictReader(f, delimiter="\t"))
        assert len(vocabulary) == len(terms)
        for entry in vocabulary:
            for source in entry["sources"].split(";"):
                match = re.fullmatch(r"(\w+):(\d+)(:NameShort|:Feminine)?", source)
                if not match:
                    continue
                sheet, row_id, suffix = match.groups()
                if sheet not in tables:
                    tables[sheet] = {}
                    for language in ("ja", "en"):
                        path = ROOT / ".cache" / COMMIT / language / (sheet + ".csv")
                        with path.open(encoding="utf-8-sig", newline="") as rows:
                            tables[sheet][language] = {r["#"]: r for r in csv.DictReader(rows)}
                field = SHEETS[sheet][0]
                if suffix == ":NameShort":
                    ja_field, en_field = "NameShort", field
                elif suffix == ":Feminine":
                    ja_field, en_field = "Feminine", "Feminine"
                else:
                    ja_field, en_field = field, field
                japanese = clean(tables[sheet]["ja"][row_id][ja_field])
                english = clean(tables[sheet]["en"][row_id][en_field])
                assert (japanese, english.casefold()) == (entry["expression"], entry["english"].casefold()), (source, japanese, english, entry)
                aligned += 1

        print(f"PASS: official Yomitan v3 schemas, ZIP integrity, {len(terms):,} entries, {len(used)} embedded images, single-card structured formatting.")
        print(f"PASS: {aligned:,} source-row references match the pinned 7.58 Japanese and English game tables.")


if __name__ == "__main__":
    sys.stdout.reconfigure(encoding="utf-8")
    main()
