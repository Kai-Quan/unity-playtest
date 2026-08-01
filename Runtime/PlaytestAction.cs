#if UNITY_EDITOR || DEVELOPMENT_BUILD
using System.Collections;
using System.Collections.Generic;
using System.Text;
using UnityEngine;

namespace Elegist.Playtest
{
    /// <summary>
    /// A HUMAN-LEVEL play interface, for agents that should actually play the game
    /// rather than script it.
    ///
    /// One declarative action in, a series of screenshots out:
    ///
    ///     {"action":"key",   "key":"w", "seconds":3}
    ///     {"action":"click", "x":960, "y":540}
    ///     {"action":"scroll","notches":6}
    ///     {"action":"drag",  "from":[960,400], "to":[960,700], "seconds":0.4}
    ///     {"action":"wait",  "seconds":2}
    ///     {"action":"look"}
    ///
    /// EVERY ACTION ANSWERS IN TWO PICTURES. A contact sheet of the whole gesture
    /// — before it, during it, after it, numbered in order — and then the screen
    /// as it stands now, full size. Motion is a relationship between frames, and
    /// tiling them puts both halves of that relationship inside one image where a
    /// model can actually compare them; the separate full-size frame is the one
    /// whose text gets read and whose positions get clicked. Loose frames did
    /// neither job well and paid full price for both.
    ///
    /// COORDINATES ARE IN THE COORDINATE SPACE OF THE PICTURES. The screenshots
    /// are smaller than the real window, so an agent reading a position off one
    /// and passing it back would land somewhere else entirely. Rather than warn
    /// about the arithmetic, this scales for the caller: what you see is what you
    /// click, and the real resolution is something an agent never has to know.
    ///
    /// THE VOCABULARY DELIBERATELY EXPOSES NO INTERNAL NAMES. No GameObjects, no
    /// facet ids, no scene paths — only what a person at the keyboard has:
    /// coordinates, keys, and their own eyes. An agent that can click
    /// "Evidence_Diary" by name cannot discover that the diary is impossible to
    /// SEE, and that failure is exactly the kind a hand-written regression test is
    /// blind to. Aim by reading the previous screenshot; every result reports the
    /// screen size so coordinates mean something.
    ///
    /// Actions span frames, so this is start-then-collect:
    ///     PlaytestAction.Begin(json)   -> "running"
    ///     PlaytestAction.Result()      -> JSON once done
    /// An MCP wrapper should poll Result() internally and hand the agent one
    /// tool call whose payload embeds the images inline. That is the real prize:
    /// screenshots the agent SEES without choosing to open them.
    /// </summary>
    public static class PlaytestAction
    {
        public static string LastResult = "{\"ok\":false,\"error\":\"nothing run yet\"}";
        public static bool Running;

        private static int _seq;

        public static string Begin(string json)
        {
            if (PlaytestBridge.Instance == null)
                return "{\"ok\":false,\"error\":\"not in play mode\"}";
            if (Running) return "{\"ok\":false,\"error\":\"an action is still running\"}";

            var a = Parse(json);
            if (a == null) return "{\"ok\":false,\"error\":\"could not parse action\"}";

            Running = true;
            LastResult = "";
            PlaytestBridge.Instance.StartCoroutine(Run(a));
            return "running";
        }

        public static string Result() => Running ? "running" : LastResult;

        // ── the action ──────────────────────────────────────────────────

        private class Act
        {
            public string action = "look";
            public string key;
            public float seconds;
            public int notches;
            public float x, y;
            public float fromX, fromY, toX, toY;
            public bool hasFrom;
            /// <summary>How many pictures the caller wants from the middle of a
            /// gesture. Left to the agent because only it knows whether it is
            /// watching something move or just getting somewhere.</summary>
            public int frames;
        }

        private static IEnumerator Run(Act a)
        {
            var frames = new List<Texture2D>();
            var times = new List<float>();
            var log = new StringBuilder();

            // Positions arrive in picture space; the game wants window pixels.
            float k = ShotToScreen();
            float t0 = Time.realtimeSinceStartup;

            yield return Capture(frames, times, t0);

            switch (a.action)
            {
                case "key":
                    if (a.seconds > 0.05f)
                    {
                        // Hold, sampling as it goes so the agent sees the middle of
                        // the action and not only its end. Six samples is what fits
                        // a legible grid; a longer hold just spaces them wider.
                        PlaytestBridge.HoldKey(a.key, a.seconds);
                        float step = Mathf.Clamp(a.seconds / 6f, 0.25f, 1f);
                        for (float t = 0f; t < a.seconds; t += step)
                        {
                            yield return new WaitForSecondsRealtime(Mathf.Min(step, a.seconds - t));
                            yield return Capture(frames, times, t0);
                        }
                    }
                    else
                    {
                        PlaytestBridge.Key(a.key);
                        yield return Settle();
                    }
                    log.Append($"pressed '{a.key}'");
                    if (a.seconds > 0.05f) log.Append($" for {a.seconds:0.#}s");
                    break;

                case "click":
                    PlaytestBridge.MoveMouse(a.x * k, a.y * k);
                    yield return null;
                    PlaytestBridge.Click(a.x * k, a.y * k);
                    yield return Settle();
                    log.Append($"clicked ({a.x:0}, {a.y:0})");
                    break;

                case "move":
                    PlaytestBridge.MoveMouse(a.x * k, a.y * k);
                    yield return new WaitForSecondsRealtime(0.15f);
                    log.Append($"moved the cursor to ({a.x:0}, {a.y:0})");
                    break;

                case "scroll":
                    PlaytestBridge.Scroll(a.notches);
                    yield return Settle();
                    // The lens keeps easing after the wheel stops; wait for the
                    // picture to stop changing or the shot is of a half-done zoom.
                    yield return new WaitForSecondsRealtime(0.5f);
                    log.Append($"scrolled {a.notches} notch(es)");
                    break;

                case "drag":
                    {
                        // A drag is the one gesture whose MIDDLE is the interesting
                        // part — turning an object in the hand looks like nothing at
                        // either end. Break it into legs and shoot between them, so
                        // the agent sees the thing rotating rather than two stills
                        // that could equally be no movement at all.
                        int legs = Mathf.Clamp(a.frames > 0 ? a.frames : 5, 1, 8);
                        float dur = a.seconds > 0.05f ? a.seconds : 0.5f;
                        for (int i = 0; i < legs; i++)
                        {
                            float s0 = i / (float)legs, s1 = (i + 1) / (float)legs;
                            PlaytestBridge.HoldDrag(
                                Mathf.Lerp(a.fromX, a.toX, s0) * k, Mathf.Lerp(a.fromY, a.toY, s0) * k,
                                Mathf.Lerp(a.fromX, a.toX, s1) * k, Mathf.Lerp(a.fromY, a.toY, s1) * k,
                                Mathf.Max(4, Mathf.RoundToInt(dur / legs * 60f)));
                            yield return Settle();
                            if (i < legs - 1) yield return Capture(frames, times, t0);
                        }
                        log.Append($"dragged ({a.fromX:0},{a.fromY:0}) to ({a.toX:0},{a.toY:0})");
                    }
                    break;

                case "wait":
                    yield return new WaitForSecondsRealtime(Mathf.Max(0.1f, a.seconds));
                    log.Append($"waited {a.seconds:0.#}s");
                    break;

                default:
                    log.Append("looked");
                    break;
            }

            yield return Capture(frames, times, t0);

            LastResult = Publish(frames, times, log.ToString());
            foreach (var f in frames) Object.Destroy(f);
            Running = false;
        }

        /// <summary>Screen pixels per screenshot pixel — the one number that keeps
        /// the agent's coordinate space and its eyes in agreement.</summary>
        private static float ShotToScreen()
        {
            int edge = Mathf.Max(Screen.width, Screen.height);
            return edge <= PlaytestBridge.ScreenshotLongEdge
                ? 1f
                : edge / (float)PlaytestBridge.ScreenshotLongEdge;
        }

        /// <summary>Write the sequence and the final frame, and describe both. The
        /// captions matter as much as the pixels: a sheet nobody knows how to read
        /// is just a small blurry screenshot.</summary>
        private static string Publish(List<Texture2D> frames, List<float> times, string did)
        {
            if (frames.Count == 0)
                return "{\"ok\": false, \"error\": \"captured nothing — is the game still running?\"}";

            string dir = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "playtest");
            System.IO.Directory.CreateDirectory(dir);
            _seq++;

            var last = frames[frames.Count - 1];
            var shots = new List<(string path, string caption)>();

            if (frames.Count > 1)
            {
                string sheet = System.IO.Path.Combine(dir, $"{_seq:000}_sequence.png");
                System.IO.File.WriteAllBytes(sheet, PlaytestContactSheet.Compose(frames));

                var when = new StringBuilder();
                for (int i = 0; i < times.Count; i++)
                    when.Append(i == 0 ? "" : ", ").Append($"{times[i]:0.0}s");
                shots.Add((sheet,
                    $"THE WHOLE ACTION, {frames.Count} frames in order — left to right then down, " +
                    $"numbered 1-{frames.Count}. 1 is before it, {frames.Count} is after it. " +
                    $"Taken at {when}. Read MOVEMENT off this; do not read positions off it."));
            }

            string now = System.IO.Path.Combine(dir, $"{_seq:000}_now.png");
            System.IO.File.WriteAllBytes(now, last.EncodeToPNG());
            shots.Add((now, "THE SCREEN NOW, full size. Aim off this one — the numbers you " +
                            "read here are the numbers to pass back."));

            var sb = new StringBuilder("{");
            sb.Append("\"ok\": true, ");
            sb.Append($"\"did\": \"{PlaytestBridge.EscapeJson(did)}\", ");
            sb.Append($"\"screen\": [{last.width}, {last.height}], ");
            sb.Append("\"shots\": [");
            for (int i = 0; i < shots.Count; i++)
            {
                if (i > 0) sb.Append(", ");
                sb.Append($"{{\"path\": \"{PlaytestBridge.EscapeJson(shots[i].path.Replace('\\', '/'))}\", ");
                sb.Append($"\"caption\": \"{PlaytestBridge.EscapeJson(shots[i].caption)}\"}}");
            }
            sb.Append("]}");
            return sb.ToString();
        }

        private static IEnumerator Settle()
        {
            // Wait for the gesture to START before waiting for it to end — the busy
            // flag is not set on the frame the gesture is requested, so waiting only
            // for "not busy" returns instantly and screenshots land mid-action.
            float t = 0f;
            while (t < 0.5f && !PlaytestBridge.Busy) { t += Time.unscaledDeltaTime; yield return null; }
            t = 0f;
            while (t < 5f && PlaytestBridge.Busy) { t += Time.unscaledDeltaTime; yield return null; }
            yield return new WaitForSecondsRealtime(0.2f);
        }

        private static IEnumerator Capture(List<Texture2D> into, List<float> times, float t0)
        {
            // End of frame, or the capture reads a half-drawn back buffer.
            yield return new WaitForEndOfFrame();
            var tex = PlaytestBridge.GrabFrame(PlaytestBridge.ScreenshotLongEdge);
            if (tex == null) yield break;

            // Drop a frame identical to the one before it. Every cell an agent
            // receives costs context it could have spent playing longer, and a
            // duplicate teaches it nothing — "nothing changed" is better said once
            // in words than shown twice.
            if (into.Count > 0 && SamePixels(into[into.Count - 1], tex))
            {
                Object.Destroy(tex);
                yield break;
            }
            into.Add(tex);
            times.Add(Time.realtimeSinceStartup - t0);
        }

        private static bool SamePixels(Texture2D a, Texture2D b)
        {
            if (a == null || b == null || a.width != b.width || a.height != b.height) return false;
            var pa = a.GetRawTextureData();
            var pb = b.GetRawTextureData();
            if (pa.Length != pb.Length) return false;
            for (int i = 0; i < pa.Length; i++) if (pa[i] != pb[i]) return false;
            return true;
        }

        // ── tiny JSON reader ────────────────────────────────────────────
        // Deliberately hand-rolled: this takes flat objects of strings and
        // numbers, and pulling in a parser for that would be the larger cost.

        private static Act Parse(string json)
        {
            if (string.IsNullOrWhiteSpace(json)) return null;
            var a = new Act();
            a.action = Str(json, "action") ?? "look";
            a.key = Str(json, "key");
            a.seconds = Num(json, "seconds", Num(json, "durationInSeconds", 0f));
            a.notches = Mathf.RoundToInt(Num(json, "notches", 0f));
            a.frames = Mathf.RoundToInt(Num(json, "frames", 0f));
            a.x = Num(json, "x", 0f);
            a.y = Num(json, "y", 0f);

            // "keyPress" is accepted as a friendlier alias for {"action":"key"}.
            var kp = Str(json, "keyPress");
            if (!string.IsNullOrEmpty(kp)) { a.action = "key"; a.key = kp; }

            var from = Pair(json, "from");
            var to = Pair(json, "to");
            if (from.HasValue && to.HasValue)
            {
                a.hasFrom = true;
                a.fromX = from.Value.x; a.fromY = from.Value.y;
                a.toX = to.Value.x; a.toY = to.Value.y;
                if (a.action == "look") a.action = "drag";
            }
            return a;
        }

        private static string Str(string json, string key)
        {
            int i = json.IndexOf($"\"{key}\"", System.StringComparison.OrdinalIgnoreCase);
            if (i < 0) return null;
            int c = json.IndexOf(':', i); if (c < 0) return null;
            int q1 = json.IndexOf('"', c); if (q1 < 0) return null;
            int q2 = json.IndexOf('"', q1 + 1); if (q2 < 0) return null;
            // Reject a number that happens to follow — only quoted values count.
            for (int k = c + 1; k < q1; k++) if (!char.IsWhiteSpace(json[k])) return null;
            return json.Substring(q1 + 1, q2 - q1 - 1);
        }

        private static float Num(string json, string key, float fallback)
        {
            int i = json.IndexOf($"\"{key}\"", System.StringComparison.OrdinalIgnoreCase);
            if (i < 0) return fallback;
            int c = json.IndexOf(':', i); if (c < 0) return fallback;
            var sb = new StringBuilder();
            for (int k = c + 1; k < json.Length; k++)
            {
                char ch = json[k];
                if (char.IsWhiteSpace(ch)) { if (sb.Length > 0) break; continue; }
                if (char.IsDigit(ch) || ch == '-' || ch == '.') sb.Append(ch);
                else break;
            }
            return float.TryParse(sb.ToString(), out float v) ? v : fallback;
        }

        private static Vector2? Pair(string json, string key)
        {
            int i = json.IndexOf($"\"{key}\"", System.StringComparison.OrdinalIgnoreCase);
            if (i < 0) return null;
            int open = json.IndexOf('[', i); if (open < 0) return null;
            int close = json.IndexOf(']', open); if (close < 0) return null;
            var parts = json.Substring(open + 1, close - open - 1).Split(',');
            if (parts.Length < 2) return null;
            if (!float.TryParse(parts[0].Trim(), out float x)) return null;
            if (!float.TryParse(parts[1].Trim(), out float y)) return null;
            return new Vector2(x, y);
        }
    }
}
#endif
