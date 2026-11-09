import io
import copy
import tempfile
import unittest
from pathlib import Path

from PIL import Image, ImageDraw

import benchmark


class MeasurementTests(unittest.TestCase):
    def test_cadence_is_refresh_relative_and_does_not_hide_a_stall(self):
        row = benchmark.cadence([0, 10, 20, 120, 130], 10)
        self.assertEqual(9, row["missedRefreshes"])
        self.assertEqual(100, row["gapMaxMs"])
        self.assertEqual(1, row["hitchesOver50Ms"])
        for stamps in ([], [0], [0, 1], [0, 1, 1], [0, 2, 1], [0, 1, float("nan")]):
            with self.assertRaises(ValueError):
                benchmark.cadence(stamps, 10)
        self.assertEqual(500, benchmark.cadence([10, 510], 10, minimum=2)["gapMaxMs"])

    def test_surface_collector_counts_actual_presentation_not_ready_or_desired_times(self):
        period, frames = benchmark.surface_frames(
            "8333333\n1 10000000 9000000\n2 0 20000000\n3 9223372036854775807 30000000\n4 40000000 39000000\n")
        self.assertAlmostEqual(8.333333, period)
        self.assertEqual([10, 40], frames)
        for text in ("", "0\n", "no access", "8333333\n1 2\n", "8333333\n1 x 3"):
            with self.assertRaises(ValueError):
                benchmark.surface_frames(text)
        name = "SurfaceView[com.lumi.mobile.smoothness/MainActivity](BLAST)#148"
        self.assertEqual(name, benchmark.surface_name(name))
        self.assertEqual(name, benchmark.surface_name(f"RequestedLayerState{{{name} parentId=147}}"))
        self.assertEqual([110, 120, 130], benchmark.rendered_window([90, 110, 120, 130, 140], 100, 125))

    def test_compositor_trace_requires_unique_action_boundaries(self):
        events = [{"name": "start", "ts": 1000}, {"name": "end", "ts": 6000}]
        events += [{"name": "Display::DrawAndSwap", "ph": "X", "ts": time}
                   for time in (0, 2000, 3000, 4000, 7000)]
        self.assertEqual([2, 3, 4], benchmark.trace_swaps(events, "start", "end"))
        with self.assertRaises(ValueError):
            benchmark.trace_swaps(events + [events[0]], "start", "end")
        with self.assertRaises(ValueError):
            benchmark.trace_swaps([], "start", "end")

    def test_native_sampling_rejects_a_gap_that_can_wrap_the_surface_history(self):
        benchmark.check_surface_poll(.25, 1000 / 120)
        benchmark.check_surface_poll(1, 1000 / 120)
        with self.assertRaises(ValueError):
            benchmark.check_surface_poll(1.1, 1000 / 120)
        with self.assertRaises(ValueError):
            benchmark.check_surface_poll(2.2, 1000 / 60)

    def test_scrolling_requires_content_change_not_an_animated_header(self):
        def png(image):
            output = io.BytesIO()
            image.save(output, "PNG")
            return output.getvalue()
        image = Image.new("RGB", (390, 844), "white")
        header = image.copy()
        ImageDraw.Draw(header).rectangle((0, 0, 390, 100), fill="black")
        with self.assertRaises(ValueError):
            benchmark.verify_motion(png(image), png(header), True)
        moved = image.copy()
        ImageDraw.Draw(moved).rectangle((20, 300, 350, 500), fill="black")
        self.assertGreater(benchmark.verify_motion(png(image), png(moved), True), .1)
        with self.assertRaises(ValueError):
            benchmark.verify_motion(png(image), png(Image.new("RGB", (400, 844))), True)

    def test_stable_runs_and_a_known_slowdown_are_separate_controls(self):
        def row(gap, maximum=20):
            return {"scenario": "scroll-rich",
                    "contentFrameGaps": {"gapP95Ms": gap, "gapMaxMs": maximum},
                    "measurement": {"injectedStallMs": 0}}
        slowed = row(30, 130)
        slowed["measurement"]["injectedStallMs"] = 120
        slowed["measurement"]["injectedStallAtMs"] = 15
        slowed["measurement"]["frames"] = [{"timeMs": value} for value in (0, 10, 140, 150)]
        good = benchmark.controls([row(16), row(17), row(16)], slowed)
        self.assertTrue(good["repeatable"])
        self.assertTrue(good["known120MsStallDetected"])
        unstable = benchmark.controls([row(16), row(40), row(16)], slowed)
        self.assertFalse(unstable["repeatable"])
        self.assertFalse(benchmark.controls([row(16)] * 3, row(16))["known120MsStallDetected"])
        slowed["measurement"]["injectedStallAtMs"] = 200
        self.assertFalse(benchmark.controls([row(16)] * 3, slowed)["known120MsStallDetected"])

    def test_missing_actions_never_pass_as_zero_cost(self):
        with self.assertRaises(ValueError):
            benchmark.validate_measurement({"measurement": None}, "scroll-rich")
        with self.assertRaises(ValueError):
            benchmark.compare_reports({"complete": False}, {"complete": False})

    def test_comparisons_reject_unmatched_measured_workloads_and_later_trials(self):
        trial = {
            "viewport": {"width": 390, "height": 844}, "refreshMs": 1000 / 60,
            "device": "phone", "build": "os-build",
            "rows": [{
                "scenario": "switch-plain", "fixture": "fixture-v1", "configuration": "Release",
                "measurement": {"inputToReadyMs": 300},
                "contentFrameGaps": {"gapP95Ms": 20}, "renderOutputGaps": None}]
        }
        reference = {
            "platform": "android", "fixture": "fixture-v1", "configuration": "Release",
            "driverSha256": "driver", "scenarios": ["switch-plain"], "warmup": 1,
            "complete": True, "controls": {"repeatable": True},
            "trials": [copy.deepcopy(trial) for _ in range(3)]}
        candidate = copy.deepcopy(reference)
        self.assertEqual(1, len(benchmark.compare_reports(reference, candidate)))
        for field in ("fixture", "configuration"):
            candidate = copy.deepcopy(reference)
            for value in candidate["trials"]:
                value["rows"][0][field] = "different"
            with self.subTest(field=field), self.assertRaises(ValueError):
                benchmark.compare_reports(reference, candidate)
        candidate = copy.deepcopy(reference)
        candidate["warmup"] = 0
        with self.assertRaises(ValueError):
            benchmark.compare_reports(reference, candidate)
        candidate = copy.deepcopy(reference)
        candidate["trials"][1]["viewport"]["width"] = 412
        with self.assertRaises(ValueError):
            benchmark.compare_reports(reference, candidate)
        candidate = copy.deepcopy(reference)
        candidate["trials"][1]["refreshMs"] = 1000 / 120
        with self.assertRaises(ValueError):
            benchmark.compare_reports(reference, candidate)

    def test_composer_focus_does_not_claim_a_soft_keyboard_on_a_desktop_browser(self):
        measurement = {
            "scenario": "composer-code", "complete": True, "inputToReadyMs": 20,
            "startMs": 1, "activeEndMs": 40, "inputToFocusMs": 18,
            "softKeyboardExpected": False, "frames": [
                {"timeMs": 10, "offset": 0, "blankFraction": 0},
                {"timeMs": 30, "offset": 0, "blankFraction": 0}]
        }
        state = {"loadedTurns": 24, "measurement": measurement}
        benchmark.validate_measurement(state, "composer-code")
        measurement["softKeyboardExpected"] = True
        with self.assertRaises(ValueError):
            benchmark.validate_measurement(state, "composer-code")
        measurement.update(inputToKeyboardRequestMs=2, inputToKeyboardReportedMs=15)
        benchmark.validate_measurement(state, "composer-code")

    def test_stable_frames_do_not_certify_variable_chat_switch_latency(self):
        rows = [
            {"scenario": "switch-code", "contentFrameGaps": {"gapP95Ms": 16},
             "measurement": {"inputToReadyMs": ready}}
            for ready in (300, 310, 700)]
        result = benchmark.controls(rows, None)
        self.assertFalse(result["repeatable"])
        self.assertFalse(result["repeatableByScenario"]["switch-code"])
        self.assertGreater(result["readySpreadByScenario"]["switch-code"], .20)

    def test_busy_ui_coalesced_moves_remain_measureable_but_no_gesture_is_invalid(self):
        state = {
            "loadedTurns": 16,
            "measurement": {
                "scenario": "scroll-rich", "complete": True, "error": None,
                "inputToReadyMs": 16, "inputToScrollMs": 16, "startMs": 0, "activeEndMs": 32,
                "travel": 270, "gestureUpdates": 2,
                "frames": [{"timeMs": time, "offset": -time, "blankFraction": 0} for time in (0, 16, 32)]
            }
        }
        self.assertEqual(2, benchmark.validate_measurement(state, "scroll-rich")[0]["gestureUpdates"])
        state["measurement"]["gestureUpdates"] = 0
        with self.assertRaises(ValueError):
            benchmark.validate_measurement(state, "scroll-rich")

    def test_summary_records_the_fixture_and_build_that_actually_ran(self):
        state = {
            "fixture": "actual-fixture", "configuration": "Debug", "loadedTurns": 16,
            "measurement": {
                "scenario": "scroll-rich", "complete": True, "error": None,
                "inputToReadyMs": 16, "inputToScrollMs": 16, "startMs": 0, "activeEndMs": 32,
                "travel": 270, "gestureUpdates": 2,
                "frames": [{"timeMs": time, "offset": -time, "blankFraction": 0} for time in (0, 16, 32)]}}
        result = benchmark.summarize(state, 16, [0, 16, 32], .2)
        self.assertEqual("actual-fixture", result["fixture"])
        self.assertEqual("Debug", result["configuration"])

    def test_reveal_workload_proves_cold_builds_and_retained_return_separately(self):
        state = {"loadedTurns": 16, "measurement": {
            "scenario": "scroll-reveal-cold", "complete": True, "error": None,
            "inputToReadyMs": 16, "inputToScrollMs": 16, "startMs": 0, "activeEndMs": 32,
            "travel": 3000, "gestureUpdates": 4,
            "frames": [{"timeMs": time, "offset": -time, "blankFraction": 0} for time in (0, 16, 32)],
            "presentations": [{"turnId": str(i), "phase": "cold"} for i in range(3)]}}
        benchmark.validate_measurement(state, "scroll-reveal-cold")
        state["measurement"]["presentations"].pop()
        with self.assertRaises(ValueError):
            benchmark.validate_measurement(state, "scroll-reveal-cold")
        state["measurement"]["scenario"] = "scroll-reveal-retained"
        state["measurement"]["presentations"] = [{"turnId": str(i), "phase": "retained"} for i in range(3)]
        benchmark.validate_measurement(state, "scroll-reveal-retained")
        state["measurement"]["presentations"].append({"turnId": "cold", "phase": "cold"})
        with self.assertRaises(ValueError):
            benchmark.validate_measurement(state, "scroll-reveal-retained")
        self.assertEqual(12, benchmark.stroke_count("scroll-reveal-cold"))

    def test_partial_report_is_readable_and_explicitly_incomplete(self):
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory)
            benchmark.write_report(root, {
                "trials": [], "scenarios": ["scroll-rich"], "platform": "android",
                "errors": ["Missing <frames>"], "complete": False
            })
            document = (root / "report.html").read_text(encoding="utf-8")
            self.assertIn("Missing &lt;frames&gt;", document)
            self.assertIn("Complete:</strong> False", document)
            self.assertTrue((root / "report.json").is_file())


if __name__ == "__main__":
    unittest.main()
