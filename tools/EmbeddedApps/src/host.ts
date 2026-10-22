import { AppBridge, PostMessageTransport } from "@modelcontextprotocol/ext-apps/app-bridge";

interface Configuration {
  kind: "mcp-app" | "canvas";
  title: string;
  hostVersion: string;
  url: string | null;
  sandboxUrl: string;
  html: string | null;
  input: Record<string, unknown>;
  result: Parameters<AppBridge["sendToolResult"]>[0];
  hostContext: NonNullable<Parameters<AppBridge["setHostContext"]>[0]>;
  metadata: { ui?: { csp?: Parameters<AppBridge["sendSandboxResourceReady"]>[0]["csp"] } };
}

const startTimeoutMs = 30000;
const frame = document.querySelector<HTMLIFrameElement>("#view")!;
let bridge: AppBridge | undefined;
let settled = false;

declare global {
  interface Window {
    lumiHostContext?: (context: Configuration["hostContext"]) => void;
  }
}
window.lumiHostContext = (context) => bridge?.setHostContext(context);

async function rpc<T>(method: string, params: unknown): Promise<T> {
  const response = await fetch("./rpc", {
    method: "POST",
    credentials: "omit",
    headers: { "Content-Type": "application/json" },
    body: JSON.stringify({ method, params }),
  });
  const body: { result?: T; error?: string } = await response.json();
  if (!response.ok || body.error) throw new Error(body.error || `Request failed (${response.status})`);
  return body.result as T;
}

// Lumi shows its own loading and error states until the app reports, once, how it started.
function settle(method: "ready" | "failed", params: object): void {
  if (settled) return;
  settled = true;
  clearTimeout(startTimeout);
  void rpc(method, params).catch(console.error);
}

// Before the app shows, a failure stops it opening; afterwards the app handles its own errors.
function fail(error: unknown): void {
  const message = error instanceof Error ? error.message : String(error);
  if (settled) void rpc("log", { level: "error", data: message }).catch(console.error);
  else settle("failed", { message });
}

const startTimeout = setTimeout(() => fail(new Error("The app did not start.")), startTimeoutMs);

async function start(): Promise<void> {
  const response = await fetch("./config", { credentials: "omit" });
  if (!response.ok) throw new Error(`Could not load this app (${response.status})`);
  const config: Configuration = await response.json();
  document.title = config.title;
  if (config.kind === "canvas") {
    if (!config.url) throw new Error("This canvas has no web interface.");
    frame.setAttribute("sandbox", "allow-scripts allow-same-origin allow-forms allow-downloads");
    frame.addEventListener("load", () => settle("ready", {}), { once: true });
    frame.src = config.url;
    return;
  }
  if (!config.html) throw new Error("The connector did not supply an app interface.");

  bridge = new AppBridge(null, { name: "Lumi", version: config.hostVersion }, {
    serverTools: {},
    serverResources: {},
    openLinks: {},
    logging: {},
    message: { text: {} },
    updateModelContext: { text: {} },
  }, { hostContext: config.hostContext });
  bridge.oncalltool = (params) => rpc("tools/call", params);
  bridge.onreadresource = (params) => rpc("resources/read", params);
  bridge.onmessage = (params) => rpc("ui/message", params);
  bridge.onopenlink = (params) => rpc("ui/open-link", params);
  bridge.onupdatemodelcontext = (params) => rpc("ui/update-model-context", params);
  bridge.onloggingmessage = (params) => { void rpc("log", params).catch(console.error); };
  bridge.onrequestdisplaymode = async () => ({ mode: "inline" });
  bridge.onerror = fail;
  bridge.oninitialized = () => {
    bridge!.sendToolInput({ arguments: config.input });
    bridge!.sendToolResult(config.result);
    settle("ready", {});
  };

  frame.setAttribute("sandbox", "allow-scripts allow-same-origin allow-forms");
  const sandboxOrigin = new URL(config.sandboxUrl).origin;
  const proxyReady = new Promise<void>((resolve, reject) => {
    const timeout = setTimeout(() => {
      window.removeEventListener("message", ready);
      reject(new Error("The app sandbox did not initialize."));
    }, 15000);
    function ready(event: MessageEvent): void {
      if (event.source !== frame.contentWindow || event.origin !== sandboxOrigin
          || event.data?.method !== "ui/notifications/sandbox-proxy-ready") return;
      clearTimeout(timeout);
      window.removeEventListener("message", ready);
      resolve();
    }
    window.addEventListener("message", ready);
  });
  frame.src = config.sandboxUrl;
  await proxyReady;
  await bridge.connect(new PostMessageTransport(frame.contentWindow!, frame.contentWindow!));
  await bridge.sendSandboxResourceReady({ html: config.html, csp: config.metadata.ui?.csp });
}

window.addEventListener("pagehide", () => { void bridge?.teardownResource({}).catch(console.error); });
void start().catch(fail);
