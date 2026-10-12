#!/usr/bin/env python3
"""Build the plugin and write the two release zips.

  dist/NoMissileCam-<version>-plugins.zip  files at the zip root, for mod managers (NOMM/NOMNOM unpack
                                           the first release asset into BepInEx/plugins/<mod id>/).
                                           Upload it FIRST.
  dist/NoMissileCam-<version>.zip          the same files under BepInEx/plugins/NoMissileCam/, for players
                                           who unzip it into the game folder by hand. Upload it second.

Also writes dist/SHA256SUMS for the two zips.

Usage: scripts/package.py [extra dotnet build args, e.g. -p:GameManagedDir=...]
"""
import hashlib
import re
import subprocess
import sys
import zipfile
from pathlib import Path

NAME = "NoMissileCam"

root = Path(__file__).resolve().parent.parent
version = re.search(r"<Version>(.*?)</Version>", (root / f"{NAME}.csproj").read_text()).group(1)
plugin_version = re.search(r'const string Version = "(.*?)"', (root / "src/Plugin.cs").read_text()).group(1)
if plugin_version != version:
    sys.exit(f"Version mismatch: csproj {version}, Plugin.cs {plugin_version}")
subprocess.run(["dotnet", "build", "-c", "Release", *sys.argv[1:]], cwd=root, check=True)

dist = root / "dist"
dist.mkdir(exist_ok=True)
files = [root / "bin/Release/netstandard2.1" / f"{NAME}.dll", root / "README.md", root / "LICENSE"]
outputs = [
    (dist / f"{NAME}-{version}-plugins.zip", ""),
    (dist / f"{NAME}-{version}.zip", f"BepInEx/plugins/{NAME}/"),
]
sums = []
for out, prefix in outputs:
    with zipfile.ZipFile(out, "w", zipfile.ZIP_DEFLATED) as z:
        for f in files:
            z.write(f, prefix + f.name)
    sums.append(f"{hashlib.sha256(out.read_bytes()).hexdigest()}  {out.name}")
    print(out)
(dist / "SHA256SUMS").write_text("\n".join(sums) + "\n")
print("\n".join(sums))
