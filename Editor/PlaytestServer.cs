using System.IO;
using UnityEditor;
using UnityEngine;

namespace Elegist.Playtest.EditorTools
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

        private static string LastSeen
        {
            get => SessionState.GetString(SeenKey, "");
            set => SessionState.SetString(SeenKey, value);
        }

        static PlaytestServer()
        {
            Directory.CreateDirectory(Dir);
            EditorApplication.update += Tick;
        }

        private static void Tick()
        {
            // Finishing a "start" that spanned the domain reload: the game is up,
            // so take the first look and answer with it. Begin() refusing just
            // means the bridge has not bootstrapped yet — try again next tick.
            string resuming = SessionState.GetString(PendingKey, "");
            if (resuming != "")
            {
                if (!EditorApplication.isPlaying || EditorApplication.isCompiling) return;
                if (PlaytestAction.Begin("{\"action\":\"look\"}") != "running") return;
                SessionState.EraseString(PendingKey);
                _servingId = resuming;
                return;
            }

            // A result is pending: publish it the moment the action finishes.
            if (_servingId != null)
            {
                if (PlaytestAction.Running) return;
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

            // Two tools share one transport. `inspect` answers immediately — it is
            // a camera move and a render, not a gesture that plays out over frames —
            // so it never becomes a pending job.
            if (Field(action, "tool") == "inspect")
            {
                Write(id, SceneInspector.Run(action));
                return;
            }

            switch (Field(action, "action"))
            {
                case "start":
                    if (!EditorApplication.isPlaying)
                    {
                        // Answer AFTER the reload, not now — "the game is starting"
                        // is not a useful reply to an agent that wants to see it.
                        SessionState.SetString(PendingKey, id);
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

            string started = PlaytestAction.Begin(action);
            if (started != "running") { Write(id, started); return; }
            _servingId = id;
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
