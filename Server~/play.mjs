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

const USAGE = `Drive a running Unity game, and see what happened.

  node play.mjs '<json>'

Two tools share this script. Every call prints picture paths — OPEN THEM, they
are the answer; the text is only a label.

PLAY — the player's eyes. Needs the game running; "start" starts it.
  {"action":"start"}                        run the game (answers once it is up)
  {"action":"stop"}                         leave play mode
  {"action":"look"}                         look at the screen
  {"action":"key","key":"e"}                tap a key ("seconds":3 to hold)
  {"action":"click","at":[480,270]}         click where you saw something
  {"action":"move","at":[480,270]}          move the cursor without clicking
  {"action":"scroll","notches":6}           turn the wheel (+ is in)
  {"action":"drag","from":[x,y],"to":[x,y]} press, drag, release
  {"action":"wait","seconds":2}             let something finish

  Coordinates are in the PICTURE's own pixels, origin bottom-left — read a
  position off the full-size picture and pass those exact numbers.
  Every action is filmed until the screen stops changing, so you get a numbered
  sequence plus the final frame. NO SEQUENCE MEANS NOTHING HAPPENED.
  "watch":3 waits longer for a slow reaction; "fps":10 samples more finely.

INSPECT — the developer's camera. Works in edit mode and during play; in play it
shows the live scene, which is the only way to see where something instantiated at
runtime ended up. NOT FOR PLAYTESTING: it sees through walls, and a report is only
worth anything if it came from someone who could not.
  {"tool":"inspect","action":"look"}
  {"tool":"inspect","action":"turn","yaw":30,"pitch":-10}     look around
  {"tool":"inspect","action":"pan","right":1,"forward":2}     slide, angle kept
  {"tool":"inspect","action":"zoom","by":1}                   each step halves the field
  {"tool":"inspect","action":"frame","target":"Desk"}         one object, one angle
  {"tool":"inspect","action":"orbit","target":"Desk","angles":4}
  {"tool":"inspect","action":"plan","from":"top","target":"Desk"}

  ORBIT is usually what you want: whatever hides a thing from here is exactly
  what you cannot see past, so one angle cannot answer "show me this object".
  PLAN is orthographic — the only way to settle whether two things are touching,
  because perspective is what makes that unanswerable. from: top front back
  left right iso. Anything nearer than the target is clipped, so a wall between
  you and it never becomes the picture.

Requires the Unity editor open. Nothing here can open it for you.`;

const raw = process.argv[2];
if (!raw || raw === "--help" || raw === "-h") {
  console.log(USAGE);
  process.exit(raw ? 0 : 1);
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
