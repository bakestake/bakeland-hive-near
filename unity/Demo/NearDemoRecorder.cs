using System;
using System.IO;
using Bakeland.Cutscenes;
using UnityEditor;
using UnityEditor.Recorder;
using UnityEditor.Recorder.Encoder;
using UnityEditor.Recorder.Input;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace Bakeland.EditorTools
{
    /// <summary>
    /// NEAR integration demo capture (Unity Recorder, Game View 1560x720 at 30 fps: exactly the MX-77 screen of the
    /// house film frame at 4x, so the edit places it 1:1). Dev sign-in, waits for the MainGame world, plays
    /// <see cref="NearDemoCutscene"/> and records it to Recordings/NearDemo_*.mp4. Beat marks go to Logs/near-demo.txt.
    /// Driven by the dev bridge: "menu Bakeland/Cutscenes/NEAR Demo/Sign In, Play And Record".
    /// </summary>
    [InitializeOnLoad]
    public static class NearDemoRecorder
    {
        private const string Root = "Bakeland/Cutscenes/NEAR Demo/";
        private const string Pending = "Bakeland.NearDemo.Pending";
        private const string PendingRecord = "Bakeland.NearDemo.Record";
        private const string LogPath = "Logs/near-demo.txt";
        private const int Width = 1560, Height = 720;
        private const float Fps = 30f;

        private enum Phase { Idle, WaitingForWorld, WaitingForSet, Recording }
        private static Phase phase;
        private static double phaseStarted, readySince = -1;
        private static bool requestedMainGame, record;
        private static NearDemoCutscene cinematic;
        private static RecorderController controller;
        private static string outputBase;

        static NearDemoRecorder()
        {
            EditorApplication.update += Tick;
            EditorApplication.playModeStateChanged += state =>
            {
                if (state == PlayModeStateChange.EnteredPlayMode && SessionState.GetBool(Pending, false))
                {
                    SessionState.SetBool(Pending, false);
                    record = SessionState.GetBool(PendingRecord, true);
                    Arm();
                }
                if (state == PlayModeStateChange.ExitingPlayMode && phase != Phase.Idle) Abort("play mode ended");
            };
        }

        [MenuItem(Root + "Sign In, Play And Record", false, 1)]
        public static void SignInPlayAndRecord() => Start(true);

        [MenuItem(Root + "Sign In And Play (No Recording)", false, 2)]
        public static void SignInAndPlay() => Start(false);

        [MenuItem(Root + "Stop", false, 20)]
        private static void StopNow() => Abort("stopped from the menu");

        private static void Start(bool rec)
        {
            if (EditorApplication.isPlaying) { record = rec; Arm(); return; }
            SessionState.SetBool(Pending, true);
            SessionState.SetBool(PendingRecord, rec);
            Log("entering play mode with the dev sign-in (record=" + rec + ")");
            if (!EditorApplication.ExecuteMenuItem("Bakeland/Dev/Play And Sign In")) EditorApplication.EnterPlaymode();
        }

        private static void Arm()
        {
            if (phase != Phase.Idle) { Log("already running (" + phase + ")"); return; }
            requestedMainGame = false;
            readySince = -1;
            Enter(Phase.WaitingForWorld);
            Log("armed: waiting for the MainGame world");
        }

        private static void Enter(Phase next) { phase = next; phaseStarted = EditorApplication.timeSinceStartup; }

        private static void Tick()
        {
            if (phase == Phase.Idle || !EditorApplication.isPlaying) return;
            double inPhase = EditorApplication.timeSinceStartup - phaseStarted;
            try
            {
                switch (phase)
                {
                    case Phase.WaitingForWorld:
                        if (inPhase > 240) { Abort("timed out waiting for the signed-in MainGame world"); return; }
                        if (!WorldReady()) { readySince = -1; return; }
                        if (readySince < 0) readySince = EditorApplication.timeSinceStartup;
                        if (EditorApplication.timeSinceStartup - readySince < 6.0) return;
                        cinematic = NearDemoCutscene.Begin(out string why);
                        if (cinematic == null) { Abort("the demo could not start: " + why); return; }
                        Log(cinematic.Report);
                        Enter(Phase.WaitingForSet);
                        break;

                    case Phase.WaitingForSet:
                        if (cinematic == null) { Abort("the demo ended during setup"); return; }
                        if (inPhase < 2.0) return;   // HUD, weather and camera settle
                        if (record) StartRecorder();
                        cinematic.Roll();
                        Enter(Phase.Recording);
                        break;

                    case Phase.Recording:
                        if (cinematic != null && !cinematic.IsFinished && inPhase < 240) return;
                        StopRecorder();
                        if (cinematic != null) Log("report: " + cinematic.Report + " | marks: " + cinematic.Marks);
                        Log("NEAR_DEMO_DONE file=" + (record ? outputBase + ".mp4" : "(not recorded)"));
                        if (cinematic != null) UnityEngine.Object.Destroy(cinematic.gameObject);
                        cinematic = null;
                        phase = Phase.Idle;
                        break;
                }
            }
            catch (Exception e) { Debug.LogException(e); Abort("error: " + e.Message); }
        }

        private static bool WorldReady()
        {
            if (PlayerController.Instance == null || !SpacetimeManager.HasLocalPlayer) return false;
            if (WorldSceneTransitionService.IsTransitioning) return false;
            if (LoadingWheel.instance != null && LoadingWheel.instance.IsObscuringGameplay) return false;
            bool main = SceneManager.GetActiveScene().name == "MainGame" &&
                        UnityEngine.Object.FindObjectsByType<Bakeland.HiveLink.HiveLinkTerminal>(FindObjectsInactive.Include, FindObjectsSortMode.None).Length > 0;
            if (!main)
            {
                if (!requestedMainGame && SceneManager.GetActiveScene().name != "MainGame")
                {
                    requestedMainGame = true;
                    Log("not in MainGame; travelling there");
                    WorldSceneTransitionService.Load("MainGame");
                }
                return false;
            }
            return true;
        }

        private static void StartRecorder()
        {
            string folder = Path.GetFullPath(Path.Combine(Application.dataPath, "..", "Recordings"));
            Directory.CreateDirectory(folder);
            outputBase = Path.Combine(folder, "NearDemo_" + DateTime.Now.ToString("yyyyMMdd_HHmmss"));
            var movie = ScriptableObject.CreateInstance<MovieRecorderSettings>();
            movie.name = "NEAR Demo";
            movie.Enabled = true;
            movie.EncoderSettings = new CoreEncoderSettings
            {
                Codec = CoreEncoderSettings.OutputCodec.MP4,
                EncodingQuality = CoreEncoderSettings.VideoEncodingQuality.Custom,
                TargetBitRate = 40f,        // near-lossless for 4x pixel art (the edit re-encodes)
            };
            movie.ImageInputSettings = new GameViewInputSettings { OutputWidth = Width, OutputHeight = Height };
            movie.AudioInputSettings.PreserveAudio = true;
            movie.OutputFile = outputBase;
            var settings = ScriptableObject.CreateInstance<RecorderControllerSettings>();
            settings.AddRecorderSettings(movie);
            settings.SetRecordModeToManual();
            settings.FrameRatePlayback = FrameRatePlayback.Constant;
            settings.FrameRate = Fps;
            settings.CapFrameRate = true;
            controller = new RecorderController(settings);
            controller.PrepareRecording();
            bool started = controller.StartRecording();
            Log($"recorder {(started ? "started" : "FAILED to start")} {Width}x{Height}@{Fps} -> {outputBase}.mp4");
        }

        private static void StopRecorder()
        {
            if (controller == null) return;
            if (controller.IsRecording()) controller.StopRecording();
            controller = null;
        }

        private static void Abort(string reason)
        {
            StopRecorder();
            if (cinematic != null) { cinematic.Stop(); }
            cinematic = null;
            phase = Phase.Idle;
            Log("NEAR_DEMO_ABORTED " + reason);
        }

        private static void Log(string message)
        {
            try { Directory.CreateDirectory("Logs"); File.AppendAllText(LogPath, $"[{DateTime.Now:HH:mm:ss}] {message}\n"); }
            catch (IOException) { }
            Debug.Log("[NearDemo] " + message);
        }
    }
}
