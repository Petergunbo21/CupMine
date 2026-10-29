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
    //  - Blocky green "creepers" that walk to the nearest enemy and blast it through the game's
    //    own damage system (DamageReceiver.TakeDamage). They never hurt the player.
    //  - Solid blocks: layer/tag setup copied from what Assembly-CSharp.dll shows the game checks
    // All art is generated in code, so there are no extra files to install.
    [BepInPlugin("local.cupmine", "CupMine", "0.9.0")]
    public class Plugin : BaseUnityPlugin
    {
        ConfigEntry<bool> cfgHotbar, cfgSkin, cfgCreepers;
        ConfigEntry<float> cfgPersonaScale, cfgCreeperDamage, cfgBlast;

        Texture2D slotTex, selTex, heartTex;
        Texture2D[] icons;
        Sprite playerSprite, creeperSprite, puffSprite;
        Sprite[] playerFrames;

        class Skinned
        {
            public Transform root;
            public GameObject skin;
            public SpriteRenderer skinSr;
            public Component controller;   // LevelPlayerController or MapPlayerController
            public Component colliderMgr;  // LevelPlayerColliderManager (levels only)
            public Component motor;        // LevelPlayerMotor (levels only)
            public bool levelAnchor;       // true = persona is placed from the game's own hitbox
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
            public Transform target;       // the enemy this creeper is walking to
            public float retarget;
            public float age;
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
            public float hp = 1f;          // blocks only: enemy attacks wear this down
            public float hitCool;          // blocks only: seconds until the next hit counts
        }

        readonly List<Skinned> skinned = new List<Skinned>();
        readonly List<Creeper> creepers = new List<Creeper>();
        readonly List<Puff> puffs = new List<Puff>();
        readonly Dictionary<string, Type> typeCache = new Dictionary<string, Type>();
        readonly List<Component> enemyList = new List<Component>();
        public static Plugin Instance;
        static readonly BindingFlags BF = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
        bool loggedEnemies;
        int visLayer;                      // GameObject layer that the game's cameras draw the player on

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
            cfgCreepers = Config.Bind("General", "Creepers", true, "Let creepers walk to enemies and blast them.");
            cfgPersonaScale = Config.Bind("General", "PersonaScale", 1.0f, "Persona size compared to Cuphead's hitbox (1 = default, bigger = larger).");
            cfgCreeperDamage = Config.Bind("Creepers", "Damage", 10.0f, "Damage a creeper blast deals to each enemy it hits.");
            cfgBlast = Config.Bind("Creepers", "BlastRadius", 1.6f, "Creeper blast radius, in persona heights.");

            Instance = this;
            BuildArt();
            UnityEngine.SceneManagement.SceneManager.sceneLoaded += OnSceneLoaded;
            Camera.onPreCull += OnCamPreCull;
            Logger.LogInfo("CupMine loaded.");
        }

        void OnDestroy()
        {
            UnityEngine.SceneManagement.SceneManager.sceneLoaded -= OnSceneLoaded;
            Camera.onPreCull -= OnCamPreCull;
        }

        void OnSceneLoaded(UnityEngine.SceneManagement.Scene scene, UnityEngine.SceneManagement.LoadSceneMode mode)
        {
            groundY = float.MaxValue;
            foreach (var sk in skinned) { if (sk.skin != null) Destroy(sk.skin); }
            skinned.Clear();
            enemyList.Clear();
            loggedEnemies = false;
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

            TickBlockHits();
            TickCreepers();
            TickPuffs();
        }

        // Reads a property, a get_ method or a field by name. Used for the game's own members, which
        // are only known at run time. Returns null if the member is missing or throws.
        object CallGet(object o, string name)
        {
            if (o == null) return null;
            try
            {
                Type t = o.GetType();
                PropertyInfo pi = t.GetProperty(name, BF);
                if (pi != null && pi.CanRead && pi.GetIndexParameters().Length == 0) return pi.GetValue(o, null);
                MethodInfo mi = t.GetMethod("get_" + name, BF);
                if (mi != null && mi.GetParameters().Length == 0) return mi.Invoke(o, null);
                FieldInfo fi = t.GetField(name, BF);
                if (fi != null) return fi.GetValue(o);
            }
            catch (Exception) { }
            return null;
        }

        // LookDirection is a Trilean2 (x, y); each is a Trilean whose Value is -1, 0 or 1.
        int ReadTrileanX(object tri2)
        {
            if (tri2 == null) return 0;
            try
            {
                FieldInfo xf = tri2.GetType().GetField("x", BF);
                if (xf == null) return 0;
                object x = xf.GetValue(tri2);
                object v = CallGet(x, "Value");
                return v == null ? 0 : Convert.ToInt32(v);
            }
            catch (Exception)
            {
                return 0;
            }
        }

        void LateUpdate()
        {
            AnimateSkins();
            ApplyVisuals();
        }

        // Runs right before each camera draws, after every other script has finished, so the
        // game's own sprites stay hidden and the persona sits exactly on the player's hitbox.
        void OnCamPreCull(Camera cam)
        {
            ApplyVisuals();
        }

        void ApplyVisuals()
        {
            for (int i = 0; i < skinned.Count; i++)
            {
                var sk = skinned[i];
                if (sk.root == null || sk.skin == null) continue;
                bool alive = sk.root.gameObject.activeInHierarchy;
                if (sk.skin.activeSelf != alive) sk.skin.SetActive(alive);
                if (!alive) continue;
                for (int j = 0; j < sk.hidden.Count; j++)
                {
                    if (sk.hidden[j] != null) sk.hidden[j].enabled = false;
                }
                if (sk.levelAnchor) PositionLevelSkin(sk);
            }
        }

        // The game's player hitbox: LevelPlayerColliderManager.DefaultCenter is the world position
        // of the standing hitbox centre, DefaultHeight its height (both read from Assembly-CSharp.dll).
        bool ReadAnchor(Skinned sk, out Vector2 center, out float height)
        {
            center = Vector2.zero;
            height = 0f;
            if (sk.colliderMgr == null) return false;
            object c = CallGet(sk.colliderMgr, "DefaultCenter");
            object h = CallGet(sk.colliderMgr, "DefaultHeight");
            if (c == null || h == null) return false;
            try
            {
                center = (Vector2)c;
                height = Convert.ToSingle(h);
            }
            catch (Exception)
            {
                return false;
            }
            return height > 0.01f;
        }

        void PositionLevelSkin(Skinned sk)
        {
            Vector2 c;
            float h;
            if (!ReadAnchor(sk, out c, out h)) return;
            sk.skin.transform.position = new Vector3(c.x, c.y - h * 0.5f, sk.root.position.z);
        }

        // Picks the sprite: front/back/side view from the direction of travel (map), walk frames
        // while moving, a jump pose while airborne, and the building pose after placing a block.
        void AnimateSkins()
        {
            for (int i = 0; i < skinned.Count; i++)
            {
                var sk = skinned[i];
                if (sk.root == null || sk.skinSr == null) continue;

                Vector3 rp = sk.root.position;
                float ddx = sk.hasLast ? rp.x - sk.lastX : 0f;
                float ddy = sk.hasLast ? rp.y - sk.lastY : 0f;
                sk.lastX = rp.x;
                sk.lastY = rp.y;
                sk.hasLast = true;
                float eps = 0.002f * Mathf.Max(1f, skinScale);
                bool moving = Mathf.Abs(ddx) > eps || Mathf.Abs(ddy) > eps;

                if (Mathf.Abs(ddx) > eps) sk.faceDir = ddx > 0f ? 1f : -1f;
                bool grounded = true;
                if (!sk.isMap)
                {
                    if (sk.motor != null)
                    {
                        object g = CallGet(sk.motor, "Grounded");
                        if (g is bool) grounded = (bool)g;
                        int lx = ReadTrileanX(CallGet(sk.motor, "LookDirection"));
                        if (lx != 0) sk.faceDir = lx > 0 ? 1f : -1f;
                    }
                    else if (Mathf.Abs(ddy) > eps * 5f)
                    {
                        grounded = false;
                    }
                }
                lastFace = sk.faceDir;

                int view = 2;
                if (sk.isMap)
                {
                    if (moving)
                    {
                        if (Mathf.Abs(ddy) > Mathf.Abs(ddx) * 1.2f) sk.mapView = ddy > 0f ? 1 : 0;
                        else sk.mapView = 2;
                    }
                    view = sk.mapView;
                }

                int step = 0;
                if (moving)
                {
                    sk.animT += Time.deltaTime;
                    step = ((int)(sk.animT * 8f)) % 2 == 0 ? 1 : 2;
                }
                else
                {
                    sk.animT = 0f;
                }
                if (!sk.isMap && !grounded) step = 1; // in the air

                int flicker = (((int)(Time.time * 6f)) % 2) * 11;
                Sprite frame = playerFrames[flicker + view * 3 + step];
                if (buildTimer > 0f) frame = playerFrames[flicker + 9 + (((int)(buildTimer * 8f)) % 2)];
                sk.skinSr.sprite = frame;

                // A skin parented to the player inherits the player's flip; the level skin does not.
                float ps = sk.levelAnchor ? 1f : (sk.skin.transform.lossyScale.x >= 0f ? 1f : -1f);
                sk.skinSr.flipX = (sk.faceDir * ps) < 0f;
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
                Component ctl = found[0];
                playerPos = ctl.transform.position;
                mapMode = ctl.GetType().Name == "MapPlayerController";
                if (mapMode) MapScan(ctl);
                else LevelScan(ctl);
                if (groundY == float.MaxValue) groundY = playerPos.y;
            }

            RefreshEnemies();

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
                foreach (var sk in skinned)
                {
                    if (sk.root == comp.transform) { existing = sk; break; }
                }

                if (existing == null) ApplySkin(comp);
                else RefreshHidden(existing);
            }
        }

        // Map: learn the ground line and the draw layer from the player's own sprites.
        void MapScan(Component ctl)
        {
            SpriteRenderer[] prs = GetRenderers(ctl.transform, false);
            int lid, ord;
            if (prs.Length > 0 && ComputeSort(prs, out lid, out ord))
            {
                skinLayerId = lid;
                skinOrder = ord + 1;
                visLayer = VisualLayerOf(prs);
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
        }

        // Level: use the game's own hitbox for position and size, and its Grounded flag to follow
        // the floor the player last stood on (so blocks and creepers sit on the right level).
        void LevelScan(Component ctl)
        {
            object mgr = CallGet(ctl, "colliderManager");
            object motor = CallGet(ctl, "motor");
            object c = CallGet(mgr, "DefaultCenter");
            object h = CallGet(mgr, "DefaultHeight");
            if (c != null && h != null)
            {
                try
                {
                    Vector2 cv = (Vector2)c;
                    float hv = Convert.ToSingle(h);
                    if (hv > 0.01f)
                    {
                        float feet = cv.y - hv * 0.5f;
                        object g = CallGet(motor, "Grounded");
                        bool grounded = g is bool && (bool)g;
                        if (groundY == float.MaxValue || grounded) groundY = feet;
                        skinScale = hv * 1.25f * cfgPersonaScale.Value / 2.25f;
                        playerPos = new Vector3(cv.x, feet, ctl.transform.position.z);
                    }
                }
                catch (Exception) { }
            }

            SpriteRenderer[] prs = GetRenderers(ctl.transform, false);
            int lid, ord;
            if (prs.Length > 0 && ComputeSort(prs, out lid, out ord))
            {
                skinLayerId = lid;
                skinOrder = ord + 1;
                visLayer = VisualLayerOf(prs);
            }
        }

        // The GameObject layer of the player's own sprites. The game's cameras only draw some layers,
        // so everything this mod spawns uses the same layer as the player instead of the default one.
        int VisualLayerOf(SpriteRenderer[] rs)
        {
            foreach (var r in rs)
            {
                if (r != null && r.enabled && r.sprite != null && IsPlayerArt(r)) return r.gameObject.layer;
            }
            return 0;
        }

        void RefreshHidden(Skinned s)
        {
            s.hidden.Clear();
            foreach (var r in GetRenderers(s.root, true))
            {
                if (r.gameObject != s.skin && IsPlayerArt(r)) s.hidden.Add(r);
            }
        }

        void ApplySkin(Component controller)
        {
            Transform root = controller.transform;
            bool isMap = controller.GetType().Name == "MapPlayerController";

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

            var go = new GameObject("CupMineSkin");
            go.layer = VisualLayerOf(rs);
            var sr = go.AddComponent<SpriteRenderer>();
            sr.sprite = playerSprite;
            sr.sortingLayerID = layer;
            sr.sortingOrder = order + 1;

            var sk = new Skinned { root = root, skin = go, skinSr = sr, controller = controller, isMap = isMap };

            bool anchored = false;
            Vector2 c = Vector2.zero;
            float h = 0f;
            if (!isMap)
            {
                sk.colliderMgr = CallGet(controller, "colliderManager") as Component;
                sk.motor = CallGet(controller, "motor") as Component;
                anchored = ReadAnchor(sk, out c, out h);
            }

            float scale;
            if (anchored)
            {
                // Level: sit on the game's own player hitbox. The skin is not parented to the player,
                // it is moved onto the hitbox every frame (see PositionLevelSkin).
                scale = h * 1.25f * cfgPersonaScale.Value / 2.25f;
                go.transform.localScale = new Vector3(scale, scale, 1f);
                go.transform.position = new Vector3(c.x, c.y - h * 0.5f, root.position.z);
                sk.levelAnchor = true;
                groundY = c.y - h * 0.5f;
            }
            else
            {
                // Map (or no hitbox found): size and place from the player's sprites, parented to the player.
                float height = Mathf.Max(b.size.y, 0.05f);
                scale = height / 2f * cfgPersonaScale.Value; // the persona sprite is about 2 world units tall at scale 1
                go.transform.position = new Vector3(b.center.x, b.min.y, root.position.z);
                go.transform.SetParent(root, true);
                Vector3 ls = root.lossyScale;
                go.transform.localScale = new Vector3(
                    scale / Mathf.Max(Mathf.Abs(ls.x), 0.0001f),
                    scale / Mathf.Max(Mathf.Abs(ls.y), 0.0001f),
                    1f);
                if (b.min.y < groundY) groundY = b.min.y;
            }

            RefreshHidden(sk);
            skinned.Add(sk);

            skinScale = scale;
            skinLayerId = layer;
            skinOrder = order + 1;
            visLayer = go.layer;

            Logger.LogInfo("Skin applied to " + root.name + " (" + controller.GetType().Name + "): anchored=" + anchored +
                " scale=" + scale.ToString("0.00") + " drawLayer=" + go.layer + "(" + LayerMask.LayerToName(go.layer) + ")" +
                " sortLayerId=" + layer + " order=" + (order + 1) + " hitboxCenter=" + c + " hitboxHeight=" + h.ToString("0.0") +
                " rootPos=" + root.position + " hiddenRenderers=" + sk.hidden.Count);
            LogCameras(go.layer);
            StartCoroutine(LogSkinVisibleLater(sk, 1.5f));
        }

        // Diagnostics: which cameras exist and whether they draw the layer the persona is on.
        void LogCameras(int layer)
        {
            try
            {
                int n = 0;
                foreach (var cam in Camera.allCameras)
                {
                    if (cam == null) continue;
                    if (n++ >= 6) break;
                    bool sees = (cam.cullingMask & (1 << layer)) != 0;
                    Logger.LogInfo("Camera " + cam.name + ": ortho=" + cam.orthographic + " size=" + cam.orthographicSize.ToString("0.0") +
                        " cullingMask=0x" + cam.cullingMask.ToString("x") + " drawsPersonaLayer=" + sees + " depth=" + cam.depth);
                }
            }
            catch (Exception e)
            {
                Logger.LogWarning("Camera log failed: " + e.Message);
            }
        }

        // Diagnostics: after a moment, ask Unity whether any camera is actually drawing the persona.
        System.Collections.IEnumerator LogSkinVisibleLater(Skinned sk, float delay)
        {
            yield return new WaitForSeconds(delay);
            if (sk == null || sk.skinSr == null || sk.skin == null) yield break;
            Logger.LogInfo("Skin check: isVisible=" + sk.skinSr.isVisible + " active=" + sk.skin.activeInHierarchy +
                " pos=" + sk.skin.transform.position + " scale=" + sk.skin.transform.lossyScale + " order=" + sk.skinSr.sortingOrder);
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

        // The camera that draws the level or map (an orthographic one).
        Camera GetCam()
        {
            Camera cam = Camera.main;
            if (cam != null && cam.orthographic) return cam;
            foreach (var c in Camera.allCameras)
            {
                if (c != null && c.orthographic && c.enabled) return c;
            }
            return null;
        }

        float BlastRadius()
        {
            return cfgBlast.Value * 2.25f * skinScale;
        }

        // Finds every enemy: DamageReceiver components whose type is Enemy (0). The enum order in
        // Assembly-CSharp.dll is Enemy, Player, Other.
        void RefreshEnemies()
        {
            enemyList.Clear();
            if (!inLevel || mapMode) return;
            Type recvT = FindType("DamageReceiver");
            if (recvT == null) return;
            FieldInfo typeF = recvT.GetField("type", BF);
            if (typeF == null) return;
            foreach (var o in UnityEngine.Object.FindObjectsOfType(recvT))
            {
                Component r = o as Component;
                if (r == null) continue;
                Behaviour bh = r as Behaviour;
                if (bh != null && !bh.enabled) continue;
                if (!r.gameObject.activeInHierarchy) continue;
                int tv;
                try { tv = Convert.ToInt32(typeF.GetValue(r)); } catch (Exception) { continue; }
                if (tv != 0) continue;
                enemyList.Add(r);
            }
            if (!loggedEnemies && enemyList.Count > 0)
            {
                loggedEnemies = true;
                string names = "";
                for (int i = 0; i < enemyList.Count && i < 6; i++) names += enemyList[i].gameObject.name + " ";
                Logger.LogInfo("Enemies found: " + enemyList.Count + " e.g. " + names);
            }
        }

        // Nearest on-screen enemy, measured sideways.
        Transform PickTarget(Vector3 from, Camera cam)
        {
            Transform best = null;
            float bd = float.MaxValue;
            for (int i = 0; i < enemyList.Count; i++)
            {
                Component r = enemyList[i];
                if (r == null) continue;
                Vector3 p = r.transform.position;
                if (cam != null && Mathf.Abs(p.x - cam.transform.position.x) > cam.orthographicSize * cam.aspect * 1.15f) continue;
                float d = Mathf.Abs(p.x - from.x);
                if (d < bd) { bd = d; best = r.transform; }
            }
            return best;
        }

        // In levels a creeper walks to the nearest enemy and blasts it; with no enemy it stays beside
        // the player. On the map, where there are no enemies, it just wanders to the player and poofs.
        void TickCreepers()
        {
            if (!havePlayer || groundY == float.MaxValue) return;
            Camera cam = GetCam();
            float dt = Time.deltaTime;

            if (cfgCreepers.Value && cam != null && (mapMode || enemyList.Count > 0))
            {
                spawnTimer -= dt;
                if (spawnTimer <= 0f && creepers.Count < 3)
                {
                    spawnTimer = UnityEngine.Random.Range(6f, 10f);
                    SpawnCreeper(cam);
                }
            }

            for (int i = creepers.Count - 1; i >= 0; i--)
            {
                var c = creepers[i];
                if (c.go == null) { creepers.RemoveAt(i); continue; }
                c.age += dt;

                Vector3 pos = c.go.transform.position;

                if (c.fuse < 0f)
                {
                    c.retarget -= dt;
                    if (!mapMode && (c.target == null || c.retarget <= 0f))
                    {
                        c.target = PickTarget(pos, cam);
                        c.retarget = 1f;
                    }

                    bool hasTarget = c.target != null;
                    float goalX = hasTarget ? c.target.position.x : playerPos.x;
                    float dx = goalX - pos.x;
                    float stopAt = hasTarget ? 1.8f * skinScale : 2.4f * skinScale;

                    if (Mathf.Abs(dx) > stopAt)
                    {
                        c.dir = dx >= 0f ? 1f : -1f;
                        pos.x += c.dir * 2.2f * skinScale * dt;
                        c.go.transform.position = pos;
                    }
                    else if (hasTarget || mapMode)
                    {
                        c.fuse = 0f;
                    }
                    else if (c.age > 90f)
                    {
                        Destroy(c.go);
                        creepers.RemoveAt(i);
                    }
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
                        Blast(c.go.transform.position + new Vector3(0f, skinScale, 0f));
                        Destroy(c.go);
                        creepers.RemoveAt(i);
                    }
                }
            }
        }

        // The explosion: a visual puff, plus real damage to enemies in levels.
        void Blast(Vector3 at)
        {
            Explode(at);
            if (!mapMode) DamageEnemiesAt(at, BlastRadius(), cfgCreeperDamage.Value);
        }

        void SpawnCreeper(Camera cam)
        {
            float half = cam.orthographicSize * cam.aspect;
            float side = UnityEngine.Random.value < 0.5f ? -1f : 1f;
            float x = cam.transform.position.x + side * (half + skinScale);

            var go = new GameObject("CupMineCreeper");
            go.layer = visLayer;
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
                go.layer = visLayer;
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
                go.layer = visLayer;
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

        // Stone and grass slots: place a solid block in front of the player, facing the way the player
        // looks. Place more at the same spot to stack them into steps. The oldest block goes when
        // there are 8. In levels the block gets colliders the game's player motor and enemies react to.
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

            // The block itself only draws. It stays on the player's draw layer so the camera shows it;
            // the solid parts are separate child objects on the game's Bounds_* layers.
            var go = new GameObject("CupMineBlock");
            go.layer = visLayer;
            var sr = go.AddComponent<SpriteRenderer>();
            sr.sprite = blockSprites[slot];
            sr.sortingLayerID = skinLayerId;
            sr.sortingOrder = skinOrder;
            go.transform.position = new Vector3(bx, by, 0f);
            go.transform.localScale = new Vector3(size, size, 1f);

            if (!mapMode) AddLevelColliders(go);

            puffs.Add(new Puff
            {
                go = go,
                sr = sr,
                vel = Vector3.zero,
                life = 25f,
                maxLife = 25f,
                gravity = 0f,
                tint = Color.white,
                hp = 3f
            });

            if (!mapMode)
            {
                StartCoroutine(LogGroundStateLater(0.6f));
                StartCoroutine(LogGroundStateLater(2.0f));
            }
        }

        // Evidence from Assembly-CSharp.dll (read by disassembling the methods):
        //  - LevelPlayerMotor's constructor sets wallMask = 1<<18 (Bounds_Walls), ceilingMask = 1<<19
        //    (Bounds_Ceiling) and groundMask = 1<<20 (Bounds_Ground).
        //  - Each physics tick HandleRaycasts box-casts left/right against wallMask, up against
        //    ceilingMask, and down against groundMask, and takes the highest ground hit that is not
        //    more than 20 units above the player. So a block is floor only if a solid collider on
        //    layer 20 sits under the player.
        //  - Every other object (enemy projectiles, enemies) decides what it touched from the TAG:
        //    AbstractCollidableObject.checkCollision tests "Wall", "Ceiling", "Ground", "Enemy",
        //    "EnemyProjectile", "Player", "PlayerProjectile".
        // So each solid part gets both the matching layer and the matching tag.
        int LayerOf(string name, int fallback)
        {
            int l = LayerMask.NameToLayer(name);
            return l >= 0 ? l : fallback;
        }

        void SetTagSafe(GameObject g, string tag)
        {
            try { g.tag = tag; } catch (Exception) { }
        }

        // Block = three non-trigger BoxCollider2Ds on three child objects so each mask finds one:
        // top on Bounds_Ground (floor), sides on Bounds_Walls, underside on Bounds_Ceiling.
        // The side collider stops a little below the top so a player standing on the block is not
        // pushed sideways by it. The block's pivot is its bottom centre, so local y runs 0..1.
        void AddLevelColliders(GameObject block)
        {
            int ground = LayerOf("Bounds_Ground", 20);
            int walls = LayerOf("Bounds_Walls", 18);
            int ceiling = LayerOf("Bounds_Ceiling", 19);

            GameObject top = MakeBlockPart(block, "CupMineBlockTop", ground, "Ground", new Vector2(1f, 1f), new Vector2(0f, 0.5f));
            GameObject side = MakeBlockPart(block, "CupMineBlockWall", walls, "Wall", new Vector2(1f, 0.8f), new Vector2(0f, 0.4f));
            MakeBlockPart(block, "CupMineBlockCeiling", ceiling, "Ceiling", new Vector2(1f, 1f), new Vector2(0f, 0.5f));

            // Sensors tell the block when an enemy attack touches it.
            AddSensor(top, block);
            AddSensor(side, block);

            Logger.LogInfo("Block colliders: ground layer " + ground + ", walls " + walls + ", ceiling " + ceiling +
                " tags=" + top.tag + "/" + side.tag + " blockPos=" + block.transform.position + " blockSize=" + block.transform.localScale.x.ToString("0.0"));
        }

        GameObject MakeBlockPart(GameObject block, string name, int layer, string tag, Vector2 size, Vector2 offset)
        {
            var g = new GameObject(name);
            g.transform.SetParent(block.transform, false);
            g.layer = layer;
            SetTagSafe(g, tag);
            var bc = g.AddComponent<BoxCollider2D>();
            bc.size = size;
            bc.offset = offset;
            bc.isTrigger = false;
            return g;
        }

        void AddSensor(GameObject part, GameObject block)
        {
            var sn = part.AddComponent<BlockSensor>();
            sn.block = block;
        }

        // Unity calls these when something touches the block's colliders.
        public class BlockSensor : MonoBehaviour
        {
            public GameObject block;

            void OnTriggerEnter2D(Collider2D other)
            {
                if (Instance != null && other != null) Instance.BlockTouched(block, other.gameObject);
            }

            void OnCollisionEnter2D(Collision2D hit)
            {
                if (Instance != null && hit != null && hit.collider != null) Instance.BlockTouched(block, hit.collider.gameObject);
            }
        }

        // The game tags enemy attacks "EnemyProjectile" (see checkCollision). The tag may sit on the
        // object or on one of its parents.
        bool IsEnemyShot(GameObject g)
        {
            Transform t = g != null ? g.transform : null;
            for (int i = 0; i < 3 && t != null; i++)
            {
                try { if (t.gameObject.CompareTag("EnemyProjectile")) return true; } catch (Exception) { }
                t = t.parent;
            }
            return false;
        }

        void BlockTouched(GameObject blockGo, GameObject other)
        {
            if (blockGo == null || other == null) return;
            if (!IsEnemyShot(other)) return;
            DamageBlock(blockGo, other.name);
        }

        // One hit counts per short cooldown, so the sensor and the poll below never double count.
        void DamageBlock(GameObject blockGo, string by)
        {
            foreach (var p in puffs)
            {
                if (p.go != blockGo) continue;
                if (p.hp <= 0f || p.hitCool > 0f) return;
                p.hitCool = 0.12f;
                p.hp -= 1f;
                Logger.LogInfo("Block hit by " + by + ", hp left " + p.hp);
                if (p.hp <= 0f)
                {
                    Explode(blockGo.transform.position + new Vector3(0f, 0.5f * blockGo.transform.localScale.x, 0f));
                    p.life = 0f;
                }
                return;
            }
        }

        // Backup for the sensors: look for enemy attacks overlapping each block.
        float blockPollT;
        void TickBlockHits()
        {
            blockPollT -= Time.deltaTime;
            if (blockPollT > 0f) return;
            blockPollT = 0.05f;
            if (mapMode) return;
            int projMask = (1 << LayerOf("Projectile", 12)) | (1 << LayerOf("ProjectileNoClip", 13));
            for (int i = 0; i < puffs.Count; i++)
            {
                var p = puffs[i];
                if (p.go == null || p.go.name != "CupMineBlock" || p.hp <= 0f) continue;
                Vector3 pos = p.go.transform.position;
                float sz = p.go.transform.localScale.x;
                Collider2D[] hits = Physics2D.OverlapAreaAll(
                    new Vector2(pos.x - 0.6f * sz, pos.y - 0.1f * sz),
                    new Vector2(pos.x + 0.6f * sz, pos.y + 1.1f * sz), projMask);
                foreach (var h in hits)
                {
                    if (h == null || !IsEnemyShot(h.gameObject)) continue;
                    DamageBlock(p.go, h.gameObject.name);
                    break;
                }
            }
        }

        // Diagnostics: after a block is placed, report what the player's own motor sees below it.
        // Reads LevelPlayerMotor (Grounded, groundMask, directionManager.down) through reflection;
        // all of these names exist in Assembly-CSharp.dll.
        System.Collections.IEnumerator LogGroundStateLater(float delay)
        {
            yield return new WaitForSeconds(delay);
            if (skinned.Count == 0 || skinned[0].root == null) yield break;
            try
            {
                GameObject root = skinned[0].root.gameObject;
                Type mt = FindType("LevelPlayerMotor");
                if (mt == null) { Logger.LogInfo("Diag: LevelPlayerMotor type not found"); yield break; }
                Component motor = root.GetComponentInChildren(mt);
                if (motor == null) { Logger.LogInfo("Diag: no LevelPlayerMotor on player"); yield break; }
                object grounded = CallGet(motor, "Grounded");
                FieldInfo gm = mt.GetField("groundMask", BF);
                object gmask = gm != null ? gm.GetValue(motor) : null;
                string downInfo = "n/a";
                FieldInfo dmf = mt.GetField("directionManager", BF);
                if (dmf != null)
                {
                    object dm = dmf.GetValue(motor);
                    FieldInfo df = dm != null ? dm.GetType().GetField("down", BF) : null;
                    object hit = df != null ? df.GetValue(dm) : null;
                    if (hit != null)
                    {
                        Type ht = hit.GetType();
                        object able = ht.GetField("able", BF).GetValue(hit);
                        object dist = ht.GetField("distance", BF).GetValue(hit);
                        GameObject hg = ht.GetField("gameObject", BF).GetValue(hit) as GameObject;
                        downInfo = "able=" + able + " distance=" + dist + " object=" +
                            (hg != null ? hg.name + " layer=" + hg.layer + " tag=" + hg.tag : "none");
                    }
                }
                Collider2D pc = root.GetComponentInChildren<Collider2D>();
                string pcInfo = pc != null
                    ? pc.GetType().Name + " layer=" + pc.gameObject.layer + " trigger=" + pc.isTrigger + " bounds=" + pc.bounds.min + ".." + pc.bounds.max
                    : "none";
                Logger.LogInfo("Diag after block: Grounded=" + grounded + " groundMask=" + gmask + " down{" + downInfo + "} playerPos=" +
                    root.transform.position + " playerCollider{" + pcInfo + "}");
            }
            catch (Exception e)
            {
                Logger.LogWarning("Ground diagnostics failed: " + e.Message);
            }
        }

        // Creeper blast damage through the game's own damage system (Assembly-CSharp.dll):
        //   DamageReceiver.TakeDamage(DamageDealer.DamageInfo)   -> raises OnDamageTaken(info)
        //   DamageDealer.DamageInfo(float damage, Direction, Vector2 origin, DamageSource)
        //   AbstractPlatformingLevelEnemy.OnDamageTaken: Health -= info.damage; Die() at 0 or below.
        // DamageReceiver.type is Enemy (0), Player (1) or Other (2); only Enemy receivers are hurt.
        void DamageEnemiesAt(Vector3 at, float radius, float amount)
        {
            try
            {
                Type recvT = FindType("DamageReceiver");
                Type infoT = FindType("DamageDealer+DamageInfo");
                Type childT = FindType("DamageReceiverChild");
                if (recvT == null || infoT == null) { Logger.LogInfo("Damage: types not found"); return; }
                MethodInfo take = recvT.GetMethod("TakeDamage", BF);
                FieldInfo typeF = recvT.GetField("type", BF);
                ConstructorInfo ci = null;
                foreach (var c in infoT.GetConstructors(BF))
                {
                    if (c.GetParameters().Length == 4) { ci = c; break; }
                }
                if (take == null || typeF == null || ci == null) { Logger.LogInfo("Damage: members not found"); return; }

                var done = new List<Component>();
                int hurt = 0;

                // Enemies whose collider the blast circle overlaps.
                Collider2D[] cols = Physics2D.OverlapCircleAll(new Vector2(at.x, at.y), radius);
                foreach (var col in cols)
                {
                    if (col == null) continue;
                    Component r = col.GetComponentInParent(recvT);
                    if (r == null && childT != null)
                    {
                        Component ch = col.GetComponentInParent(childT);
                        if (ch != null) r = CallGet(ch, "receiver") as Component;
                    }
                    if (HurtReceiver(r, typeF, take, ci, at, amount, done)) hurt++;
                }

                // Enemies whose centre is inside the blast even if no collider overlapped it.
                for (int i = 0; i < enemyList.Count; i++)
                {
                    Component r = enemyList[i];
                    if (r == null) continue;
                    Vector3 p = r.transform.position;
                    if (Vector2.Distance(new Vector2(p.x, p.y), new Vector2(at.x, at.y)) > radius) continue;
                    if (HurtReceiver(r, typeF, take, ci, at, amount, done)) hurt++;
                }

                Logger.LogInfo("Creeper blast at " + at + " radius " + radius.ToString("0") + " hit " + hurt + " enemies (colliders " + cols.Length + ", known enemies " + enemyList.Count + ")");
            }
            catch (Exception e)
            {
                Logger.LogWarning("Creeper damage failed: " + (e.InnerException != null ? e.InnerException.Message : e.Message));
            }
        }

        bool HurtReceiver(Component r, FieldInfo typeF, MethodInfo take, ConstructorInfo ci, Vector3 at, float amount, List<Component> done)
        {
            if (r == null || done.Contains(r)) return false;
            Behaviour bh = r as Behaviour;
            if (bh != null && !bh.enabled) return false;
            if (Convert.ToInt32(typeF.GetValue(r)) != 0) return false; // 0 = Enemy
            done.Add(r);
            ParameterInfo[] ps = ci.GetParameters();
            object info = ci.Invoke(new object[]
            {
                amount,
                Enum.ToObject(ps[1].ParameterType, 0),   // Direction.Neutral
                (Vector2)at,
                Enum.ToObject(ps[3].ParameterType, 0)    // DamageSource.Neutral
            });
            take.Invoke(r, new object[] { info });
            Logger.LogInfo("Creeper damaged " + r.gameObject.name + " for " + amount);
            return true;
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
                    Camera cam = GetCam();
                    if (cam != null && creepers.Count < 6) SpawnCreeper(cam);
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
                if (p.hitCool > 0f) p.hitCool -= dt;
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
