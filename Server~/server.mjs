#!/usr/bin/env node
/**
 * Playtest MCP server — lets an agent PLAY Dear Suspect and SEE what happened.
 *
 * The whole reason this exists rather than a script call: screenshots come back
 * INLINE as images in the tool result. With file paths the agent has to choose
 * to open them, and friction that discourages looking defeats the point of a
 * playtest. Here the pictures are simply there.
 *
 * Transport is a pair of files shared with Assets/Scripts/Editor/PlaytestServer.cs.
 * No sockets, no threads, no dependencies — this file is dependency-free JSON-RPC
 * over stdio, so there is nothing to npm install and nothing to keep in sync.
 *
 * REQUIRES Unity to be open with the game running in play mode. If it is not,
 * the tool says so plainly rather than hanging.
 *
 * Register in .mcp.json:
 *   { "mcpServers": { "playtest": { "command": "node",
 *       "args": ["tools/playtest-mcp/server.mjs"] } } }
 */

import { readFile, writeFile, mkdir } from "node:fs/promises";
import { existsSync } from "node:fs";
import { join } from "node:path";
import { tmpdir } from "node:os";

const DIR = process.env.UNITY_PLAYTEST_DIR ?? join(tmpdir(), "unity-playtest");
const CMD = join(DIR, "command.json");
const RES = join(DIR, "result.json");
const TIMEOUT_MS = 60_000;
// Entering play mode compiles, reloads the domain and loads a scene. Judging that
// by the same stopwatch as a mouse click reports a working editor as a dead one.
const START_TIMEOUT_MS = 240_000;

await mkdir(DIR, { recursive: true });

// ── the one tool ────────────────────────────────────────────────────────

const TOOL = {
  name: "play",
  description:
    "Play the game as a human would and see the result as screenshots.\n\n" +
    "Give ONE action. You get TWO pictures back:\n" +
    "  1. THE SEQUENCE — every frame of the action tiled into one image, numbered " +
    "in order, left to right then down. Frame 1 is before the action, the last is " +
    "after it. This is where you see MOVEMENT: compare one cell against the next.\n" +
    "  2. THE SCREEN NOW — the same final moment, full size. This is where you " +
    "READ text and MEASURE positions.\n\n" +
    "Actions:\n" +
    '  {"action":"start"}                                 run the game (answers once it is up)\n' +
    '  {"action":"stop"}                                  leave play mode\n' +
    '  {"action":"look"}                                  just look at the screen\n' +
    '  {"action":"key","key":"f"}                         tap a key\n' +
    '  {"action":"key","key":"w","seconds":3}             HOLD a key (sampled once a second)\n' +
    '  {"action":"click","at":[960,540]}                  click a screen position\n' +
    '  {"action":"move","at":[960,540]}                   move the cursor without clicking\n' +
    '  {"action":"scroll","notches":6}                    turn the wheel (+ in, - out)\n' +
    '  {"action":"drag","from":[960,400],"to":[960,700]}  press, drag, release\n' +
    '  {"action":"wait","seconds":2}                      let something finish\n\n' +
    "COORDINATES ARE IN THE PICTURE'S OWN PIXELS, origin BOTTOM-LEFT. Point at " +
    "something in THE SCREEN NOW and pass those exact numbers — no scaling, no " +
    "arithmetic. (Do not measure off the sequence sheet; its cells are shrunk.) " +
    "There is no way to address anything by name, on purpose: if you cannot see it, " +
    "you cannot click it, and that is the point.\n\n" +
    "EVERY ACTION IS FILMED. The input is the fast part; what the game does about " +
    "it is not. After each action the camera keeps rolling until the picture stops " +
    "changing, so a click that starts a slow camera move gives you the whole move, " +
    "not a photograph taken half way through it.\n" +
    '  "watch": 3      wait up to 3s for a slow reaction (default 1.2, max 10)\n' +
    '  "fps": 10       sample more finely while watching (default 5, max 20)\n' +
    '  "steps": 8      break a drag into more movements (max 8)\n' +
    "Raise watch when something is still moving in the last cell. Raise fps when the " +
    "cells jump and you want to see what happened between them.\n\n" +
    "You do not choose how many pictures you get. Sampling is fixed by fps and watch; " +
    "a sample only becomes a picture if the screen actually changed since the last one, " +
    "and the run ends as soon as the screen goes still. So no sequence at all means " +
    "nothing happened — which is usually the most important thing an action can tell you.",
  inputSchema: {
    type: "object",
    properties: {
      action: {
        type: "string",
        enum: ["start", "stop", "look", "key", "click", "move", "scroll", "drag", "wait"],
        description: "What to do.",
      },
      key: { type: "string", description: 'Key name for "key", e.g. w, f, escape, space.' },
      seconds: { type: "number", description: "Hold/wait duration. A held key is sampled once a second." },
      at: { type: "array", items: { type: "number" }, description: "[x,y] for click/move. Origin bottom-left — the same shape as from/to." },
      x: { type: "number", description: "Deprecated: X for click/move. Prefer at:[x,y]." },
      y: { type: "number", description: "Deprecated: Y for click/move. Prefer at:[x,y]." },
      notches: { type: "number", description: "Wheel notches for scroll. Positive zooms in." },
      from: { type: "array", items: { type: "number" }, description: "[x,y] drag start." },
      to: { type: "array", items: { type: "number" }, description: "[x,y] drag end." },
      steps: { type: "number", description: "How many movements to break a drag into (1-8). Not a picture count — n steps photograph the n-1 moments between them." },
      watch: { type: "number", description: "Seconds to keep filming after the action, waiting for the game to settle. Default 1.2, max 10. Raise it when the last cell is still moving." },
      fps: { type: "number", description: "Frames per second while watching. Default 5, max 20. Raise it to see what happened between cells." },
    },
    required: ["action"],
  },
};

const INSPECT_TOOL = {
  name: "inspect",
  description:
    "Look at the scene with the DEVELOPER's camera — the counterpart to `play`, " +
    "which is the player's. Use it to answer staging questions: does this float, " +
    "does it intersect, is it the right size, is it where I think it is.\n\n" +
    "EDIT MODE ONLY. It refuses while the game is running, on purpose — a flying " +
    "camera would let a playtest see what a player cannot.\n\n" +
    '  {"action":"look"}                                shoot from where you are\n' +
    '  {"action":"turn","yaw":30,"pitch":-10}           look around, camera stays put\n' +
    '  {"action":"pan","right":1,"up":0.5,"forward":2}  slide, angle unchanged\n' +
    '  {"action":"zoom","by":1}                         closer (negative backs off)\n' +
    '  {"action":"frame","target":"Desk","yaw":35}      one object, one angle\n' +
    '  {"action":"orbit","target":"Desk","angles":4}    a ring of angles, one sheet\n' +
    '  {"action":"plan","from":"top","target":"Desk"}   orthographic, no perspective\n\n' +
    "ORBIT is usually what you want. Whatever stands in front of a thing from here " +
    "is exactly what you cannot see past, and you do not know it is there until you " +
    "have moved — so one angle cannot answer 'show me this object'.\n\n" +
    "PLAN is how you settle whether two things are touching. Perspective is what " +
    "makes that unanswerable from any normal angle; an orthographic top view " +
    "answers it in one picture. `from` takes top, front, back, left, right, iso.\n\n" +
    "Anything nearer to the camera than the target is clipped away, so a wall " +
    "between you and the subject never becomes the picture. `target` matches a " +
    "full path, then an exact name, then any substring, including inactive objects.",
  inputSchema: {
    type: "object",
    properties: {
      action: {
        type: "string",
        enum: ["look", "turn", "pan", "zoom", "frame", "orbit", "plan"],
        description: "What to do.",
      },
      target: { type: "string", description: "Object to look at, for frame/orbit/plan." },
      yaw: { type: "number", description: "Degrees clockwise. Turns the camera, or sets the first angle of an orbit." },
      pitch: { type: "number", description: "Degrees down from level. Positive looks down." },
      right: { type: "number", description: "Metres to slide right (pan)." },
      up: { type: "number", description: "Metres to slide up (pan)." },
      forward: { type: "number", description: "Metres to slide forward (pan)." },
      by: { type: "number", description: "Zoom steps; each one halves the field." },
      angles: { type: "number", description: "How many angles in an orbit (1-8, default 4)." },
      margin: { type: "number", description: "Framing room around the target. >1 pulls back, default 1.6." },
      from: { type: "string", description: "Plan direction: top, front, back, left, right, iso." },
    },
    required: ["action"],
  },
};

// ── talking to Unity ────────────────────────────────────────────────────

async function run(action) {
  const id = `${Date.now()}-${Math.floor(Math.random() * 1e6)}`;
  await writeFile(CMD, JSON.stringify({ id, action }), "utf8");

  const deadline =
    Date.now() + (action?.action === "start" ? START_TIMEOUT_MS : TIMEOUT_MS);
  while (Date.now() < deadline) {
    await new Promise((r) => setTimeout(r, 120));
    if (!existsSync(RES)) continue;
    let parsed;
    try {
      parsed = JSON.parse(await readFile(RES, "utf8"));
    } catch {
      continue; // half-written; look again
    }
    // Only ever accept the answer to the question we asked. A stale result from
    // an earlier run must never be mistaken for this one's.
    if (parsed.id === id) return parsed;
  }
  return {
    ok: false,
    error:
      "Unity did not answer in time. Is the editor open? If it is, it may be busy " +
      "compiling — wait and try again.",
  };
}

async function toContent(result) {
  const out = [];
  const headline = result.ok
    ? `${result.did || "done"}   ·   screen ${result.screen?.[0]}x${result.screen?.[1]}`
    : `FAILED: ${result.error || "unknown error"}`;
  out.push({ type: "text", text: headline });

  for (const shot of result.shots ?? []) {
    // Unity used to send bare paths and now sends {path, caption}; accept both so
    // a half-updated editor still answers instead of returning nothing.
    const path = typeof shot === "string" ? shot : shot.path;
    const caption = typeof shot === "string" ? path.split(/[\\/]/).pop() : shot.caption;
    try {
      const bytes = await readFile(path);
      out.push({ type: "text", text: caption });
      out.push({ type: "image", data: bytes.toString("base64"), mimeType: "image/png" });
    } catch {
      out.push({ type: "text", text: `(could not read ${path})` });
    }
  }
  return out;
}

// ── JSON-RPC over stdio ─────────────────────────────────────────────────

function send(msg) {
  process.stdout.write(JSON.stringify(msg) + "\n");
}

function reply(id, result) {
  if (id !== undefined && id !== null) send({ jsonrpc: "2.0", id, result });
}

let buffer = "";
process.stdin.setEncoding("utf8");
process.stdin.on("data", async (chunk) => {
  buffer += chunk;
  let nl;
  while ((nl = buffer.indexOf("\n")) >= 0) {
    const line = buffer.slice(0, nl).trim();
    buffer = buffer.slice(nl + 1);
    if (!line) continue;

    let msg;
    try {
      msg = JSON.parse(line);
    } catch {
      continue;
    }

    try {
      if (msg.method === "initialize") {
        reply(msg.id, {
          protocolVersion: "2024-11-05",
          capabilities: { tools: {} },
          serverInfo: { name: "dear-suspect-playtest", version: "1.0.0" },
        });
      } else if (msg.method === "tools/list") {
        reply(msg.id, { tools: [TOOL, INSPECT_TOOL] });
      } else if (msg.method === "tools/call") {
        const name = msg.params?.name;
        if (name !== "play" && name !== "inspect") {
          reply(msg.id, {
            content: [{ type: "text", text: `unknown tool '${name}'` }],
            isError: true,
          });
        } else {
          // Both tools share one transport; the `tool` field is what Unity routes
          // on, so an inspect call is a play call wearing a label.
          const args = msg.params.arguments ?? { action: "look" };
          const result = await run(name === "inspect" ? { ...args, tool: "inspect" } : args);
          reply(msg.id, { content: await toContent(result), isError: !result.ok });
        }
      } else if (msg.id !== undefined && msg.id !== null) {
        // Anything else we do not implement: answer rather than hang.
        reply(msg.id, {});
      }
    } catch (e) {
      if (msg.id !== undefined && msg.id !== null) {
        send({ jsonrpc: "2.0", id: msg.id, error: { code: -32000, message: String(e?.message ?? e) } });
      }
    }
  }
});
