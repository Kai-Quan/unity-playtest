#!/usr/bin/env node
/**
 * The same playtest action, as a plain CLI.
 *
 * The MCP server is the nicer path — it returns screenshots inline as images, so
 * an agent sees them without deciding to. But MCP servers only load when the
 * session starts, so a freshly registered one is unavailable until a restart.
 * This is the door that is open right now: same file transport, same Unity half,
 * and it prints the screenshot paths for the caller to open.
 *
 * USAGE
 *   node tools/playtest-mcp/play.mjs '{"action":"look"}'
 *   node tools/playtest-mcp/play.mjs '{"action":"key","key":"w","seconds":3}'
 *   node tools/playtest-mcp/play.mjs '{"action":"click","x":960,"y":540}'
 *
 * Requires Unity open with the game running in play mode.
 */

import { readFile, writeFile, mkdir } from "node:fs/promises";
import { existsSync } from "node:fs";
import { join } from "node:path";
import { tmpdir } from "node:os";

const DIR = process.env.UNITY_PLAYTEST_DIR ?? join(tmpdir(), "unity-playtest");
await mkdir(DIR, { recursive: true });

const raw = process.argv[2];
if (!raw) {
  console.error('need an action, e.g. \'{"action":"look"}\'');
  process.exit(1);
}

let action;
try {
  action = JSON.parse(raw);
} catch (e) {
  console.error("that is not valid JSON: " + e.message);
  process.exit(1);
}

const id = `${Date.now()}-${Math.floor(Math.random() * 1e6)}`;
await writeFile(join(DIR, "command.json"), JSON.stringify({ id, action }), "utf8");

const deadline = Date.now() + (action.action === "start" ? 240_000 : 60_000);
while (Date.now() < deadline) {
  await new Promise((r) => setTimeout(r, 120));
  const res = join(DIR, "result.json");
  if (!existsSync(res)) continue;
  let parsed;
  try {
    parsed = JSON.parse(await readFile(res, "utf8"));
  } catch {
    continue; // half-written
  }
  // Only accept the answer to the question we asked.
  if (parsed.id !== id) continue;

  if (!parsed.ok) {
    console.log("FAILED: " + (parsed.error ?? "unknown error"));
    process.exit(2);
  }
  console.log(`${parsed.did}   ·   screen ${parsed.screen?.[0]}x${parsed.screen?.[1]}`);
  console.log("\npictures — OPEN EVERY ONE:");
  for (const s of parsed.shots ?? []) {
    const path = typeof s === "string" ? s : s.path;
    if (typeof s !== "string") console.log("  " + s.caption);
    console.log("  " + path + "\n");
  }
  process.exit(0);
}

console.log(
  "FAILED: Unity did not answer within 60s. Is the editor open AND the game running " +
    "in play mode? Nothing here can start it for you."
);
process.exit(3);
