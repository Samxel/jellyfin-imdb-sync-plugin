"""Adds a release to manifest.json (the Jellyfin plugin repository file)."""
import json
import sys
from datetime import datetime, timezone
from pathlib import Path

import yaml  # PyYAML is preinstalled on GitHub runners

version, url, checksum = sys.argv[1:4]
meta = yaml.safe_load(Path("build.yaml").read_text(encoding="utf-8"))
path = Path("manifest.json")
manifest = json.loads(path.read_text(encoding="utf-8")) if path.exists() else []

plugin = next((p for p in manifest if p["guid"] == meta["guid"]), None)
if plugin is None:
    plugin = {"guid": meta["guid"], "versions": []}
    manifest.append(plugin)

plugin.update({
    "name": meta["name"],
    "description": meta["description"].strip(),
    "overview": meta["overview"],
    "owner": meta["owner"],
    "category": meta["category"],
})
plugin["versions"] = [v for v in plugin["versions"] if v["version"] != version]
plugin["versions"].insert(0, {
    "version": version,
    "changelog": meta.get("changelog", "").strip(),
    "targetAbi": meta["targetAbi"],
    "sourceUrl": url,
    "checksum": checksum,
    "timestamp": datetime.now(timezone.utc).strftime("%Y-%m-%dT%H:%M:%SZ"),
})
path.write_text(json.dumps(manifest, indent=2) + "\n", encoding="utf-8")
