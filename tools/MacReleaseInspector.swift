import AppKit
import ApplicationServices
import Foundation

func emit(_ value: Any) {
    do {
        let data = try JSONSerialization.data(withJSONObject: value, options: [.prettyPrinted, .sortedKeys])
        print(String(data: data, encoding: .utf8)!)
    } catch {
        fputs("Inspector JSON error: \(error)\n", stderr)
        exit(1)
    }
}

func appInfo(_ app: NSRunningApplication) -> [String: Any] {
    return [
        "pid": app.processIdentifier,
        "name": app.localizedName ?? "",
        "bundleIdentifier": app.bundleIdentifier ?? "",
        "bundlePath": app.bundleURL?.path ?? "",
        "executablePath": app.executableURL?.path ?? "",
        "active": app.isActive,
        "hidden": app.isHidden,
        "terminated": app.isTerminated,
        "finishedLaunching": app.isFinishedLaunching,
    ]
}

func attribute(_ element: AXUIElement, _ name: String) -> CFTypeRef? {
    var result: CFTypeRef?
    return AXUIElementCopyAttributeValue(element, name as CFString, &result) == .success ? result : nil
}

func describe(_ element: AXUIElement, depth: Int, budget: inout Int) -> [String: Any] {
    budget -= 1
    var result: [String: Any] = [:]
    for name in ["AXRole", "AXSubrole", "AXTitle", "AXDescription", "AXIdentifier", "AXEnabled", "AXFocused"] {
        if let value = attribute(element, name) as? String {
            result[name] = value
        } else if let value = attribute(element, name) as? NSNumber {
            result[name] = value
        }
    }
    if depth < 8, budget > 0, let children = attribute(element, "AXChildren") as? [AXUIElement] {
        var nodes: [[String: Any]] = []
        for child in children.prefix(80) where budget > 0 {
            nodes.append(describe(child, depth: depth + 1, budget: &budget))
        }
        result["children"] = nodes
    }
    return result
}

let arguments = CommandLine.arguments
guard arguments.count == 3 else {
    fputs("Usage: inspector locate <bundle-path> | inspect/activate/hide/quit <pid>\n", stderr)
    exit(1)
}

let action = arguments[1]
if action == "locate" {
    let expected = URL(fileURLWithPath: arguments[2]).standardizedFileURL.resolvingSymlinksInPath()
    emit(NSWorkspace.shared.runningApplications.filter {
        $0.bundleURL?.standardizedFileURL.resolvingSymlinksInPath() == expected
    }.map(appInfo))
    exit(0)
}

guard let pid = Int32(arguments[2]), let app = NSRunningApplication(processIdentifier: pid) else {
    fputs("No running application with the specified PID.\n", stderr)
    exit(2)
}

switch action {
case "activate":
    app.unhide()
    emit(["requested": app.activate(options: [.activateAllWindows, .activateIgnoringOtherApps])])
case "hide":
    emit(["requested": app.hide()])
case "quit":
    emit(["requested": app.terminate()])
case "inspect":
    var result = appInfo(app)
    let windows = CGWindowListCopyWindowInfo([.optionAll, .excludeDesktopElements], kCGNullWindowID) as? [[String: Any]] ?? []
    result["windows"] = windows.filter {
        ($0[kCGWindowOwnerPID as String] as? NSNumber)?.int32Value == pid
    }
    result["accessibilityTrusted"] = AXIsProcessTrusted()
    result["screenCaptureAccess"] = CGPreflightScreenCaptureAccess()
    if AXIsProcessTrusted() {
        var budget = 350
        result["accessibility"] = describe(AXUIElementCreateApplication(pid), depth: 0, budget: &budget)
    }
    emit(result)
default:
    fputs("Unsupported inspector action.\n", stderr)
    exit(1)
}
