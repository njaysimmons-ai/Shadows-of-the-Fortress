using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Text.RegularExpressions;
using HarmonyLib;
using UnityEngine;
using UnityEngine.AI;
using UnityEngine.SceneManagement;

namespace LivingNPCs
{
    /// <summary>
    /// /find Name: a soft trail of mist drifts along the walkable path from you to someone (or somewhere) in
    /// this zone, and keeps following them if they wander. /find alone lists who's here, /find stop clears it.
    /// </summary>
    internal class MistTrail : MonoBehaviour
    {
        private class Wisp
        {
            public GameObject Go;
            public Material Mat;
            public float Along, Speed, Age, Life, Size, Phase;
            public Vector3 Offset;
            public bool Active;
        }

        private static MistTrail instance;
        private static Transform targetTransform;
        private static Vector3 targetPoint;
        private static string targetName;
        private static bool active, followingPerson;
        private static float startedAt;
        private static string startedScene;

        private readonly List<Wisp> wisps = new List<Wisp>();
        private readonly List<Vector3> corners = new List<Vector3>();
        private readonly List<float> lengths = new List<float>();
        private float pathLength;
        private float nextPath, nextSpawn;
        private NavMeshPath navPath;
        private Mesh quad;
        private Texture2D puff;
        private Shader shader;

        private const int MaxWisps = 70;
        private const float TrailReach = 30f;
        private const float Timeout = 6f * 60f;

        private void Awake() { instance = this; }

        // ---------------------------------------------------------------- commands

        public static void Begin(string query)
        {
            if (GameData.PlayerControl == null) return;
            if (!Locate(query, out Transform t, out Vector3 p, out string name))
            {
                UpdateSocialLog.LogAdd("No one called \"" + query + "\" here. /find on its own lists who's in this zone.", "yellow");
                return;
            }
            targetTransform = t;
            followingPerson = t != null;
            targetPoint = t != null ? t.position : p;
            targetName = name;
            active = true;
            startedAt = Time.time;
            startedScene = SceneManager.GetActiveScene().name;
            if (instance != null) instance.nextPath = 0f;
            float d = Vector3.Distance(GameData.PlayerControl.transform.position, targetPoint);
            UpdateSocialLog.LogAdd("A pale mist gathers and drifts toward " + name + " (" + Mathf.RoundToInt(d) + "m). /find stop to dismiss it.", "#9FD8FF");
        }

        public static void Stop(string message = null)
        {
            bool was = active;
            active = false;
            targetTransform = null;
            if (was && message != null) UpdateSocialLog.LogAdd(message, "#9FD8FF");
        }

        public static void List()
        {
            var people = new List<string>();
            var sims = new List<string>();
            Vector3 me = GameData.PlayerControl.transform.position;
            foreach (NPCDialogManager m in UnityEngine.Object.FindObjectsOfType<NPCDialogManager>()
                         .OrderBy(x => Vector3.Distance(x.transform.position, me)))
            {
                NPC npc = m.GetComponent<NPC>();
                if (npc == null || npc.SimPlayer || string.IsNullOrEmpty(npc.NPCName)) continue;
                NpcRole role = WorldContext.RoleOf(npc);
                string label = role == NpcRole.Townsfolk ? npc.NPCName : npc.NPCName + " (" + role.ToString().ToLowerInvariant() + ")";
                if (!people.Contains(label)) people.Add(label);
                if (people.Count >= 25) break;
            }
            foreach (SimPlayer s in UnityEngine.Object.FindObjectsOfType<SimPlayer>()
                         .OrderBy(x => Vector3.Distance(x.transform.position, me)))
            {
                if (s == null || s.gameObject == GameData.PlayerControl.gameObject) continue;
                if (!sims.Contains(s.name)) sims.Add(s.name);
                if (sims.Count >= 15) break;
            }
            if (people.Count == 0 && sims.Count == 0)
            {
                UpdateSocialLog.LogAdd("There's no one here to find.", "yellow");
                return;
            }
            if (people.Count > 0) UpdateSocialLog.LogAdd("NPCs here (nearest first): " + string.Join(", ", people), "#9FD8FF");
            if (sims.Count > 0) UpdateSocialLog.LogAdd("Players here: " + string.Join(", ", sims), "#9FD8FF");
            UpdateSocialLog.LogAdd("/find Name, or a role like /find vendor, /find banker, /find exit.", "yellow");
        }

        private static string Simplify(string s) => Regex.Replace((s ?? "").ToLowerInvariant(), "[^a-z0-9 ]", "").Trim();

        /// <summary>A person by name (NPCs and sims, followed as they move), else a role, spot or exit.</summary>
        private static bool Locate(string query, out Transform t, out Vector3 point, out string name)
        {
            t = null;
            point = Vector3.zero;
            name = query;
            string wanted = Simplify(Regex.Replace(query ?? "", @"^\s*(the|a|an)\s+", "", RegexOptions.IgnoreCase));
            if (wanted.Length < 2) return false;
            Vector3 me = GameData.PlayerControl.transform.position;

            // Exact names first, then partial ones, nearest wins.
            foreach (bool exact in new[] { true, false })
            {
                float best = float.MaxValue;
                foreach (NPC npc in UnityEngine.Object.FindObjectsOfType<NPC>())
                {
                    if (npc == null || npc.gameObject == GameData.PlayerControl.gameObject) continue;
                    string label = npc.SimPlayer ? npc.name : npc.NPCName;
                    if (string.IsNullOrEmpty(label)) continue;
                    Character c = npc.GetComponent<Character>();
                    if (c != null && !c.Alive) continue;
                    string n = Simplify(label);
                    bool hit = exact ? n == wanted : (n.Contains(wanted) || (wanted.Length >= 4 && n.Split(' ').Contains(wanted)));
                    if (!hit) continue;
                    float d = Vector3.Distance(npc.transform.position, me);
                    if (d < best) { best = d; t = npc.transform; name = label; }
                }
                if (t != null) return true;
            }

            if (wanted.Length >= 3 && SimEscort.FindPlace(query, out Vector3 where, out string placeName))
            {
                point = where;
                name = placeName;
                return true;
            }
            return false;
        }

        // ---------------------------------------------------------------- the mist

        private void Update()
        {
            try
            {
                Tick();
            }
            catch (Exception e)
            {
                Plugin.Log.LogError("Mist trail failed: " + e);
                Stop();
            }
        }

        private void Tick()
        {
            float dt = Time.deltaTime;
            if (active)
            {
                PlayerControl pc = GameData.PlayerControl;
                if (pc == null || SceneManager.GetActiveScene().name != startedScene) Stop("The mist fades as you leave the area.");
                else if (Time.time - startedAt > Timeout) Stop("The mist thins and fades away.");
            }
            if (active && followingPerson)
            {
                if (targetTransform == null) Stop("The mist loses " + targetName + "'s trail.");
                else
                {
                    Character c = targetTransform.GetComponent<Character>();
                    if (!targetTransform.gameObject.activeInHierarchy || (c != null && !c.Alive))
                    {
                        Stop("The mist loses " + targetName + "'s trail.");
                    }
                    else targetPoint = targetTransform.position;
                }
            }
            if (active)
            {
                Vector3 me = GameData.PlayerControl.transform.position;
                if (Vector3.Distance(me, targetPoint) < 4f)
                {
                    Stop("The mist settles around " + targetName + ". You've found them.");
                }
                else
                {
                    if (Time.time >= nextPath) { BuildPath(me); nextPath = Time.time + 0.75f; }
                    if (Time.time >= nextSpawn && pathLength > 0.5f)
                    {
                        Spawn();
                        nextSpawn = Time.time + 0.09f;
                    }
                }
            }
            Animate(dt);
        }

        private void BuildPath(Vector3 me)
        {
            corners.Clear();
            lengths.Clear();
            pathLength = 0f;
            if (navPath == null) navPath = new NavMeshPath();
            Vector3 from = SimEscort.Reachable(me);
            Vector3 to = SimEscort.Reachable(targetPoint);
            bool ok = NavMesh.CalculatePath(from, to, NavMesh.AllAreas, navPath) && navPath.status != NavMeshPathStatus.PathInvalid && navPath.corners.Length >= 2;
            if (ok) corners.AddRange(navPath.corners);
            else { corners.Add(me); corners.Add(targetPoint); }
            corners[0] = me;
            lengths.Add(0f);
            for (int i = 1; i < corners.Count; i++)
            {
                pathLength += Vector3.Distance(corners[i - 1], corners[i]);
                lengths.Add(pathLength);
            }
        }

        private Vector3 PointAt(float along)
        {
            if (corners.Count == 0) return Vector3.zero;
            if (along <= 0f) return corners[0];
            for (int i = 1; i < corners.Count; i++)
            {
                if (along <= lengths[i])
                {
                    float seg = lengths[i] - lengths[i - 1];
                    return Vector3.Lerp(corners[i - 1], corners[i], seg <= 0.001f ? 1f : (along - lengths[i - 1]) / seg);
                }
            }
            return corners[corners.Count - 1];
        }

        private void Spawn()
        {
            Wisp w = wisps.FirstOrDefault(x => !x.Active);
            if (w == null)
            {
                if (wisps.Count >= MaxWisps) return;
                w = MakeWisp();
                if (w == null) return;
                wisps.Add(w);
            }
            w.Active = true;
            w.Along = UnityEngine.Random.Range(1.5f, 3f);
            w.Speed = UnityEngine.Random.Range(3.2f, 4.8f);
            w.Age = 0f;
            w.Life = Mathf.Min(TrailReach, pathLength) / w.Speed + UnityEngine.Random.Range(0.2f, 0.8f);
            w.Size = UnityEngine.Random.Range(0.45f, 0.9f);
            w.Phase = UnityEngine.Random.Range(0f, 6.28f);
            w.Offset = new Vector3(UnityEngine.Random.Range(-0.35f, 0.35f), UnityEngine.Random.Range(0.5f, 1.3f), UnityEngine.Random.Range(-0.35f, 0.35f));
            w.Go.SetActive(true);
        }

        private void Animate(float dt)
        {
            Camera cam = Camera.main;
            foreach (Wisp w in wisps)
            {
                if (!w.Active) continue;
                w.Age += dt;
                w.Along += w.Speed * dt;
                bool done = w.Age >= w.Life || w.Along >= pathLength || corners.Count == 0;
                if (done || (!active && w.Age > w.Life * 0.5f))
                {
                    w.Active = false;
                    w.Go.SetActive(false);
                    continue;
                }
                float t = w.Age / w.Life;
                float fade = Mathf.Clamp01(t / 0.15f) * Mathf.Clamp01((1f - t) / 0.35f);
                if (!active) fade *= 0.5f;
                Vector3 sway = new Vector3(Mathf.Sin(Time.time * 1.7f + w.Phase), Mathf.Sin(Time.time * 1.1f + w.Phase * 2f) * 0.4f,
                    Mathf.Cos(Time.time * 1.3f + w.Phase)) * 0.18f;
                w.Go.transform.position = PointAt(w.Along) + w.Offset + sway;
                if (cam != null) w.Go.transform.rotation = cam.transform.rotation;
                float size = w.Size * (0.8f + 0.5f * t);
                w.Go.transform.localScale = new Vector3(size, size, size);
                w.Mat.color = new Color(0.72f, 0.86f, 1f, 0.42f * fade);
            }
        }

        private Wisp MakeWisp()
        {
            if (shader == null)
            {
                foreach (string s in new[] { "Sprites/Default", "Legacy Shaders/Particles/Alpha Blended", "Particles/Standard Unlit", "UI/Default" })
                {
                    shader = Shader.Find(s);
                    if (shader != null) break;
                }
                if (shader == null) return null;
            }
            if (quad == null) quad = BuildQuad();
            if (puff == null) puff = BuildPuff();
            var go = new GameObject("LivingNPCs.Mist") { hideFlags = HideFlags.HideAndDontSave };
            go.transform.SetParent(transform, false);
            go.AddComponent<MeshFilter>().sharedMesh = quad;
            MeshRenderer r = go.AddComponent<MeshRenderer>();
            var mat = new Material(shader) { mainTexture = puff, renderQueue = 3100 };
            r.sharedMaterial = mat;
            r.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            r.receiveShadows = false;
            go.SetActive(false);
            return new Wisp { Go = go, Mat = mat };
        }

        private static Mesh BuildQuad()
        {
            var m = new Mesh { name = "LivingNPCs.MistQuad" };
            m.vertices = new[] { new Vector3(-0.5f, -0.5f, 0), new Vector3(0.5f, -0.5f, 0), new Vector3(0.5f, 0.5f, 0), new Vector3(-0.5f, 0.5f, 0) };
            m.uv = new[] { new Vector2(0, 0), new Vector2(1, 0), new Vector2(1, 1), new Vector2(0, 1) };
            m.colors = new[] { Color.white, Color.white, Color.white, Color.white };
            // Both windings, so the puff shows whichever way the shader culls.
            m.triangles = new[] { 0, 2, 1, 0, 3, 2, 0, 1, 2, 0, 2, 3 };
            m.RecalculateBounds();
            return m;
        }

        private static Texture2D BuildPuff()
        {
            const int size = 64;
            var tex = new Texture2D(size, size, TextureFormat.RGBA32, false) { wrapMode = TextureWrapMode.Clamp, name = "LivingNPCs.MistPuff" };
            var px = new Color[size * size];
            for (int y = 0; y < size; y++)
            {
                for (int x = 0; x < size; x++)
                {
                    float dx = (x + 0.5f) / size * 2f - 1f, dy = (y + 0.5f) / size * 2f - 1f;
                    float d = Mathf.Sqrt(dx * dx + dy * dy);
                    float a = Mathf.Clamp01(1f - d);
                    a = a * a * (3f - 2f * a);
                    // A little lumpiness so it reads as mist, not a dot.
                    a *= 0.75f + 0.25f * Mathf.PerlinNoise(x * 0.15f, y * 0.15f);
                    px[y * size + x] = new Color(1f, 1f, 1f, a);
                }
            }
            tex.SetPixels(px);
            tex.Apply();
            return tex;
        }
    }

    [HarmonyPatch(typeof(TypeText), "CheckCommands")]
    internal static class FindCommand
    {
        private static readonly FieldInfo TypedField = AccessTools.Field(typeof(TypeText), "typed");

        private static bool Prefix(TypeText __instance)
        {
            object typed = TypedField?.GetValue(__instance);
            string text = (typed?.GetType().GetProperty("text")?.GetValue(typed) as string ?? "").Trim();
            if (text != "/find" && !text.StartsWith("/find ")) return true;
            try
            {
                string what = text.Length > 5 ? text.Substring(6).Trim() : "";
                if (what.Length == 0)
                {
                    Character target = GameData.PlayerControl.CurrentTarget;
                    if (target != null && target != GameData.PlayerControl.Myself) MistTrail.Begin(target.name);
                    else MistTrail.List();
                }
                else if (what == "stop" || what == "off" || what == "clear") MistTrail.Stop("The mist disperses.");
                else MistTrail.Begin(what);
            }
            catch (Exception e)
            {
                Plugin.Log.LogError("/find failed: " + e);
            }
            return false;
        }
    }
}
