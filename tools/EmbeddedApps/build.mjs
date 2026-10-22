import { build } from "esbuild";
import { fileURLToPath } from "node:url";
import { readFile, readdir, writeFile } from "node:fs/promises";

const result = await build({
  entryPoints: {
    host: fileURLToPath(new URL("./src/host.ts", import.meta.url)),
    sandbox: fileURLToPath(new URL("./src/sandbox.ts", import.meta.url)),
  },
  outdir: fileURLToPath(new URL("../../src/Lumi/Assets/EmbeddedApps/", import.meta.url)),
  bundle: true,
  minify: true,
  format: "esm",
  platform: "browser",
  target: "es2022",
  legalComments: "eof",
  metafile: true,
});

const packages = new Set(Object.keys(result.metafile.inputs).flatMap((input) => {
  const match = input.replaceAll("\\", "/").match(/node_modules\/((?:@[^/]+\/)?[^/]+)/);
  return match ? [match[1]] : [];
}));
const notices = [];
for (const name of [...packages].sort()) {
  const root = new URL(`./node_modules/${name}/`, import.meta.url);
  const info = JSON.parse(await readFile(new URL("package.json", root), "utf8"));
  const files = (await readdir(root)).filter((file) => /^(licen[cs]e|notice)(\.(md|txt))?$/i.test(file));
  if (!files.some((file) => /^licen[cs]e/i.test(file)))
    throw new Error(`No license file found for bundled dependency ${name}`);
  for (const file of files.sort())
    notices.push(`${name} ${info.version} - ${file}\n${await readFile(new URL(file, root), "utf8")}`);
}
await writeFile(new URL("../../src/Lumi/Assets/EmbeddedApps/LICENSES.txt", import.meta.url),
  notices.join("\n\n----------------------------------------\n\n"));
