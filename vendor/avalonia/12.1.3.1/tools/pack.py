"""Construct the local NuGet artifact from pinned official bytes and source-built Base/Controls."""

from __future__ import annotations

import argparse
import hashlib
import io
import json
from pathlib import Path
import subprocess
import xml.etree.ElementTree as ET
from zipfile import ZipFile, ZipInfo, ZIP_DEFLATED

UPSTREAM_COMMIT = "8eeda4f6f546165b3f72e63c9f42247abb306905"
ORIGINAL_SHA256 = "dffb6605b02e144866cb23765eb5af7255379ebb7ede75cf11066f3248d83734"
VERSION = "12.1.3.1"
TFMS = ("net8.0", "net10.0")
ASSEMBLIES = ("Avalonia.Base", "Avalonia.Controls")
PATCH_NAMES = (
    "0001-textbox-selection-notifications.patch",
    "0002-textline-empty-ranges.patch",
)
DESCRIPTION = (
    "Unofficial local Avalonia 12.1.3 source backports: batch TextBox text and "
    "input-method selection notifications and return empty TextLine bounds for empty ranges. "
    "Both fixes are merged upstream but not yet in this stable base. "
    "Not an upstream release. See lumi-patch/PROVENANCE.md."
)


def sha256(data: bytes) -> str:
    return hashlib.sha256(data).hexdigest()


def xml_bytes(root: ET.Element, namespace: str) -> bytes:
    ET.register_namespace("", namespace)
    return ET.tostring(root, encoding="utf-8", xml_declaration=True)


def main() -> None:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--root", type=Path, required=True)
    parser.add_argument("--inputs", type=Path, required=True)
    args = parser.parse_args()
    root = args.root.resolve()
    inputs = args.inputs.resolve()
    source = root / "source"
    revision = subprocess.check_output(
        ["git", "-C", str(source), "rev-parse", "HEAD"], text=True
    ).strip()
    if revision != UPSTREAM_COMMIT:
        raise SystemExit(f"Unexpected source commit: {revision}")
    for patch_name in PATCH_NAMES:
        subprocess.run(
            [
                "git", "-C", str(source), "apply", "--reverse", "--check",
                str(inputs / "patches" / patch_name),
            ],
            check=True,
        )
    tracked_changes = subprocess.check_output(
        ["git", "-C", str(source), "diff", "--name-only"], text=True
    ).splitlines()
    if tracked_changes != [
        "src/Avalonia.Base/Media/TextFormatting/TextLine.cs",
        "src/Avalonia.Base/Media/TextFormatting/TextLineImpl.cs",
        "src/Avalonia.Controls/TextBox.cs",
        "src/Avalonia.Controls/TextBoxTextInputMethodClient.cs",
        "tests/Avalonia.Skia.UnitTests/Media/TextFormatting/TextLineTests.cs",
    ]:
        raise SystemExit(f"Unexpected tracked source changes: {tracked_changes}")

    original_bytes = (root / "reference" / "Avalonia.12.1.3.nupkg").read_bytes()
    if sha256(original_bytes) != ORIGINAL_SHA256:
        raise SystemExit("Original package SHA-256 mismatch.")
    with ZipFile(io.BytesIO(original_bytes)) as archive:
        original = {entry.filename: archive.read(entry) for entry in archive.infolist()}
    package = dict(original)
    removed = (".signature.p7s", "_manifest/cyclonedx/bom.cdx.json")
    for name in removed:
        del package[name]

    rebuilt = {}
    for tfm in TFMS:
        for assembly in ASSEMBLIES:
            output = source / "src" / assembly / "bin" / "Release" / tfm
            for suffix in ("dll", "pdb"):
                name = f"lib/{tfm}/{assembly}.{suffix}"
                payload = (output / f"{assembly}.{suffix}").read_bytes()
                package[name] = payload
                rebuilt[name] = sha256(payload)
            if package[f"lib/{tfm}/{assembly}.dll"] == original[f"lib/{tfm}/{assembly}.dll"]:
                raise SystemExit(f"{tfm} {assembly} DLL was not rebuilt.")

    ns = "http://schemas.microsoft.com/packaging/2013/05/nuspec.xsd"
    nuspec = ET.fromstring(package["Avalonia.nuspec"])
    metadata = nuspec.find(f"{{{ns}}}metadata")
    assert metadata is not None
    assert metadata.find(f"{{{ns}}}id").text == "Avalonia"
    assert metadata.find(f"{{{ns}}}version").text == "12.1.3"
    assert metadata.find(f"{{{ns}}}repository").get("commit") == UPSTREAM_COMMIT
    metadata.find(f"{{{ns}}}version").text = VERSION
    metadata.find(f"{{{ns}}}description").text = DESCRIPTION
    metadata.find(f"{{{ns}}}releaseNotes").text = "Local source backports; see lumi-patch/PROVENANCE.md."
    package["Avalonia.nuspec"] = xml_bytes(nuspec, ns)

    name = "build/AvaloniaVersion.props"
    props = package[name].decode("utf-8-sig")
    old_version = "<AvaloniaMainPackageVersion>12.1.3</AvaloniaMainPackageVersion>"
    assert props.count(old_version) == 1
    package[name] = props.replace(
        old_version, f"<AvaloniaMainPackageVersion>{VERSION}</AvaloniaMainPackageVersion>"
    ).encode("utf-8")

    name = "package/services/metadata/core-properties/nuget.psmdcp"
    cp_ns = "http://schemas.openxmlformats.org/package/2006/metadata/core-properties"
    core = ET.fromstring(package[name])
    core.find(f"{{{cp_ns}}}version").text = VERSION
    core.find("{http://purl.org/dc/elements/1.1/}description").text = DESCRIPTION
    core.find(f"{{{cp_ns}}}lastModifiedBy").text = "Local Avalonia source-build packager (pack.py)"
    package[name] = xml_bytes(core, cp_ns)

    for patch_name in PATCH_NAMES:
        package[f"lumi-patch/{patch_name}"] = (inputs / "patches" / patch_name).read_bytes()
    package["lumi-patch/PROVENANCE.md"] = (inputs / "PROVENANCE.md").read_bytes()
    package["lumi-patch/LICENSE.md"] = (source / "licence.md").read_bytes()
    package["lumi-patch/NOTICE.md"] = (source / "NOTICE.md").read_bytes()

    ct_ns = "http://schemas.openxmlformats.org/package/2006/content-types"
    content_types = ET.fromstring(package["[Content_Types].xml"])
    for extension in ("pdb", "patch", "md"):
        ET.SubElement(content_types, f"{{{ct_ns}}}Default", {
            "Extension": extension, "ContentType": "application/octet-stream"
        })
    package["[Content_Types].xml"] = xml_bytes(content_types, ct_ns)

    allowed_changed = {
        "Avalonia.nuspec",
        "build/AvaloniaVersion.props",
        "package/services/metadata/core-properties/nuget.psmdcp",
        "[Content_Types].xml",
        *(f"lib/{tfm}/{assembly}.dll" for tfm in TFMS for assembly in ASSEMBLIES),
    }
    changed = {
        name for name in original.keys() & package.keys()
        if original[name] != package[name]
    }
    if changed != allowed_changed:
        raise SystemExit(f"Unexpected package payload changes: {changed ^ allowed_changed}")
    manifest = {
        "package": f"Avalonia/{VERSION}",
        "upstreamCommit": UPSTREAM_COMMIT,
        "officialPackageSha256": ORIGINAL_SHA256,
        "acceptedFixCommits": {
            "textBox": "1b50ee4bdd5e39d893f00ec52cb56efb35bb6212",
            "textLine": "11ea9b1120f1ef2a8bbf1a513c9e35520eb7f675",
        },
        "targetFrameworks": list(TFMS),
        "rebuiltPayloadSha256": rebuilt,
        "patchSha256": {
            patch_name: sha256(package[f"lumi-patch/{patch_name}"])
            for patch_name in PATCH_NAMES
        },
        "changedOriginalEntries": sorted(changed),
        "removedOriginalEntries": list(removed),
        "unchangedOriginalEntryCount": len(original) - len(changed) - len(removed),
        "referenceAssemblies": "All official ref/ entries preserved byte-for-byte; no API change.",
        "nugetPackageSignature": "Unsigned local modified package; original signature removed.",
    }
    package["lumi-patch/payload-manifest.json"] = (
        json.dumps(manifest, indent=2, sort_keys=True) + "\n"
    ).encode("utf-8")

    buffer = io.BytesIO()
    with ZipFile(buffer, "w", compression=ZIP_DEFLATED, compresslevel=9) as archive:
        for name in sorted(package):
            entry = ZipInfo(name, date_time=(2026, 10, 6, 0, 0, 0))
            entry.create_system = 0
            entry.external_attr = 0x20
            entry.compress_type = ZIP_DEFLATED
            archive.writestr(entry, package[name], compresslevel=9)
    result = buffer.getvalue()
    output = root / "feed" / f"Avalonia.{VERSION}.nupkg"
    output.parent.mkdir(parents=True, exist_ok=True)
    if output.exists() and output.read_bytes() != result:
        raise SystemExit(
            f"Refusing to overwrite {output} with different bytes at the same version. "
            "Use a new numeric revision for a new patch."
        )
    if not output.exists():
        output.write_bytes(result)
    print(json.dumps(manifest, indent=2, sort_keys=True))
    print(f"PACKAGE: {output}")
    print(f"SHA256: {sha256(result)}")
    print(f"SIZE: {len(result)} bytes")


if __name__ == "__main__":
    main()
