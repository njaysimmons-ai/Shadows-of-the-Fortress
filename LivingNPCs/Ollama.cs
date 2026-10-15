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

        /// <summary>Why the last request or setup failed, in plain words, for /ai.</summary>
        public static volatile string LastError = "";

        /// <summary>What setup is doing right now, for /ai: "downloading qwen2.5:14b: 47%" and so on.</summary>
        public static volatile string Phase = "not started yet";
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
                Phase = "looking for Ollama at " + BaseUrl;
                bool ok = Ping();
                if (!ok && autoStart && TryLaunch())
                {
                    Phase = "starting Ollama";
                    Post(status, "Living NPCs: starting the AI...");
                    for (int i = 0; i < 20 && !ok; i++)
                    {
                        Thread.Sleep(1000);
                        ok = Ping();
                    }
                }
                if (!ok)
                {
                    LastError = "Ollama isn't running at " + BaseUrl + " (is it installed? see ollama.com)";
                    Phase = "waiting for Ollama (retrying every minute)";
                    Post(status, "Living NPCs: AI chat can't reach Ollama. It will keep retrying; type /ai for help.");
                    Post(() => ready(false));
                    return;
                }
                if (!HasModel(model))
                {
                    if (!autoPull)
                    {
                        LastError = "model '" + model + "' isn't downloaded";
                        Post(status, "Living NPCs: the AI model '" + model + "' isn't downloaded. Run: ollama pull " + model);
                        Post(() => ready(false));
                        return;
                    }
                    Phase = "downloading " + model;
                    Post(status, "Living NPCs: downloading the AI model '" + model + "' (one time only, it's several GB). Type /ai to see progress.");
                    try
                    {
                        Pull(model, status);
                    }
                    catch (Exception e)
                    {
                        LastError = "couldn't download model '" + model + "': " + e.Message;
                        Phase = "download failed (will retry in a minute)";
                        Post(status, "Living NPCs: couldn't download the AI model: " + e.Message);
                        Post(() => ready(false));
                        return;
                    }
                }
                // Load the model into video memory now, so the first real reply isn't slow.
                Phase = "loading " + model + " into video memory";
                try
                {
                    Http("POST", "/api/generate", "{\"model\":" + Json.Quote(model) + ",\"prompt\":\"hi\",\"stream\":false,\"keep_alive\":\"30m\",\"options\":{\"num_predict\":1}}", 180 * 1000);
                }
                catch (Exception e)
                {
                    LastError = "the model failed to load: " + e.Message;
                    Phase = "model failed to load (will retry in a minute)";
                    Post(status, "Living NPCs: the AI model '" + model + "' failed to load: " + e.Message);
                    Post(() => ready(false));
                    return;
                }
                LastError = "";
                Phase = "ready";
                Post(status, "Living NPCs: AI chat is ready (" + model + "). Talk to sims and NPCs!");
                Post(() => ready(true));
            });
        }

        /// <summary>Downloads a model, streaming progress into <see cref="Phase"/> and a chat line every 25%.</summary>
        private static void Pull(string model, Action<string> status)
        {
            var request = (HttpWebRequest)WebRequest.Create(BaseUrl + "/api/pull");
            request.Method = "POST";
            request.Proxy = null;
            request.Timeout = 60 * 1000;
            request.ReadWriteTimeout = 10 * 60 * 1000;
            byte[] bytes = Encoding.UTF8.GetBytes("{\"name\":" + Json.Quote(model) + ",\"stream\":true}");
            request.ContentType = "application/json";
            request.ContentLength = bytes.Length;
            using (Stream s = request.GetRequestStream()) s.Write(bytes, 0, bytes.Length);
            int lastQuarter = 0;
            using (var response = (HttpWebResponse)request.GetResponse())
            using (var reader = new StreamReader(response.GetResponseStream(), Encoding.UTF8))
            {
                string line;
                while ((line = reader.ReadLine()) != null)
                {
                    string error = Json.Field(line, "error");
                    if (error != null) throw new Exception(error);
                    double total = Json.Number(line, "total"), done = Json.Number(line, "completed");
                    if (total > 0)
                    {
                        int percent = (int)(done * 100 / total);
                        Phase = "downloading " + model + ": " + percent + "% of " + (total / 1e9).ToString("0.0") + " GB";
                        int quarter = percent / 25;
                        if (quarter > lastQuarter && quarter < 4)
                        {
                            lastQuarter = quarter;
                            Post(status, "Living NPCs: downloading the AI model: " + percent + "%");
                        }
                    }
                    else
                    {
                        string state = Json.Field(line, "status");
                        if (!string.IsNullOrEmpty(state)) Phase = model + ": " + state;
                    }
                }
            }
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

        private static string Truncate(string s, int max) => s == null ? "" : s.Length <= max ? s : s.Substring(0, max) + "...";

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
            int timeout = (int)(Math.Max(10f, Plugin.AiTimeoutSeconds.Value) * 1000);

            Interlocked.Increment(ref inFlight);
            ThreadPool.QueueUserWorkItem(_ =>
            {
                string reply = null;
                try
                {
                    string json = Http("POST", "/api/chat", payload, timeout);
                    reply = Json.Field(json, "content");
                    if (reply == null) LastError = "Ollama answered with something unexpected: " + Truncate(Json.Field(json, "error") ?? json, 150);
                }
                catch (WebException e) when (e.Response is HttpWebResponse r)
                {
                    string detail = "";
                    try { using (var sr = new StreamReader(r.GetResponseStream())) detail = Json.Field(sr.ReadToEnd(), "error") ?? ""; } catch { }
                    LastError = "Ollama returned an error: " + (detail.Length > 0 ? detail : e.Message);
                    Plugin.Log.LogWarning("AI chat failed: " + LastError);
                }
                catch (Exception e)
                {
                    LastError = e is WebException we && we.Status == WebExceptionStatus.Timeout
                        ? "no reply within " + (timeout / 1000) + " seconds (raise ReplyTimeoutSeconds, or use a smaller model)"
                        : e.Message;
                    Plugin.Log.LogWarning("AI chat failed: " + LastError);
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

        /// <summary>Returns the first numeric value of the given key, or 0.</summary>
        public static double Number(string json, string key)
        {
            if (json == null) return 0;
            string marker = "\"" + key + "\"";
            int at = json.IndexOf(marker, StringComparison.Ordinal);
            if (at < 0) return 0;
            int i = json.IndexOf(':', at + marker.Length) + 1;
            if (i <= 0) return 0;
            int start = i;
            while (i < json.Length && (char.IsDigit(json[i]) || json[i] == '.' || json[i] == '-' || json[i] == ' ')) i++;
            return double.TryParse(json.Substring(start, i - start).Trim(), System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.InvariantCulture, out double v) ? v : 0;
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
