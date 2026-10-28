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
    [BepInPlugin("local.cupmine", "CupMine", "0.8.0")]
    public class Plugin : BaseUnityPlugin
    {
        ConfigEntry<bool> cfgHotbar, cfgSkin, cfgCreepers;

        Texture2D slotTex, selTex, heartTex;
        Texture2D[] icons;
        Sprite playerSprite, creeperSprite, puffSprite;
        Sprite[] playerFrames;

        class Skinned
        {
            public Transform root;
            public GameObject skin;
            public SpriteRenderer skinSr;
            public float lastX;
            public float lastY;
            public float animT;
            public float faceDir = 1f;
            public int mapView;       // 0 = front, 1 = back, 2 = side
            public bool isMap;
            public bool hasLast;
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
            public float maxLife = 0.7f;
            public float gravity = 6f;
            public Color tint = Color.white;
        }

        readonly List<Skinned> skinned = new List<Skinned>();
        readonly List<Creeper> creepers = new List<Creeper>();
        readonly List<Puff> puffs = new List<Puff>();
        readonly Dictionary<string, Type> typeCache = new Dictionary<string, Type>();

        static readonly string[] PlayerTypeNames = { "LevelPlayerController", "MapPlayerController" };

        float scanTimer;
        float buildTimer;
        bool mapMode;
        float lastFace = 1f;
        float spawnTimer = 4f;
        bool inLevel;
        int hp = -1;
        int selected;
        float groundY = float.MaxValue;
        float skinScale = 0.75f;
        Sprite[] blockSprites;
        Vector3 playerPos;
        bool havePlayer;
        int skinWarn;
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

        // The player's persona: a fluffy black puppy with horns, a blue flame, a baggy white shirt,
        // dark pants and a fluffy tail with a white tip.
        // view: 0 = front, 1 = back, 2 = side (faces right). step: 0 = stand, 1/2 = walk.
        // build: 0 = none, 1 = arm raised, 2 = arm down holding a block. flame: 0/1 flicker.
        Sprite MakePlayerFrame(int view, int step, int build, int flame)
        {
            var p = new Pix(16, 36);
            Color32 fur = C(28, 28, 36), furLight = C(58, 58, 72), hair = C(18, 18, 26);
            Color32 eye = C(8, 8, 14), rim = C(70, 110, 235), white = C(250, 250, 250), heart = C(230, 40, 55);
            Color32 shirt = C(236, 236, 242), shirtShade = C(188, 190, 206);
            Color32 pants = C(46, 54, 80), pantsDark = C(32, 38, 60);
            Color32 horn = C(205, 205, 210), spike = C(235, 195, 80);
            Color32 fireA = C(60, 130, 255), fireB = C(150, 205, 255), fireC = C(235, 246, 255);
            Color32 tip = C(240, 240, 246);

            int lLift = step == 1 ? 2 : 0;
            int rLift = step == 2 ? 2 : 0;
            int lArm = step == 2 ? 1 : 0;
            int rArm = step == 1 ? 1 : 0;

            if (view == 2)
            {
                // side view, facing right
                int ax = 5, bx = 8, aLift = 0, bLift = 0;
                if (step == 1) { ax = 4; bx = 9; bLift = 1; }
                else if (step == 2) { ax = 8; bx = 5; aLift = 1; }
                int tailY = step == 1 ? 12 : (step == 2 ? 10 : 11);
                int armX = step == 1 ? 6 : (step == 2 ? 8 : 7);

                p.Rect(1, tailY, 4, 3, fur, 3);
                p.Rect(0, tailY + 2, 2, 2, tip, 0);
                p.Rect(bx, bLift, 3, 10 - bLift, pantsDark, 4);
                p.Rect(ax, aLift, 3, 10 - aLift, pants, 4);
                p.Rect(bx, bLift, 4, 2, fur, 3);
                p.Rect(ax, aLift, 4, 2, fur, 3);
                p.Rect(4, 10, 8, 10, shirt, 5);
                p.Rect(4, 10, 8, 1, shirtShade, 0);
                p.Rect(4, 10, 1, 10, shirtShade, 0);
                p.Rect(armX - 1, 13, 4, 7, shirt, 5);
                p.Rect(armX - 1, 13, 4, 1, shirtShade, 0);
                p.Rect(armX, 10, 3, 3, fur, 3);
                p.Rect(4, 20, 9, 12, fur, 3);
                p.Rect(13, 21, 2, 3, fur, 3);
                p.Rect(14, 23, 1, 1, furLight, 0);
                p.Rect(9, 22, 3, 5, eye, 0);
                p.Rect(9, 22, 3, 1, rim, 0);
                p.Rect(10, 25, 1, 1, white, 0);
                p.Rect(10, 23, 1, 1, heart, 0);
                p.Rect(4, 26, 9, 6, hair, 3);
                p.Rect(12, 25, 2, 4, hair, 3);
                p.Rect(4, 24, 2, 3, hair, 3);

                // ear, horn, spikes and flame
                p.Rect(5, 32, 3, 2, fur, 3);
                p.Rect(5, 34, 2, 1, fur, 0);
                p.Rect(8, 32, 1, 3, horn, 6);
                p.Rect(9, 33, 1, 1, spike, 0);
                p.Rect(7, 34, 1, 1, spike, 0);
                p.Rect(9, 32, 2, 2, fireA, 0);
                if (flame == 0)
                {
                    p.Rect(9, 34, 2, 2, fireB, 0);
                }
                else
                {
                    p.Rect(10, 34, 1, 2, fireB, 0);
                    p.Rect(9, 34, 1, 1, fireA, 0);
                }
                p.Rect(9, 32, 2, 1, fireC, 0);
            }
            else
            {
                // front (0) and back (1) views
                p.Rect(4, lLift, 4, 10 - lLift, pants, 4);
                p.Rect(8, rLift, 4, 10 - rLift, pants, 4);
                p.Rect(7, 2, 2, 8, pantsDark, 0);
                p.Rect(3, lLift, 5, 2, fur, 3);
                p.Rect(8, rLift, 5, 2, fur, 3);
                p.Rect(5, lLift, 1, 1, furLight, 0);
                p.Rect(10, rLift, 1, 1, furLight, 0);

                if (view == 0)
                {
                    p.Rect(12, 4, 4, 4, fur, 3);
                    p.Rect(14, 8, 2, 2, tip, 0);
                }

                p.Rect(3, 10, 10, 10, shirt, 5);
                p.Rect(3, 10, 10, 1, shirtShade, 0);
                p.Rect(3, 10, 1, 10, shirtShade, 0);
                if (view == 0)
                {
                    p.Rect(7, 12, 1, 6, shirtShade, 0);
                }
                else
                {
                    int dx = step == 1 ? -1 : (step == 2 ? 1 : 0);
                    p.Rect(11 + dx, 6, 4, 7, fur, 3);
                    p.Rect(12 + dx, 12, 3, 2, tip, 0);
                }

                p.Rect(0, 13, 3, 7, shirt, 5);
                p.Rect(0, 13, 3, 1, shirtShade, 0);
                p.Rect(0, 10 + lArm, 3, 3 - lArm, fur, 3);
                if (build == 1)
                {
                    p.Rect(13, 16, 3, 3, shirt, 5);
                    p.Rect(14, 19, 2, 3, shirt, 5);
                    p.Rect(14, 22, 2, 2, fur, 3);
                }
                else if (build == 2)
                {
                    p.Rect(13, 13, 3, 7, shirt, 5);
                    p.Rect(13, 10, 3, 3, fur, 3);
                    p.Rect(14, 8, 2, 2, C(125, 125, 125), 20);
                }
                else
                {
                    p.Rect(13, 13, 3, 7, shirt, 5);
                    p.Rect(13, 13, 3, 1, shirtShade, 0);
                    p.Rect(13, 10 + rArm, 3, 3 - rArm, fur, 3);
                }

                p.Rect(2, 20, 12, 12, fur, 3);
                p.Rect(1, 21, 1, 3, fur, 3);
                p.Rect(14, 21, 1, 3, fur, 3);
                if (view == 0)
                {
                    p.Rect(3, 22, 4, 5, eye, 0);
                    p.Rect(9, 22, 4, 5, eye, 0);
                    p.Rect(3, 22, 4, 1, rim, 0);
                    p.Rect(9, 22, 4, 1, rim, 0);
                    p.Rect(5, 25, 1, 1, white, 0);
                    p.Rect(11, 25, 1, 1, white, 0);
                    p.Rect(4, 23, 1, 1, heart, 0);
                    p.Rect(10, 23, 1, 1, heart, 0);
                    p.Rect(7, 21, 2, 1, furLight, 0);
                }
                p.Rect(1, 26, 14, 6, hair, 3);
                p.Rect(2, 25, 2, 1, hair, 3);
                p.Rect(6, 25, 3, 1, hair, 3);
                p.Rect(12, 25, 2, 1, hair, 3);
                if (view == 1) p.Rect(1, 20, 14, 6, hair, 3);

                // ears, horns, spikes and flame
                p.Rect(2, 32, 3, 2, fur, 3);
                p.Rect(2, 34, 2, 1, fur, 0);
                p.Rect(11, 32, 3, 2, fur, 3);
                p.Rect(12, 34, 2, 1, fur, 0);
                p.Rect(5, 32, 1, 3, horn, 6);
                p.Rect(10, 32, 1, 3, horn, 6);
                p.Rect(4, 33, 1, 1, spike, 0);
                p.Rect(11, 33, 1, 1, spike, 0);
                p.Rect(6, 34, 1, 1, spike, 0);
                p.Rect(9, 34, 1, 1, spike, 0);
                p.Rect(7, 32, 2, 2, fireA, 0);
                p.Rect(6, 33, 1, 1, fireA, 0);
                p.Rect(9, 33, 1, 1, fireA, 0);
                if (flame == 0)
                {
                    p.Rect(7, 34, 2, 2, fireB, 0);
                }
                else
                {
                    p.Rect(8, 34, 1, 2, fireB, 0);
                    p.Rect(7, 34, 1, 1, fireA, 0);
                }
                p.Rect(7, 32, 2, 1, fireC, 0);
            }

            return Sprite.Create(p.ToTexture(), new Rect(0, 0, 16, 36), new Vector2(0.5f, 0f), 16f);
        }

        void BuildArt()
        {
            // Player persona: 16 x 36. Per flame flicker (2): frames 0-8 = front/back/side x
            // (stand, walk A, walk B), frames 9 and 10 = the building animation.
            playerFrames = new Sprite[22];
            for (int fl = 0; fl < 2; fl++)
            {
                for (int v = 0; v < 3; v++)
                {
                    for (int st = 0; st < 3; st++) playerFrames[fl * 11 + v * 3 + st] = MakePlayerFrame(v, st, 0, fl);
                }
                playerFrames[fl * 11 + 9] = MakePlayerFrame(0, 0, 1, fl);
                playerFrames[fl * 11 + 10] = MakePlayerFrame(0, 0, 2, fl);
            }
            playerSprite = playerFrames[0];

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

            blockSprites = new Sprite[icons.Length];
            for (int i = 0; i < icons.Length; i++)
            {
                blockSprites[i] = Sprite.Create(icons[i], new Rect(0, 0, 16, 16), new Vector2(0.5f, 0f), 16f);
            }

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
            if (buildTimer > 0f) buildTimer -= Time.deltaTime;
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

                // Controller: Back/Select = next slot, left stick click = previous, right stick click = use.
                if (Input.GetKeyDown(KeyCode.JoystickButton6)) selected = (selected + 1) % icons.Length;
                if (Input.GetKeyDown(KeyCode.JoystickButton8)) selected = (selected + icons.Length - 1) % icons.Length;
                if (Input.GetKeyDown(KeyCode.JoystickButton9) || Input.GetKeyDown(KeyCode.E)) UseItem(selected);
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

                // Animation: pick front/back/side view from the direction of travel, swap
                // walk frames while moving, and play the building pose after placing a block.
                if (s.skinSr != null)
                {
                    Vector3 rp = s.root.position;
                    float ddx = s.hasLast ? rp.x - s.lastX : 0f;
                    float ddy = s.hasLast ? rp.y - s.lastY : 0f;
                    s.lastX = rp.x;
                    s.lastY = rp.y;
                    s.hasLast = true;
                    bool moving = Mathf.Abs(ddx) > 0.002f || Mathf.Abs(ddy) > 0.002f;

                    int view = 2;
                    if (s.isMap)
                    {
                        if (moving)
                        {
                            if (Mathf.Abs(ddy) > Mathf.Abs(ddx) * 1.2f) s.mapView = ddy > 0f ? 1 : 0;
                            else s.mapView = 2;
                        }
                        view = s.mapView;
                    }
                    if (Mathf.Abs(ddx) > 0.002f) s.faceDir = ddx > 0f ? 1f : -1f;
                    lastFace = s.faceDir;

                    int step = 0;
                    if (moving)
                    {
                        s.animT += Time.deltaTime;
                        step = ((int)(s.animT * 8f)) % 2 == 0 ? 1 : 2;
                    }
                    else
                    {
                        s.animT = 0f;
                    }
                    if (!s.isMap && Mathf.Abs(ddy) > 0.01f) step = 1; // in the air

                    int flicker = (((int)(Time.time * 6f)) % 2) * 11;
                    Sprite frame = playerFrames[flicker + view * 3 + step];
                    if (buildTimer > 0f) frame = playerFrames[flicker + 9 + (((int)(buildTimer * 8f)) % 2)];
                    s.skinSr.sprite = frame;

                    float ps = s.skin.transform.lossyScale.x >= 0f ? 1f : -1f;
                    s.skinSr.flipX = (s.faceDir * ps) < 0f;
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

            havePlayer = inLevel;
            if (inLevel)
            {
                Transform pt = found[0].transform;
                playerPos = pt.position;
                mapMode = found[0].GetType().Name == "MapPlayerController";

                // Learn where the ground is and which draw layer the player uses,
                // even if the blocky skin can't be applied.
                SpriteRenderer[] prs = GetRenderers(pt, false);
                int lid, ord;
                if (prs.Length > 0 && ComputeSort(prs, out lid, out ord))
                {
                    skinLayerId = lid;
                    skinOrder = ord + 1;
                    Bounds pb = new Bounds();
                    bool pany = false;
                    foreach (var r in prs)
                    {
                        if (!r.enabled || r.sprite == null || !IsPlayerArt(r)) continue;
                        if (!pany) { pb = r.bounds; pany = true; }
                        else pb.Encapsulate(r.bounds);
                    }
                    if (pany && pb.min.y < groundY) groundY = pb.min.y;
                }
                if (groundY == float.MaxValue) groundY = playerPos.y;
            }

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
            foreach (var r in GetRenderers(s.root, true))
            {
                if (r.gameObject != s.skin && IsPlayerArt(r)) s.hidden.Add(r);
            }
        }

        void ApplySkin(Transform root)
        {
            SpriteRenderer[] rs = GetRenderers(root, false);
            Bounds b = new Bounds();
            bool any = false;
            foreach (var r in rs)
            {
                if (!r.enabled || r.sprite == null || !IsPlayerArt(r)) continue;
                if (!any)
                {
                    b = r.bounds;
                    any = true;
                }
                else
                {
                    b.Encapsulate(r.bounds);
                }
            }

            int layer, order;
            if (!any || !ComputeSort(rs, out layer, out order))
            {
                if (skinWarn < 3)
                {
                    skinWarn++;
                    Logger.LogInfo("Skin: no usable sprites under " + root.name + " (renderers found: " + rs.Length + ")");
                }
                return; // sprites not ready yet, try again next scan
            }

            float height = Mathf.Max(b.size.y, 0.05f);
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

            var s = new Skinned { root = root, skin = go, skinSr = sr };
            Type mapType = FindType("MapPlayerController");
            s.isMap = mapType != null && root.GetComponent(mapType) != null;
            RefreshHidden(s);
            skinned.Add(s);

            skinScale = scale;
            skinLayerId = layer;
            skinOrder = order + 1;
            if (b.min.y < groundY) groundY = b.min.y;

            Logger.LogInfo("Skin applied to " + root.name + ": renderers=" + rs.Length +
                " height=" + b.size.y.ToString("0.00") + " layerId=" + layer + " order=" + (order + 1));
        }

        // The player's own sprites are small compared to the screen. Anything bigger (screen fades,
        // backdrops) is ignored, so it can't throw off the skin's size or position, and is never hidden.
        // Sizes are judged against the camera view, because levels and the map use different units.
        bool IsPlayerArt(SpriteRenderer r)
        {
            if (r.sprite == null) return false;
            Vector3 size = r.bounds.size;
            float viewH = ViewHeight();
            return size.y <= viewH * 0.45f && size.x <= viewH * 0.6f;
        }

        float ViewHeight()
        {
            Camera cam = Camera.main;
            if (cam == null || !cam.orthographic)
            {
                cam = null;
                foreach (var c in Camera.allCameras)
                {
                    if (c != null && c.orthographic) { cam = c; break; }
                }
            }
            return cam != null ? cam.orthographicSize * 2f : 10f;
        }

        // Finds the sprites that draw this player. If the controller object has none of its own,
        // look one level up in case the art sits on the parent object.
        SpriteRenderer[] GetRenderers(Transform root, bool includeInactive)
        {
            SpriteRenderer[] rs = root.GetComponentsInChildren<SpriteRenderer>(includeInactive);
            if (rs.Length == 0 && root.parent != null)
            {
                SpriteRenderer[] up = root.parent.GetComponentsInChildren<SpriteRenderer>(includeInactive);
                if (up.Length > 0 && up.Length <= 12) return up;
            }
            return rs;
        }

        // Picks the draw layer and order of whichever sprite is drawn in front, so the skin
        // is never hidden behind the level.
        bool ComputeSort(SpriteRenderer[] rs, out int layerId, out int order)
        {
            layerId = 0;
            order = 0;
            int bestValue = int.MinValue;
            bool first = true;
            foreach (var r in rs)
            {
                if (!r.enabled || r.sprite == null || !IsPlayerArt(r)) continue;
                int value = SortingLayer.GetLayerValueFromID(r.sortingLayerID);
                if (first || value > bestValue || (value == bestValue && r.sortingOrder > order))
                {
                    bestValue = value;
                    layerId = r.sortingLayerID;
                    order = r.sortingOrder;
                    first = false;
                }
            }
            return !first;
        }

        // ------------------------------------------------------------------ creepers

        void TickCreepers()
        {
            if (!havePlayer || groundY == float.MaxValue) return;
            Camera cam = Camera.main;

            float dt = Time.deltaTime;
            if (cfgCreepers.Value && inLevel && cam != null && cam.orthographic)
            {
                spawnTimer -= dt;
                if (spawnTimer <= 0f && creepers.Count < 3)
                {
                    spawnTimer = UnityEngine.Random.Range(5f, 10f);
                    SpawnCreeper(cam);
                }
            }

            for (int i = creepers.Count - 1; i >= 0; i--)
            {
                var c = creepers[i];
                if (c.go == null) { creepers.RemoveAt(i); continue; }

                Vector3 pos = c.go.transform.position;
                float dx = playerPos.x - pos.x;
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
                    vel = new Vector3(Mathf.Cos(ang) * spd, Mathf.Sin(ang) * spd + skinScale, 0f),
                    life = 0.7f,
                    maxLife = 0.7f,
                    gravity = 8f * skinScale,
                    tint = Color.white
                });
            }
        }

        // Cyan sparkle burst (diamond slot).
        void Sparkle(Vector3 at)
        {
            for (int i = 0; i < 24; i++)
            {
                var go = new GameObject("CupMineSparkle");
                var sr = go.AddComponent<SpriteRenderer>();
                sr.sprite = puffSprite;
                sr.sortingLayerID = skinLayerId;
                sr.sortingOrder = skinOrder + 2;
                go.transform.position = at;
                float size = skinScale * UnityEngine.Random.Range(0.4f, 0.9f);
                go.transform.localScale = new Vector3(size, size, 1f);

                float ang = UnityEngine.Random.Range(0f, 6.2831f);
                float spd = UnityEngine.Random.Range(1.0f, 3.5f) * skinScale;
                puffs.Add(new Puff
                {
                    go = go,
                    sr = sr,
                    vel = new Vector3(Mathf.Cos(ang) * spd, Mathf.Sin(ang) * spd, 0f),
                    life = 1.0f,
                    maxLife = 1.0f,
                    gravity = 0f,
                    tint = new Color(0.3f, 0.95f, 0.95f, 1f)
                });
            }
        }

        // Stone and grass slots: place a solid block in front of the player. Place more at the same
        // spot to stack them into steps. In levels the block copies the physics layer of the ground
        // under the player so the game treats it as floor. The oldest block goes when there are 8.
        void PlaceBlock(int slot)
        {
            buildTimer = 0.5f;

            float size = skinScale;
            float bx = playerPos.x + lastFace * 1.3f * size;
            float by = groundY;
            int count = 0;
            Puff oldest = null;
            foreach (var q in puffs)
            {
                if (q.go == null || q.go.name != "CupMineBlock") continue;
                count++;
                if (oldest == null || q.life < oldest.life) oldest = q;
                Vector3 qp = q.go.transform.position;
                if (Mathf.Abs(qp.x - bx) < 0.5f * size && qp.y + size > by) by = qp.y + size;
            }
            if (count >= 8 && oldest != null) oldest.life = 0.3f;

            var go = new GameObject("CupMineBlock");
            var sr = go.AddComponent<SpriteRenderer>();
            sr.sprite = blockSprites[slot];
            sr.sortingLayerID = skinLayerId;
            sr.sortingOrder = skinOrder;
            go.transform.position = new Vector3(bx, by, 0f);
            go.transform.localScale = new Vector3(size, size, 1f);

            if (!mapMode)
            {
                int layer = FindGroundLayer();
                if (layer >= 0) go.layer = layer;
                if (!string.IsNullOrEmpty(groundTag))
                {
                    try { go.tag = groundTag; } catch (Exception) { }
                }
                var col = go.AddComponent<BoxCollider2D>();
                col.size = new Vector2(1f, 1f);
                col.offset = new Vector2(0f, 0.5f);
            }

            puffs.Add(new Puff
            {
                go = go,
                sr = sr,
                vel = Vector3.zero,
                life = 25f,
                maxLife = 25f,
                gravity = 0f,
                tint = Color.white
            });
        }

        string groundTag;
        bool loggedColliders;
        int groundLogCount;

        // Looks straight down from the player's feet for the floor and returns its physics layer,
        // or -1 if nothing is found. Trigger colliders count too, because the game may use them.
        int FindGroundLayer()
        {
            try
            {
                float tol = 0.6f * skinScale;
                Vector2 origin = new Vector2(playerPos.x, groundY - 0.05f * skinScale);
                RaycastHit2D[] hits = Physics2D.RaycastAll(origin, Vector2.down, 4f * skinScale);
                Collider2D best = null;
                foreach (var h in hits)
                {
                    Collider2D hc = h.collider;
                    if (hc == null || hc.gameObject.name == "CupMineBlock") continue;
                    if (best == null) best = hc;
                    if (Mathf.Abs(hc.bounds.max.y - groundY) <= tol) { best = hc; break; }
                }

                if (best == null)
                {
                    foreach (var c in UnityEngine.Object.FindObjectsOfType<Collider2D>())
                    {
                        if (c == null || c.gameObject.name == "CupMineBlock") continue;
                        string n = c.gameObject.name.ToLower();
                        if (n.Contains("ground") || n.Contains("floor") || n.Contains("platform")) { best = c; break; }
                    }
                }

                if (best != null)
                {
                    GameObject g = best.gameObject;
                    groundTag = g.tag;
                    if (groundLogCount < 3)
                    {
                        groundLogCount++;
                        string comps = "";
                        foreach (var comp in g.GetComponents<Component>())
                        {
                            if (comp != null) comps += comp.GetType().Name + " ";
                        }
                        Logger.LogInfo("Block layer " + g.layer + " (" + LayerMask.LayerToName(g.layer) + ") from " + g.name +
                            " tag=" + g.tag + " trigger=" + best.isTrigger + " components: " + comps);
                    }
                    return g.layer;
                }

                if (!loggedColliders)
                {
                    loggedColliders = true;
                    LogNearbyColliders();
                }
            }
            catch (Exception e)
            {
                Logger.LogWarning("Block layer search failed: " + e.Message);
            }

            if (groundLogCount < 3)
            {
                groundLogCount++;
                Logger.LogInfo("Block layer: no ground found, using default");
            }
            return -1;
        }

        // Writes the colliders near the player to the log, to show how the level's floor is built.
        void LogNearbyColliders()
        {
            float vh = ViewHeight();
            int shown = 0;
            Collider2D[] all = UnityEngine.Object.FindObjectsOfType<Collider2D>();
            Logger.LogInfo("Colliders in scene: " + all.Length);
            foreach (var c in all)
            {
                if (c == null || shown >= 25) break;
                Bounds b = c.bounds;
                if (Mathf.Abs(b.center.x - playerPos.x) > vh * 1.5f || Mathf.Abs(b.center.y - groundY) > vh) continue;
                shown++;
                GameObject g = c.gameObject;
                Logger.LogInfo("  collider " + g.name + " layer=" + g.layer + "(" + LayerMask.LayerToName(g.layer) + ") trigger=" +
                    c.isTrigger + " top=" + b.max.y.ToString("0") + " size=" + b.size.x.ToString("0") + "x" + b.size.y.ToString("0"));
            }
        }

        void UseItem(int slot)
        {
            if (!havePlayer || groundY == float.MaxValue) return;
            switch (slot)
            {
                case 0:
                case 1:
                    PlaceBlock(slot);
                    break;
                case 2:
                case 3:
                    Camera cam = Camera.main;
                    if (cam != null && cam.orthographic && creepers.Count < 6) SpawnCreeper(cam);
                    break;
                default:
                    Sparkle(playerPos + new Vector3(0f, skinScale, 0f));
                    break;
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
                p.vel.y -= p.gravity * dt;
                p.go.transform.position += p.vel * dt;
                p.sr.color = new Color(p.tint.r, p.tint.g, p.tint.b, Mathf.Clamp01(p.life / Mathf.Min(p.maxLife, 1f)));
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

                // Tap or click a slot to select it; tap the selected slot again to use it.
                Event ev = Event.current;
                if (ev.type == EventType.MouseDown && r.Contains(ev.mousePosition))
                {
                    if (selected == i) UseItem(i);
                    else selected = i;
                    ev.Use();
                }

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
