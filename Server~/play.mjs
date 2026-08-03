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
 * USAGE: run with --help. The usage text lives in the USAGE const below so there
 * is exactly ONE copy of it. This header used to carry a second copy, and it
 * rotted: it gave the path as tools/playtest-mcp/play.mjs, which has not existed
 * for some time, and documented click as {"x":960,"y":540} long after the wire
 * format became {"at":[x,y]}. That matters more here than it looks — two sibling
 * CLIs in this repo tell agents "see the header of this file", so reading headers
 * is an established habit, and a wrong header is worse than no header.
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
  {"action":"start"}                        run the game (answers once it is up).
                                            THIS TAKES OVER THE AUTHOR'S EDITOR —
                                            they cannot use Unity until "stop".
                                            Only start play mode when you were
                                            asked to.
  {"action":"stop"}                         leave play mode
  {"action":"status"}                       is it running, is it compiling, are
                                            the input devices healthy? Free, and
                                            the first thing to send when something
                                            behaves impossibly.
  {"action":"look"}                         look at the screen
  {"action":"key","key":"e"}                tap a key ("seconds":3 to hold)
  {"action":"click","at":[480,270]}         click where you saw something
  {"action":"move","at":[480,270]}          move the cursor without clicking
  {"action":"scroll","notches":6}           turn the wheel (+ is in)
  {"action":"drag","from":[x,y],"to":[x,y]} press, drag, release
  {"action":"wait","seconds":2}             let something finish

  Action names are lower-case and exact. An unknown one is refused rather than
  quietly treated as "look" — if you asked for a click and were told "looked",
  the verb was wrong, not the game.

  Coordinates are in the PICTURE's own pixels, origin bottom-left — read a
  position off the FULL-SIZE picture and pass those exact numbers. Never measure
  off a contact-sheet tile: tiles are about a third the size, so tile numbers are
  in range, silently wrong, and land in the lower-left quadrant.

  Every action is filmed until the PIXELS stop changing, so you usually get a
  numbered sequence plus the final frame. A missing sequence means the picture
  never changed — which is NOT the same as "nothing happened". A state change
  with no visible motion shows no sequence, and a scene with idle camera sway
  shows one for every action including "look". Judge from the picture, not from
  the presence of a sheet.
  "watch":3 waits longer for a slow reaction; "fps":10 samples more finely.

INSPECT — the developer's camera. Works in edit mode and during play; in play it
shows the live scene, which is the only way to see where something instantiated at
runtime ended up. NOT FOR PLAYTESTING: it sees through walls, and a report is only
worth anything if it came from someone who could not.
  {"tool":"inspect","action":"look"}
  {"tool":"inspect","action":"turn","yaw":30,"pitch":-10}     look around (yaw is
                                                              a delta; the reply
                                                              gives the absolute)
  {"tool":"inspect","action":"pan","right":1,"forward":2}     slide, angle kept
                                                              ("up":1 also slides)
  {"tool":"inspect","action":"zoom","by":1}                   each step halves the
                                                              field; negative backs off
  {"tool":"inspect","action":"frame","target":"Desk","yaw":35}  one object, your angle
  {"tool":"inspect","action":"orbit","target":"Desk","angles":4}   angles is 1-8
  {"tool":"inspect","action":"plan","from":"top","target":"Desk"}
  {"tool":"inspect","action":"plan","from":"top"}             no target = whole scene

  Add "brighten":0 to any of these. Captures are exposure-lifted by default so a
  dark scene can still be measured — turn it off whenever the LIGHTING itself is
  what you are judging, or you will be reading a processed picture.

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

// Check the verb HERE, before Unity ever sees it. Two failures used to hide in
// the gap: an unknown action fell through to "look" on the Unity side and came
// back reporting `looked` with a picture of an unchanged screen (so a typo read
// as "the object is not clickable" — a bug filed against the GAME), and before
// play mode the same typo hit the not-running guard first and was answered with
// `Send {"action":"start"} first`, which recommends seizing the author's editor
// as the remedy for a spelling mistake.
// Three tools share this transport, keyed on "tool". Snapshot's action is
// optional (absent means write); the other two always need one.
const TOOLS = {
  play: { actions: ["start", "stop", "status", "look", "key", "click", "move", "scroll", "drag", "wait"],
          actionRequired: true },
  inspect: { actions: ["look", "turn", "pan", "zoom", "frame", "orbit", "plan"],
             actionRequired: true },
  snapshot: { actions: ["write", "diff"], actionRequired: false },
};

if (action === null || typeof action !== "object" || Array.isArray(action)) {
  console.error(`the argument must be a JSON OBJECT, e.g. '{"action":"look"}'`);
  process.exit(1);
}

const which = action.tool ?? "play";
const spec = TOOLS[which];
if (!spec) {
  console.error(`'${action.tool}' is not a tool. Use one of: ${Object.keys(TOOLS).join(", ")} ` +
                `— or omit "tool" entirely for play.\nNothing was sent to Unity.`);
  process.exit(1);
}
if (!action.action && spec.actionRequired) {
  console.error(`no "action" in that JSON. Every ${which} call needs one, e.g. {"action":"look"}\n` +
                `  ${which} actions: ${spec.actions.join(", ")}\n` +
                `Nothing was sent to Unity.`);
  process.exit(1);
}
if (action.action && !spec.actions.includes(action.action)) {
  console.error(`'${action.action}' is not a ${which} action. Names are lower-case and exact.\n` +
                `  ${which} actions: ${spec.actions.join(", ")}\n` +
                (which === "play"
                  ? `  (for the developer camera add "tool":"inspect" — its actions are ` +
                    `${TOOLS.inspect.actions.join(", ")})\n`
                  : "") +
                `Nothing was sent to Unity.`);
  process.exit(1);
}

const id = `${Date.now()}-${Math.floor(Math.random() * 1e6)}`;
try {
  await writeFile(join(DIR, "command.json"), JSON.stringify({ id, action }), "utf8");
} catch (e) {
  // One command file, one at a time. Batching independent tool calls is standard
  // practice everywhere else, and here it raced two writers onto this path and
  // surfaced as a raw EBUSY stack.
  if (e.code === "EBUSY" || e.code === "EPERM") {
    console.log("FAILED: another play.mjs call is already in flight. This tool is " +
                "one-at-a-time — send actions sequentially, never as parallel tool " +
                "calls — then retry.");
    process.exit(4);
  }
  throw e;
}

const startedAt = Date.now();
const deadline = startedAt + (action.action === "start" ? 240_000 : 60_000);
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
  // Only promise pictures when there are some. "stop", and any action that
  // produced no frames, used to print this heading over nothing.
  if ((parsed.shots ?? []).length) console.log("\npictures — OPEN EVERY ONE:");
  for (const s of parsed.shots ?? []) {
    const path = typeof s === "string" ? s : s.path;
    if (typeof s !== "string") console.log("  " + s.caption);
    console.log("  " + path + "\n");
  }
  process.exit(0);
}

// Report the deadline actually used. This was hardcoded to 60s while `start` is
// allowed 240, so a start timeout misreported its own budget by 4x and sent you
// hunting a one-minute hang that never existed.
const waited = Math.round((deadline - startedAt) / 1000);
console.log(
  `FAILED: Unity did not answer within ${waited}s. Is the editor open` +
    (action.action === "start" ? "" : ", and is the game running in play mode") +
    `, and is the Unity console free of compile errors?\n` +
    `Send {"action":"status"} to ask the bridge what it believes is true. ` +
    `Nothing here can open the editor for you.`
);
process.exit(3);
