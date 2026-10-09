"""Fails if any NuGet dependency (direct or transitive) is not under an approved open-source licence.

Run after `dotnet restore`:  python build/license-audit.py
Reads each package's .nuspec from the NuGet cache. Packages that only declare a licence URL are
listed in KNOWN_URL_LICENSES after a manual check; anything unknown fails the build.
"""
import json
import os
import re
import subprocess
import sys
from pathlib import Path

ALLOWED = {"MIT", "Apache-2.0", "BSD-2-Clause", "BSD-3-Clause", "ISC", "OFL-1.1", "Zlib", "MS-PL", "Unlicense", "PostgreSQL"}

# id (lower-case) -> SPDX id, verified by hand because the package only declares a URL.
KNOWN_URL_LICENSES = {
    "avalonia.angle.windows.natives": "BSD-3-Clause",  # ANGLE Project licence file, checked 2026-10-09 (v2.1.25547)
    "xunit.abstractions": "Apache-2.0",                # xunit license.txt, checked 2026-10-09 (v2.0.3, test-only)
}

# Build/test-only packages that never ship inside the app.
NOT_SHIPPED = {"microsoft.net.test.sdk", "microsoft.codecoverage", "microsoft.testplatform.objectmodel",
               "microsoft.testplatform.testhost", "xunit", "xunit.abstractions", "xunit.analyzers", "xunit.assert",
               "xunit.core", "xunit.extensibility.core", "xunit.extensibility.execution", "xunit.runner.visualstudio",
               "avalonia.headless", "avalonia.headless.xunit", "avalonia.diagnostics", "avalonia.buildservices"}

ROOT = Path(__file__).resolve().parent.parent
cache = Path(os.environ.get("NUGET_PACKAGES") or Path.home() / ".nuget" / "packages")


def packages():
    out = subprocess.run(["dotnet", "list", str(ROOT / "PokeStudio.sln"), "package", "--include-transitive", "--format", "json"],
                         capture_output=True, text=True, check=True).stdout
    found = {}
    for project in json.loads(out).get("projects", []):
        for fw in project.get("frameworks", []):
            for key in ("topLevelPackages", "transitivePackages"):
                for p in fw.get(key, []):
                    found[p["id"]] = p["resolvedVersion"]
    return found


def license_of(pkg_id, version):
    folder = cache / pkg_id.lower() / version.lower()
    nuspec = next(folder.glob("*.nuspec"), None)
    if nuspec is None:
        return None, "nuspec not found (run dotnet restore)"
    text = nuspec.read_text(encoding="utf-8", errors="replace")
    m = re.search(r'<license\s+type="expression"\s*>([^<]+)</license>', text)
    if m:
        return m.group(1).strip(), None
    m = re.search(r'<license\s+type="file"\s*>([^<]+)</license>', text)
    if m:
        return KNOWN_URL_LICENSES.get(pkg_id.lower()), f"licence file {m.group(1)}"
    m = re.search(r"<licenseUrl>([^<]+)</licenseUrl>", text)
    return KNOWN_URL_LICENSES.get(pkg_id.lower()), (f"licence URL {m.group(1)}" if m else "no licence declared")


def ok(expr):
    # handles "MIT", "Apache-2.0 OR MIT", "(MIT AND BSD-3-Clause)"
    tokens = [t for t in re.split(r"[()\s]+", expr) if t]
    if "OR" in tokens:
        return any(t in ALLOWED for t in tokens if t not in ("OR", "AND"))
    return all(t in ALLOWED for t in tokens if t not in ("AND",))


def main():
    failures = []
    rows = []
    for pkg_id, version in sorted(packages().items(), key=lambda kv: kv[0].lower()):
        lic, note = license_of(pkg_id, version)
        shipped = pkg_id.lower() not in NOT_SHIPPED
        status = "ok" if lic and ok(lic) else "FAIL"
        rows.append(f"{status:4}  {'ship' if shipped else 'dev '}  {pkg_id} {version}: {lic or '?'}{'  (' + note + ')' if note else ''}")
        if status == "FAIL":
            failures.append(pkg_id)
    print("\n".join(rows))
    if failures:
        print(f"\nNot approved: {', '.join(failures)}. Check the licence and add it to KNOWN_URL_LICENSES or remove the package.")
        return 1
    print(f"\nAll {len(rows)} packages use approved open-source licences.")
    return 0


if __name__ == "__main__":
    sys.exit(main())
