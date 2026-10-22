const parentOrigin = new URL(document.referrer).origin;
if (window === window.top || parentOrigin === location.origin)
  throw new Error("The app sandbox must have a different origin from its host.");

const inner = document.createElement("iframe");
inner.title = "App";
inner.setAttribute("sandbox", "allow-scripts allow-same-origin allow-forms");
document.body.append(inner);

window.addEventListener("message", (event: MessageEvent) => {
  if (event.source === window.parent && event.origin === parentOrigin) {
    if (event.data?.method === "ui/notifications/sandbox-resource-ready") {
      // The host serves the exact approved resource with an HTTP CSP, not a mutable meta tag.
      inner.src = new URL("./resource", location.href).href;
    } else {
      inner.contentWindow?.postMessage(event.data, location.origin);
    }
  } else if (event.source === inner.contentWindow && event.origin === location.origin) {
    if (!String(event.data?.method ?? "").startsWith("ui/notifications/sandbox-"))
      window.parent.postMessage(event.data, parentOrigin);
  }
});

window.parent.postMessage({
  jsonrpc: "2.0", method: "ui/notifications/sandbox-proxy-ready", params: {},
}, parentOrigin);
