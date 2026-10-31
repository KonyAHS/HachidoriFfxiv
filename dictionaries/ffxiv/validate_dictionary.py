"""Validate archive schemas, embedded images, provenance, and requested coverage.

Requires Pillow and jsonschema. Downloads and caches Yomitan's official schemas.
"""
import io
import csv
import json
import re
import sys
import zipfile

from PIL import Image
from jsonschema import Draft7Validator

from build_dictionary import ROOT, COMMIT, SHEETS, clean, fetch


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
        assert len({(t[0], t[1], t[2], t[5][0]) for t in terms}) == len(terms), "Duplicate entries"
        bilingual_categories = {}
        for term in terms:
            english = term[5][0].rsplit(" — ", 1)[0].casefold()
            bilingual_categories.setdefault((term[0], english), set()).add(term[2])
        assert not any("enemy" in categories and categories & {"character", "minion", "mount"}
                       for categories in bilingual_categories.values()), "Duplicate battle / NPC / companion entry"
        assert len([t for t in terms if t[0] == "アルフィノ"]) == 1, "Alphinaud should be one lookup entry"
        images = {i["path"]: i for i in manifest["images"]}
        used = set()
        for term in terms:
            assert term[0].strip() == term[0] and "\ufffd" not in term[0]
            for glossary in term[5]:
                if isinstance(glossary, dict) and glossary.get("type") == "image":
                    path = glossary["path"]
                    assert path in images and path in z.namelist(), f"Missing image: {path}"
                    with Image.open(io.BytesIO(z.read(path))) as im:
                        assert im.size == (glossary["width"], glossary["height"])
                        im.verify()
                    used.add(path)
        assert used == set(images), "Embedded image not referenced by any entry"
        for japanese, english in {
            "ヤ・シュトラ": "Y'shtola", "アルフィノ": "Alphinaud", "アリゼー": "Alisaie",
            "リムサ・ロミンサ": "Limsa Lominsa", "グリダニア": "Gridania", "ウルダハ": "Ul'dah",
            "オールド・シャーレアン": "Old Sharlayan", "ヴァイパー": "viper",
            "ピクトマンサー": "pictomancer", "竜騎士": "dragoon", "木工師": "carpenter",
            "星芒祭": "Starlight Celebration", "紅蓮祭": "Moonfire Faire", "新生祭": "The Rising",
            "守護天節": "All Saints' Wake", "降神祭": "Heavensturn", "ヒカセン": "Warrior of Light",
        }.items():
            assert any(t[0] == japanese and english.casefold() in t[5][0].casefold() for t in terms), (japanese, english)
        assert any(t[0] == "竜騎士" and t[1] == "りゅうきし" for t in terms)
        assert any(t[0] == "ヤ・シュトラ" and any(isinstance(g, dict) and g.get("type") == "image" for g in t[5]) for t in terms)
        assert any(t[0] == "リムサ・ロミンサ" and any(isinstance(g, dict) and g.get("type") == "image" for g in t[5]) for t in terms)
        for japanese in ["コーナ", "ゾラージャ", "グルージャジャ", "バクージャジャ", "スフェーン", "カフキワ",
                         "ヤーナ", "ネユニ", "ユトロープ", "ヘルメス", "ヒュトロダエウス", "アテナ", "N-7000",
                         "カリュクス", "ハルマルト", "ゴルベーザ", "ドゥランテ"]:
            assert any(t[0] == japanese and any(isinstance(g, dict) and g.get("type") == "image" for g in t[5])
                       for t in terms), f"Recent character image missing: {japanese}"
        coverage = list(csv.DictReader(io.StringIO(z.read("character_coverage.tsv").decode("utf-8")), delimiter="\t"))
        assert sum(r["image_status"] == "embedded" for r in coverage) == report["images_by_category"]["character"]
        tables = {}
        aligned = 0
        with (ROOT / "vocabulary.tsv").open(encoding="utf-8", newline="") as f:
            vocabulary = list(csv.DictReader(f, delimiter="\t"))
        assert len(vocabulary) == len(terms)
        for entry in vocabulary:
            for source in entry["sources"].split(";"):
                match = re.fullmatch(r"(\w+):(\d+)(:NameShort)?", source)
                if not match:
                    continue  # Curated URLs / editorial vocabulary are separate.
                sheet, row_id, short = match.groups()
                if sheet not in tables:
                    tables[sheet] = {}
                    for language in ("ja", "en"):
                        path = ROOT / ".cache" / COMMIT / language / (sheet + ".csv")
                        with path.open(encoding="utf-8-sig", newline="") as rows:
                            tables[sheet][language] = {r["#"]: r for r in csv.DictReader(rows)}
                field = SHEETS[sheet][0]
                japanese = clean(tables[sheet]["ja"][row_id]["NameShort" if short else field])
                english = clean(tables[sheet]["en"][row_id][field])
                assert (japanese, english.casefold()) == (entry["expression"], entry["english"].casefold()), source
                aligned += 1
        print(f"PASS: official Yomitan schemas, ZIP integrity, {len(terms):,} entries, {len(used)} embedded images, requested coverage and readings.")
        print(f"PASS: {aligned:,} source-row references match the pinned Japanese and English tables.")
        print("Hachidori import and live in-game lookup still require host validation.")


if __name__ == "__main__":
    sys.stdout.reconfigure(encoding="utf-8")
    main()
