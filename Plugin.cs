using BepInEx;
using BepInEx.Configuration;
using UnityEngine;

namespace MonkeFur
{
    [BepInPlugin(Guid, Name, Version)]
    public sealed class Plugin : BaseUnityPlugin
    {
        public const string Guid = "com.monkefur.gorillatag";
        public const string Name = "MonkeFur";
        public const string Version = "1.0.0";

        internal static Plugin Instance { get; private set; }

        internal ConfigEntry<bool> Enabled;
        internal ConfigEntry<KeyCode> ToggleKey;

        internal ConfigEntry<int> StrandCount;
        internal ConfigEntry<int> GuideCount;
        internal ConfigEntry<int> Segments;
        internal ConfigEntry<int> BladesPerStrand;
        internal ConfigEntry<bool> DoubleSided;
        internal ConfigEntry<string> FurSubmeshes;
        internal ConfigEntry<int> Seed;
        internal ConfigEntry<bool> HeadFurInFirstPerson;
        internal ConfigEntry<float> HeadClearRadius;

        internal ConfigEntry<float> FurLength;
        internal ConfigEntry<float> LengthVariation;
        internal ConfigEntry<float> StrandWidth;
        internal ConfigEntry<float> TipWidth;
        internal ConfigEntry<float> Tilt;

        internal ConfigEntry<float> Stiffness;
        internal ConfigEntry<float> Damping;
        internal ConfigEntry<float> GravityScale;
        internal ConfigEntry<float> Inertia;
        internal ConfigEntry<float> WorldDrag;
        internal ConfigEntry<float> MinLift;
        internal ConfigEntry<float> TeleportDistance;

        private FurController controller;

        private void Awake()
        {
            Instance = this;

            Enabled = Config.Bind("General", "Enabled", true, "Show fur on your gorilla.");
            ToggleKey = Config.Bind("General", "ToggleKey", KeyCode.F8, "Keyboard key that toggles the fur on/off.");

            StrandCount = Config.Bind("Shape", "StrandCount", 10000,
                new ConfigDescription("Number of rendered fur strands. These are skinned on the GPU, so they cost almost no CPU.", new AcceptableValueRange<int>(100, 60000)));
            GuideCount = Config.Bind("Shape", "GuideCount", 300,
                new ConfigDescription("Strands that actually run physics. Every other strand copies the motion of its nearest guide. This is the CPU cost knob.", new AcceptableValueRange<int>(10, 5000)));
            Segments = Config.Bind("Shape", "Segments", 3,
                new ConfigDescription("Physics joints per strand. More = bendier fur.", new AcceptableValueRange<int>(1, 8)));
            BladesPerStrand = Config.Bind("Shape", "BladesPerStrand", 2,
                new ConfigDescription("1 = flat strands, 2 = crossed strands (look full from every angle and in mirrors).", new AcceptableValueRange<int>(1, 2)));
            DoubleSided = Config.Bind("Shape", "DoubleSided", true, "Render strands from both sides (the body shader culls back faces).");
            FurSubmeshes = Config.Bind("Shape", "FurSubmeshes", "0",
                "Comma-separated body submesh indices that grow fur. 0 = main colored body, 1 = chest/face patch.");
            Seed = Config.Bind("Shape", "Seed", 1337, "Random seed for strand placement.");
            HeadFurInFirstPerson = Config.Bind("Shape", "HeadFurInFirstPerson", false,
                "If false, head/neck fur only renders in mirrors and cameras so it never blocks your view.");
            HeadClearRadius = Config.Bind("Shape", "HeadClearRadius", 0.22f,
                "Strands rooted within this distance (meters, at normal size) of your eyes count as head fur.");

            FurLength = Config.Bind("Look", "FurLength", 0.045f,
                new ConfigDescription("Strand length in meters at normal size.", new AcceptableValueRange<float>(0.005f, 0.5f)));
            LengthVariation = Config.Bind("Look", "LengthVariation", 0.4f,
                new ConfigDescription("Random length spread (0 = all equal).", new AcceptableValueRange<float>(0f, 0.95f)));
            StrandWidth = Config.Bind("Look", "StrandWidth", 0.011f,
                new ConfigDescription("Strand width at the root in meters.", new AcceptableValueRange<float>(0.001f, 0.1f)));
            TipWidth = Config.Bind("Look", "TipWidth", 0.15f,
                new ConfigDescription("Tip width as a fraction of root width.", new AcceptableValueRange<float>(0f, 1f)));
            Tilt = Config.Bind("Look", "Tilt", 0.35f,
                new ConfigDescription("How much strands lean away from the surface normal at rest (messier fur).", new AcceptableValueRange<float>(0f, 2f)));

            Stiffness = Config.Bind("Physics", "Stiffness", 0.22f,
                new ConfigDescription("How strongly strands spring back to their rest shape.", new AcceptableValueRange<float>(0f, 1f)));
            Damping = Config.Bind("Physics", "Damping", 0.9f,
                new ConfigDescription("Velocity kept per 1/60 s. Lower = calmer, higher = bouncier.", new AcceptableValueRange<float>(0f, 0.999f)));
            GravityScale = Config.Bind("Physics", "GravityScale", 0.35f,
                new ConfigDescription("How much gravity pulls the fur down.", new AcceptableValueRange<float>(0f, 3f)));
            Inertia = Config.Bind("Physics", "Inertia", 0.6f,
                new ConfigDescription("How much fur swings from motion relative to your body (arm swings, jiggle). 0 = rigid, 1 = fully lags.", new AcceptableValueRange<float>(0f, 1f)));
            WorldDrag = Config.Bind("Physics", "WorldDrag", 0.05f,
                new ConfigDescription("How much fur trails behind when your whole gorilla moves or turns (running, flinging). 0 = none.", new AcceptableValueRange<float>(0f, 1f)));
            MinLift = Config.Bind("Physics", "MinLift", 0.15f,
                new ConfigDescription("Keeps strands from folding into the skin (fraction of height above the surface).", new AcceptableValueRange<float>(0f, 1f)));
            TeleportDistance = Config.Bind("Physics", "TeleportDistance", 1.0f,
                "If a strand root jumps farther than this in one frame (teleports, respawns), the strand snaps instead of flailing.");

            Config.SettingChanged += OnSettingChanged;

            var go = new GameObject("MonkeFur");
            DontDestroyOnLoad(go);
            go.hideFlags = HideFlags.HideAndDontSave;
            controller = go.AddComponent<FurController>();

            Logger.LogInfo($"{Name} {Version} loaded.");
        }

        private void OnSettingChanged(object sender, SettingChangedEventArgs e)
        {
            if (controller == null) return;
            string section = e.ChangedSetting.Definition.Section;
            if (section == "Shape" || section == "Look") controller.RequestRebuild();
        }

        internal static void Log(string msg) => Instance?.Logger.LogInfo(msg);
        internal static void Warn(string msg) => Instance?.Logger.LogWarning(msg);
        internal static void Error(string msg) => Instance?.Logger.LogError(msg);
    }
}
