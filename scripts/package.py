#!/usr/bin/env python3
"""Build the plugin and write a player zip that extracts into the Nuclear Option game folder.

Usage: scripts/package.py [extra dotnet build args, e.g. -p:GameManagedDir=...]
"""
import re
import subprocess
import sys
import zipfile
from pathlib import Path

NAME = "NoMissileCam"

root = Path(__file__).resolve().parent.parent
version = re.search(r"<Version>(.*?)</Version>", (root / f"{NAME}.csproj").read_text()).group(1)
subprocess.run(["dotnet", "build", "-c", "Release", *sys.argv[1:]], cwd=root, check=True)

dist = root / "dist"
dist.mkdir(exist_ok=True)
out = dist / f"{NAME}-{version}.zip"
files = [root / "bin/Release/netstandard2.1" / f"{NAME}.dll", root / "README.md", root / "LICENSE"]
with zipfile.ZipFile(out, "w", zipfile.ZIP_DEFLATED) as z:
    for f in files:
        z.write(f, f"BepInEx/plugins/{NAME}/{f.name}")
print(out)
