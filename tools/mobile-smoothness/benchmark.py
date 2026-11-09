"""Headed, isolated mobile smoothness benchmark. See docs/testing.md."""

import argparse
import asyncio
import base64
import hashlib
import html
import io
import json
import math
import re
import shlex
import socket
import statistics
import subprocess
import threading
import time
from http.server import SimpleHTTPRequestHandler, ThreadingHTTPServer
from pathlib import Path

from PIL import Image, ImageChops

ROOT = Path(__file__).resolve().parents[2]
SCENARIOS = [
    "composer-empty",
    "scroll-plain", "scroll-rich", "scroll-code", "scroll-chats", "scroll-models",
    "switch-plain", "switch-rich", "switch-code",
    "popup-model", "popup-project", "popup-title",
    "nav-jobs", "nav-customize", "nav-settings", "nav-chats",
    "composer-plain", "composer-rich", "composer-code",
    "scroll-reveal-cold", "scroll-reveal-retained",
]
TRANSCRIPT_STROKES = 4
REVEAL_STROKES = 12
SURFACE_HISTORY_FRAMES = 128


def stroke_count(scenario):
    return REVEAL_STROKES if scenario.startswith("scroll-reveal-") else (
        1 if scenario in ("scroll-chats", "scroll-models") else TRANSCRIPT_STROKES)

# Installed before Avalonia/workerFrames. Clock opportunities, GL submissions and compositor swaps
# are independent channels; none of these is advertised as physical input-to-photon latency.
PROBE = r"""
(() => {
    const state = window.__lumiSmoothness = {active:false, batches:[], errors:[], touches:[]};
    addEventListener('error', e => state.errors.push(e.message));
    addEventListener('unhandledrejection', e => state.errors.push(String(e.reason)));
    addEventListener('pointerdown', e => {
        if (state.active) state.touches.push({time:performance.now(), trusted:e.isTrusted, type:e.pointerType});
    }, {capture:true, passive:true});
    const getContext = HTMLCanvasElement.prototype.getContext;
    HTMLCanvasElement.prototype.getContext = function(type, options) {
        const gl = getContext.call(this,type,options);
        if (!gl || !this.classList.contains('avalonia-canvas') || !['webgl','webgl2'].includes(type)
            || gl.__smoothnessWrapped) return gl;
        gl.__smoothnessWrapped = true;
        let pending = false;
        for (const name of ['clear','clearBufferfv','clearBufferiv','clearBufferuiv','clearBufferfi',
            'drawArrays','drawElements','drawArraysInstanced','drawElementsInstanced',
            'drawRangeElements','blitFramebuffer']) {
            const original = gl[name];
            if (typeof original !== 'function') continue;
            gl[name] = function(...args) {
                const result = original.apply(this,args);
                if (state.active && !pending) {
                    pending = true;
                    queueMicrotask(() => {
                        pending = false;
                        if (state.active) state.batches.push(performance.now());
                    });
                }
                return result;
            };
        }
        return gl;
    };
})();
"""


def digest(path):
    return hashlib.sha256(Path(path).read_bytes()).hexdigest()

def native_wasm(root):
    files = list((root / "_framework").glob("dotnet.native*.wasm"))
    loader = root / "_framework" / "dotnet.js"
    active = [path for path in files if loader.is_file() and path.name in loader.read_text(encoding="utf-8")]
    if len(active) != 1:
        raise ValueError(f"One native WASM artifact referenced by the runtime loader is required, found {active}")
    return active[0]

def web_build_fingerprint(root):
    native_wasm(root)
    digestor = hashlib.sha256()
    files = sorted(path for path in root.rglob("*") if path.is_file()
                   and path.suffix not in (".br", ".gz", ".map"))
    for path in files:
        digestor.update(str(path.relative_to(root)).replace("\\", "/").encode())
        digestor.update(bytes.fromhex(digest(path)))
    return digestor.hexdigest()


def percentile(values, fraction):
    if not values or not all(math.isfinite(value) for value in values):
        raise ValueError("Missing or non-finite timing evidence")
    ordered = sorted(values)
    return ordered[min(len(ordered) - 1, math.ceil((len(ordered) - 1) * fraction))]


def cadence(stamps, refresh_ms, minimum=3):
    if not math.isfinite(refresh_ms) or refresh_ms <= 0 or len(stamps) < minimum:
        raise ValueError("Insufficient frame evidence")
    gaps = [b - a for a, b in zip(stamps, stamps[1:])]
    if any(not math.isfinite(gap) or gap <= 0 for gap in gaps):
        raise ValueError("Frame timestamps must be finite and strictly increasing")
    missed = [max(0, round(gap / refresh_ms) - 1) for gap in gaps]
    return {
        "frames": len(stamps),
        "gapP50Ms": percentile(gaps, .5),
        "gapP95Ms": percentile(gaps, .95),
        "gapMaxMs": max(gaps),
        "missedRefreshes": sum(missed),
        "missedRefreshShare": sum(missed) / (len(gaps) + sum(missed)),
        "hitchesOver50Ms": sum(gap > 50 for gap in gaps),
        "excessGapMs": sum(max(0, gap - refresh_ms) for gap in gaps),
    }


def verify_motion(before, after, scrolling, composer=False):
    first, second = [Image.open(io.BytesIO(png)).convert("RGB") for png in (before, after)]
    if first.size != second.size:
        raise ValueError("The viewport changed during measurement")
    width, height = first.size
    # Ignore the floating header, composer and OS bars: a breathing indicator is not scroll motion.
    region = (int(width * .05), int(height * (.5 if composer else .22)),
              int(width * .95), int(height * (.96 if composer else .7)))
    diff = ImageChops.difference(first.crop(region), second.crop(region)).convert("L")
    changed = sum(diff.histogram()[20:]) / (diff.width * diff.height)
    if changed < (.005 if scrolling else .002):
        raise ValueError(f"No verified content change ({changed:.2%} changed pixels)")
    return changed


def validate_measurement(state, scenario):
    if state.get("error"):
        raise ValueError(state["error"])
    row = state.get("measurement")
    if not row or not row.get("complete") or row.get("error") or row.get("scenario") != scenario:
        raise ValueError(f"Incomplete/failed action {scenario}: "
                         f"{row.get('error') if row else 'no measurement'}")
    if row.get("inputToReadyMs") is None or row["inputToReadyMs"] < 0:
        raise ValueError("Missing input-to-content-ready evidence")
    if row["activeEndMs"] <= row["startMs"]:
        raise ValueError("Empty active measurement window")
    if scenario.startswith("scroll-"):
        if row["travel"] < 150 or row["gestureUpdates"] < 1 or row.get("inputToScrollMs") is None:
            raise ValueError("The real touch gesture did not scroll the intended content")
    if scenario.startswith("scroll-reveal-"):
        presentations = row.get("presentations", [])
        cold = [event for event in presentations if event["phase"] == "cold"]
        retained = [event for event in presentations if event["phase"] == "retained"]
        if row["travel"] < 2000:
            raise ValueError("The reveal workload did not cross enough real transcript content")
        if scenario.endswith("cold") and len({event["turnId"] for event in cold}) < 3:
            raise ValueError("The cold workload did not build at least three new transcript turns")
        if scenario.endswith("retained") and (cold or len({event["turnId"] for event in retained}) < 3):
            raise ValueError("The retained workload must revisit at least three turns without cold construction")
    if scenario.startswith("composer-"):
        focus = row.get("inputToFocusMs")
        if focus is None or focus < 0:
            raise ValueError("The actual composer did not receive input focus")
        if row.get("softKeyboardExpected") and any(
                row.get(key) is None or row[key] < 0
                for key in ("inputToKeyboardRequestMs", "inputToKeyboardReportedMs")):
            raise ValueError("Android did not request and report an actual soft keyboard")
    frames = [frame for frame in row["frames"] if row["startMs"] <= frame["timeMs"] <= row["activeEndMs"]]
    minimum = 3 if scenario.startswith("scroll-") else 2
    if len(frames) < minimum:
        raise ValueError(f"The action produced fewer than {minimum} UI-frame samples")
    if state["loadedTurns"] > 24:
        raise ValueError("The fixture bypassed bounded initial transcript loading")
    return row, frames


def trace_swaps(events, start, end):
    boundaries = [[event["ts"] for event in events if event.get("name") == name] for name in (start, end)]
    if any(len(values) != 1 for values in boundaries) or boundaries[1][0] <= boundaries[0][0]:
        raise ValueError("Missing or ambiguous compositor trace boundaries")
    low, high = boundaries[0][0], boundaries[1][0]
    stamps = sorted({
        event["ts"] / 1000 for event in events
        if event.get("name") == "Display::DrawAndSwap" and event.get("ph") in ("B", "X")
        and low <= event["ts"] <= high
    })
    if not stamps:
        raise ValueError("The headed compositor produced no swap evidence")
    return stamps


def surface_frames(text):
    lines = text.strip().splitlines()
    if not lines or not lines[0].strip().isdigit() or int(lines[0]) <= 0:
        raise ValueError("SurfaceFlinger did not report a display refresh period")
    period = int(lines[0]) / 1_000_000
    frames = []
    for line in lines[1:]:
        fields = line.split()
        if len(fields) != 3 or not all(field.isdigit() for field in fields):
            raise ValueError("Malformed SurfaceFlinger frame evidence")
        desired, presented, ready = map(int, fields)
        if presented not in (0, 9223372036854775807):
            frames.append(presented / 1_000_000)
    return period, frames

def surface_name(line):
    # Recent Android dumps describe a requested layer rather than printing just its name.
    match = re.fullmatch(r"RequestedLayerState\{(.+) parentId=\d+\}", line)
    return match.group(1) if match else line

def check_surface_poll(elapsed_seconds, refresh_ms):
    if elapsed_seconds * 1000 >= SURFACE_HISTORY_FRAMES * refresh_ms:
        raise ValueError("SurfaceFlinger sampling could have wrapped its frame history; evidence is incomplete")

def rendered_window(stamps, start, end):
    in_window = [stamp for stamp in stamps if start <= stamp <= end]
    # Layout/input is observed before its render submission. Keep the first completed frame
    # after the final content update, rather than cutting off the paint of that update.
    following = next((stamp for stamp in stamps if stamp > end), None)
    if following is not None:
        in_window.append(following)
    return in_window


def controls(rows, slowed):
    groups = {}
    for row in rows:
        groups.setdefault(row["scenario"], []).append(row["contentFrameGaps"]["gapP95Ms"])
    spreads = {
        key: (max(values) - min(values)) / max(statistics.median(values), 1)
        for key, values in groups.items()
    }
    ready_spreads = {}
    for scenario in groups:
        if not scenario.startswith("scroll-"):
            values = [row["measurement"]["inputToReadyMs"] for row in rows if row["scenario"] == scenario]
            ready_spreads[scenario] = (max(values) - min(values)) / max(statistics.median(values), 1)
    repeatable_by_scenario = {
        key: value <= .20 and ready_spreads.get(key, 0) <= .20
        for key, value in spreads.items()
    }
    measurement = slowed["measurement"] if slowed else {}
    at = measurement.get("injectedStallAtMs")
    frames = measurement.get("frames", [])
    detected = bool(at is not None and measurement.get("injectedStallMs") == 120
                    and any(first["timeMs"] <= at and second["timeMs"] >= at + 115
                            and second["timeMs"] - first["timeMs"] >= 115
                            for first, second in zip(frames, frames[1:])))
    return {
        "spreadByScenario": spreads,
        "readySpreadByScenario": ready_spreads,
        "repeatableByScenario": repeatable_by_scenario,
        "maximumAllowedSpread": .20,
        "repeatable": all(repeatable_by_scenario.values()),
        "known120MsStallDetected": detected,
    }


def summarize(state, refresh_ms, render_stamps, changed):
    row, frames = validate_measurement(state, state["measurement"]["scenario"])
    opportunities = cadence([frame["timeMs"] for frame in frames], refresh_ms,
                            3 if row["scenario"].startswith("scroll-") else 2)
    # Repeated identical offsets do not make a frozen transcript smooth just because its glow renders.
    moving = [frames[0]["timeMs"]]
    previous = frames[0]["offset"]
    for frame in frames[1:]:
        if abs(frame["offset"] - previous) > .2:
            moving.append(frame["timeMs"])
            previous = frame["offset"]
    content = cadence(moving, refresh_ms) if row["scenario"].startswith("scroll-") else opportunities
    if not render_stamps:
        raise ValueError(f"No rendered output during {row['scenario']}")
    required = 3 if row["scenario"].startswith("scroll-") else 2
    render = cadence(render_stamps, refresh_ms, required) if len(render_stamps) >= required else None
    if row["scenario"].startswith("scroll-") and render is None:
        raise ValueError(f"Insufficient continuous scroll frames during {row['scenario']}")
    return {
        "scenario": row["scenario"],
        "fixture": state["fixture"],
        "configuration": state["configuration"],
        "measurement": row,
        "uiFrameOpportunities": opportunities,
        "contentFrameGaps": content,
        "renderOutputGaps": render,
        "renderedFrames": len(render_stamps),
        "blankP95": percentile([frame["blankFraction"] for frame in frames], .95),
        "blankMax": max(frame["blankFraction"] for frame in frames),
        "unbuiltViewportMs": sum(
            max(first["blankFraction"], second["blankFraction"]) * (second["timeMs"] - first["timeMs"])
            for first, second in zip(frames, frames[1:])),
        "changedPixelShare": changed,
        "presentationCount": len(row.get("presentations", [])),
        "coldTurns": len({event["turnId"] for event in row.get("presentations", []) if event["phase"] == "cold"}),
        "retainedTurns": len({event["turnId"] for event in row.get("presentations", []) if event["phase"] == "retained"}),
        "presentationMaxMs": max((event["durationMs"] for event in row.get("presentations", [])), default=0),
    }


class WebRoot(ThreadingHTTPServer):
    def __init__(self, root):
        class Handler(SimpleHTTPRequestHandler):
            def __init__(self, *args, **kwargs):
                super().__init__(*args, directory=str(root), **kwargs)

            def translate_path(self, path):
                if path.startswith("/app/"):
                    path = path[4:]
                return super().translate_path(path)

            def log_message(self, *args):
                pass

        super().__init__(("127.0.0.1", 0), Handler)


async def read_trace(cdp):
    complete = asyncio.get_running_loop().create_future()
    cdp.once("Tracing.tracingComplete", lambda event: complete.set_result(event))
    await cdp.send("Tracing.end")
    event = await asyncio.wait_for(complete, 30)
    chunks = []
    while True:
        part = await cdp.send("IO.read", {"handle": event["stream"]})
        chunks.append(base64.b64decode(part["data"]) if part.get("base64Encoded") else part["data"].encode())
        if part.get("eof"):
            break
        if sum(map(len, chunks)) > 128 * 1024 * 1024:
            raise ValueError("The trace exceeded its bounded 128 MiB budget")
    await cdp.send("IO.close", {"handle": event["stream"]})
    return json.loads(b"".join(chunks))


async def browser_trial(args, root, output, trial, slowdown=False):
    from playwright.async_api import async_playwright

    server = WebRoot(root)
    thread = threading.Thread(target=server.serve_forever, daemon=True)
    thread.start()
    try:
        async with async_playwright() as playwright:
            browser = await playwright.chromium.launch(
                executable_path=str(args.browser_exe), headless=False,
                args=["--window-position=20,20", f"--window-size={max(640,args.width + 100)},{args.height + 200}"]
                     + ([f"--force-device-scale-factor={args.dpr}"] if args.dpr else []),
                ignore_default_args=["--disable-background-timer-throttling",
                                     "--disable-renderer-backgrounding", "--disable-backgrounding-occluded-windows"])
            try:
                context = await browser.new_context(
                    no_viewport=True, has_touch=True, color_scheme="dark")
                page = await context.new_page()
                messages = []
                page.on("console", lambda message: messages.append(f"{message.type}: {message.text}"))
                page.on("pageerror", lambda error: messages.append(f"pageerror: {error}"))
                await page.bring_to_front()
                await page.set_content("<style>body{background:#111}div{width:40px;height:40px;background:white;"
                                       "animation:move 1s linear infinite alternate}@keyframes move{to{transform:translateX(100px)}}"
                                       "</style><div></div>")
                calibration = await page.evaluate("""async () => {
                    const stamps=[]; await new Promise(resolve => {
                        const tick=t=>{stamps.push(t); stamps.length<90?requestAnimationFrame(tick):resolve()};
                        requestAnimationFrame(tick);
                    }); return {stamps, hidden:document.hidden};
                }""")
                refresh_ms = statistics.median([
                    b - a for a, b in zip(calibration["stamps"], calibration["stamps"][1:])])
                if calibration["hidden"] or refresh_ms > 35:
                    raise ValueError("The browser is hidden/throttled; this is not a valid headed smoothness run")
                if args.refresh_hz and abs(refresh_ms - 1000 / args.refresh_hz) > 1000 / args.refresh_hz * .15:
                    raise ValueError("Observed display cadence does not match --refresh-hz")
                await context.add_init_script(PROBE)
                # Use a real native browser surface, not CDP's emulated DPR (which can disagree with
                # Avalonia's backing-store scale). The embedded phone gets an exact CSS/DIP viewport.
                await context.add_init_script(f"""
                    addEventListener('DOMContentLoaded',()=>{{
                        const out=document.getElementById('out');
                        if(out) Object.assign(out.style,{{width:'{args.width}px',height:'{args.height}px',
                            position:'absolute',left:'0',top:'0',right:'auto',bottom:'auto'}});
                    }});
                """)
                await page.goto(f"http://127.0.0.1:{server.server_port}/app/?smoothness=1&workerframes={args.workerframes}")
                await page.wait_for_function("window.lumiSmoothness", timeout=120000)
                await page.wait_for_function("""() => {
                    const canvas=document.querySelector('#out canvas.avalonia-canvas');
                    if(!canvas) return false;
                    const box=canvas.getBoundingClientRect();
                    return box.width>0 && box.height>0 &&
                        Math.abs(canvas.width-box.width*devicePixelRatio)<=1 &&
                        Math.abs(canvas.height-box.height*devicePixelRatio)<=1;
                }""", timeout=10000)
                cdp = await context.new_cdp_session(page)
                identity = await page.evaluate("""() => {
                    const canvas=document.querySelector('#out canvas.avalonia-canvas');
                    const box=canvas.getBoundingClientRect();
                    const gl=canvas.getContext('webgl2')||canvas.getContext('webgl');
                    return {width:box.width,height:box.height,pixels:[canvas.width,canvas.height],dpr:devicePixelRatio,
                        hidden:document.hidden,worker:window.lumiWorkerFrames?.state??'normal',
                        browserViewport:[innerWidth,innerHeight],
                        gpu:gl?gl.getParameter(gl.RENDERER):'unreported'};
                }""")
                (output / f"metadata-{trial}.json").write_text(json.dumps(identity), encoding="utf-8")
                if identity["hidden"] or identity["browserViewport"][0] < args.width \
                    or identity["browserViewport"][1] < args.height \
                    or abs(identity["width"]-args.width)>1 or abs(identity["height"]-args.height)>1 \
                    or any(abs(pixels - css * identity["dpr"]) > 1 for pixels, css in
                                           zip(identity["pixels"], (identity["width"], identity["height"]))):
                    raise ValueError(f"Invalid canvas geometry or visibility: {identity}")
                rows = []
                selected = ["scroll-rich"] if slowdown else args.scenarios
                for scenario in selected:
                    for warm in range(args.warmup if not slowdown and scenario != "composer-empty" else 0):
                        await browser_action(page, cdp, scenario, output, f"warm-{trial}-{scenario}-{warm}", False)
                    await cdp.send("Tracing.start", {
                        "categories": "blink.user_timing,viz,benchmark",
                        "transferMode": "ReturnAsStream",
                    })
                    row = await browser_action(page, cdp, scenario, output, f"{trial}-{scenario}", slowdown)
                    trace = await read_trace(cdp)
                    (output / f"trace-{trial}-{scenario}.json").write_text(json.dumps(trace), encoding="utf-8")
                    swaps = trace_swaps(trace["traceEvents"], row.pop("startMark"), row.pop("endMark"))
                    row["compositorSwapGaps"] = cadence(swaps, refresh_ms, 2) if len(swaps) >= 2 else None
                    state = row.pop("state")
                    batches = row.pop("batches")
                    row.update(summarize(state, refresh_ms, batches, row["changedPixelShare"]))
                    row["trial"] = trial
                    (output / f"result-{trial}-{row['scenario']}.json").write_text(json.dumps(row), encoding="utf-8")
                    rows.append(row)
                errors = await page.evaluate("__lumiSmoothness.errors")
                if errors:
                    raise ValueError(f"Browser runtime errors: {errors}")
                return {"browserVersion": browser.version, "browserSha256": digest(args.browser_exe),
                        "viewport": identity, "refreshMs": refresh_ms,
                        "calibration": calibration, "rows": rows}
            finally:
                if "page" in locals():
                    (output / f"console-{trial}.json").write_text(json.dumps(messages, indent=2), encoding="utf-8")
                    if "rows" not in locals() or not rows:
                        try:
                            (output / f"failure-{trial}.png").write_bytes(await page.screenshot())
                        except Exception as error:
                            messages.append(f"Failure screenshot unavailable: {error}")
                await browser.close()
    finally:
        server.shutdown()
        server.server_close()
        thread.join(timeout=5)


async def browser_action(page, cdp, scenario, output, label, slowdown):
    state = await page.evaluate("request=>lumiSmoothness.command(request)",
                                {"operation": "prepare", "scenario": scenario})
    if state.get("error"):
        raise ValueError(state["error"])
    target = state["target"]
    before = await page.locator("#out").screenshot(scale="css")
    armed = await page.evaluate("request=>lumiSmoothness.command(request)",
                               {"operation": "arm", "scenario": scenario, "stallMs": 120 if slowdown else 0})
    if armed.get("error"):
        raise ValueError(armed["error"])
    start, end = f"lumi-{label}-start", f"lumi-{label}-end"
    await page.evaluate("""name=>{
        __lumiSmoothness.batches=[]; __lumiSmoothness.touches=[]; __lumiSmoothness.active=true;
        performance.mark(name);
    }""", start)
    if scenario.startswith("scroll-"):
        for _ in range(stroke_count(scenario)):
            await cdp.send("Input.synthesizeScrollGesture", {
                "x": target["x"], "y": target["y"],
                "yDistance": -260 if scenario in ("scroll-chats", "scroll-models") else 260,
                "speed": 650, "gestureSourceType": "touch", "preventFling": False,
            })
    else:
        await cdp.send("Input.dispatchTouchEvent", {"type": "touchStart",
                       "touchPoints": [{"x": target["x"], "y": target["y"]}]})
        await cdp.send("Input.dispatchTouchEvent", {"type": "touchEnd", "touchPoints": []})
    finished = await page.evaluate("request=>lumiSmoothness.command(request)", {"operation": "finish-input"})
    if finished.get("error"):
        raise ValueError(finished["error"])
    deadline = time.monotonic() + 20
    while True:
        await asyncio.sleep(.15)
        state = await page.evaluate("""async () => {
            const state=await lumiSmoothness.command({operation:'read'});
            state.browserClockMs=performance.now(); return state;
        }""")
        if state.get("error") or state.get("measurement", {}).get("complete"):
            break
        if time.monotonic() > deadline:
            raise ValueError(f"Timed out waiting for {scenario}")
    (output / f"{label}-state.json").write_text(json.dumps(state), encoding="utf-8")
    validate_measurement(state, scenario)
    data = await page.evaluate("""name=>{
        performance.mark(name); __lumiSmoothness.active=false;
        return {batches:__lumiSmoothness.batches,touches:__lumiSmoothness.touches};
    }""", end)
    if not data["touches"] or any(not touch["trusted"] or touch["type"] != "touch" for touch in data["touches"]):
        raise ValueError("The browser did not receive trusted touch input")
    after = await page.locator("#out").screenshot(scale="css")
    (output / f"{label}-before.png").write_bytes(before)
    (output / f"{label}-after.png").write_bytes(after)
    mapping = state["nativeClockMs"] - state["clockMs"]
    measure = state["measurement"]
    batches = rendered_window(data["batches"], measure["startMs"] + mapping, measure["activeEndMs"] + mapping)
    return {"state": state, "batches": batches, "startMark": start, "endMark": end,
            "changedPixelShare": verify_motion(before, after, scenario.startswith("scroll-"),
                                              scenario.startswith("composer-"))}


class Android:
    def __init__(self, args):
        self.args = args
        self.port = args.port

    def adb(self, *arguments, binary=False):
        return subprocess.check_output([str(self.args.adb), "-s", self.args.serial, *arguments],
                                       text=not binary, timeout=30)

    def command(self, request):
        with socket.create_connection(("127.0.0.1", self.port), timeout=30) as client:
            client.sendall((json.dumps(request) + "\n").encode())
            data = bytearray()
            while not data.endswith(b"\n"):
                part = client.recv(65536)
                if not part:
                    raise ConnectionError("Benchmark control channel closed without a response")
                data.extend(part)
                if len(data) > 2 * 1024 * 1024:
                    raise ValueError("Benchmark control response exceeded 2 MiB")
            return json.loads(data)


def android_trial(args, output, trial, slowdown=False):
    device = Android(args)
    device.adb("shell", "am", "force-stop", "com.lumi.mobile.smoothness")
    device.adb("shell", "monkey", "-p", "com.lumi.mobile.smoothness", "-c",
               "android.intent.category.LAUNCHER", "1")
    device.adb("forward", f"tcp:{args.port}", "tcp:47692")
    deadline = time.monotonic() + 30
    while True:
        try:
            state = device.command({"operation": "read"})
            if state.get("error"):
                raise ValueError(state["error"])
            break
        except (ConnectionError, OSError):
            if time.monotonic() > deadline:
                raise
            time.sleep(.2)
    deadline = time.monotonic() + 15
    while True:
        surfaces = [surface_name(name) for name in device.adb("shell", "dumpsys", "SurfaceFlinger", "--list").splitlines()
                    if "com.lumi.mobile.smoothness" in name and "SurfaceView" in name and "BLAST" in name]
        if surfaces or time.monotonic() >= deadline:
            break
        # The control endpoint can exist before Android has presented its native surface.
        time.sleep(.2)
    if len(surfaces) != 1:
        raise ValueError(f"An unambiguous Avalonia surface is required: {surfaces}")
    surface = surfaces[0]
    period, _ = surface_frames(device.adb("shell", "dumpsys", "SurfaceFlinger", "--latency", shlex.quote(surface)))
    if args.refresh_hz and abs(period - 1000 / args.refresh_hz) > period * .15:
        raise ValueError("Android display cadence does not match --refresh-hz")
    rows = []
    for scenario in (["scroll-rich"] if slowdown else args.scenarios):
        for warm in range(args.warmup if not slowdown and scenario != "composer-empty" else 0):
            android_action(device, scenario, surface, output, f"warm-{trial}-{scenario}-{warm}", False, period)
        row = android_action(device, scenario, surface, output, f"{trial}-{scenario}", slowdown, period)
        row["trial"] = trial
        rows.append(row)
        (output / f"result-{trial}-{scenario}.json").write_text(json.dumps(row), encoding="utf-8")
    return {"surface": surface, "refreshMs": period,
            "device": device.adb("shell", "getprop", "ro.product.model").strip(),
            "build": device.adb("shell", "getprop", "ro.build.fingerprint").strip(),
            "viewport": rows[0]["viewport"],
            "rows": rows}


def android_action(device, scenario, surface, output, label, slowdown, period):
    state = device.command({"operation": "prepare", "scenario": scenario})
    if state.get("error"):
        raise ValueError(state["error"])
    viewport = {"width": state["viewWidth"], "height": state["viewHeight"], "scale": state["scale"]}
    target = state["target"]
    x, y = round(target["x"] * target["scale"]), round(target["y"] * target["scale"])
    before = device.adb("exec-out", "screencap", "-p", binary=True)
    armed = device.command({"operation": "arm", "scenario": scenario, "stallMs": 120 if slowdown else 0})
    if armed.get("error"):
        raise ValueError(armed["error"])
    stamps, raw, failures = set(), [], []
    stop = threading.Event()

    def collect():
        last_poll = time.monotonic()
        while not stop.is_set():
            try:
                text = device.adb("shell", "dumpsys", "SurfaceFlinger", "--latency", shlex.quote(surface))
                observed_period, frames = surface_frames(text)
                if abs(observed_period - period) > period * .10:
                    raise ValueError("Android changed refresh mode during the action")
                now = time.monotonic()
                check_surface_poll(now - last_poll, period)
                last_poll = now
                stamps.update(frames)
                raw.append(text)
            except Exception as error:
                failures.append(str(error))
                return
            stop.wait(.25)

    collector = threading.Thread(target=collect, daemon=True)
    collector.start()
    try:
        if scenario.startswith("scroll-"):
            distance = round(260 * target["scale"])
            direction = -1 if scenario in ("scroll-chats", "scroll-models") else 1
            for _ in range(stroke_count(scenario)):
                device.adb("shell", "input", "touchscreen", "swipe", str(x), str(y),
                           str(x), str(y + direction * distance), "400")
        else:
            device.adb("shell", "input", "touchscreen", "tap", str(x), str(y))
        finished = device.command({"operation": "finish-input"})
        if finished.get("error"):
            raise ValueError(finished["error"])
        deadline = time.monotonic() + 20
        while True:
            time.sleep(.15)
            state = device.command({"operation": "read"})
            if state.get("error") or state.get("measurement", {}).get("complete"):
                break
            if time.monotonic() > deadline:
                raise ValueError(f"Timed out waiting for {scenario}")
    finally:
        stop.set()
        collector.join(timeout=35)
        (output / f"{label}-state.json").write_text(json.dumps(state), encoding="utf-8")
        (output / f"{label}-surface.json").write_text(json.dumps(raw), encoding="utf-8")
    if failures or collector.is_alive():
        raise ValueError(f"Incomplete Android surface collector: {failures}")
    validate_measurement(state, scenario)
    after = device.adb("exec-out", "screencap", "-p", binary=True)
    (output / f"{label}-before.png").write_bytes(before)
    (output / f"{label}-after.png").write_bytes(after)
    (output / f"{label}-surface.json").write_text(json.dumps(raw), encoding="utf-8")
    mapping = state["nativeClockMs"] - state["clockMs"]
    measurement = state["measurement"]
    frames = rendered_window(sorted(stamps), measurement["startMs"] + mapping, measurement["activeEndMs"] + mapping)
    result = summarize(state, period, frames, verify_motion(before, after, scenario.startswith("scroll-"),
                                                         scenario.startswith("composer-")))
    result["viewport"] = viewport
    return result


def validate_trials(trials):
    if not trials or not trials[0].get("rows"):
        raise ValueError("Missing trial evidence")
    first = trials[0]
    row = first["rows"][0]
    fixture, configuration = row.get("fixture"), row.get("configuration")
    if not fixture or configuration not in ("Debug", "Release"):
        raise ValueError("Missing measured fixture/build configuration")
    for trial in trials:
        for key in ("viewport", "browserVersion", "browserSha256", "device", "build"):
            if first.get(key) != trial.get(key):
                raise ValueError(f"Trial environment differs: {key}")
        if abs(first["refreshMs"] - trial["refreshMs"]) > first["refreshMs"] * .10:
            raise ValueError("Trial refresh modes differ")
        if not trial.get("rows") or any(
                row.get("fixture") != fixture or row.get("configuration") != configuration
                for row in trial["rows"]):
            raise ValueError("Measured fixture/build configuration differs")
    return fixture, configuration


def compare_reports(first, second):
    keys = ("platform", "fixture", "configuration", "driverSha256", "scenarios",
            "transcriptStrokes", "revealStrokes", "warmup")
    if any(first.get(key) != second.get(key) for key in keys):
        raise ValueError("The reports describe different workloads or collectors")
    if not first.get("complete") or not second.get("complete"):
        raise ValueError("Incomplete evidence cannot pass a comparison")
    if not first["controls"]["repeatable"] or not second["controls"]["repeatable"]:
        raise ValueError("An unstable baseline/candidate cannot certify an improvement")
    fixture, configuration = validate_trials(first["trials"] + second["trials"])
    if fixture != first["fixture"] or configuration != first["configuration"]:
        raise ValueError("Report metadata does not match the measured fixture/build")
    changes = []
    for scenario in first["scenarios"]:
        a = [row for trial in first["trials"] for row in trial["rows"] if row["scenario"] == scenario]
        b = [row for trial in second["trials"] for row in trial["rows"] if row["scenario"] == scenario]
        if not a or not b:
            raise ValueError(f"Missing scenario: {scenario}")
        changes.append({
            "scenario": scenario,
            "readyBeforeMs": statistics.median(row["measurement"]["inputToReadyMs"] for row in a),
            "readyAfterMs": statistics.median(row["measurement"]["inputToReadyMs"] for row in b),
            "contentGap95BeforeMs": statistics.median(row["contentFrameGaps"]["gapP95Ms"] for row in a),
            "contentGap95AfterMs": statistics.median(row["contentFrameGaps"]["gapP95Ms"] for row in b),
            "renderGap95BeforeMs": statistics.median(row["renderOutputGaps"]["gapP95Ms"] for row in a
                if row["renderOutputGaps"] is not None) if all(row["renderOutputGaps"] is not None for row in a) else None,
            "renderGap95AfterMs": statistics.median(row["renderOutputGaps"]["gapP95Ms"] for row in b
                if row["renderOutputGaps"] is not None) if all(row["renderOutputGaps"] is not None for row in b) else None,
        })
    return changes

def write_report(output, report):
    (output / "report.json").write_text(json.dumps(report, indent=2), encoding="utf-8")
    rows = [row for trial in report["trials"] for row in trial["rows"]]
    table = []
    for scenario in report["scenarios"]:
        group = [row for row in rows if row["scenario"] == scenario]
        if not group:
            continue
        ready = percentile([row["measurement"]["inputToReadyMs"] for row in group], .95)
        gap = statistics.median(row["contentFrameGaps"]["gapP95Ms"] for row in group)
        worst = statistics.median(row["contentFrameGaps"]["gapMaxMs"] for row in group)
        outputs = [row["renderOutputGaps"]["gapP95Ms"] for row in group if row["renderOutputGaps"]]
        rendered = f"{statistics.median(outputs):.1f}" if outputs else "single redraw"
        stable = report.get("controls", {}).get("repeatableByScenario", {}).get(scenario)
        stability = "yes" if stable else "no" if stable is False else "not established"
        table.append((max(ready, worst), f"<tr><td>{html.escape(scenario)}</td><td>{ready:.1f}</td>"
                      f"<td>{gap:.1f}</td><td>{worst:.1f}</td><td>{rendered}</td>"
                      f"<td>{max(row['blankP95'] for row in group):.1%}</td><td>{stability}</td></tr>"))
    keyboard_rows = []
    for scenario in report["scenarios"]:
        if not scenario.startswith("composer-"):
            continue
        group = [row for row in rows if row["scenario"] == scenario]
        if not group:
            continue
        def timing(key):
            values = [row["measurement"].get(key) for row in group]
            return f"{percentile(values, .95):.1f}" if all(value is not None for value in values) else "not available"
        keyboard_rows.append(f"<tr><td>{html.escape(scenario)}</td><td>{timing('inputToFocusMs')}</td>"
                             f"<td>{timing('inputToKeyboardRequestMs')}</td>"
                             f"<td>{timing('inputToKeyboardReportedMs')}</td></tr>")
    errors = "".join(f"<li>{html.escape(error)}</li>" for error in report["errors"])
    content = f"""<!doctype html><html lang="en"><meta charset="utf-8">
<title>Lumi mobile smoothness</title><style>
body{{font:15px system-ui;margin:40px auto;max-width:1100px;padding:0 20px;background:#15151a;color:#ededf1}}
table{{border-collapse:collapse;width:100%}}th,td{{padding:12px;text-align:left;border-bottom:1px solid #3a3a43}}
th{{color:#b7b8cf}}code{{word-break:break-all}}a{{color:#a4b9ff}}p{{line-height:1.6}}
</style><h1>Lumi mobile smoothness — {html.escape(report['platform'])}</h1>
<p><strong>Complete:</strong> {report['complete']} · <strong>Repeatable:</strong> {report.get('repeatable','not established')}
 · <strong>Known stall detected:</strong> {report.get('controls',{}).get('known120MsStallDetected','not established')}</p>
<p>Trusted touch, real shared mobile views, independent frame channels. Times are milliseconds;
these are not physical touch-to-photon measurements. Rows are ranked by readiness/worst content gap.</p>
<table><tr><th>Action</th><th>Ready p95</th><th>Content gap p95<br>(median run)</th>
<th>Worst content gap<br>(median run)</th><th>Rendered gap p95</th><th>Unbuilt viewport p95</th><th>Repeatable</th></tr>
{''.join(row for _,row in sorted(table,reverse=True))}</table><ul>{errors}</ul>
<h2>Composer and keyboard</h2>
<p>Focus, Lumi's native IME request, and the OS-reported keyboard opening are separate timings.
The keyboard report is an inset notification, not the first visible keyboard pixel.
Composer readiness waits for the reported inset's animation and valid composer layout above it.
Desktop-hosted PWA runs have no OS soft keyboard: only actual editor focus is measured.</p>
<table><tr><th>Action</th><th>Focus p95</th><th>IME request p95</th><th>IME reported p95</th></tr>
{''.join(keyboard_rows)}</table>
<p>Build fingerprint: <code>{html.escape(report.get('binarySha256','not established'))}</code></p>
<p><a href="report.json">Raw report and per-run samples</a>. Preserve noisy/failed controls;
compare identical environments and frozen builds, not the fastest isolated trial.</p></html>"""
    (output / "report.html").write_text(content, encoding="utf-8")


async def run(args):
    args.output.mkdir(parents=True, exist_ok=False)
    report = {
        "platform": args.platform, "fixture": None, "configuration": None, "scenarios": args.scenarios,
        "warmup": args.warmup,
        "transcriptStrokes": TRANSCRIPT_STROKES,
        "revealStrokes": REVEAL_STROKES,
        "firstComposerTapUnwarmed": "composer-empty",
        "driverSha256": digest(__file__), "complete": False, "trials": [], "errors": [],
        "physicalDisplayPresentationVerified": False,
        "measurementBoundary": "Trusted touch to semantic/layout-ready content; frame channels recorded separately.",
    }
    try:
        if args.platform == "pwa":
            report["binarySha256"] = web_build_fingerprint(args.web_root)
        else:
            report["binarySha256"] = digest(args.apk)
            device = Android(args)
            tools = args.adb.parent.parent / "build-tools"
            candidates = list(tools.glob("*/aapt2.exe")) + list(tools.glob("*/aapt2"))
            if not candidates:
                raise ValueError("The Android SDK's aapt2 is required to verify the isolated APK before installation")
            badging = subprocess.check_output([str(sorted(candidates)[-1]), "dump", "badging", str(args.apk)],
                                              text=True, timeout=30)
            if not re.search(r"^package: name='com\.lumi\.mobile\.smoothness'", badging, re.MULTILINE):
                raise ValueError("Refusing to install anything except the isolated com.lumi.mobile.smoothness APK")
            device.adb("install", "-r", str(args.apk))
        for trial in range(args.repeats):
            result = await browser_trial(args, args.web_root, args.output, str(trial)) if args.platform == "pwa" else \
                await asyncio.to_thread(android_trial, args, args.output, str(trial))
            report["trials"].append(result)
            report["fixture"], report["configuration"] = validate_trials(report["trials"])
            current_hash = web_build_fingerprint(args.web_root) if args.platform == "pwa" else digest(args.apk)
            if current_hash != report["binarySha256"]:
                raise ValueError("The build changed while it was being benchmarked; discard this run")
            write_report(args.output, report)
        slowed = await browser_trial(args, args.web_root, args.output, "known-stall", True) if args.platform == "pwa" else \
            await asyncio.to_thread(android_trial, args, args.output, "known-stall", True)
        report["slowdownControl"] = slowed
        validate_trials(report["trials"] + [slowed])
        rows = [row for trial in report["trials"] for row in trial["rows"]]
        report["controls"] = controls(rows, slowed["rows"][0])
        report["complete"] = len(rows) == len(args.scenarios) * args.repeats and report["controls"]["known120MsStallDetected"]
        if not report["complete"]:
            report["errors"].append("The collector did not detect its known 120 ms UI/content stall")
        report["repeatable"] = report["controls"]["repeatable"]
    except Exception as error:
        report["errors"].append(str(error))
    finally:
        if args.platform == "android":
            try:
                Android(args).adb("forward", "--remove", f"tcp:{args.port}")
                Android(args).adb("shell", "am", "force-stop", "com.lumi.mobile.smoothness")
            except Exception as error:
                report["errors"].append(f"Benchmark cleanup failed: {error}")
                report["complete"] = False
        write_report(args.output, report)
    if report["errors"]:
        print("\n".join(report["errors"]))
        return 1
    for scenario in args.scenarios:
        rows = [row for trial in report["trials"] for row in trial["rows"] if row["scenario"] == scenario]
        outputs = [row["renderOutputGaps"]["gapP95Ms"] for row in rows if row["renderOutputGaps"] is not None]
        output_text = f"{statistics.median(outputs):6.1f} ms" if outputs else "single redraw (no cadence)"
        print(f"{scenario:20} ready {statistics.median(row['measurement']['inputToReadyMs'] for row in rows):7.1f} ms"
              f"  content gap95 {statistics.median(row['contentFrameGaps']['gapP95Ms'] for row in rows):6.1f} ms"
              f"  output gap95 {output_text}")
    return 0 if report["repeatable"] else 3


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--platform", choices=("pwa", "android"), default="pwa")
    parser.add_argument("--web-root", type=Path)
    parser.add_argument("--browser-exe", type=Path)
    parser.add_argument("--adb", type=Path)
    parser.add_argument("--serial")
    parser.add_argument("--apk", type=Path)
    parser.add_argument("--port", type=int, default=47692)
    parser.add_argument("--output", type=Path, required=True)
    parser.add_argument("--repeats", type=int, default=3)
    parser.add_argument("--warmup", type=int, default=1)
    parser.add_argument("--filter", default="")
    parser.add_argument("--width", type=int, default=390)
    parser.add_argument("--height", type=int, default=844)
    parser.add_argument("--dpr", type=float, default=0, help="Native Chromium scale override; 0 uses the display's own scale")
    parser.add_argument("--refresh-hz", type=float)
    parser.add_argument("--workerframes", choices=("0", "1"), default="0")
    parser.add_argument("--compare", nargs=2, type=Path, metavar=("BASELINE", "CANDIDATE"))
    args = parser.parse_args()
    if args.compare:
        changes = compare_reports(*(json.loads(path.read_text(encoding="utf-8")) for path in args.compare))
        args.output.mkdir(parents=True, exist_ok=False)
        (args.output / "comparison.json").write_text(json.dumps(changes, indent=2), encoding="utf-8")
        for row in changes:
            print(f"{row['scenario']:20} ready {row['readyBeforeMs']:.1f} -> {row['readyAfterMs']:.1f} ms"
                  f"  content gap95 {row['contentGap95BeforeMs']:.1f} -> {row['contentGap95AfterMs']:.1f} ms")
        return 0
    if args.repeats < 3 or not 0 <= args.warmup <= 5:
        parser.error("At least three repetitions and 0-5 warmups are required")
    prefixes = [value.strip() for value in args.filter.split(",") if value.strip()]
    args.scenarios = [scenario for scenario in SCENARIOS if not prefixes or any(scenario.startswith(p) for p in prefixes)]
    if not args.scenarios:
        parser.error("The filter must match real scenarios")
    if args.platform == "pwa" and (not args.web_root or not args.browser_exe):
        parser.error("PWA requires --web-root and a pinned --browser-exe")
    if args.platform == "android" and (not args.adb or not args.serial or not args.apk):
        parser.error("Android requires --adb, --serial and an isolated benchmark --apk")
    return asyncio.run(run(args))


if __name__ == "__main__":
    raise SystemExit(main())
