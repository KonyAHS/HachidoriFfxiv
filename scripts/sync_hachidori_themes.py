"""Copies Hachidori's popup theme stylesheets into HachidoriFfxiv/Themes.

The plugin interprets these stylesheets (see Rendering/Css/PopupThemeEngine.cs) so its popup
follows the theme selected in Hachidori. Run it again after a Hachidori update.

Usage: python -I scripts/sync_hachidori_themes.py <Hachidori extension directory>
  e.g. "%LOCALAPPDATA%/Google/Chrome/User Data/Default/Extensions/<id>/<version>"
"""
import json
import shutil
import sys
from pathlib import Path

source = Path(sys.argv[1])
target = Path(__file__).resolve().parent.parent / "HachidoriFfxiv" / "Themes"
target.mkdir(exist_ok=True)

shutil.copyfile(source / "render" / "reader.css", target / "default.css")
themes = json.loads((source / "vendor" / "themes" / "index.json").read_text(encoding="utf-8"))["themes"]
for theme in themes:
    slug = theme["slug"]
    folder = source / "vendor" / "themes" / slug
    if (folder / "theme.css").exists():
        shutil.copyfile(folder / "theme.css", target / f"{slug}.css")
    for extra in ("ATTRIBUTION.md", "LICENSE.Apache-2.0"):
        if (folder / extra).exists():
            shutil.copyfile(folder / extra, target / f"{slug}.{extra}")

build = json.loads((source / "SOURCE.json").read_text(encoding="utf-8")) if (source / "SOURCE.json").exists() else {}
(target / "SOURCE.txt").write_text(
    "Copied from Hachidori (GPL-3.0-or-later) by scripts/sync_hachidori_themes.py.\n"
    f"Hachidori version: {build.get('version', 'unknown')}, revision {build.get('revision', 'unknown')}.\n"
    "default.css is Hachidori's render/reader.css; the others are vendor/themes/<slug>/theme.css.\n",
    encoding="utf-8")
print(f"Synced {len(themes)} themes into {target}")
