"""Refresh full CLI pins for the official SDK version selected in Lumi.csproj."""

from __future__ import annotations

import argparse
import hashlib
from pathlib import Path
import re
import shutil
import sys
import tarfile
import tempfile
from typing import BinaryIO
from urllib.request import urlopen
import xml.etree.ElementTree as ET
from zipfile import BadZipFile, ZipFile


PLATFORMS = {
    "win-x64": "copilot-win32-x64.zip",
    "win-arm64": "copilot-win32-arm64.zip",
    "linux-x64": "copilot-linux-x64.tar.gz",
    "linux-arm64": "copilot-linux-arm64.tar.gz",
    "linux-musl-x64": "copilot-linuxmusl-x64.tar.gz",
    "linux-musl-arm64": "copilot-linuxmusl-arm64.tar.gz",
    "osx-x64": "copilot-darwin-x64.tar.gz",
    "osx-arm64": "copilot-darwin-arm64.tar.gz",
}


def download(url: str, destination: Path) -> None:
    with urlopen(url, timeout=600) as response, destination.open("wb") as output:
        shutil.copyfileobj(response, output)


def sha256(stream: BinaryIO) -> str:
    digest = hashlib.sha256()
    for block in iter(lambda: stream.read(1024 * 1024), b""):
        digest.update(block)
    return digest.hexdigest()


def executable_hash(archive: Path) -> str:
    if archive.suffix == ".zip":
        with ZipFile(archive) as package:
            entries = package.infolist()
            if len(entries) != 1 or entries[0].filename != "copilot.exe":
                raise ValueError(f"Expected only copilot.exe in {archive}.")
            with package.open(entries[0]) as executable:
                return sha256(executable)

    with tarfile.open(archive, "r:gz") as package:
        entries = package.getmembers()
        if (len(entries) != 1 or entries[0].name != "copilot"
                or not entries[0].isfile() or entries[0].mode != 0o755):
            raise ValueError(f"Expected only a regular copilot entry with mode 0755 in {archive}.")
        executable = package.extractfile(entries[0])
        if executable is None:
            raise ValueError(f"No executable content in {archive}.")
        with executable:
            return sha256(executable)


def version(value: str | None, label: str) -> str:
    if value is None or re.fullmatch(r"\d+\.\d+\.\d+(?:-[A-Za-z0-9.-]+)?", value) is None:
        raise ValueError(f"{label} must be an explicit version, got {value!r}.")
    return value


def render_pins(cli_version: str, pins: list[tuple[str, str, str, str]]) -> str:
    lines = [
        "<Project>",
        f"  <!-- Official v{cli_version} archive digests match SHA256SUMS.txt. Executable hashes",
        "       were computed after archive verification and single-executable layout checks. -->",
        "  <ItemGroup>",
    ]
    for rid, asset, archive_hash, binary_hash in pins:
        lines.extend([
            f'    <LumiCopilotCliPin Include="{cli_version}/{rid}">',
            f"      <AssetName>{asset}</AssetName>",
            f"      <ArchiveSha256>{archive_hash}</ArchiveSha256>",
            f"      <ExecutableSha256>{binary_hash}</ExecutableSha256>",
            "    </LumiCopilotCliPin>",
        ])
    return "\n".join([*lines, "  </ItemGroup>", "</Project>", ""])


def update_pins(root: Path, check: bool = False) -> None:
    project = root / "src" / "Lumi" / "Lumi.csproj"
    references = ET.parse(project).findall(".//PackageReference[@Include='GitHub.Copilot.SDK']")
    if len(references) != 1:
        raise ValueError(f"Expected one official GitHub.Copilot.SDK reference in {project}.")
    sdk_version = version(references[0].get("Version"), "SDK version")
    pin_file = root / "build" / "Copilot" / "CopilotCliPins.props"

    with tempfile.TemporaryDirectory(prefix="lumi-copilot-pins-") as directory:
        temporary = Path(directory)
        sdk = temporary / "sdk.nupkg"
        download(
            f"https://api.nuget.org/v3-flatcontainer/github.copilot.sdk/{sdk_version.lower()}/"
            f"github.copilot.sdk.{sdk_version.lower()}.nupkg",
            sdk,
        )
        with ZipFile(sdk) as package:
            if "build/GitHub.Copilot.SDK.props" not in package.namelist():
                raise ValueError("The official SDK package has no CLI version props.")
            props = ET.fromstring(package.read("build/GitHub.Copilot.SDK.props"))
            cli_version = version(props.findtext(".//CopilotCliVersion"), "CLI version")

        release = f"https://github.com/github/copilot-cli/releases/download/v{cli_version}/"
        manifest_file = temporary / "SHA256SUMS.txt"
        download(release + "SHA256SUMS.txt", manifest_file)
        manifest = manifest_file.read_text(encoding="utf-8")
        pins = []
        for rid, asset in PLATFORMS.items():
            matches = re.findall(
                rf"^([a-fA-F0-9]{{64}})[ \t]+\*?{re.escape(asset)}[ \t]*$",
                manifest,
                re.MULTILINE,
            )
            if len(matches) != 1:
                raise ValueError(f"Official SHA256SUMS.txt must contain exactly one checksum for {asset}.")
            expected = matches[0].lower()
            archive = root / "src" / "Lumi" / "obj" / "copilot-full" / cli_version / rid / asset
            if not archive.is_file():
                archive = temporary / asset
                download(release + asset, archive)
            with archive.open("rb") as stream:
                actual = sha256(stream)
            if actual != expected:
                raise ValueError(f"Checksum mismatch for {archive}: expected {expected}, got {actual}.")
            pins.append((rid, asset, actual, executable_hash(archive)))
            print(f"Verified {cli_version}/{rid}.", flush=True)

        content = render_pins(cli_version, pins)
        if check:
            if pin_file.read_text(encoding="utf-8") != content:
                raise ValueError(f"{pin_file} is stale; rerun this script without --check.")
            print(f"CLI pins match GitHub.Copilot.SDK {sdk_version}.")
            return

        pending = None
        try:
            with tempfile.NamedTemporaryFile(
                mode="w", encoding="utf-8", newline="\n",
                dir=pin_file.parent, prefix=pin_file.name + ".", delete=False,
            ) as output:
                pending = Path(output.name)
                output.write(content)
            pending.replace(pin_file)
        finally:
            if pending is not None:
                pending.unlink(missing_ok=True)
        print(f"Updated {pin_file} for GitHub.Copilot.SDK {sdk_version}.")


def main() -> int:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--check", action="store_true", help="Verify current pins without editing them.")
    args = parser.parse_args()
    try:
        update_pins(Path(__file__).resolve().parent.parent, check=args.check)
    except (OSError, ValueError, ET.ParseError, BadZipFile, tarfile.TarError) as error:
        print(f"Copilot CLI pin update failed: {error}", file=sys.stderr)
        return 1
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
