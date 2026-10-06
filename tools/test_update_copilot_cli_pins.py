import hashlib
import io
from pathlib import Path
import tarfile
import tempfile
import unittest
from unittest.mock import patch
import xml.etree.ElementTree as ET
from zipfile import ZipFile

import update_copilot_cli_pins as updater


class UpdateCopilotCliPinsTests(unittest.TestCase):
    def setUp(self) -> None:
        self.directory = tempfile.TemporaryDirectory()
        self.addCleanup(self.directory.cleanup)
        self.root = Path(self.directory.name)
        project = self.root / "src" / "Lumi" / "Lumi.csproj"
        project.parent.mkdir(parents=True)
        project.write_text(
            '<Project><ItemGroup><PackageReference Include="GitHub.Copilot.SDK" '
            'Version="1.2.3-preview.4" /></ItemGroup></Project>',
            encoding="utf-8",
        )
        self.pin_file = self.root / "build" / "Copilot" / "CopilotCliPins.props"
        self.pin_file.parent.mkdir(parents=True)
        self.pin_file.write_text("old pins\n", encoding="utf-8")
        self.sdk = io.BytesIO()
        with ZipFile(self.sdk, "w") as package:
            package.writestr(
                "build/GitHub.Copilot.SDK.props",
                "<Project><PropertyGroup><CopilotCliVersion>4.5.6-7</CopilotCliVersion>"
                "</PropertyGroup></Project>",
            )
        self.archives = {}
        self.binaries = {}
        self.downloaded_archives = []
        for rid, asset in updater.PLATFORMS.items():
            path = self.root / "src" / "Lumi" / "obj" / "copilot-full" / "4.5.6-7" / rid / asset
            path.parent.mkdir(parents=True)
            payload = rid.encode("ascii")
            if asset.endswith(".zip"):
                with ZipFile(path, "w") as package:
                    package.writestr("copilot.exe", payload)
            else:
                with tarfile.open(path, "w:gz") as package:
                    entry = tarfile.TarInfo("copilot")
                    entry.size = len(payload)
                    entry.mode = 0o755
                    package.addfile(entry, io.BytesIO(payload))
            self.archives[asset] = path
            self.binaries[asset] = hashlib.sha256(payload).hexdigest()
        self.manifest = "\n".join(
            f"{hashlib.sha256(path.read_bytes()).hexdigest()}  {asset}"
            for asset, path in self.archives.items()
        )

    def download(self, url: str, destination: Path) -> None:
        if url.endswith(".nupkg"):
            self.assertIn("/1.2.3-preview.4/", url)
            destination.write_bytes(self.sdk.getvalue())
        elif url.endswith("SHA256SUMS.txt"):
            self.assertIn("/v4.5.6-7/", url)
            destination.write_text(self.manifest, encoding="utf-8")
        else:
            self.assertIn("/v4.5.6-7/", url)
            asset = url.rsplit("/", 1)[-1]
            self.assertIn(asset, self.archives)
            self.downloaded_archives.append(asset)
            destination.write_bytes(self.archives[asset].read_bytes())

    def run_update(self, check: bool = False) -> None:
        with patch.object(updater, "download", side_effect=self.download):
            updater.update_pins(self.root, check=check)

    def test_refresh_matches_sdk_cli_version_and_hashes_for_all_platforms(self) -> None:
        self.run_update()

        pins = ET.parse(self.pin_file).findall(".//LumiCopilotCliPin")
        self.assertEqual([], self.downloaded_archives)
        self.assertEqual(len(updater.PLATFORMS), len(pins))
        for pin, (rid, asset) in zip(pins, updater.PLATFORMS.items()):
            self.assertEqual(f"4.5.6-7/{rid}", pin.get("Include"))
            self.assertEqual(asset, pin.findtext("AssetName"))
            self.assertEqual(
                hashlib.sha256(self.archives[asset].read_bytes()).hexdigest(),
                pin.findtext("ArchiveSha256"),
            )
            self.assertEqual(self.binaries[asset], pin.findtext("ExecutableSha256"))

    def test_missing_archive_is_downloaded_temporarily(self) -> None:
        asset = "copilot-win32-x64.zip"
        cache_path = self.archives[asset]
        fixture_path = self.root / asset
        cache_path.rename(fixture_path)
        self.archives[asset] = fixture_path

        self.run_update()

        self.assertEqual([asset], self.downloaded_archives)
        self.assertFalse(cache_path.exists())
        pin = ET.parse(self.pin_file).find(".//LumiCopilotCliPin")
        self.assertIsNotNone(pin)
        self.assertEqual(self.binaries[asset], pin.findtext("ExecutableSha256"))

    def test_check_rejects_stale_pins_without_editing_and_accepts_refreshed_pins(self) -> None:
        with self.assertRaisesRegex(ValueError, "is stale"):
            self.run_update(check=True)
        self.assertEqual("old pins\n", self.pin_file.read_text(encoding="utf-8"))
        self.run_update()
        before = self.pin_file.read_bytes()
        self.run_update(check=True)
        self.assertEqual(before, self.pin_file.read_bytes())

    def test_corrupt_cached_archive_does_not_replace_pins(self) -> None:
        self.archives["copilot-win32-x64.zip"].write_bytes(b"corrupt archive")

        with self.assertRaisesRegex(ValueError, "Checksum mismatch"):
            self.run_update()

        self.assertEqual("old pins\n", self.pin_file.read_text(encoding="utf-8"))

    def test_incomplete_official_manifest_does_not_replace_pins(self) -> None:
        self.manifest = ""

        with self.assertRaisesRegex(ValueError, "exactly one checksum"):
            self.run_update()

        self.assertEqual("old pins\n", self.pin_file.read_text(encoding="utf-8"))

    def test_zip_extra_entries_and_tar_nonexecutable_mode_are_rejected(self) -> None:
        archive = self.archives["copilot-win32-x64.zip"]
        with ZipFile(archive, "a") as package:
            package.writestr("unexpected.txt", "extra")
        with self.assertRaisesRegex(ValueError, "Expected only copilot.exe"):
            updater.executable_hash(archive)

        archive = self.archives["copilot-linux-x64.tar.gz"]
        with tarfile.open(archive, "w:gz") as package:
            entry = tarfile.TarInfo("copilot")
            entry.size = 1
            entry.mode = 0o644
            package.addfile(entry, io.BytesIO(b"x"))
        with self.assertRaisesRegex(ValueError, "mode 0755"):
            updater.executable_hash(archive)


if __name__ == "__main__":
    unittest.main()
