using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Text;
using System.Threading;

namespace LivingNPCs
{
    /// <summary>
    /// Talks to a local Ollama server (https://ollama.com) over HTTP. Requests run on a background
    /// thread; results are handed back to the game thread through <see cref="Pump"/>, because Unity
    /// may only be touched from its own thread.
    /// </summary>
    public static class Ollama
    {
        public struct Message
        {
            public string Role;
            public string Content;
            public Message(string role, string content) { Role = role; Content = content; }
        }

        private static readonly ConcurrentQueue<Action> Completed = new ConcurrentQueue<Action>();
        private static Process launched;
        private static int inFlight;

        public static string BaseUrl => Plugin.AiUrl.Value.TrimEnd('/');
        public static bool Busy => Volatile.Read(ref inFlight) > 0;

        /// <summary>Runs finished callbacks on the game thread. Call every frame.</summary>
        public static void Pump()
        {
            while (Completed.TryDequeue(out Action done))
            {
                try { done(); }
                catch (Exception e) { Plugin.Log.LogError("AI callback failed: " + e); }
            }
        }

        private static void Background(Action work)
        {
            ThreadPool.QueueUserWorkItem(_ =>
            {
                try { work(); }
                catch (Exception e) { Plugin.Log.LogWarning("AI request failed: " + e.Message); }
            });
        }

        private static string Http(string method, string path, string body, int timeoutMs)
        {
            var request = (HttpWebRequest)WebRequest.Create(BaseUrl + path);
            request.Method = method;
            request.Timeout = timeoutMs;
            request.ReadWriteTimeout = timeoutMs;
            request.Proxy = null;
            if (body != null)
            {
                byte[] bytes = Encoding.UTF8.GetBytes(body);
                request.ContentType = "application/json";
                request.ContentLength = bytes.Length;
                using (Stream s = request.GetRequestStream()) s.Write(bytes, 0, bytes.Length);
            }
            using (var response = (HttpWebResponse)request.GetResponse())
            using (var reader = new StreamReader(response.GetResponseStream(), Encoding.UTF8))
            {
                return reader.ReadToEnd();
            }
        }

        // ---------------------------------------------------------------- setup

        /// <summary>
        /// Makes sure the server is running and the model is downloaded, launching Ollama and pulling
        /// the model if allowed. Reports progress as chat messages via <paramref name="status"/>.
        /// </summary>
        public static void Prepare(Action<string> status, Action<bool> ready)
        {
            string model = Plugin.AiModel.Value;
            bool autoStart = Plugin.AiAutoStart.Value;
            bool autoPull = Plugin.AiAutoDownload.Value;
            Background(() =>
            {
                bool ok = Ping();
                if (!ok && autoStart && TryLaunch())
                {
                    Post(status, "Living NPCs: starting the AI...");
                    for (int i = 0; i < 20 && !ok; i++)
                    {
                        Thread.Sleep(1000);
                        ok = Ping();
                    }
                }
                if (!ok)
                {
                    Post(status, "Living NPCs: AI chat is off because Ollama isn't running. Install it from ollama.com, then type /ai on.");
                    Post(() => ready(false));
                    return;
                }
                if (!HasModel(model))
                {
                    if (!autoPull)
                    {
                        Post(status, "Living NPCs: the AI model '" + model + "' isn't downloaded. Run: ollama pull " + model);
                        Post(() => ready(false));
                        return;
                    }
                    Post(status, "Living NPCs: downloading the AI model '" + model + "' (a few GB, one time only). Sims will start talking when it's done.");
                    try
                    {
                        Http("POST", "/api/pull", "{\"name\":" + Json.Quote(model) + ",\"stream\":false}", 60 * 60 * 1000);
                    }
                    catch (Exception e)
                    {
                        Post(status, "Living NPCs: couldn't download the AI model: " + e.Message);
                        Post(() => ready(false));
                        return;
                    }
                }
                Post(status, "Living NPCs: AI chat is ready (" + model + "). Talk to sims and NPCs!");
                Post(() => ready(true));
            });
        }

        private static void Post(Action<string> status, string message) => Completed.Enqueue(() => status(message));
        private static void Post(Action action) => Completed.Enqueue(action);

        private static bool Ping()
        {
            try { return Http("GET", "/api/tags", null, 3000) != null; }
            catch { return false; }
        }

        private static bool HasModel(string model)
        {
            try
            {
                string tags = Http("GET", "/api/tags", null, 5000);
                string wanted = model.Contains(":") ? model : model + ":latest";
                return tags.Contains("\"" + wanted + "\"") || tags.Contains("\"" + model + "\"");
            }
            catch { return false; }
        }

        /// <summary>Starts "ollama serve" in the background, if Ollama is installed.</summary>
        private static bool TryLaunch()
        {
            var candidates = new List<string>();
            if (!string.IsNullOrEmpty(Plugin.AiOllamaPath.Value)) candidates.Add(Plugin.AiOllamaPath.Value);
            string local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            candidates.Add(Path.Combine(local, "Programs", "Ollama", "ollama.exe"));
            candidates.Add("ollama");
            foreach (string exe in candidates)
            {
                try
                {
                    if (exe.Contains(Path.DirectorySeparatorChar.ToString()) && !File.Exists(exe)) continue;
                    launched = Process.Start(new ProcessStartInfo(exe, "serve")
                    {
                        CreateNoWindow = true,
                        UseShellExecute = false,
                        WindowStyle = ProcessWindowStyle.Hidden,
                    });
                    if (launched != null) return true;
                }
                catch
                {
                    // Not installed at that path; try the next one.
                }
            }
            return false;
        }

        /// <summary>Stops the Ollama server, but only if this mod started it.</summary>
        public static void StopIfWeStartedIt()
        {
            try
            {
                if (launched != null && !launched.HasExited) launched.Kill();
            }
            catch
            {
                // Already gone.
            }
        }

        // ---------------------------------------------------------------- chat

        /// <summary>Asks the model for one reply. <paramref name="done"/> runs on the game thread with null on failure.</summary>
        public static void Chat(List<Message> messages, int maxTokens, Action<string> done)
        {
            var body = new StringBuilder();
            body.Append("{\"model\":").Append(Json.Quote(Plugin.AiModel.Value));
            body.Append(",\"stream\":false,\"keep_alive\":\"30m\"");
            body.Append(",\"options\":{\"temperature\":0.85,\"num_predict\":").Append(maxTokens).Append("}");
            body.Append(",\"messages\":[");
            for (int i = 0; i < messages.Count; i++)
            {
                if (i > 0) body.Append(',');
                body.Append("{\"role\":").Append(Json.Quote(messages[i].Role))
                    .Append(",\"content\":").Append(Json.Quote(messages[i].Content)).Append('}');
            }
            body.Append("]}");
            string payload = body.ToString();
            int timeout = (int)(Plugin.AiTimeoutSeconds.Value * 1000);

            Interlocked.Increment(ref inFlight);
            ThreadPool.QueueUserWorkItem(_ =>
            {
                string reply = null;
                try
                {
                    string json = Http("POST", "/api/chat", payload, timeout);
                    reply = Json.Field(json, "content");
                }
                catch (Exception e)
                {
                    Plugin.Log.LogWarning("AI chat failed: " + e.Message);
                }
                finally
                {
                    Interlocked.Decrement(ref inFlight);
                }
                Completed.Enqueue(() => done(reply));
            });
        }
    }

    /// <summary>Just enough JSON for Ollama's requests and replies.</summary>
    internal static class Json
    {
        public static string Quote(string s)
        {
            var sb = new StringBuilder("\"");
            foreach (char c in s ?? "")
            {
                switch (c)
                {
                    case '"': sb.Append("\\\""); break;
                    case '\\': sb.Append("\\\\"); break;
                    case '\n': sb.Append("\\n"); break;
                    case '\r': sb.Append("\\r"); break;
                    case '\t': sb.Append("\\t"); break;
                    default:
                        if (c < 0x20) sb.Append("\\u").Append(((int)c).ToString("x4"));
                        else sb.Append(c);
                        break;
                }
            }
            return sb.Append('"').ToString();
        }

        /// <summary>Returns the first string value of the given key, unescaped, or null.</summary>
        public static string Field(string json, string key)
        {
            if (json == null) return null;
            string marker = "\"" + key + "\"";
            int at = json.IndexOf(marker, StringComparison.Ordinal);
            if (at < 0) return null;
            int i = json.IndexOf(':', at + marker.Length);
            if (i < 0) return null;
            i++;
            while (i < json.Length && char.IsWhiteSpace(json[i])) i++;
            if (i >= json.Length || json[i] != '"') return null;
            i++;
            var sb = new StringBuilder();
            while (i < json.Length && json[i] != '"')
            {
                char c = json[i++];
                if (c != '\\' || i >= json.Length)
                {
                    sb.Append(c);
                    continue;
                }
                char e = json[i++];
                switch (e)
                {
                    case 'n': sb.Append('\n'); break;
                    case 't': sb.Append('\t'); break;
                    case 'r': break;
                    case 'u':
                        if (i + 4 <= json.Length && int.TryParse(json.Substring(i, 4), System.Globalization.NumberStyles.HexNumber, null, out int code))
                        {
                            sb.Append((char)code);
                            i += 4;
                        }
                        break;
                    default: sb.Append(e); break;
                }
            }
            return sb.ToString();
        }
    }
}
