using System.IO;
using UnityEditor;
using UnityEngine;

namespace KaiQuan.Playtest.EditorTools
{
    /// <summary>
    /// Unity half of the playtest MCP bridge.
    ///
    /// Watches a command file, runs the action, writes a result file. Deliberately
    /// file-based rather than a socket: an HTTP listener inside the editor has to
    /// marshal every request back onto the main thread before it can touch a
    /// coroutine, and that machinery would be larger than the thing it serves.
    /// Files need no threading, survive a domain reload, and can be inspected by
    /// hand when something goes wrong.
    ///
    /// Protocol, in %TEMP%/unity-playtest/:
    ///   command.json   {"id":"abc","action":{...}}   written by the MCP server
    ///   result.json    {"id":"abc","ok":true,...}    written back by this
    ///
    /// The id is what makes it safe: a result is only ever consumed by the caller
    /// that asked for it, so a stale file from a previous run cannot be mistaken
    /// for an answer.
    ///
    /// One directory serves one editor. If you have two Unity projects open with
    /// this package, point each pair at its own by setting UNITY_PLAYTEST_DIR —
    /// which beats inventing a discovery protocol for a case most people never hit.
    /// </summary>
    [InitializeOnLoad]
    public static class PlaytestServer
    {
        public static readonly string Dir =
            System.Environment.GetEnvironmentVariable("UNITY_PLAYTEST_DIR")
            ?? Path.Combine(Path.GetTempPath(), "unity-playtest");

        private static string CommandPath => Path.Combine(Dir, "command.json");
        private static string ResultPath => Path.Combine(Dir, "result.json");

        // Entering play mode reloads the domain, which wipes statics — so the two
        // facts that have to outlive it live in SessionState instead. Without this
        // the editor comes back up, re-reads the same command file, and starts the
        // game it has already started, forever.
        private const string PendingKey = "elegist.playtest.pendingStart";
        private const string SeenKey = "elegist.playtest.lastSeen";

        private static string _servingId;
        private static double _servingSince;

        // NOTHING MAY WAIT FOREVER. Both waiting states here — a job in flight, and
        // a start that spans a domain reload — used to have no way out, so anything
        // that killed the coroutine (leaving play mode mid-action, an exception)
        // wedged the bridge permanently: it stopped reading commands at all, and the
        // only cure was restarting the editor. A dead action that ANSWERS is a bad
        // afternoon; one that goes silent costs whoever is driving it their session.
        private const double JobDeadline = 90.0;
        private const double StartDeadline = 300.0;
        private const string PendingSinceKey = "elegist.playtest.pendingSince";

        private static string LastSeen
        {
            get => SessionState.GetString(SeenKey, "");
            set => SessionState.SetString(SeenKey, value);
        }

        static PlaytestServer()
        {
            Directory.CreateDirectory(Dir);
            EditorApplication.update += Tick;
            EditorApplication.playModeStateChanged += OnPlayModeChanged;
        }

        /// <summary>Leaving play mode kills any coroutine mid-action, so the caller
        /// is owed an answer now rather than in ninety seconds. The deadline stays
        /// as the backstop for everything this does not catch.</summary>
        private static void OnPlayModeChanged(PlayModeStateChange change)
        {
            if (change != PlayModeStateChange.ExitingPlayMode || _servingId == null) return;
            PlaytestAction.Abandon("play mode was stopped while this action was running.");
            Write(_servingId, PlaytestAction.Result());
            _servingId = null;
        }

        private static void Tick()
        {
            // Finishing a "start" that spanned the domain reload: the game is up,
            // so take the first look and answer with it. Begin() refusing just
            // means the bridge has not bootstrapped yet — try again next tick.
            string resuming = SessionState.GetString(PendingKey, "");
            if (resuming != "")
            {
                if (Overdue(PendingSinceKey, StartDeadline))
                {
                    SessionState.EraseString(PendingKey);
                    PlaytestAction.Abandon(
                        "the game was asked to start but never came up. It may still be loading, or " +
                        "play mode may have been stopped while it did. Try again.");
                    Write(resuming, PlaytestAction.Result());
                    return;
                }
                if (!EditorApplication.isPlaying || EditorApplication.isCompiling) return;
                EditorApplication.ExecuteMenuItem("Window/General/Game");
                if (PlaytestAction.Begin("{\"action\":\"look\"}") != "running") return;
                SessionState.EraseString(PendingKey);
                _servingId = resuming;
                // STAMP THE CLOCK. _servingSince is a plain static, so the domain
                // reload this start just crossed has reset it to 0 — and the job
                // deadline below is `timeSinceStartup - _servingSince`, which
                // against 0 is however long the EDITOR has been open. Miss this
                // line and the very next tick judges the first screenshot
                // ~thousands of seconds overdue and abandons it instantly, so
                // EVERY start reports failure while the game is up and healthy.
                _servingSince = EditorApplication.timeSinceStartup;
                return;
            }

            // A result is pending: publish it the moment the action finishes.
            if (_servingId != null)
            {
                if (PlaytestAction.Running)
                {
                    if (EditorApplication.timeSinceStartup - _servingSince < JobDeadline) return;
                    // Say what is KNOWN, not what is guessed. This used to assert
                    // "play mode was probably stopped while it ran", which sent
                    // whoever read it looking at the game — and it was wrong every
                    // time, because the real cause was an unstamped clock here.
                    // A confident wrong diagnosis costs more than no diagnosis.
                    PlaytestAction.Abandon(
                        $"the action was still running after {JobDeadline:0}s and was abandoned. " +
                        (EditorApplication.isPlaying
                            ? "The game IS still running — send {\"action\":\"look\"} to see it. "
                            : "Play mode is no longer running. ") +
                        "The bridge is listening again.");
                }
                Write(_servingId, PlaytestAction.Result());
                _servingId = null;
                return;
            }

            if (!File.Exists(CommandPath)) return;

            string text;
            try { text = File.ReadAllText(CommandPath); }
            catch { return; }   // still being written; try again next tick

            string id = Field(text, "id");
            if (string.IsNullOrEmpty(id) || id == LastSeen) return;
            LastSeen = id;

            string action = Object(text, "action");
            if (string.IsNullOrEmpty(action)) action = "{\"action\":\"look\"}";

            // Three tools share one transport. `inspect` and `snapshot` both answer
            // immediately — one is a camera move and a render, the other is a walk
            // of the hierarchy — so neither ever becomes a pending job.
            if (Field(action, "tool") == "inspect")
            {
                Write(id, SceneInspector.Run(action));
                return;
            }

            if (Field(action, "tool") == "snapshot")
            {
                Write(id, SceneSnapshot.Run(action));
                return;
            }

            switch (Field(action, "action"))
            {
                // ASK THE BRIDGE WHAT IT THINKS IS TRUE. Handled before the
                // is-playing guard below, because "is the game even running?" is
                // exactly the question you have when nothing is working.
                //
                // This exists because two separate bugs were diagnosable in one
                // call and instead took a code read plus a controlled experiment:
                // `stop` reports success before it stops (it must — the domain
                // reload would eat a later answer), and `start` used to fail while
                // the game was up and healthy. Neither is visible from the outside
                // without this.
                case "status":
                    Write(id, Status());
                    return;

                case "start":
                    if (!EditorApplication.isPlaying)
                    {
                        // Answer AFTER the reload, not now — "the game is starting"
                        // is not a useful reply to an agent that wants to see it.
                        SessionState.SetString(PendingKey, id);
                        SessionState.SetFloat(PendingSinceKey, (float)EditorApplication.timeSinceStartup);
                        EditorApplication.isPlaying = true;
                        return;
                    }
                    action = "{\"action\":\"look\"}";   // already running: just look
                    break;

                case "stop":
                    // Write first, stop second. Stopping reloads the domain, and
                    // an answer written after that never arrives.
                    Write(id, "{\"ok\":true, \"did\":\"stopped the game\", \"screen\":[0,0], \"shots\":[]}");
                    EditorApplication.isPlaying = false;
                    return;
            }

            if (!EditorApplication.isPlaying)
            {
                Write(id, "{\"ok\":false,\"error\":\"The game is not running. Send {\\\"action\\\":\\\"start\\\"} first.\"}");
                return;
            }

            // BRING THE GAME VIEW FORWARD FIRST. Screenshots wait on
            // WaitForEndOfFrame, which in the editor only fires while the Game view
            // is actually rendering — so with the Scene tab in front, every play
            // action hangs at its first capture and never returns. Using `inspect`
            // brings the Scene tab forward, so the two tools were quietly disabling
            // each other: inspect once, and `play` stopped answering.
            EditorApplication.ExecuteMenuItem("Window/General/Game");

            string started = PlaytestAction.Begin(action);
            if (started != "running") { Write(id, started); return; }
            _servingId = id;
            _servingSince = EditorApplication.timeSinceStartup;
        }

        /// <summary>What the bridge believes about itself, in one answer.
        ///
        /// Reports the LEAKED VIRTUAL DEVICE COUNT deliberately: a script
        /// recompile during play reloads the domain, which kills the bridge
        /// component while the Input System PERSISTS its device list, so each
        /// recompile used to strand another PlaytestMouse. Past a few dozen,
        /// `Mouse.current` resolves to a dead one whose position never updates
        /// and every click silently lands at (0,0) — while keys keep working, so
        /// it reads as a coordinate problem. That cost a whole session's
        /// misdiagnosis ("the bridge cannot click"). One number here would have
        /// named it immediately.</summary>
        private static string Status()
        {
            KaiQuan.Playtest.PlaytestBridge.CountVirtualDevices(out int mice, out int keebs);

            bool pending = SessionState.GetString(PendingKey, "") != "";
            string scene = UnityEngine.SceneManagement.SceneManager.GetActiveScene().name;

            string note =
                EditorApplication.isCompiling ? "compiling — commands will queue until it finishes" :
                pending ? "a start is in flight across the domain reload" :
                _servingId != null ? "an action is in flight" :
                // NO DOUBLE QUOTES IN ANY OF THESE. They land inside a JSON string
                // that is assembled by hand below; a raw quote here breaks the
                // parse on the client, which retries silently until it times out
                // — so Unity answers correctly and the caller sees "did not answer
                // in time". This branch had one and was the ONLY branch that did,
                // which is why status worked in play mode and hung in edit mode.
                !EditorApplication.isPlaying ? "not in play mode — send the start action" :
                (mice > 1 || keebs > 1) ? "LEAKED VIRTUAL DEVICES — clicks may land at (0,0); leave play mode to clear them" :
                "ready";

            // Everything goes in `did`, because that is the field the caller
            // actually SEES. A status action whose answer is only in fields the
            // client does not render is a status action that reports nothing.
            string did =
                $"playMode={(EditorApplication.isPlaying ? "RUNNING" : "stopped")} · " +
                $"scene={scene} · " +
                $"compiling={(EditorApplication.isCompiling ? "yes" : "no")} · " +
                $"busy={(_servingId != null || pending ? "yes" : "no")} · " +
                $"virtual devices: {mice} mouse / {keebs} keyboard · " +
                note;

            // Belt as well as braces: whatever ends up in `did`, it must not be
            // able to break the envelope it is embedded in.
            did = did.Replace("\\", "/").Replace("\"", "'");

            return "{\"ok\":true, \"did\":\"" + did + "\", \"screen\":[0,0], \"shots\":[], " +
                   $"\"playMode\":{(EditorApplication.isPlaying ? "true" : "false")}, " +
                   $"\"compiling\":{(EditorApplication.isCompiling ? "true" : "false")}, " +
                   $"\"busy\":{(_servingId != null || pending ? "true" : "false")}, " +
                   $"\"scene\":\"{scene}\", " +
                   $"\"virtualMice\":{mice}, \"virtualKeyboards\":{keebs}}}";
        }

        /// <summary>Has a stamped wait outlived its deadline? Missing stamp counts
        /// as overdue — a wait nobody can date is a wait nobody can end, which is
        /// precisely the state this exists to break.</summary>
        private static bool Overdue(string key, double seconds)
        {
            float since = SessionState.GetFloat(key, -1f);
            return since < 0f || EditorApplication.timeSinceStartup - since > seconds;
        }

        /// <summary>An installed package lives at a path nobody can guess — under
        /// Library/PackageCache with a hash in it — so the editor prints its own
        /// MCP registration rather than asking anyone to find it.</summary>
        [MenuItem("Tools/Playtest/Print MCP config")]
        private static void PrintMcpConfig()
        {
            var pkg = UnityEditor.PackageManager.PackageInfo.FindForAssembly(
                typeof(PlaytestServer).Assembly);
            string server = pkg == null
                ? "<could not resolve the package path>"
                : Path.Combine(pkg.resolvedPath, "Server~", "server.mjs").Replace('\\', '/');

            Debug.Log("[Playtest] add this to .mcp.json:\n{\n  \"mcpServers\": {\n" +
                      "    \"playtest\": { \"command\": \"node\", \"args\": [\"" + server + "\"] }\n" +
                      "  }\n}\n\nTransport directory: " + Dir);
        }

        private static void Write(string id, string payload)
        {
            // Splice the id in so the caller can prove the answer is theirs.
            string body = payload.TrimStart();
            body = body.StartsWith("{")
                ? "{\"id\":\"" + id + "\", " + body.Substring(1)
                : "{\"id\":\"" + id + "\", \"ok\":false, \"error\":\"" + Escape(payload) + "\"}";
            try { File.WriteAllText(ResultPath, body); } catch { /* next tick */ }
        }

        private static string Field(string json, string key)
        {
            int i = json.IndexOf("\"" + key + "\"", System.StringComparison.Ordinal);
            if (i < 0) return null;
            int q1 = json.IndexOf('"', json.IndexOf(':', i));
            if (q1 < 0) return null;
            int q2 = json.IndexOf('"', q1 + 1);
            return q2 < 0 ? null : json.Substring(q1 + 1, q2 - q1 - 1);
        }

        /// <summary>Extract a nested object by brace matching — the action payload
        /// is arbitrary and cannot be read with a flat string scan.</summary>
        private static string Object(string json, string key)
        {
            int i = json.IndexOf("\"" + key + "\"", System.StringComparison.Ordinal);
            if (i < 0) return null;
            int open = json.IndexOf('{', i);
            if (open < 0) return null;
            int depth = 0;
            for (int k = open; k < json.Length; k++)
            {
                if (json[k] == '{') depth++;
                else if (json[k] == '}' && --depth == 0) return json.Substring(open, k - open + 1);
            }
            return null;
        }

        private static string Escape(string s) =>
            string.IsNullOrEmpty(s) ? "" : s.Replace("\\", "\\\\").Replace("\"", "\\\"").Replace("\n", " ");
    }
}
