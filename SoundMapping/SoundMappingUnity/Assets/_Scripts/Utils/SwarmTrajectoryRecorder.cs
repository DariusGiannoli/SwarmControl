// SwarmTrajectoryRecorder.cs
// Records swarm trajectories into JSON, labels "main group" per sample using your runtime network,
// supports adjustable recording frequency, safely saves on scene changes / quit, and
// stores a single "Run" timing window (start/stop) for downstream analysis.
// NOW also labels the embodied drone both in-file (embodiedId/embodiedName) and per-frame flag 'e'.
//
// Call from other scripts:
//   SwarmTrajectoryRecorder.MarkTrialStart("Run");  // level/timer starts
//   SwarmTrajectoryRecorder.MarkTrialStop("Run");   // level/timer ends
//
// Attach this to a persistent GameObject (e.g., in Setup).


using System;
using System.Collections.Generic;
using System.IO;
using System.Text.RegularExpressions;
using UnityEngine;
using UnityEngine.SceneManagement;


#if UNITY_EDITOR
using UnityEditor;
#endif


public class SwarmTrajectoryRecorder : MonoBehaviour
{
    // -------------------- Configuration --------------------
    [Header("Swarm root discovery")]
    public string swarmRootTag = "Swarm";
    public string swarmRootName = "Swarm";
    [Tooltip("How often to re-check child count and rediscover drones (sec). 0 = never.")]
    public float rescanChildrenEverySec = 1f;


    [Header("Drone discovery")]
    [Tooltip("Type name of a component on each drone (e.g., 'DroneController'). Empty = any Transform child.")]
    public string droneComponentTypeName = "DroneController";


    [Header("Sampling")]
    [Tooltip("Samples per second (<=0 = every Update).")]
    public float sampleHz = 15f;
    [Tooltip("Wait to start sampling until at least one drone is found.")]
    public bool waitForDronesToStart = true;


    [Header("Recording frequency")]
    [Tooltip("Records per second (independent of sampleHz). 0 = record every sample.")]
    public float recordHz = 0f;
    [Tooltip("If recordHz==0, record every Nth sample (1 = every sample).")]
    public int recordEveryNthSample = 1;


    [Header("Lifecycle")]
    [Tooltip("Keep this recorder across scene loads.")]
    public bool dontDestroyOnLoad = true;
    [Tooltip("If true, keep trying to discover drones after start.")]
    public bool lazyDiscover = true;


    [Header("Output location")]
    [Tooltip("Subfolder under PID folder.")]
    public string outSubfolder = "Trajectories";
    [Tooltip("Override participant/session id.")]
    public string pidOverride = "";


    [Header("Output naming")]
    [Tooltip("Override scene label in filename (otherwise inferred).")]
    public string sceneLabelOverride = "";
    [Tooltip("Name of the setup scene (only used to disambiguate labels).")]
    public string setupSceneName = "Scene Selector";
    [Tooltip("Custom prefix for the output filename. If empty, uses default naming.")]
    public string customFilePrefix = "";
    [Tooltip("Show a startup prompt that writes into customFilePrefix before saving.")]
    public bool promptForFilePrefixOnStart = true;
    [Tooltip("Pause gameplay while the startup filename prompt is open.")]
    public bool pauseWhileFilePrefixPromptOpen = true;


    [Header("Quality of life")]
    [Tooltip("Autosave interval in seconds. 0 = off.")]
    public float autosaveEverySec = 0f;
    [Tooltip("Enable F7 hotkey to save immediately.")]
    public bool enableHotkeySave = true;


    [Header("Main group selection")]
    [Tooltip("Use runtime network (swarmModel.network) to choose main group.")]
    public bool useNetworkForMainGroup = true;
    [Tooltip("If network is unavailable this frame, include everyone (true) or use proximity fallback (false).")]
    public bool includeAllUntilNetworkReady = true;


    [Header("Proximity fallback (only if network unavailable and includeAllUntilNetworkReady=false)")]
    [Tooltip("Meters: drones closer than this are linked into the same group.")]
    public float linkDistance = 3f;
    [Tooltip("Smallest cluster that qualifies as the 'main group'.")]
    public int minMainGroupSize = 3;
    [Tooltip("Use XZ-plane distance for grouping (recommended). If false, use full 3D distance.")]
    public bool useXZDistance = true;


    [Header("Run labeling")]
    [Tooltip("Label name to enforce single-run policy on.")]
    public string runLabelName = "Run";
    [Tooltip("If true, only one 'Run' window (start->stop) will be recorded per file.")]
    public bool singleRunMode = true;


    [Header("Input Logging")]
    [Tooltip("InputFusionManager — every input source is read through this each sample. Auto-found if empty.")]
    public InputFusionManager fusionManager;
    [Tooltip("WebSocketClient — logs raw values from the Python tracker. Auto-found if empty. Optional.")]
    public WebSocketClient webSocketClient;
    [Tooltip("If false, the inputs[] block is omitted from saved files (saves disk and JSON parse time).")]
    public bool logInputs = true;


    [Header("Embodied drone labeling")]
    [Tooltip("If set, this transform is treated as the embodied drone.")]
    public Transform embodiedDroneOverride;
    [Tooltip("Fallback: try to find an object with this tag.")]
    public string embodiedTag = "Embodied";
    [Tooltip("Fallback: try to find an object with this exact name.")]
    public string embodiedName = "EmbodiedDrone";
    [Tooltip("If your drone component (e.g., DroneController) has a bool flag like 'isEmbodied'/'IsEmbodied', we'll use it.")]
    public string embodiedFlagFieldOrProperty = "isEmbodied";
    public string embodiedFlagAltProperty = "IsEmbodied";


    // -------------------- Internals --------------------
    public Transform swarmRoot;
    private readonly Dictionary<int, DroneTraj> _trajById = new Dictionary<int, DroneTraj>();
    private readonly List<Transform> _droneTransforms = new List<Transform>();


    private float _accum, _discoverTimer, _swarmFindTimer, _childrenRescanTimer, _autosaveTimer;
    private int _lastChildCount = -1;
    private bool _samplingEnabled;


    // Recording schedule
    private float _recordAccum = 0f;
    private int _sampleIndex = 0;


    // Singleton + save debounce
    private static SwarmTrajectoryRecorder _instance;
    private enum SaveReason { Auto, Manual, Final }
    private bool _finalized;
    private float _lastSaveRealtime;
    private const float SaveDebounceSec = 0.25f;


    // Scene/file labeling
    private string _sceneLabelForThisRun = null;
    private bool _justClearedForNewScene = false;
    private bool _filePrefixPromptOpen;
    private bool _filePrefixPromptFocused;
    private string _pendingFilePrefix = "";
    private float _timeScaleBeforeFilePrefixPrompt = 1f;
    private int _filePrefixPromptOpenedFrame = -1;
    private string _recorderHostSceneName = "";
    private string _resolvedCustomFileName = null;
    private string _resolvedCustomFileNameKey = null;
    private string _resolvedCustomFileNameRoot = null;


    // Coroutine to ensure swarm exists after scene load
    private Coroutine _ensureSwarmCoro;


    // --- Single-run state ---
    private bool _runFinalized = false;     // a 'Run' window has been closed in this file


    // --- Embodied detection cache ---
    private Transform _embodiedTransform;
    private int _embodiedStableId = int.MinValue;
    
    // --- Collectibles tracking ---
    private static int _collectiblesCounter = 0;
    private readonly List<CollectibleEvent> _collectibleBuffer = new List<CollectibleEvent>();

    // --- Crash event buffer ---
    private readonly List<CrashEvent> _crashBuffer = new List<CrashEvent>();

    // --- Input snapshot buffer ---
    private readonly List<InputFrame> _inputBuffer = new List<InputFrame>();

    // --- Per-frame swarm-wide snapshot buffer ---
    private readonly List<SwarmFrame> _swarmFrameBuffer = new List<SwarmFrame>();

    // --- Per-frame subnetwork snapshot buffer (one row per component per sample) ---
    private readonly List<SubnetSnapshot> _subnetBuffer = new List<SubnetSnapshot>();

    // --- One-shot scene snapshot, captured on first MarkTrialStart ---
    private int _totalCollectibles = 0;
    private List<ObstacleInfo> _obstacleSnapshot = new List<ObstacleInfo>();
    private List<CourseLineInfo> _courseLineSnapshot = new List<CourseLineInfo>();
    private bool _sceneSnapshotTaken = false;

    // Cached MigrationPointController, resolved lazily.
    private MigrationPointController _migrationController;


    // -------------------- Data types --------------------
    [Serializable]
    public struct TrajFrame
    {
        public float t;
        public float x, y, z;
        public float vx, vy, vz;   // velocity (world-space, m/s) — sourced from DroneFake.velocity
        public float sc;           // score in [0,1]: 1 if in main network, 0 otherwise (per DroneFake.score)
        public byte l;             // network layer (DroneFake.layer; 1=embodied, 2=neighbor, ...)
        public byte g;             // 0 = not in main group; 1 = in main group
        public byte e;             // 1 = embodied drone at this frame, 0 = not embodied
    }

    /// <summary>
    /// Per-frame swarm-wide snapshot. Time-aligned with `TrajFrame.t` and `InputFrame.t`.
    /// Lets analysis ask questions about the swarm as a whole (e.g. when did the swarm
    /// split, how many drones were disconnected from the main group at time t, etc.)
    /// without having to re-derive everything from the per-drone arrays.
    /// </summary>
    [Serializable]
    public struct SwarmFrame
    {
        public float t;
        public int n;                 // active drone count (children of swarmHolder)
        public int nMain;             // size of the largest connected component (main group)
        public int nDisc;             // drones NOT in the main group at this instant
        public int nCrash;            // cumulative crash count so far (Timer.numberDroneDied)
        public int nSub;              // number of disconnected subnetworks (gap count = nSub-1)
        public int colRemain;         // collectibles (tag "Collectibles") still present in scene
        public float mx, mz;          // migrationPoint XZ
        public float avx, avy, avz;   // alignementVector (target direction)
        public float spreadCur;       // current desired separation
        public float spreadMin;       // runtime lower bound
        public float spreadMax;       // runtime upper bound
        public float tElapsed;        // Timer.elapsedTime (study clock)
    }

    /// <summary>
    /// One row per subnetwork per sample. `t` is the sample time, `idx` is the rank
    /// of this subnetwork when sorted by size (0 = main group), `size` is its drone
    /// count, `cx/cy/cz` is its centroid, `extent` is the max distance from any
    /// member to the centroid (a crude "gap size" proxy). When the swarm is fully
    /// connected there is exactly one row per t; when it splits, you get one per
    /// component, so the gap geometry is recoverable.
    /// </summary>
    [Serializable]
    public struct SubnetSnapshot
    {
        public float t;
        public int idx;
        public int size;
        public float cx, cy, cz;
        public float extent;
    }

    /// <summary>
    /// One-shot dump of a scene-static obstacle. Captured once at the first
    /// MarkTrialStart so analysis knows the level layout without having to load
    /// the .unity file. Box/cylinder rotation is encoded as a quaternion (qx..qw).
    /// </summary>
    [Serializable]
    public struct ObstacleInfo
    {
        public string type;
        public float cx, cy, cz;
        public float radius;
        public float sx, sy, sz;
        public float height;
        public float qx, qy, qz, qw;
    }

    [Serializable]
    public struct CourseLineInfo
    {
        public string role;     // "start" or "end"
        public string name;     // scene object name, e.g. Starting_line
        public float cx, cy, cz;
        public float sx, sy, sz;
        public float qx, qy, qz, qw;
    }


    [Serializable]
    public class DroneTraj
    {
        public int id;
        public string name;
        public List<TrajFrame> frames = new List<TrajFrame>(4096);
    }


    /// <summary>
    /// One crash event. Captured at the moment DroneController.crash() fires,
    /// before the drone is removed from the swarm so the position is still valid.
    /// `embodied` lets you separate "the participant's drone crashed" from
    /// "a peripheral swarm drone crashed" without joining against `embodiedId`.
    /// </summary>
    [Serializable]
    public struct CrashEvent
    {
        public float t;            // Time.time at crash
        public float x, y, z;      // world position of the drone at crash
        public int droneId;        // matches DroneTraj.id
        public string droneName;
        public byte embodied;      // 1 if this drone was the embodied one at crash time
    }

    /// <summary>
    /// One collectible pickup event. Captured at the moment a collectible trigger
    /// fires, before the collectible is destroyed.
    /// </summary>
    [Serializable]
    public struct CollectibleEvent
    {
        public float t;            // Time.time at pickup
        public float tElapsed;     // Timer.elapsedTime at pickup
        public string name;        // collectible GameObject name, e.g. Star_Gap (4)_5_5
        public int droneId;        // drone that triggered the pickup
        public float x, y, z;      // world position of the collectible at pickup
    }


    /// <summary>
    /// Per-frame snapshot of every input signal flowing into the swarm.
    /// Sampled at the same cadence as TrajFrame so analysis can join on `t`.
    /// Unavailable / unassigned sources record NaN so pandas can distinguish
    /// "source not present" from "source present, reading 0".
    ///
    /// Field naming: `f*` = fused output (what InputFusionManager actually
    /// produced this frame). Source-prefixed fields are raw per-source readings.
    /// </summary>
    [Serializable]
    public struct InputFrame
    {
        public float t;

        // Fused outputs (what drove the swarm this frame)
        public float fmx, fmy, fmz;   // SwarmMovement (XYZ)
        public float fs;              // SwarmSpread
        public float fr;              // CameraRotation
        public byte sabs;             // 1 if spread is absolute target, 0 if rate

        // TraditionalInput (joystick / keyboard)
        public float trad_mx, trad_my;  // MovementInput.x, .y
        public float trad_h;            // HeightInput
        public float trad_s;            // SpreadInput
        public float trad_r;            // RotationInput

        // ChestIMU
        public float imu_mx, imu_mz;  // IMUMovementInput.MovementVector.x, .z
        public float imu_y;           // IMUYawInput.YawRotationRate

        // ArmIMU (forearm sensors); reflects HandIMUFusion when present
        public float arm_h, arm_s;    // HeightControl / SpreadControl

        // Meta Quest
        public float mq_y;            // HeadsetYawInput.RotationControl
        public float mqh_h, mqh_s;    // HandHeightInput / HandSpreadInput
        public float ctl_h, ctl_s;    // ControllerHeightInput / ControllerSpreadInput

        // Webcam pose tracking — Unity-side processed values
        public float pose_h, pose_s, pose_y;
        // Raw WebSocket values straight from Python (pre-Unity processing)
        public float ws_d, ws_h, ws_y;
    }


    [Serializable]
    public class TrialWindow
    {
        public string label;        // e.g., "Run", "Network"
        public float startGameTime; // Time.time when started
        public float startRealtime; // Time.realtimeSinceStartup when started
        public float endGameTime;   // Time.time when ended (0 if still open)
        public float endRealtime;   // Time.realtimeSinceStartup when ended (0 if open)
    }


    [Serializable]
    public class TrajectoryLog
    {
        public string scene;   // active scene at save time (for reference)
        public string pid;
        public string haptics; // "H" / "NH"
        public string order;   // "O" / "NO"
        public float sampleHz; // sampling cadence (record cadence implied by data)
        public List<DroneTraj> trajectories = new List<DroneTraj>();
        public List<TrialWindow> trials = new List<TrialWindow>(); // timing windows


        // --- NEW: embodied metadata written once per file ---
        public int embodiedId;        // -2147483648 (int.MinValue) if unknown
        public string embodiedName;   // empty if unknown
        
        // --- Collectibles and elapsed time ---
        public int collectiblesPickedUp;  // total collectibles collected during run
        public float elapsedTime;         // time between start and end collider (seconds)
        public List<CollectibleEvent> collectibles = new List<CollectibleEvent>();

        // --- Crashes ---
        public int crashCount;                  // total crashes during the run (== crashes.Count)
        public List<CrashEvent> crashes = new List<CrashEvent>();

        // --- Per-frame input snapshots ---
        // Time-aligned with `trajectories[].frames`: same `t` axis, same cadence
        // (governed by sampleHz / recordHz). Empty when logInputs is false.
        public List<InputFrame> inputs = new List<InputFrame>();

        // --- Per-frame swarm-wide snapshots (subnetwork count, main group size,
        // migration target, spread bounds, timer). Time-aligned with `inputs`.
        public List<SwarmFrame> swarmFrames = new List<SwarmFrame>();

        // --- Per-frame subnetwork rows ---
        // One row per disconnected component per sample, so analysis can recover
        // "gap geometry" without re-running the union-find offline.
        public List<SubnetSnapshot> subnetworks = new List<SubnetSnapshot>();

        // --- Total collectibles in the scene at run start ---
        // collectiblesPickedUp / totalCollectibles gives the completion ratio.
        public int totalCollectibles;

        // --- Scene-static obstacles, captured once at the start of the run ---
        public List<ObstacleInfo> obstacles = new List<ObstacleInfo>();
        public List<CourseLineInfo> courseLines = new List<CourseLineInfo>();
    }


    // Trial buffers
    private readonly List<TrialWindow> _trialsBuffer = new List<TrialWindow>();
    private TrialWindow _openTrial = null;


    // -------------------- Unity lifecycle --------------------
    private void Awake()
    {
        if (_instance != null && _instance != this) { Destroy(gameObject); return; }
        _instance = this;

        _recorderHostSceneName = gameObject.scene.name;

        if (dontDestroyOnLoad) DontDestroyOnLoad(gameObject);
        SceneManager.sceneLoaded   += OnSceneLoaded;
        SceneManager.sceneUnloaded += OnSceneUnloaded; // save AFTER the old scene truly unloads


        _accum = _autosaveTimer = 0f;
        _recordAccum = 0f;
        _sampleIndex = 0;

        OpenFilePrefixPromptIfNeeded();
    }


    private void OnDestroy()
    {
        CloseFilePrefixPrompt(false);
        if (_instance == this) _instance = null;
        SceneManager.sceneLoaded   -= OnSceneLoaded;
        SceneManager.sceneUnloaded -= OnSceneUnloaded;
    }


    private void OnEnable()
    {
        TryFindSwarmRootNow();
        CollectDrones();
        RefreshEmbodiedLabeling();
        EnsureInputRefs();
        _samplingEnabled = !waitForDronesToStart || _droneTransforms.Count > 0;
    }


    private void OnGUI()
    {
        if (!_filePrefixPromptOpen) return;

        const float width = 420f;
        const float height = 170f;
        Rect windowRect = new Rect(
            (Screen.width - width) * 0.5f,
            (Screen.height - height) * 0.5f,
            width,
            height);

        GUI.ModalWindow(GetInstanceID(), windowRect, DrawFilePrefixPrompt, "Trajectory file name");
    }


    private void DrawFilePrefixPrompt(int windowId)
    {
        Event currentEvent = Event.current;
        bool submittedByKey = currentEvent != null
            && currentEvent.type == EventType.KeyDown
            && Time.frameCount > _filePrefixPromptOpenedFrame + 1
            && (currentEvent.keyCode == KeyCode.Return || currentEvent.keyCode == KeyCode.KeypadEnter);

        GUILayout.Space(8f);
        GUILayout.Label("Enter the file name to save this trajectory.");
        GUILayout.Label("Leave empty to use the default recorder name.");

        GUI.SetNextControlName("TrajectoryFilePrefixInput");
        _pendingFilePrefix = GUILayout.TextField(_pendingFilePrefix ?? string.Empty, 128);

        if (!_filePrefixPromptFocused)
        {
            GUI.FocusControl("TrajectoryFilePrefixInput");
            _filePrefixPromptFocused = true;
        }

        GUILayout.Space(12f);
        GUILayout.BeginHorizontal();
        if (GUILayout.Button("Use Default", GUILayout.Height(32f)))
        {
            _pendingFilePrefix = string.Empty;
            CloseFilePrefixPrompt(true);
        }
        if (GUILayout.Button("Start", GUILayout.Height(32f)) || submittedByKey)
        {
            CloseFilePrefixPrompt(true);
        }
        GUILayout.EndHorizontal();

        if (submittedByKey) currentEvent.Use();
    }


    private void OpenFilePrefixPromptIfNeeded()
    {
        if (!promptForFilePrefixOnStart || _filePrefixPromptOpen) return;

        _pendingFilePrefix = !string.IsNullOrEmpty(customFilePrefix)
            ? customFilePrefix
            : ResolveLatestDataFilePrefix();
        _filePrefixPromptOpen = true;
        _filePrefixPromptFocused = false;
        _filePrefixPromptOpenedFrame = Time.frameCount;

        if (pauseWhileFilePrefixPromptOpen)
        {
            _timeScaleBeforeFilePrefixPrompt = Time.timeScale;
            Time.timeScale = 0f;
        }
    }


    private void CloseFilePrefixPrompt(bool applyValue)
    {
        if (!_filePrefixPromptOpen) return;

        if (applyValue)
        {
            string trimmedPrefix = (_pendingFilePrefix ?? string.Empty).Trim();
            customFilePrefix = string.IsNullOrEmpty(trimmedPrefix) ? string.Empty : trimmedPrefix;
            _resolvedCustomFileName = null;
            _resolvedCustomFileNameKey = null;
            _resolvedCustomFileNameRoot = null;
        }

        _filePrefixPromptOpen = false;
        _filePrefixPromptFocused = false;
        _filePrefixPromptOpenedFrame = -1;

        if (pauseWhileFilePrefixPromptOpen)
        {
            Time.timeScale = _timeScaleBeforeFilePrefixPrompt;
        }
    }

    /// <summary>
    /// Resolve InputFusionManager and WebSocketClient if the Inspector left them
    /// empty. Cheap to call repeatedly; bails early once both are set.
    /// </summary>
    private void EnsureInputRefs()
    {
        if (fusionManager == null) fusionManager = FindObjectOfType<InputFusionManager>();
        if (webSocketClient == null) webSocketClient = FindObjectOfType<WebSocketClient>();
    }


    private void OnApplicationQuit() => TrySave(SaveReason.Final);


    private void OnDisable()
    {
        // Always flush whatever we have
        TrySave(SaveReason.Final);
    }


    private void OnSceneUnloaded(Scene s)
    {
        // Flush data from the scene that just finished unloading
        bool any = false;
        foreach (var kv in _trajById) { if (kv.Value.frames.Count > 0) { any = true; break; } }
        if (any)
        {
            Debug.Log($"[SwarmTrajectoryRecorder] Scene unloaded -> saving previous scene '{s.name}'");
            TrySave(SaveReason.Final);
        }


        // Prepare for next scene
        ClearBuffers();
    }


    private void OnSceneLoaded(Scene s, LoadSceneMode m)
    {
        if (!_justClearedForNewScene) ClearBuffers();
        _justClearedForNewScene = false;
        BeginSceneRecording(s);

        // The recorder is hosted by the additive Setup scene and persists after
        // that scene unloads. Re-open the prompt for each newly loaded gameplay
        // scene, but not for the selector or the recorder's own host scene.
        if (s.name != setupSceneName && s.name != _recorderHostSceneName)
        {
            OpenFilePrefixPromptIfNeeded();
        }
    }


    private void Update()
    {
        if (enableHotkeySave && Input.GetKeyDown(KeyCode.F7)) TrySave(SaveReason.Manual);


        _swarmFindTimer += Time.deltaTime;
        if (!swarmRoot && _swarmFindTimer >= 0.5f)
        {
            _swarmFindTimer = 0f;
            TryFindSwarmRootNow();
            if (swarmRoot)
            {
                CollectDrones();
                RefreshEmbodiedLabeling();
                _samplingEnabled = !waitForDronesToStart || _droneTransforms.Count > 0;
            }
        }


        if (lazyDiscover && swarmRoot && _droneTransforms.Count == 0)
        {
            _discoverTimer += Time.deltaTime;
            if (_discoverTimer > 0.5f)
            {
                _discoverTimer = 0f;
                int before = _droneTransforms.Count;
                CollectDrones();
                if (_droneTransforms.Count > before)
                {
                    RefreshEmbodiedLabeling();
                    _samplingEnabled = true;
                }
            }
        }


        if (swarmRoot && rescanChildrenEverySec > 0f)
        {
            _childrenRescanTimer += Time.deltaTime;
            if (_childrenRescanTimer >= rescanChildrenEverySec)
            {
                _childrenRescanTimer = 0f;
                if (_lastChildCount != swarmRoot.childCount)
                {
                    _lastChildCount = swarmRoot.childCount;
                    CollectDrones();
                    RefreshEmbodiedLabeling();
                }
            }
        }


        if (!_samplingEnabled || _droneTransforms.Count == 0) return;


        if (sampleHz <= 0f)
        {
            bool recordNow = ShouldRecordThisSample(Time.deltaTime);
            SampleOnce(recordNow);
        }
        else
        {
            float period = 1f / sampleHz;
            _accum += Time.deltaTime;
            while (_accum >= period)
            {
                bool recordNow = ShouldRecordThisSample(period);
                SampleOnce(recordNow);
                _accum -= period;
            }
        }


        if (autosaveEverySec > 0f)
        {
            _autosaveTimer += Time.deltaTime;
            if (_autosaveTimer >= autosaveEverySec)
            {
                _autosaveTimer = 0f;
                TrySave(SaveReason.Auto);
            }
        }
    }


    // -------------------- Public trial API --------------------
    public static void MarkTrialStart(string label = "Run")
    {
        if (_instance != null) _instance.MarkTrialStartInternal(label);
    }


    public static void MarkTrialStop(string label = "Run")
    {
        if (_instance != null) _instance.MarkTrialStopInternal(label);
    }
    
    public static void RecordCollectible()
    {
        RecordCollectible(string.Empty, int.MinValue, Vector3.zero);
    }

    public static int CollectiblesPickedUp
    {
        get { return _collectiblesCounter; }
    }

    public static void RecordCollectible(string collectibleName, int droneId, Vector3 position)
    {
        _collectiblesCounter++;
        if (_instance != null)
            _instance.RecordCollectibleInternal(collectibleName, droneId, position);
#if UNITY_EDITOR
        Debug.Log($"[SwarmTrajectoryRecorder] Collectible recorded. Total: {_collectiblesCounter}");
#endif
    }

    private void RecordCollectibleInternal(string collectibleName, int droneId, Vector3 position)
    {
        if (_openTrial == null || _openTrial.label != runLabelName) return;

        _collectibleBuffer.Add(new CollectibleEvent
        {
            t = Time.time,
            tElapsed = Timer.elapsedTime,
            name = collectibleName ?? string.Empty,
            droneId = droneId,
            x = position.x,
            y = position.y,
            z = position.z
        });
    }

    /// <summary>
    /// Record a crash event. Call from DroneController.crash() *before* the drone
    /// is removed from the swarm — we still need its position. Events outside an
    /// open Run window are ignored so analysis aligns with the trial timing.
    /// </summary>
    public static void RecordCrash(GameObject drone)
    {
        if (_instance == null || drone == null) return;
        _instance.RecordCrashInternal(drone.transform);
    }

    private void RecordCrashInternal(Transform tr)
    {
        // Only count crashes within an active run, matching the existing
        // collectibles convention (counter resets on MarkTrialStart).
        if (_openTrial == null || _openTrial.label != runLabelName) return;

        int id = GetStableId(tr);
        Vector3 p = tr.position;
        bool wasEmbodied = (id == _embodiedStableId);

        _crashBuffer.Add(new CrashEvent
        {
            t = Time.time,
            x = p.x, y = p.y, z = p.z,
            droneId = id,
            droneName = tr.name,
            embodied = (byte)(wasEmbodied ? 1 : 0),
        });

#if UNITY_EDITOR
        Debug.Log($"[SwarmTrajectoryRecorder] Crash #{_crashBuffer.Count} at t={Time.time:F2} pos={p} embodied={wasEmbodied}");
#endif
    }


    // STRICT single-run policy: ignore duplicate starts, ignore stop when no run is open.
    private void MarkTrialStartInternal(string label)
    {
        label = string.IsNullOrEmpty(label) ? runLabelName : label;


        if (singleRunMode && _runFinalized)
        {
#if UNITY_EDITOR
            Debug.Log($"[SwarmTrajectoryRecorder] Ignoring Start('{label}') because a run was already finalized.");
#endif
            return;
        }
        if (_openTrial != null)
        {
#if UNITY_EDITOR
            Debug.Log($"[SwarmTrajectoryRecorder] Ignoring Start('{label}') because a run is already open.");
#endif
            return;
        }


        _openTrial = new TrialWindow
        {
            label = label,
            startGameTime = Time.time,
            startRealtime = Time.realtimeSinceStartup,
            endGameTime = 0f,
            endRealtime = 0f
        };

        // Reset collectibles counter for new run
        _collectiblesCounter = 0;
        _collectibleBuffer.Clear();

        // Reset crash buffer for new run
        _crashBuffer.Clear();

        // One-shot scene snapshot (total collectibles + obstacle list).
        // Done here rather than at scene-load so collectibles spawned by trial
        // setup scripts are counted, and obstacle lists populated by registrars
        // that run on Awake have already finished.
        TakeSceneSnapshot();
        
#if UNITY_EDITOR
        Debug.Log($"[SwarmTrajectoryRecorder] Trial START '{_openTrial.label}' at t={_openTrial.startGameTime:F2}s");
#endif
    }


    private void MarkTrialStopInternal(string label)
    {
        label = string.IsNullOrEmpty(label) ? runLabelName : label;


        if (_openTrial == null)
        {
#if UNITY_EDITOR
            Debug.Log($"[SwarmTrajectoryRecorder] Ignoring Stop('{label}') because no run is open.");
#endif
            return;
        }


        _openTrial.endGameTime = Time.time;
        _openTrial.endRealtime = Time.realtimeSinceStartup;
        _trialsBuffer.Add(_openTrial);
#if UNITY_EDITOR
        Debug.Log($"[SwarmTrajectoryRecorder] Trial STOP '{_openTrial.label}' at t={_openTrial.endGameTime:F2}s (dur {( _openTrial.endGameTime - _openTrial.startGameTime):F2}s)");
#endif
        if (singleRunMode && _openTrial.label == runLabelName) _runFinalized = true;
        _openTrial = null;
    }


    // -------------------- Scene prep / buffers --------------------
    private void BeginSceneRecording(Scene s)
    {
        // Stable label for THIS scene
        string activeName = s.IsValid() ? s.name : SceneManager.GetActiveScene().name;
        string label = !string.IsNullOrEmpty(sceneLabelOverride) ? sceneLabelOverride :
                       (ResolveSelectedSceneLabel() ?? ((activeName == setupSceneName) ? "UnknownScene" : activeName));
        _sceneLabelForThisRun = MakeFileSafe(label);


        // stop any previous search and start a fresh one
        if (_ensureSwarmCoro != null) StopCoroutine(_ensureSwarmCoro);
        _ensureSwarmCoro = StartCoroutine(EnsureSwarmAvailable());

        // Resolve input-logging targets in case the new scene brought them in.
        EnsureInputRefs();


        _accum = _autosaveTimer = 0f;
        _recordAccum = 0f; _sampleIndex = 0;
        _samplingEnabled = false; // will flip to true when drones found


        // reset single-run flags for the new file
        _runFinalized = false;
        _trialsBuffer.Clear();
        _openTrial = null;


        // also refresh embodied at scene begin
        RefreshEmbodiedLabeling();
    }


    private void ClearBuffers()
    {
        // We do NOT auto-close here; Save() on unload/quit/disable already captures any open run.
        _openTrial = null;
        _trialsBuffer.Clear();
        _trajById.Clear();
        _droneTransforms.Clear();
        _lastChildCount = -1;
        _justClearedForNewScene = true;
        _finalized = false; // allow a final save for the new scene
        _runFinalized = false; // new file can have its own single run

        _crashBuffer.Clear();
        _inputBuffer.Clear();
        _swarmFrameBuffer.Clear();
        _subnetBuffer.Clear();

        _totalCollectibles = 0;
        _obstacleSnapshot.Clear();
        _courseLineSnapshot.Clear();
        _sceneSnapshotTaken = false;

        _embodiedTransform = null;
        _embodiedStableId = int.MinValue;
    }


    private System.Collections.IEnumerator EnsureSwarmAvailable()
    {
        float timeout = 10f; // try up to 10s; set 0 for infinite
        float t0 = Time.unscaledTime;


        while (true)
        {
            TryFindSwarmRootNow();


            // If still not found, try to infer by scanning active scene roots for the drone component type
            if (!swarmRoot)
            {
                var type = GetTypeByName(droneComponentTypeName);
                if (type != null)
                {
                    var scene = SceneManager.GetActiveScene();
                    var roots = scene.IsValid() ? scene.GetRootGameObjects() : null;
                    if (roots != null)
                    {
                        foreach (var go in roots)
                        {
                            var tr = FindTransformHavingComponent(go.transform, type);
                            if (tr != null)
                            {
                                swarmRoot = tr.root;
                                break;
                            }
                        }
                    }
                }
            }


            if (swarmRoot)
            {
                CollectDrones();
                RefreshEmbodiedLabeling();
                if (_droneTransforms.Count > 0)
                {
                    _samplingEnabled = true;
                    Debug.Log($"[SwarmTrajectoryRecorder] Found Swarm with {_droneTransforms.Count} drones; recording enabled.");
                    yield break;
                }
            }


            if (timeout > 0f && (Time.unscaledTime - t0) > timeout)
            {
                Debug.LogWarning("[SwarmTrajectoryRecorder] Swarm not found within timeout; will keep idle and retry.");
                // light retry while idle
                while (!_samplingEnabled)
                {
                    TryFindSwarmRootNow();
                    if (swarmRoot)
                    {
                        CollectDrones();
                        RefreshEmbodiedLabeling();
                        if (_droneTransforms.Count > 0)
                        {
                            _samplingEnabled = true;
                            Debug.Log($"[SwarmTrajectoryRecorder] Late-found Swarm with {_droneTransforms.Count} drones; recording enabled.");
                            yield break;
                        }
                    }
                    yield return new WaitForSeconds(0.5f);
                }
            }


            yield return null; // next frame
        }
    }


    private Transform FindTransformHavingComponent(Transform root, Type compType)
    {
        if (root.GetComponent(compType) != null) return root;
        for (int i = 0; i < root.childCount; i++)
        {
            var hit = FindTransformHavingComponent(root.GetChild(i), compType);
            if (hit != null) return hit;
        }
        return null;
    }


    // -------------------- Recording schedule --------------------
    private bool ShouldRecordThisSample(float dt)
    {
        // Priority 1: rate-based throttle
        if (recordHz > 0f)
        {
            float recPeriod = 1f / recordHz;
            _recordAccum += dt;
            if (_recordAccum + 1e-6f >= recPeriod)
            {
                _recordAccum -= recPeriod;
                _sampleIndex++; // advance index for consistency
                return true;
            }
            _sampleIndex++;
            return false;
        }


        // Priority 2: decimation-based throttle
        if (recordEveryNthSample <= 1)
        {
            _sampleIndex++;
            return true; // record every sample
        }


        _sampleIndex++;
        return (_sampleIndex % recordEveryNthSample) == 0;
    }


    // -------------------- Discovery --------------------
    private void TryFindSwarmRootNow()
    {
        if (swarmRoot) return;


        if (!string.IsNullOrEmpty(swarmRootTag))
        {
            var byTag = GameObject.FindWithTag(swarmRootTag);
            if (byTag) { swarmRoot = byTag.transform; _lastChildCount = swarmRoot.childCount; return; }
        }
        if (!string.IsNullOrEmpty(swarmRootName))
        {
            var byName = GameObject.Find(swarmRootName);
            if (byName) { swarmRoot = byName.transform; _lastChildCount = swarmRoot.childCount; }
        }
    }


    private void CollectDrones()
    {
        _droneTransforms.Clear();
        if (!swarmRoot) return;


        var type = GetTypeByName(droneComponentTypeName);
        if (type != null)
        {
            var comps = swarmRoot.GetComponentsInChildren(type, true);
            foreach (var c in comps)
            {
                var tr = ((Component)c).transform;
                if (!_droneTransforms.Contains(tr)) _droneTransforms.Add(tr);
                EnsureTrajFor(tr);
            }
        }
        else
        {
            foreach (Transform tr in swarmRoot.GetComponentsInChildren<Transform>(true))
            {
                if (tr == swarmRoot) continue;
                if (!_droneTransforms.Contains(tr)) _droneTransforms.Add(tr);
                EnsureTrajFor(tr);
            }
        }
    }


    private void EnsureTrajFor(Transform tr)
    {
        int id = GetStableId(tr);
        if (!_trajById.ContainsKey(id))
            _trajById[id] = new DroneTraj { id = id, name = tr.name };
    }


    private static Type GetTypeByName(string typeName)
    {
        if (string.IsNullOrEmpty(typeName)) return null;
        foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
        {
            var t = asm.GetType(typeName);
            if (t != null) return t;
        }
        return null;
    }


    private int GetStableId(Transform t)
    {
        var compType = GetTypeByName(droneComponentTypeName);
        if (compType != null)
        {
            var comp = t.GetComponent(compType);
            if (comp != null)
            {
                var f = compType.GetField("Id") ?? compType.GetField("DroneId");
                if (f != null && f.FieldType == typeof(int)) return (int)f.GetValue(comp);
                var p = compType.GetProperty("Id") ?? compType.GetProperty("DroneId");
                if (p != null && p.PropertyType == typeof(int)) return (int)p.GetValue(comp, null);
            }
        }
        return t.GetInstanceID();
    }


    // -------------------- Embodied detection & labeling --------------------
    private void RefreshEmbodiedLabeling()
    {
        _embodiedTransform = TryFindEmbodiedTransform();
        _embodiedStableId = (_embodiedTransform != null) ? GetStableId(_embodiedTransform) : int.MinValue;


    }


    private Transform TryFindEmbodiedTransform()
    {
        // 1) Inspector override
        if (embodiedDroneOverride) return embodiedDroneOverride;


        // 2) Tag
        if (!string.IsNullOrEmpty(embodiedTag))
        {
            try
            {
                var go = GameObject.FindWithTag(embodiedTag);
                if (go) return go.transform;
            }
            catch { /* tag may not exist */ }
        }


        // 3) Name
        if (!string.IsNullOrEmpty(embodiedName))
        {
            var byName = GameObject.Find(embodiedName);
            if (byName) return byName.transform;
        }


        // 4) HapticsTest.embodiedDrone (field or property), if present
        var htType = GetTypeByName("HapticsTest");
        if (htType != null)
        {
            var objs = UnityEngine.Object.FindObjectsOfType(htType);
            foreach (var o in objs)
            {
                Transform t = null;
                var f = htType.GetField("embodiedDrone");
                if (f != null && typeof(Transform).IsAssignableFrom(f.FieldType)) t = (Transform)f.GetValue(o);
                if (t == null)
                {
                    var p = htType.GetProperty("embodiedDrone");
                    if (p != null && typeof(Transform).IsAssignableFrom(p.PropertyType)) t = (Transform)p.GetValue(o, null);
                }
                if (t) return t;
            }
        }


        // 5) Look for a bool flag on the drone component type (e.g., DroneController.isEmbodied/IsEmbodied)
        var dcType = GetTypeByName(droneComponentTypeName);
        if (dcType != null && swarmRoot)
        {
            var comps = swarmRoot.GetComponentsInChildren(dcType, true);
            foreach (var c in comps)
            {
                bool isEmb = false;
                var f = dcType.GetField(embodiedFlagFieldOrProperty);
                if (f != null && f.FieldType == typeof(bool)) isEmb = (bool)f.GetValue(c);
                if (!isEmb)
                {
                    var p = dcType.GetProperty(embodiedFlagFieldOrProperty);
                    if (p != null && p.PropertyType == typeof(bool)) isEmb = (bool)p.GetValue(c, null);
                }
                if (!isEmb && !string.IsNullOrEmpty(embodiedFlagAltProperty))
                {
                    var p2 = dcType.GetProperty(embodiedFlagAltProperty);
                    if (p2 != null && p2.PropertyType == typeof(bool)) isEmb = (bool)p2.GetValue(c, null);
                    if (!isEmb)
                    {
                        var f2 = dcType.GetField(embodiedFlagAltProperty);
                        if (f2 != null && f2.FieldType == typeof(bool)) isEmb = (bool)f2.GetValue(c);
                    }
                }


                if (isEmb) return ((Component)c).transform;
            }
        }


        return null; // not found
    }


    // -------------------- Sampling --------------------
    private void SampleOnce(bool writeThisSample)
    {
        int n = _droneTransforms.Count;
        if (n == 0) return;


        float t = Time.time;


        if (!writeThisSample) return;


        // Refresh embodied selection every sample so runtime role switches are captured.
        RefreshEmbodiedLabeling();


        var positions = new Vector3[n];
        var ids       = new int[n];
        for (int i = 0; i < n; i++)
        {
            var tr = _droneTransforms[i];
            if (!tr) continue;
            positions[i] = tr.position;
            ids[i] = GetStableId(tr);
            if (!_trajById.TryGetValue(ids[i], out var _))
                _trajById[ids[i]] = new DroneTraj { id = ids[i], name = tr.name };
        }


        for (int i = 0; i < n; i++)
        {
            if (_trajById.TryGetValue(ids[i], out var traj))
            {
                // if name changed at runtime (rare), keep it updated
                traj.name = _droneTransforms[i] ? _droneTransforms[i].name : traj.name;
            }
        }


        var inMain = new bool[n];
        ComputeMainGroupFlags(positions, inMain);


        for (int i = 0; i < n; i++)
        {
            if (!_trajById.TryGetValue(ids[i], out var traj)) continue;
            Vector3 p = positions[i];

            // Pull richer per-drone state from DroneFake. Defaults handle any
            // drone whose DroneController/droneFake hasn't been wired up yet.
            Vector3 vel = Vector3.zero;
            float score = 0f;
            byte layer = 0;
            var dc = _droneTransforms[i] ? _droneTransforms[i].GetComponent<DroneController>() : null;
            if (dc != null && dc.droneFake != null)
            {
                vel = dc.droneFake.velocity;
                score = dc.droneFake.score;
                layer = (byte)Mathf.Clamp(dc.droneFake.layer, 0, 255);
            }

            traj.frames.Add(new TrajFrame
            {
                t = t,
                x = p.x, y = p.y, z = p.z,
                vx = vel.x, vy = vel.y, vz = vel.z,
                sc = score,
                l = layer,
                g = (byte)(inMain[i] ? 1 : 0),
                e = (byte)((ids[i] == _embodiedStableId) ? 1 : 0)
            });
        }

        // Time-aligned input snapshot. Captured every recorded sample so the
        // analyst can join inputs[] and trajectories[].frames on `t`.
        if (logInputs)
        {
            EnsureInputRefs();  // additive scenes may have brought refs in late
            if (fusionManager != null) _inputBuffer.Add(SnapshotInputs(t));
        }

        // Swarm-wide snapshot (counts, gap geometry, migration target, spread, timer).
        // Cheap: a few field reads plus one walk over the network's connected
        // components. Done after the per-drone loop so positions are stable.
        SnapshotSwarmState(t);
    }

    /// <summary>
    /// One row of swarm-wide state + one row per disconnected subnetwork. Pulled
    /// straight from `swarmModel.network`, `MigrationPointController`, and
    /// `Timer` — every field is already public/static. NaN-safe defaults for any
    /// system that isn't initialized yet (e.g. first sample on scene load).
    /// </summary>
    private void SnapshotSwarmState(float t)
    {
        // Resolve MigrationPointController lazily — additive scene loads mean we
        // can't rely on it being there at Awake. Fine to retry every frame; the
        // FindObjectOfType is only called when the cached ref is null.
        if (_migrationController == null) _migrationController = FindObjectOfType<MigrationPointController>();

        int activeDrones = (swarmRoot != null) ? swarmRoot.childCount : _droneTransforms.Count;
        int nMain = 0;
        int nDisc = 0;
        int nSub = 0;

        // Subnetwork details — sort by size descending so idx=0 is always the main group.
        var net = swarmModel.network;
        List<HashSet<DroneFake>> subs = (net != null) ? net.GetSubnetworks() : null;
        if (subs != null && subs.Count > 0)
        {
            // Stable sort by descending size for predictable idx semantics across frames.
            subs.Sort((a, b) => b.Count.CompareTo(a.Count));
            nSub = subs.Count;
            nMain = subs[0].Count;

            int totalInSubs = 0;
            for (int i = 0; i < subs.Count; i++)
            {
                var comp = subs[i];
                int sz = comp.Count;
                totalInSubs += sz;

                // Centroid + extent (max distance from centroid). The extent is a
                // crude scale for the subnetwork — useful for plotting "gap size".
                Vector3 sum = Vector3.zero;
                foreach (var df in comp) sum += df.position;
                Vector3 centroid = sum / Mathf.Max(1, sz);

                float maxR2 = 0f;
                foreach (var df in comp)
                {
                    float r2 = (df.position - centroid).sqrMagnitude;
                    if (r2 > maxR2) maxR2 = r2;
                }

                _subnetBuffer.Add(new SubnetSnapshot
                {
                    t = t,
                    idx = i,
                    size = sz,
                    cx = centroid.x, cy = centroid.y, cz = centroid.z,
                    extent = Mathf.Sqrt(maxR2),
                });
            }
            nDisc = Mathf.Max(0, totalInSubs - nMain);
        }

        int colRemain = 0;
        try { colRemain = GameObject.FindGameObjectsWithTag("Collectibles").Length; }
        catch { /* tag may not exist in some scenes */ }

        Vector2 mp = (_migrationController != null) ? _migrationController.migrationPoint : Vector2.zero;
        Vector3 av = MigrationPointController.alignementVector;
        float spreadCur = (swarmModel.network != null) ? DroneFake.desiredSeparation : float.NaN;
        float spreadMin = (_migrationController != null) ? _migrationController.minSpreadnessRuntime : float.NaN;
        float spreadMax = (_migrationController != null) ? _migrationController.maxSpreadnessRuntime : float.NaN;

        _swarmFrameBuffer.Add(new SwarmFrame
        {
            t = t,
            n = activeDrones,
            nMain = nMain,
            nDisc = nDisc,
            nCrash = Timer.numberDroneDied,
            nSub = nSub,
            colRemain = colRemain,
            mx = mp.x, mz = mp.y,
            avx = av.x, avy = av.y, avz = av.z,
            spreadCur = spreadCur,
            spreadMin = spreadMin,
            spreadMax = spreadMax,
            tElapsed = Timer.elapsedTime,
        });
    }

    /// <summary>
    /// Take a one-shot snapshot of scene-static info: how many collectibles exist
    /// at run start (so completion ratio = picked / total) and every obstacle in
    /// the scene with its geometry. Called from MarkTrialStartInternal; idempotent
    /// within a single run via _sceneSnapshotTaken.
    /// </summary>
    private void TakeSceneSnapshot()
    {
        if (_sceneSnapshotTaken) return;
        _sceneSnapshotTaken = true;

        _totalCollectibles = 0;
        try { _totalCollectibles = GameObject.FindGameObjectsWithTag("Collectibles").Length; }
        catch { /* tag may not exist */ }

        _obstacleSnapshot.Clear();
        _courseLineSnapshot.Clear();
        var obstacles = ClosestPointCalculator.obstacles;
        if (obstacles != null)
        {
            foreach (var obs in obstacles)
            {
                if (obs == null) continue;
                switch (obs)
                {
                    case SphereObstacle s:
                        _obstacleSnapshot.Add(new ObstacleInfo
                        {
                            type = "Sphere",
                            cx = s.Center.x, cy = s.Center.y, cz = s.Center.z,
                            radius = s.Radius,
                            qw = 1f,
                        });
                        break;
                    case BoxObstacle b:
                        _obstacleSnapshot.Add(new ObstacleInfo
                        {
                            type = "Box",
                            cx = b.Center.x, cy = b.Center.y, cz = b.Center.z,
                            sx = b.Size.x, sy = b.Size.y, sz = b.Size.z,
                            qx = b.Rotation.x, qy = b.Rotation.y, qz = b.Rotation.z, qw = b.Rotation.w,
                        });
                        break;
                    case CylinderObstacle c:
                        _obstacleSnapshot.Add(new ObstacleInfo
                        {
                            type = "Cylinder",
                            cx = c.Center.x, cy = c.Center.y, cz = c.Center.z,
                            radius = c.Radius,
                            height = c.Height,
                            qx = c.Rotation.x, qy = c.Rotation.y, qz = c.Rotation.z, qw = c.Rotation.w,
                        });
                        break;
                    default:
                        // Unknown obstacle subclass — fall back to base radius/center.
                        _obstacleSnapshot.Add(new ObstacleInfo
                        {
                            type = obs.GetType().Name,
                            cx = obs.centerObs.x, cy = obs.centerObs.y, cz = obs.centerObs.z,
                            radius = obs.radiusObs,
                            qw = 1f,
                        });
                        break;
                }
            }
        }

        CaptureCourseLines();

#if UNITY_EDITOR
        Debug.Log($"[SwarmTrajectoryRecorder] Scene snapshot: {_totalCollectibles} collectibles, {_obstacleSnapshot.Count} obstacles, {_courseLineSnapshot.Count} course lines.");
#endif
    }

    private void CaptureCourseLines()
    {
        TestCourse tc = FindObjectOfType<TestCourse>();
        if (tc != null)
        {
            AddCourseLineSnapshot(tc.TrajectoryStartLine(), "start");
            AddCourseLineSnapshot(tc.TrajectoryEndLine(), "end");
            AddCourseLineSnapshot(tc.startingSquare, "startSquare");
            return;
        }

        AddCourseLineSnapshot(GameObject.Find("Starting_line")?.transform, "start");
        AddCourseLineSnapshot(GameObject.Find("Ending_line")?.transform, "end");
        AddCourseLineSnapshot(GameObject.Find("Starting_square")?.transform, "startSquare");
    }

    private void AddCourseLineSnapshot(Transform tr, string role)
    {
        if (tr == null) return;
        Quaternion q = tr.rotation;
        Vector3 p = tr.position;
        Vector3 s = tr.lossyScale;

        BoxCollider box = tr.GetComponent<BoxCollider>();
        if (box != null)
        {
            p = tr.TransformPoint(box.center);
            Vector3 localSize = box.size;
            Vector3 scale = tr.lossyScale;
            s = new Vector3(
                Mathf.Abs(localSize.x * scale.x),
                Mathf.Abs(localSize.y * scale.y),
                Mathf.Abs(localSize.z * scale.z)
            );
        }
        else
        {
            Renderer renderer = tr.GetComponent<Renderer>();
            if (renderer != null)
            {
                p = renderer.bounds.center;
                s = renderer.bounds.size;
            }
        }

        _courseLineSnapshot.Add(new CourseLineInfo
        {
            role = role,
            name = tr.name,
            cx = p.x, cy = p.y, cz = p.z,
            sx = s.x, sy = s.y, sz = s.z,
            qx = q.x, qy = q.y, qz = q.z, qw = q.w
        });
    }

    /// <summary>
    /// Build one InputFrame from the current state of every input source the
    /// fusion manager knows about. Unavailable sources record NaN so analysis
    /// can distinguish "source not present" from "source present, reading 0".
    /// </summary>
    private InputFrame SnapshotInputs(float t)
    {
        const float N = float.NaN;
        var fm = fusionManager;

        var f = new InputFrame
        {
            t = t,
            // Fused outputs
            fmx = fm.SwarmMovement.x, fmy = fm.SwarmMovement.y, fmz = fm.SwarmMovement.z,
            fs  = fm.SwarmSpread,
            fr  = fm.CameraRotation,
            sabs = (byte)(fm.IsSpreadAbsolute ? 1 : 0),
            // Source defaults — overwritten below if the source is present.
            trad_mx = N, trad_my = N, trad_h = N, trad_s = N, trad_r = N,
            imu_mx = N, imu_mz = N, imu_y = N,
            arm_h = N, arm_s = N,
            mq_y = N,
            mqh_h = N, mqh_s = N,
            ctl_h = N, ctl_s = N,
            pose_h = N, pose_s = N, pose_y = N,
            ws_d = N, ws_h = N, ws_y = N,
        };

        // Traditional joystick / keyboard — log even when not selected as the
        // active source, so we can see what the user *would* have done.
        if (fm.traditionalInput != null)
        {
            f.trad_mx = fm.traditionalInput.MovementInput.x;
            f.trad_my = fm.traditionalInput.MovementInput.y;
            f.trad_h  = fm.traditionalInput.HeightInput;
            f.trad_s  = fm.traditionalInput.SpreadInput;
            f.trad_r  = fm.traditionalInput.RotationInput;
        }

        // Chest IMU
        if (fm.imuMovementInput != null && fm.imuMovementInput.IsAvailable)
        {
            f.imu_mx = fm.imuMovementInput.MovementVector.x;
            f.imu_mz = fm.imuMovementInput.MovementVector.z;
        }
        if (fm.imuYawInput != null && fm.imuYawInput.IsAvailable)
            f.imu_y = fm.imuYawInput.YawRotationRate;

        // Forearm IMUs — prefer fusion output when present (it forwards to armIMU).
        if (fm.handIMUFusion != null && fm.handIMUFusion.IsAvailable)
        {
            f.arm_h = fm.handIMUFusion.HeightControl;
            f.arm_s = fm.handIMUFusion.SpreadControl;
        }
        else if (fm.armIMUInput != null && fm.armIMUInput.IsAvailable)
        {
            f.arm_h = fm.armIMUInput.HeightControl;
            f.arm_s = fm.armIMUInput.SpreadControl;
        }

        // Meta Quest
        if (fm.headsetYawInput != null && fm.headsetYawInput.IsAvailable)
            f.mq_y = fm.headsetYawInput.RotationControl;
        if (fm.handHeightInput != null && fm.handHeightInput.IsAvailable)
            f.mqh_h = fm.handHeightInput.HeightControl;
        if (fm.handSpreadInput != null && fm.handSpreadInput.IsAvailable)
            f.mqh_s = fm.handSpreadInput.SpreadControl;
        if (fm.controllerHeightInput != null && fm.controllerHeightInput.IsAvailable)
            f.ctl_h = fm.controllerHeightInput.HeightControl;
        if (fm.controllerSpreadInput != null && fm.controllerSpreadInput.IsAvailable)
            f.ctl_s = fm.controllerSpreadInput.SpreadControl;

        // Webcam pose — Unity-side processed values
        if (fm.poseHeightInput != null && fm.poseHeightInput.IsAvailable)
            f.pose_h = fm.poseHeightInput.HeightControl;
        if (fm.poseSpreadInput != null && fm.poseSpreadInput.IsAvailable)
            f.pose_s = fm.poseSpreadInput.SpreadControl;
        if (fm.poseYawInput != null && fm.poseYawInput.IsAvailable)
            f.pose_y = fm.poseYawInput.YawRate;

        // Raw values straight from the Python tracker, pre-Unity processing.
        if (webSocketClient != null && webSocketClient.IsConnected)
        {
            f.ws_d = webSocketClient.HandDistance;
            f.ws_h = webSocketClient.HandHeight;
            f.ws_y = webSocketClient.HeadYawDeg;
        }

        return f;
    }


    // -------------------- Main-group logic --------------------
    private void ComputeMainGroupFlags(Vector3[] positions, bool[] inMain)
    {
        Array.Clear(inMain, 0, inMain.Length);
        int n = positions.Length;
        if (n == 0) return;


        if (useNetworkForMainGroup)
        {
            var net = swarmModel.network; // your runtime network
            if (net != null && net.largestComponent != null && net.largestComponent.Count > 0)
            {
                var mainSet = new HashSet<DroneFake>(net.largestComponent);
                for (int i = 0; i < n; i++)
                {
                    var tr = _droneTransforms[i];
                    if (!tr) continue;
                    var dc = tr.GetComponent<DroneController>();
                    var df = (dc != null) ? dc.droneFake : null;


                    bool isInMain =
                        (df != null && mainSet.Contains(df)) ||
                        (df != null && net.IsInMainNetwork(df));


                    inMain[i] = isInMain;
                }


                if (minMainGroupSize > 1)
                {
                    int count = 0; for (int i = 0; i < n; i++) if (inMain[i]) count++;
                    if (count < minMainGroupSize) Array.Clear(inMain, 0, n);
                }
                return;
            }


            if (includeAllUntilNetworkReady)
            {
                for (int i = 0; i < n; i++) inMain[i] = true;
                return;
            }
        }


        ProximityFallback(positions, inMain);
    }


    private void ProximityFallback(Vector3[] positions, bool[] inMain)
    {
        int n = positions.Length;
        if (n == 0) return;


        int[] parent = new int[n];
        int[] size = new int[n];
        for (int i = 0; i < n; i++) { parent[i] = i; size[i] = 1; }


        int Find(int a)
        {
            while (parent[a] != a) { parent[a] = parent[parent[a]]; a = parent[a]; }
            return a;
        }
        void Union(int a, int b)
        {
            a = Find(a); b = Find(b);
            if (a == b) return;
            if (size[a] < size[b]) { var t = a; a = b; b = t; }
            parent[b] = a; size[a] += size[b];
        }


        float r2 = linkDistance * linkDistance;
        for (int i = 0; i < n; i++)
        for (int j = i + 1; j < n; j++)
        {
            float dx = positions[i].x - positions[j].x;
            float dz = positions[i].z - positions[j].z;
            float d2 = useXZDistance
                ? (dx * dx + dz * dz)
                : (dx * dx + dz * dz + (positions[i].y - positions[j].y) * (positions[i].y - positions[j].y));
            if (d2 <= r2) Union(i, j);
        }


        var counts = new Dictionary<int, int>();
        int bestRoot = -1, best = 0;
        for (int i = 0; i < n; i++)
        {
            int r = Find(i);
            counts[r] = counts.TryGetValue(r, out var cur) ? (cur + 1) : 1;
            if (counts[r] > best) { best = counts[r]; bestRoot = r; }
        }


        if (best < Mathf.Max(1, minMainGroupSize))
        {
            Array.Clear(inMain, 0, n);
            return;
        }


        for (int i = 0; i < n; i++)
            inMain[i] = (Find(i) == bestRoot);
    }


    // -------------------- Saving --------------------
    private void TrySave(SaveReason reason)
    {
        if (_finalized && reason != SaveReason.Auto) return; // already wrote final file
        if (Time.realtimeSinceStartup - _lastSaveRealtime < SaveDebounceSec) return; // debounce
        _lastSaveRealtime = Time.realtimeSinceStartup;


        Save();


        if (reason == SaveReason.Final) _finalized = true;
#if UNITY_EDITOR
        Debug.Log($"[SwarmTrajectoryRecorder] Save() wrote file. finalized={_finalized} time={Time.time:F2}");
#endif
    }


    public void Save()
    {
        bool any = false;
        foreach (var kv in _trajById) { if (kv.Value.frames.Count > 0) { any = true; break; } }
        if (!any && _droneTransforms.Count > 0) SampleOnce(true); // ensure at least one sample


        // Auto-stop open run just before saving (so it is captured in this file)
        if (_openTrial != null && _openTrial.endGameTime <= 0f)
        {
            _openTrial.endGameTime = Time.time;
            _openTrial.endRealtime = Time.realtimeSinceStartup;
            _trialsBuffer.Add(_openTrial);
            if (singleRunMode && _openTrial.label == runLabelName) _runFinalized = true;
            _openTrial = null;
        }


        // Make sure embodied flags are up-to-date right before write
        RefreshEmbodiedLabeling();
        
        // Calculate elapsed time from the Run trial if it exists
        float elapsedTime = 0f;
        var runTrial = _trialsBuffer.Find(t => t.label == runLabelName);
        if (runTrial != null && runTrial.endGameTime > 0f)
        {
            elapsedTime = runTrial.endGameTime - runTrial.startGameTime;
        }


        var log = new TrajectoryLog
        {
            scene = SceneManager.GetActiveScene().name,  // for reference
            pid = ResolvePid(),
            haptics = ResolveHaptics(),
            order = ResolveOrder(),
            sampleHz = sampleHz <= 0 ? -1f : sampleHz,
            trajectories = new List<DroneTraj>(_trajById.Values),
            trials = new List<TrialWindow>(_trialsBuffer),


            // NEW: embodied metadata
            embodiedId = _embodiedStableId,
            embodiedName = _embodiedTransform ? _embodiedTransform.name : string.Empty,

            // Collectibles and elapsed time
            collectiblesPickedUp = _collectiblesCounter,
            elapsedTime = elapsedTime,
            collectibles = new List<CollectibleEvent>(_collectibleBuffer),

            // Crashes — full event list, plus aggregate count for quick scanning
            crashCount = _crashBuffer.Count,
            crashes = new List<CrashEvent>(_crashBuffer),

            // Per-frame input snapshots (empty list when logInputs=false)
            inputs = new List<InputFrame>(_inputBuffer),

            // Per-frame swarm-wide snapshot + per-subnetwork rows for gap geometry.
            swarmFrames = new List<SwarmFrame>(_swarmFrameBuffer),
            subnetworks = new List<SubnetSnapshot>(_subnetBuffer),

            // One-shot scene data captured at run start.
            totalCollectibles = _totalCollectibles,
            obstacles = new List<ObstacleInfo>(_obstacleSnapshot),
            courseLines = new List<CourseLineInfo>(_courseLineSnapshot),
        };


        string root = ResolveOutputRoot(log.pid);
        Directory.CreateDirectory(root);


        // Use cached label for this scene (important when saving after scene switch)
        string safeScene = !string.IsNullOrEmpty(_sceneLabelForThisRun)
            ? _sceneLabelForThisRun
            : MakeFileSafe(SceneManager.GetActiveScene().name);


        string stamp = DateTime.Now.ToString("yyyyMMdd_HHmmss");
        string fileName;
        if (!string.IsNullOrEmpty(customFilePrefix))
        {
            fileName = ResolveCustomFileName(root);
        }
        else
        {
            fileName = $"{safeScene}_{log.haptics}_{log.order}_{stamp}_traj.json";
        }
        string full = Path.Combine(root, fileName);


        File.WriteAllText(full, JsonUtility.ToJson(log, true));
        Debug.Log($"[SwarmTrajectoryRecorder] Saved {log.trajectories.Count} drones to:\n{full}");


#if UNITY_EDITOR
        if (full.StartsWith(Application.dataPath)) AssetDatabase.Refresh();
#endif
    }


    // -------------------- Helpers --------------------
    private static string MakeFileSafe(string s)
    {
        if (string.IsNullOrEmpty(s)) return "Scene";
        s = Regex.Replace(s, @"\s+", "_");
        foreach (char c in Path.GetInvalidFileNameChars()) s = s.Replace(c.ToString(), "");
        return s;
    }


    private string ResolveCustomFileName(string root)
    {
        string safePrefix = MakeFileSafe(customFilePrefix);
        if (_resolvedCustomFileName != null
            && _resolvedCustomFileNameKey == safePrefix
            && _resolvedCustomFileNameRoot == root)
        {
            return _resolvedCustomFileName;
        }

        string fileName = safePrefix.EndsWith(".json", StringComparison.OrdinalIgnoreCase)
            ? safePrefix
            : $"{safePrefix}.json";

        string full = Path.Combine(root, fileName);
        if (File.Exists(full))
        {
            string suffix = DateTime.Now.ToString("yyyy_MM_dd_HH_mm_ss");
            string stem = Path.GetFileNameWithoutExtension(fileName);
            string ext = Path.GetExtension(fileName);
            fileName = $"{stem}_{suffix}{ext}";
        }

        _resolvedCustomFileName = fileName;
        _resolvedCustomFileNameKey = safePrefix;
        _resolvedCustomFileNameRoot = root;
        return _resolvedCustomFileName;
    }


    private string ResolveLatestDataFilePrefix()
    {
        string root = ResolveOutputRoot();
        if (!Directory.Exists(root)) return string.Empty;

        DirectoryInfo directory = new DirectoryInfo(root);
        FileInfo latestJson = null;
        foreach (FileInfo file in directory.GetFiles("*.json"))
        {
            if (latestJson == null || file.LastWriteTimeUtc > latestJson.LastWriteTimeUtc)
            {
                latestJson = file;
            }
        }

        if (latestJson == null) return string.Empty;

        string stem = Path.GetFileNameWithoutExtension(latestJson.Name);
        stem = Regex.Replace(stem, @"_\d{4}_\d{2}_\d{2}_\d{2}_\d{2}_\d{2}$", "");
        stem = Regex.Replace(stem, @"_\d{8}_\d{6}_traj$", "");
        return stem;
    }


    private string ResolveOutputRoot(string pid = null)
    {
#if UNITY_EDITOR
        return Path.Combine(Application.dataPath, "Trajectories");
#else
        return Path.Combine(Application.persistentDataPath, "Data", string.IsNullOrEmpty(pid) ? ResolvePid() : pid, outSubfolder);
#endif
    }


    private string ResolvePid()
    {
        if (!string.IsNullOrEmpty(pidOverride)) return pidOverride;
        var t = GetTypeByName("SceneSelectorScript");
        if (t != null)
        {
            var f = t.GetField("pid");
            if (f != null && f.FieldType == typeof(string))
            {
                var v = (string)f.GetValue(null);
                if (!string.IsNullOrEmpty(v)) return v;
            }
        }
        return "PID_Default";
    }


    private string ResolveHaptics()
    {
        var t = GetTypeByName("SceneSelectorScript");
        if (t != null)
        {
            var f = t.GetField("_haptics");
            if (f != null && f.FieldType == typeof(bool))
                return ((bool)f.GetValue(null)) ? "H" : "NH";
        }
        return "NH";
    }


    private string ResolveOrder()
    {
        var t = GetTypeByName("SceneSelectorScript");
        if (t != null)
        {
            var f = t.GetField("_order");
            if (f != null && f.FieldType == typeof(bool))
                return ((bool)f.GetValue(null)) ? "O" : "NO";
        }
        return "NO";
    }


    private string ResolveSelectedSceneLabel()
    {
        var t = GetTypeByName("SceneSelectorScript");
        if (t == null) return null;


        string[] fieldNames = { "selectedSceneName", "sceneToLoad", "targetScene", "detailScene", "SelectedLevel", "SelectedScene" };
        foreach (var fn in fieldNames)
        {
            var f = t.GetField(fn);
            if (f != null && f.FieldType == typeof(string))
            {
                var v = f.GetValue(null) as string;
                if (!string.IsNullOrEmpty(v)) return v;
            }
        }
        string[] propNames = { "SelectedSceneName", "SceneToLoad", "TargetScene", "DetailScene", "SelectedLevel", "SelectedScene" };
        foreach (var pn in propNames)
        {
            var p = t.GetProperty(pn);
            if (p != null && p.PropertyType == typeof(string))
            {
                var v = p.GetValue(null) as string;
                if (!string.IsNullOrEmpty(v)) return v;
            }
        }
        return null;
    }
}
