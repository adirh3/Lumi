import argparse
import hashlib
import json
import os
from pathlib import Path
import plistlib
import platform
import subprocess
import time
import traceback


def write_json(path, value):
    path.write_text(json.dumps(value, indent=2), encoding="utf-8")


def command(args, log=None, required=True, timeout=90):
    result = subprocess.run(
        [str(arg) for arg in args],
        text=True,
        stdout=subprocess.PIPE,
        stderr=subprocess.PIPE,
        timeout=timeout,
    )
    if log is not None:
        log.write_text(
            json.dumps({"arguments": [str(arg) for arg in args], "exitCode": result.returncode})
            + "\nSTDOUT\n" + result.stdout + "\nSTDERR\n" + result.stderr,
            encoding="utf-8",
        )
    if required and result.returncode != 0:
        raise RuntimeError(f"{args[0]} exited {result.returncode}: {result.stderr[-2000:]}")
    return result


def cpu_seconds(value):
    days = 0
    if "-" in value:
        day, value = value.split("-", 1)
        days = int(day)
    total = 0.0
    for part in value.split(":"):
        total = total * 60 + float(part)
    return days * 86400 + total


def process_tree(root_pid):
    result = command(["ps", "-axo", "pid=,ppid=,time=,rss=,comm="])
    rows = {}
    for line in result.stdout.splitlines():
        fields = line.strip().split(None, 4)
        if len(fields) == 5:
            pid, parent, cpu, rss, executable = fields
            rows[int(pid)] = {
                "pid": int(pid), "parentPid": int(parent),
                "cpuSeconds": cpu_seconds(cpu), "rssBytes": int(rss) * 1024,
                "executable": executable,
            }
    if root_pid not in rows:
        raise RuntimeError(f"The installed Lumi process {root_pid} exited during measurement.")
    selected = {root_pid}
    while True:
        added = {pid for pid, row in rows.items() if row["parentPid"] in selected}
        if added <= selected:
            break
        selected.update(added)
    return {pid: rows[pid] for pid in selected if pid in rows}


def sha256(path):
    digest = hashlib.sha256()
    with path.open("rb") as stream:
        for block in iter(lambda: stream.read(1024 * 1024), b""):
            digest.update(block)
    return digest.hexdigest()


def inspector(executable, action, target):
    return json.loads(command([executable, action, str(target)]).stdout)


def launch(bundle, executable, output, allow_installer_launch=False):
    existing = inspector(executable, "locate", bundle)
    if existing and not allow_installer_launch:
        raise RuntimeError(f"The target bundle is already running: {existing}")
    if existing:
        write_json(output / "installer-auto-launch.json", existing)
    else:
        command(["open", "-n", bundle], output / "launchservices-open.log")
    deadline = time.monotonic() + 60
    while time.monotonic() < deadline:
        apps = inspector(executable, "locate", bundle)
        if len(apps) == 1 and apps[0]["finishedLaunching"]:
            pid = apps[0]["pid"]
            write_json(output / "launched-application.json", apps[0])
            if Path(apps[0]["executablePath"]).resolve() != (bundle / "Contents" / "MacOS" / "Lumi").resolve():
                raise RuntimeError("LaunchServices started an unexpected executable.")
            inspector(executable, "activate", pid)
            return pid
        if len(apps) > 1:
            raise RuntimeError("LaunchServices returned multiple instances of the test bundle.")
        time.sleep(1)
    raise TimeoutError("The installed app did not finish its normal macOS launch.")


def close_owned_app(executable, pid, bundle, output):
    apps = inspector(executable, "locate", bundle)
    if not any(app["pid"] == pid for app in apps):
        return
    write_json(output / "quit-request.json", inspector(executable, "quit", pid))
    deadline = time.monotonic() + 25
    while time.monotonic() < deadline:
        if not any(app["pid"] == pid for app in inspector(executable, "locate", bundle)):
            return
        time.sleep(1)
    raise TimeoutError(f"Owned test app {pid} did not honor a normal macOS quit request.")


def capture(executable, pid, output, phase):
    state = inspector(executable, "inspect", pid)
    write_json(output / f"{phase}-window.json", state)
    windows = [
        window for window in state["windows"]
        if window.get("kCGWindowLayer") == 0 and window.get("kCGWindowIsOnscreen")
    ]
    if not state["hidden"] and not windows:
        raise RuntimeError(f"Installed app {pid} has no visible window to validate.")
    if windows:
        window = max(windows, key=lambda item: item.get("kCGWindowBounds", {}).get("Width", 0))
        command(
            ["screencapture", "-x", "-l", str(window["kCGWindowNumber"]), output / f"{phase}.png"],
            output / f"{phase}-screenshot.log", required=False, timeout=30,
        )
    return state


def measure(executable, pid, output, name, count=12, settle=20):
    time.sleep(settle)
    if name != "hidden":
        inspector(executable, "activate", pid)
        time.sleep(2)
    state = capture(executable, pid, output, name + "-before")
    previous = process_tree(pid)
    previous_time = time.monotonic()
    samples = []
    for index in range(count):
        time.sleep(5)
        current = process_tree(pid)
        now = time.monotonic()
        seconds = now - previous_time
        app = current[pid]
        children = []
        for child_pid, row in current.items():
            if child_pid == pid:
                continue
            old = previous.get(child_pid)
            children.append({
                **row,
                "oneCoreCpuPercent": (100 * (row["cpuSeconds"] - old["cpuSeconds"]) / seconds) if old else None,
            })
        sample = {
            "index": index,
            "seconds": seconds,
            "oneCoreCpuPercent": 100 * (app["cpuSeconds"] - previous[pid]["cpuSeconds"]) / seconds,
            "rssBytes": app["rssBytes"],
            "children": children,
        }
        samples.append(sample)
        write_json(output / f"{name}-cpu.json", {"windowBefore": state, "samples": samples})
        print(json.dumps({"phase": name, **sample}), flush=True)
        previous = current
        previous_time = now
    capture(executable, pid, output, name + "-after")
    command(
        ["sample", str(pid), "5", "-file", output / f"{name}-native-sample.txt"],
        output / f"{name}-sampler.log", required=False, timeout=40,
    )
    command(["lsof", "-a", "-p", str(pid), "-d", "cwd", "-Fn"],
            output / f"{name}-working-directory.log", required=False)
    return samples


def profile_file():
    candidates = [
        Path.home() / ".config" / "Lumi" / "data.json",
        Path.home() / "Library" / "Application Support" / "Lumi" / "data.json",
    ]
    found = [path for path in candidates if path.is_file()]
    if len(found) != 1:
        raise RuntimeError(f"Expected one profile produced by first-run launch; found {found}.")
    return found[0]


def main(args):
    if platform.system() != "Darwin" or os.environ.get("GITHUB_ACTIONS") != "true":
        raise RuntimeError("This installer experiment is restricted to ephemeral GitHub macOS runners.")
    expected_machine = "arm64" if args.architecture == "arm64" else "x86_64"
    if platform.machine() != expected_machine:
        raise RuntimeError("The release architecture does not match the native runner.")
    output = Path(args.output).resolve()
    output.mkdir(parents=True, exist_ok=True)
    assets = Path(args.assets).resolve()
    helper = Path(args.inspector).resolve()
    release = json.loads((assets / "release.json").read_text(encoding="utf-8"))
    if release["tag_name"] != "v0.9.8":
        raise RuntimeError("The experiment requires the original published v0.9.8 release.")
    suffix = "Setup.pkg" if args.distribution == "pkg" else "Portable.zip"
    asset_name = f"Lumi-osx-{args.architecture}-{suffix}"
    record = next(asset for asset in release["assets"] if asset["name"] == asset_name)
    asset = assets / asset_name
    expected = record["digest"].removeprefix("sha256:")
    actual = sha256(asset)
    if actual != expected or asset.stat().st_size != record["size"]:
        raise RuntimeError("Downloaded package does not match the official release digest/size.")
    write_json(output / "release-asset.json", {
        "tag": release["tag_name"], "publishedAt": release["published_at"],
        "asset": asset_name, "url": record["browser_download_url"],
        "expectedSha256": expected, "actualSha256": actual, "bytes": asset.stat().st_size,
        "distribution": args.distribution, "architecture": args.architecture,
        "appRebuiltOrModified": False, "launchMethod": "macOS open/LaunchServices",
        "browserDownloadQuarantineReproduced": False,
        "userAuthenticationProvided": False,
    })
    command(["sw_vers"], output / "macos-version.log")
    command(["uname", "-m"], output / "host-architecture.log")
    command(["system_profiler", "SPDisplaysDataType"], output / "display.log", required=False)
    bundle = Path("/Applications/Lumi.app")
    if bundle.exists() or (Path.home() / "Applications" / "Lumi.app").exists():
        raise RuntimeError("Refusing to replace an existing Lumi installation.")
    for parent in [Path.home() / ".config" / "Lumi", Path.home() / "Library" / "Application Support" / "Lumi"]:
        if parent.exists():
            raise RuntimeError(f"Refusing to use pre-existing Lumi profile {parent}.")
    if args.distribution == "pkg":
        command(["pkgutil", "--check-signature", asset], output / "installer-signature.log", required=False)
        command(["sudo", "-n", "installer", "-pkg", asset, "-target", "/"],
                output / "installation.log", timeout=180)
    else:
        extracted = assets / "portable-extracted"
        command(["ditto", "-x", "-k", asset, extracted], output / "extract.log", timeout=90)
        bundles = list(extracted.rglob("Lumi.app"))
        if len(bundles) != 1:
            raise RuntimeError(f"Expected one Lumi.app in portable archive, found {len(bundles)}.")
        command(["sudo", "-n", "ditto", bundles[0], bundle], output / "installation.log", timeout=90)
    if not bundle.exists():
        user_bundle = Path.home() / "Applications" / "Lumi.app"
        if user_bundle.exists():
            bundle = user_bundle
        else:
            raise RuntimeError("The release installer did not produce Lumi.app in an Applications directory.")
    info_file = bundle / "Contents" / "Info.plist"
    info = plistlib.loads(info_file.read_bytes())
    if info.get("CFBundleIdentifier") != "com.lumi.app":
        raise RuntimeError(f"Unexpected bundle identifier: {info.get('CFBundleIdentifier')}")
    if not any(str(info.get(key, "")).startswith("0.9.8") for key in ["CFBundleShortVersionString", "CFBundleVersion"]):
        raise RuntimeError("Installed bundle version is not 0.9.8.")
    binary = bundle / "Contents" / "MacOS" / info["CFBundleExecutable"]
    binary_hash = sha256(binary)
    write_json(output / "installed-bundle.json", {
        "path": str(bundle), "executable": str(binary), "binarySha256": binary_hash, "info": info,
    })
    command(["file", binary], output / "executable-format.log")
    command(["codesign", "--verify", "--deep", "--strict", bundle], output / "codesign-verify.log", required=False)
    command(["codesign", "-dv", "--verbose=2", bundle], output / "codesign-details.log", required=False)
    command(["spctl", "--assess", "--type", "execute", "--verbose", bundle],
            output / "gatekeeper-assessment.log", required=False, timeout=60)
    command(["xattr", "-l", bundle], output / "bundle-attributes.log", required=False)
    sessions = []
    for name in ["fresh-install", "onboarded-profile"]:
        phase_dir = output / name
        phase_dir.mkdir()
        if name == "onboarded-profile":
            data_path = profile_file()
            data = json.loads(data_path.read_text(encoding="utf-8-sig"))
            settings = data["settings"]
            if settings.get("isOnboarded") is not True or settings.get("userName") != "Release Test":
                raise RuntimeError("Normal UI onboarding did not persist the expected profile.")
            write_json(phase_dir / "profile-preparation.json", {
                "path": str(data_path), "changes": {},
                "reason": "Relaunch after completing real onboarding with the normal Skip for now option.",
                "backendInitializationDisabled": False,
                "uiOnboardingCompletedInteractively": True,
            })
        pid = launch(bundle, helper, phase_dir,
                     allow_installer_launch=args.distribution == "pkg" and name == "fresh-install")
        try:
            samples = measure(helper, pid, phase_dir, "visible")
            summary = {"name": name, "pid": pid, "samples": samples}
            if name == "fresh-install":
                for action in ["onboarding-name", "continue-onboarding", "skip-learning", "finish-onboarding"]:
                    write_json(phase_dir / f"{action}.json", inspector(helper, action, pid))
                    time.sleep(3)
                    capture(helper, pid, phase_dir, action)
                write_json(phase_dir / "focus-composer.json", inspector(helper, "focus-composer", pid))
                summary["afterOnboardingSamples"] = measure(helper, pid, phase_dir, "onboarded-focused")
            if name == "onboarded-profile":
                write_json(phase_dir / "focus-composer.json", inspector(helper, "focus-composer", pid))
                summary["focusedSamples"] = measure(helper, pid, phase_dir, "focused", count=6, settle=5)
                write_json(phase_dir / "hide-request.json", inspector(helper, "hide", pid))
                summary["hiddenSamples"] = measure(helper, pid, phase_dir, "hidden", count=6, settle=5)
            sessions.append(summary)
        finally:
            close_owned_app(helper, pid, bundle, phase_dir)
    if sha256(binary) != binary_hash:
        raise RuntimeError("The installed executable changed during the test.")
    write_json(output / "summary.json", {
        "release": "0.9.8", "distribution": args.distribution, "architecture": args.architecture,
        "binaryUnchanged": True, "sessions": sessions,
        "limitations": [
            "Hosted VM graphics/display differ from a physical user's Mac.",
            "Installer CLI installs the official package; the interactive Installer wizard is not driven.",
            "CLI download does not reproduce browser-added quarantine or first-open approval.",
            "No personal GitHub/Copilot credentials, real chats, or model requests.",
            "Onboarding completed through the released UI using Skip for now; no profile files edited.",
        ],
    })
    print("RELEASE_ARTIFACT_PROBE_COMPLETE", flush=True)


if __name__ == "__main__":
    parser = argparse.ArgumentParser()
    parser.add_argument("--architecture", choices=["arm64", "x64"], required=True)
    parser.add_argument("--distribution", choices=["pkg", "portable"], required=True)
    parser.add_argument("--assets", required=True)
    parser.add_argument("--output", required=True)
    parser.add_argument("--inspector", required=True)
    arguments = parser.parse_args()
    try:
        main(arguments)
    except Exception:
        error = traceback.format_exc()
        print(error, flush=True)
        directory = Path(arguments.output).resolve()
        directory.mkdir(parents=True, exist_ok=True)
        (directory / "error.txt").write_text(error, encoding="utf-8")
        raise
