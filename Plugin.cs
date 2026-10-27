using System;
using System.Collections.Generic;
using System.Reflection;
using BepInEx;
using BepInEx.Configuration;
using UnityEngine;

namespace CupMine
{
    // CupMine: a blocky-world makeover for Cuphead.
    //  - Minecraft-style hotbar and hearts on screen
    //  - Your character is drawn as a blocky pixel hero
    //  - Blocky green "creepers" wander in and go poof (cosmetic only, they never hurt you)
    // All art is generated in code, so there are no extra files to install.
    [BepInPlugin("local.cupmine", "CupMine", "0.1.0")]
    public class Plugin : BaseUnityPlugin
    {
        ConfigEntry<bool> cfgHotbar, cfgSkin, cfgCreepers;

        Texture2D slotTex, selTex, heartTex;
        Texture2D[] icons;
        Sprite playerSprite, creeperSprite, puffSprite;

        class Skinned
        {
            public Transform root;
            public GameObject skin;
            public List<Renderer> hidden = new List<Renderer>();
        }

        class Creeper
        {
            public GameObject go;
            public SpriteRenderer sr;
            public float dir;
            public float fuse = -1f;
        }

        class Puff
        {
            public GameObject go;
            public SpriteRenderer sr;
            public Vector3 vel;
            public float life;
        }

        readonly List<Skinned> skinned = new List<Skinned>();
        readonly List<Creeper> creepers = new List<Creeper>();
        readonly List<Puff> puffs = new List<Puff>();
        readonly Dictionary<string, Type> typeCache = new Dictionary<string, Type>();

        static readonly string[] PlayerTypeNames = { "LevelPlayerController", "MapPlayerController" };

        float scanTimer;
        float spawnTimer = 4f;
        bool inLevel;
        int hp = -1;
        int selected;
        float groundY = float.MaxValue;
        float skinScale = 1f;
        int skinLayerId;
        int skinOrder;

        void Awake()
        {
            cfgHotbar = Config.Bind("General", "Hotbar", true, "Show the Minecraft-style hotbar and hearts.");
            cfgSkin = Config.Bind("General", "BlockySkin", true, "Draw the player as a blocky character.");
            cfgCreepers = Config.Bind("General", "Creepers", true, "Let cosmetic creepers wander in and poof.");

            BuildArt();
            UnityEngine.SceneManagement.SceneManager.sceneLoaded += OnSceneLoaded;
            Logger.LogInfo("CupMine loaded.");
        }

        void OnDestroy()
        {
            UnityEngine.SceneManagement.SceneManager.sceneLoaded -= OnSceneLoaded;
        }

        void OnSceneLoaded(UnityEngine.SceneManagement.Scene scene, UnityEngine.SceneManagement.LoadSceneMode mode)
        {
            groundY = float.MaxValue;
            skinned.Clear();
            foreach (var c in creepers) { if (c.go != null) Destroy(c.go); }
            creepers.Clear();
            foreach (var p in puffs) { if (p.go != null) Destroy(p.go); }
            puffs.Clear();
            spawnTimer = 4f;
            scanTimer = 1f;
        }

        // ------------------------------------------------------------------ art

        class Pix
        {
            public int w, h;
            public Color32[] d;

            public Pix(int w, int h)
            {
                this.w = w;
                this.h = h;
                d = new Color32[w * h];
            }

            public void Rect(int x, int y, int rw, int rh, Color32 c, int noise)
            {
                for (int yy = y; yy < y + rh; yy++)
                {
                    for (int xx = x; xx < x + rw; xx++)
                    {
                        if (xx < 0 || yy < 0 || xx >= w || yy >= h) continue;
                        int n = noise > 0 ? (Hash(xx, yy) % (noise * 2 + 1)) - noise : 0;
                        d[yy * w + xx] = new Color32(Clamp(c.r + n), Clamp(c.g + n), Clamp(c.b + n), c.a);
                    }
                }
            }

            public Texture2D ToTexture()
            {
                var t = new Texture2D(w, h, TextureFormat.RGBA32, false);
                t.filterMode = FilterMode.Point;
                t.wrapMode = TextureWrapMode.Clamp;
                t.SetPixels32(d);
                t.Apply();
                return t;
            }
        }

        static int Hash(int x, int y)
        {
            unchecked
            {
                int n = x * 374761393 + y * 668265263;
                n = (n ^ (n >> 13)) * 1274126177;
                return (n ^ (n >> 16)) & 0x7fffffff;
            }
        }

        static byte Clamp(int v)
        {
            if (v < 0) return 0;
            if (v > 255) return 255;
            return (byte)v;
        }

        static Color32 C(int r, int g, int b)
        {
            return new Color32((byte)r, (byte)g, (byte)b, 255);
        }

        void BuildArt()
        {
            // Player: 16 x 32
            var p = new Pix(16, 32);
            p.Rect(4, 0, 4, 12, C(70, 60, 150), 6);   // left leg
            p.Rect(8, 0, 4, 12, C(70, 60, 150), 6);   // right leg
            p.Rect(4, 0, 8, 2, C(60, 60, 60), 4);     // shoes
            p.Rect(4, 12, 8, 12, C(40, 160, 170), 6); // body
            p.Rect(0, 18, 4, 6, C(40, 160, 170), 6);  // left sleeve
            p.Rect(12, 18, 4, 6, C(40, 160, 170), 6); // right sleeve
            p.Rect(0, 12, 4, 6, C(190, 140, 110), 6); // left hand
            p.Rect(12, 12, 4, 6, C(190, 140, 110), 6);// right hand
            p.Rect(4, 24, 8, 8, C(190, 140, 110), 6); // head
            p.Rect(4, 30, 8, 2, C(70, 45, 30), 4);    // hair top
            p.Rect(4, 28, 1, 2, C(70, 45, 30), 4);    // hair side
            p.Rect(11, 28, 1, 2, C(70, 45, 30), 4);
            p.Rect(5, 27, 2, 1, C(240, 240, 240), 0); // eyes (white)
            p.Rect(9, 27, 2, 1, C(240, 240, 240), 0);
            p.Rect(6, 27, 1, 1, C(60, 60, 170), 0);   // pupils
            p.Rect(9, 27, 1, 1, C(60, 60, 170), 0);
            p.Rect(6, 25, 4, 1, C(120, 70, 60), 0);   // mouth
            playerSprite = Sprite.Create(p.ToTexture(), new Rect(0, 0, 16, 32), new Vector2(0.5f, 0f), 16f);

            // Creeper: 16 x 28
            var c = new Pix(16, 28);
            c.Rect(4, 0, 8, 6, C(70, 170, 70), 18);
            c.Rect(4, 6, 8, 14, C(70, 170, 70), 18);
            c.Rect(4, 20, 8, 8, C(80, 180, 80), 18);
            c.Rect(5, 24, 2, 2, C(20, 40, 20), 0);   // eyes
            c.Rect(9, 24, 2, 2, C(20, 40, 20), 0);
            c.Rect(7, 22, 2, 2, C(20, 40, 20), 0);   // nose
            c.Rect(6, 20, 1, 2, C(20, 40, 20), 0);   // mouth
            c.Rect(9, 20, 1, 2, C(20, 40, 20), 0);
            for (int y = 0; y < 6; y++) c.Rect(7, y, 2, 1, new Color32(0, 0, 0, 0), 0); // gap between feet
            creeperSprite = Sprite.Create(c.ToTexture(), new Rect(0, 0, 16, 28), new Vector2(0.5f, 0f), 16f);

            // Poof particle: 4 x 4
            var f = new Pix(4, 4);
            f.Rect(0, 0, 4, 4, C(230, 230, 230), 20);
            puffSprite = Sprite.Create(f.ToTexture(), new Rect(0, 0, 4, 4), new Vector2(0.5f, 0.5f), 16f);

            // Hotbar icons: 16 x 16
            icons = new Texture2D[5];

            var stone = new Pix(16, 16);
            stone.Rect(0, 0, 16, 16, C(125, 125, 125), 22);
            icons[0] = stone.ToTexture();

            var grass = new Pix(16, 16);
            grass.Rect(0, 0, 16, 16, C(125, 85, 55), 16);
            grass.Rect(0, 11, 16, 5, C(80, 170, 60), 16);
            icons[1] = grass.ToTexture();

            var egg = new Pix(16, 16);
            for (int y = 0; y < 16; y++)
            {
                for (int x = 0; x < 16; x++)
                {
                    float dx = (x - 7.5f) / 5.5f;
                    float dy = (y - 7.5f) / 7f;
                    if (dx * dx + dy * dy <= 1f) egg.Rect(x, y, 1, 1, C(225, 225, 215), 8);
                }
            }
            egg.Rect(5, 8, 2, 2, C(60, 160, 70), 0);
            egg.Rect(9, 5, 2, 2, C(60, 160, 70), 0);
            icons[2] = egg.ToTexture();

            var face = new Pix(16, 16);
            face.Rect(0, 0, 16, 16, C(75, 175, 75), 18);
            face.Rect(3, 9, 3, 3, C(20, 40, 20), 0);
            face.Rect(10, 9, 3, 3, C(20, 40, 20), 0);
            face.Rect(6, 5, 4, 4, C(20, 40, 20), 0);
            face.Rect(4, 2, 2, 4, C(20, 40, 20), 0);
            face.Rect(10, 2, 2, 4, C(20, 40, 20), 0);
            icons[3] = face.ToTexture();

            var gem = new Pix(16, 16);
            for (int y = 0; y < 16; y++)
            {
                for (int x = 0; x < 16; x++)
                {
                    float d = Mathf.Abs(x - 7.5f) + Mathf.Abs(y - 7.5f);
                    if (d <= 7f) gem.Rect(x, y, 1, 1, C(70, 220, 220), 25);
                }
            }
            icons[4] = gem.ToTexture();

            var s = new Pix(1, 1);
            s.Rect(0, 0, 1, 1, new Color32(30, 30, 30, 200), 0);
            slotTex = s.ToTexture();

            var w = new Pix(1, 1);
            w.Rect(0, 0, 1, 1, new Color32(255, 255, 255, 255), 0);
            selTex = w.ToTexture();

            var h = new Pix(9, 9);
            string[] heart =
            {
                "..11.11..",
                ".1111111.",
                "111111111",
                "111111111",
                ".1111111.",
                "..11111..",
                "...111...",
                "....1....",
                "........."
            };
            for (int row = 0; row < 9; row++)
            {
                for (int col = 0; col < 9; col++)
                {
                    if (heart[row][col] == '1') h.Rect(col, 8 - row, 1, 1, C(220, 40, 50), 10);
                }
            }
            heartTex = h.ToTexture();
        }

        // ------------------------------------------------------------------ helpers

        Type FindType(string name)
        {
            Type t;
            if (typeCache.TryGetValue(name, out t)) return t;
            foreach (var a in AppDomain.CurrentDomain.GetAssemblies())
            {
                if (!a.GetName().Name.StartsWith("Assembly-CSharp")) continue;
                try { t = a.GetType(name, false); } catch { t = null; }
                if (t != null)
                {
                    typeCache[name] = t;
                    return t;
                }
            }
            return null;
        }

        int ReadHealth()
        {
            try
            {
                Type t = FindType("PlayerStatsManager");
                if (t == null) return -1;
                UnityEngine.Object o = UnityEngine.Object.FindObjectOfType(t);
                if (o == null) return -1;
                BindingFlags f = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
                object v = null;
                PropertyInfo pi = t.GetProperty("Health", f);
                if (pi != null) v = pi.GetValue(o, null);
                if (v == null)
                {
                    FieldInfo fi = t.GetField("Health", f);
                    if (fi != null) v = fi.GetValue(o);
                }
                return v == null ? -1 : Convert.ToInt32(v);
            }
            catch
            {
                return -1;
            }
        }

        // ------------------------------------------------------------------ per-frame

        void Update()
        {
            scanTimer += Time.unscaledDeltaTime;
            if (scanTimer > 0.4f)
            {
                scanTimer = 0f;
                try { Scan(); } catch (Exception e) { Logger.LogWarning("Scan failed: " + e.Message); }
            }

            if (cfgHotbar.Value && icons != null)
            {
                float scroll = Input.mouseScrollDelta.y;
                if (scroll > 0.1f) selected = (selected + icons.Length - 1) % icons.Length;
                else if (scroll < -0.1f) selected = (selected + 1) % icons.Length;
                for (int i = 0; i < icons.Length; i++)
                {
                    if (Input.GetKeyDown(KeyCode.Alpha1 + i)) selected = i;
                }
            }

            TickCreepers();
            TickPuffs();
        }

        void LateUpdate()
        {
            // The game keeps switching its own sprites back on, so keep them hidden.
            for (int i = 0; i < skinned.Count; i++)
            {
                var s = skinned[i];
                if (s.root == null) continue;
                for (int j = 0; j < s.hidden.Count; j++)
                {
                    if (s.hidden[j] != null) s.hidden[j].enabled = false;
                }
            }
        }

        void Scan()
        {
            var found = new List<Component>();
            foreach (var n in PlayerTypeNames)
            {
                Type t = FindType(n);
                if (t == null) continue;
                foreach (var o in UnityEngine.Object.FindObjectsOfType(t))
                {
                    var comp = o as Component;
                    if (comp != null) found.Add(comp);
                }
            }

            inLevel = found.Count > 0;
            hp = inLevel ? ReadHealth() : -1;

            for (int i = skinned.Count - 1; i >= 0; i--)
            {
                if (skinned[i].root == null)
                {
                    if (skinned[i].skin != null) Destroy(skinned[i].skin);
                    skinned.RemoveAt(i);
                }
            }

            if (!cfgSkin.Value) return;

            foreach (var comp in found)
            {
                Skinned existing = null;
                foreach (var s in skinned)
                {
                    if (s.root == comp.transform) { existing = s; break; }
                }

                if (existing == null) ApplySkin(comp.transform);
                else RefreshHidden(existing);
            }
        }

        void RefreshHidden(Skinned s)
        {
            s.hidden.Clear();
            foreach (var r in s.root.GetComponentsInChildren<SpriteRenderer>(true))
            {
                if (r.gameObject != s.skin) s.hidden.Add(r);
            }
        }

        void ApplySkin(Transform root)
        {
            SpriteRenderer[] rs = root.GetComponentsInChildren<SpriteRenderer>(false);
            Bounds b = new Bounds();
            bool any = false;
            int order = 0;
            int layer = 0;
            foreach (var r in rs)
            {
                if (!r.enabled || r.sprite == null) continue;
                if (!any)
                {
                    b = r.bounds;
                    layer = r.sortingLayerID;
                    any = true;
                }
                else
                {
                    b.Encapsulate(r.bounds);
                }
                if (r.sortingOrder > order) order = r.sortingOrder;
            }
            if (!any) return; // sprites not ready yet, try again next scan

            float height = Mathf.Clamp(b.size.y, 0.4f, 6f);
            float scale = height / 2f; // the blocky sprite is 2 world units tall at scale 1

            var go = new GameObject("CupMineSkin");
            var sr = go.AddComponent<SpriteRenderer>();
            sr.sprite = playerSprite;
            sr.sortingLayerID = layer;
            sr.sortingOrder = order + 1;

            go.transform.position = new Vector3(b.center.x, b.min.y, root.position.z);
            go.transform.SetParent(root, true);
            Vector3 ls = root.lossyScale;
            go.transform.localScale = new Vector3(
                scale / Mathf.Max(Mathf.Abs(ls.x), 0.0001f),
                scale / Mathf.Max(Mathf.Abs(ls.y), 0.0001f),
                1f);

            var s = new Skinned { root = root, skin = go };
            RefreshHidden(s);
            skinned.Add(s);

            skinScale = scale;
            skinLayerId = layer;
            skinOrder = order + 1;
            if (b.min.y < groundY) groundY = b.min.y;
        }

        // ------------------------------------------------------------------ creepers

        void TickCreepers()
        {
            if (!cfgCreepers.Value || !inLevel || skinned.Count == 0 || groundY == float.MaxValue) return;
            Camera cam = Camera.main;
            if (cam == null || !cam.orthographic) return;

            float dt = Time.deltaTime;
            spawnTimer -= dt;
            if (spawnTimer <= 0f && creepers.Count < 3)
            {
                spawnTimer = UnityEngine.Random.Range(5f, 10f);
                SpawnCreeper(cam);
            }

            GameObject target = null;
            foreach (var s in skinned)
            {
                if (s.skin != null) { target = s.skin; break; }
            }
            if (target == null) return;

            for (int i = creepers.Count - 1; i >= 0; i--)
            {
                var c = creepers[i];
                if (c.go == null) { creepers.RemoveAt(i); continue; }

                Vector3 pos = c.go.transform.position;
                float dx = target.transform.position.x - pos.x;
                float near = 1.4f * skinScale;

                if (c.fuse < 0f)
                {
                    c.dir = dx >= 0f ? 1f : -1f;
                    pos.x += c.dir * 1.1f * skinScale * dt;
                    c.go.transform.position = pos;
                    if (Mathf.Abs(dx) < near) c.fuse = 0f;
                }
                else
                {
                    c.fuse += dt;
                    bool flash = ((int)(c.fuse * 10f)) % 2 == 0;
                    c.sr.color = flash ? Color.white : new Color(1f, 0.55f, 0.55f, 1f);
                    float pulse = 1f + 0.12f * Mathf.Sin(c.fuse * 30f);
                    c.go.transform.localScale = new Vector3(skinScale * pulse, skinScale * pulse, 1f);
                    if (c.fuse >= 1.2f)
                    {
                        Explode(c.go.transform.position + new Vector3(0f, skinScale, 0f));
                        Destroy(c.go);
                        creepers.RemoveAt(i);
                    }
                }
            }
        }

        void SpawnCreeper(Camera cam)
        {
            float half = cam.orthographicSize * cam.aspect;
            float side = UnityEngine.Random.value < 0.5f ? -1f : 1f;
            float x = cam.transform.position.x + side * (half + 1f);

            var go = new GameObject("CupMineCreeper");
            var sr = go.AddComponent<SpriteRenderer>();
            sr.sprite = creeperSprite;
            sr.sortingLayerID = skinLayerId;
            sr.sortingOrder = skinOrder;
            go.transform.position = new Vector3(x, groundY, 0f);
            go.transform.localScale = new Vector3(skinScale, skinScale, 1f);

            creepers.Add(new Creeper { go = go, sr = sr, dir = -side });
        }

        void Explode(Vector3 at)
        {
            for (int i = 0; i < 14; i++)
            {
                var go = new GameObject("CupMinePuff");
                var sr = go.AddComponent<SpriteRenderer>();
                sr.sprite = puffSprite;
                sr.sortingLayerID = skinLayerId;
                sr.sortingOrder = skinOrder + 2;
                go.transform.position = at;
                float size = skinScale * UnityEngine.Random.Range(0.6f, 1.4f);
                go.transform.localScale = new Vector3(size, size, 1f);

                float ang = UnityEngine.Random.Range(0f, 6.2831f);
                float spd = UnityEngine.Random.Range(1.5f, 4.5f) * skinScale;
                puffs.Add(new Puff
                {
                    go = go,
                    sr = sr,
                    vel = new Vector3(Mathf.Cos(ang) * spd, Mathf.Sin(ang) * spd + 1f, 0f),
                    life = 0.7f
                });
            }
        }

        void TickPuffs()
        {
            float dt = Time.deltaTime;
            for (int i = puffs.Count - 1; i >= 0; i--)
            {
                var p = puffs[i];
                if (p.go == null) { puffs.RemoveAt(i); continue; }
                p.life -= dt;
                if (p.life <= 0f)
                {
                    Destroy(p.go);
                    puffs.RemoveAt(i);
                    continue;
                }
                p.vel.y -= 6f * dt;
                p.go.transform.position += p.vel * dt;
                p.sr.color = new Color(1f, 1f, 1f, Mathf.Clamp01(p.life / 0.7f));
            }
        }

        // ------------------------------------------------------------------ hotbar

        void OnGUI()
        {
            if (!cfgHotbar.Value || !inLevel || icons == null) return;

            int s = Mathf.RoundToInt(Screen.height * 0.075f);
            int gap = Mathf.Max(2, s / 12);
            int n = icons.Length;
            float total = n * s + (n - 1) * gap;
            float x0 = (Screen.width - total) / 2f;
            float y = Screen.height - s - Screen.height * 0.02f;

            for (int i = 0; i < n; i++)
            {
                var r = new Rect(x0 + i * (s + gap), y, s, s);
                GUI.DrawTexture(r, slotTex);
                if (i == selected)
                {
                    float t = Mathf.Max(2f, s * 0.07f);
                    GUI.DrawTexture(new Rect(r.x - t, r.y - t, r.width + 2 * t, t), selTex);
                    GUI.DrawTexture(new Rect(r.x - t, r.yMax, r.width + 2 * t, t), selTex);
                    GUI.DrawTexture(new Rect(r.x - t, r.y, t, r.height), selTex);
                    GUI.DrawTexture(new Rect(r.xMax, r.y, t, r.height), selTex);
                }
                float pad = s * 0.12f;
                GUI.DrawTexture(new Rect(r.x + pad, r.y + pad, s - 2 * pad, s - 2 * pad), icons[i]);
            }

            if (hp > 0)
            {
                int shown = Mathf.Min(hp, 12);
                float hs = s * 0.38f;
                for (int h = 0; h < shown; h++)
                {
                    GUI.DrawTexture(new Rect(x0 + h * (hs + 2f), y - hs - 8f, hs, hs), heartTex);
                }
            }
        }
    }
}
