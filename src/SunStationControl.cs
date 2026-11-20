using System;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using Newtonsoft.Json;
using OWML.ModHelper;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace SunStation
{
    // Control and observation only. Launch physics remain entirely in the base game.
    public sealed partial class Control : ModBehaviour
    {
        private string root, session, inbox, outbox, processed, routeSource, summitSource;
        private bool developmentMode;
        private StreamWriter trace, events, playerRoute;
        private long playerSamples;
        private string playerLogMode = "low";
        private float nextPlayerSample, nextPlayerFlush, playerBurstUntil;
        private bool speedChanged;
        private bool backgroundOriginal;
        private int savedPoints;
        private float nextPoll, nextStatus, nextBodyScan;
        private long fixedStep;
        private OWRigidbody station;
        private SurveyorProbe observedProbe;
        private bool wasLaunched, wasAnchored;
        private string lastMessage = "Ready";
        private const BindingFlags Flags = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
        private readonly MethodInfo fire = typeof(ProbeLauncher).GetMethod("LaunchProbe", Flags);
        private readonly MethodInfo retrieve = typeof(ProbeLauncher).GetMethod("RetrieveProbe", Flags);
        private readonly MethodInfo setPitch = typeof(PlayerCameraController).GetMethod("SetDegreesY", Flags);

        public void Start()
        {
            backgroundOriginal = Application.runInBackground;
            string modDir = Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location);
            string projectConfig = Path.Combine(modDir, "project-root.txt");
            developmentMode = File.Exists(projectConfig);
            if (developmentMode)
            {
                root = File.ReadAllText(projectConfig).Trim();
                routeSource = Path.Combine(root, "runs", "20261116-141733", "player-route-captured.jsonl");
                summitSource = Path.Combine(root, "locations", "snow-summit.json");
                inbox = Path.Combine(root, "control", "inbox");
                outbox = Path.Combine(root, "control", "outbox");
                processed = Path.Combine(root, "control", "processed");
                foreach (string dir in new[] { inbox, outbox, processed }) Directory.CreateDirectory(dir);
            }
            else
            {
                routeSource = Path.Combine(modDir, "data", "player-route.jsonl");
                summitSource = Path.Combine(modDir, "data", "snow-summit.json");
                root = Path.Combine(Application.persistentDataPath, "SunStation.Control");
            }
            session = Path.Combine(root, "runs", DateTime.Now.ToString("yyyyMMdd-HHmmss"));
            Directory.CreateDirectory(session);
            trace = new StreamWriter(Path.Combine(session, "trajectory.csv"));
            events = new StreamWriter(Path.Combine(session, "events.jsonl")) { AutoFlush = true };
            playerRoute = new StreamWriter(Path.Combine(session, "player-route.jsonl"));
            trace.WriteLine("fixed_step,loop_seconds,probe_launched,probe_anchored,th_x,th_y,th_z,th_vx,th_vy,th_vz,sun_x,sun_y,sun_z,station_distance");
            Event("loaded", new { version = "0.6.3", unity = Application.unityVersion, session, playerLogMode, playerLogIntervalSeconds = 1f, restoreBurstSeconds = 3f });
            Debug.Log(developmentMode ? "[SunStation] File control ready: " + inbox : "[SunStation] Replay ready. Logs: " + session);
            SceneManager.sceneLoaded += OnTrialSceneLoaded;
        }

        public void Update()
        {
            if (root == null) return;
            try
            {
                UpdateTrial();
                UpdateWorldCapture();
                UpdateRouteReplay();
                UpdateRouteShot();
                if (trial != null && UnityEngine.InputSystem.Keyboard.current != null && UnityEngine.InputSystem.Keyboard.current.f7Key.wasPressedThisFrame)
                    FailTrial(new OperationCanceledException("Stopped with F7"));
                if (developmentMode && UnityEngine.InputSystem.Keyboard.current != null &&
                    UnityEngine.InputSystem.Keyboard.current.f6Key.wasPressedThisFrame && SceneManager.GetActiveScene().name == "SolarSystem")
                    SavePlayerPoint();
                if (Time.unscaledTime >= nextBodyScan)
                {
                    nextBodyScan = Time.unscaledTime + 2f;
                    if (station == null) station = Resources.FindObjectsOfTypeAll<OWRigidbody>()
                        .FirstOrDefault(b => b.name == "SunStation_Body" && b.gameObject.scene.IsValid());
                }
                if (developmentMode && Time.unscaledTime >= nextPoll)
                {
                    nextPoll = Time.unscaledTime + 0.1f;
                    string path = Directory.GetFiles(inbox, "*.json").OrderBy(p => p).FirstOrDefault();
                    if (path != null) ProcessCommand(path);
                }
                if (developmentMode && Time.unscaledTime >= nextStatus)
                {
                    nextStatus = Time.unscaledTime + 0.5f;
                    AtomicJson(Path.Combine(root, "control", "status.json"), Snapshot());
                    var pose = PlayerPose();
                    if (pose != null) AtomicJson(Path.Combine(session, "player-pose-latest.json"), pose);
                }
            }
            catch (Exception ex) { lastMessage = ex.GetBaseException().Message; Debug.LogError("[SunStation] " + ex); }
        }

        private void ProcessCommand(string path)
        {
            // Claim before executing, so a failed response write cannot repeat a shot.
            string claimed = Path.Combine(processed, Path.GetFileName(path));
            File.Move(path, claimed);
            string id = Path.GetFileNameWithoutExtension(path);
            try
            {
                var c = JsonConvert.DeserializeObject<Command>(File.ReadAllText(claimed));
                if (c == null || c.id != id) throw new ArgumentException("Command id must match its filename");
                object result = Execute(c);
                AtomicJson(Path.Combine(outbox, id + ".json"), new { id, ok = true, result, state = Snapshot() });
                lastMessage = c.op + ": OK";
                Event("command", new { id, c.op, ok = true });
            }
            catch (Exception ex)
            {
                string error = ex.GetBaseException().Message;
                AtomicJson(Path.Combine(outbox, id + ".json"), new { id, ok = false, error });
                lastMessage = error;
                Event("command_error", new { id, error });
            }
        }

        private object Execute(Command c, Location locationOverride = null)
        {
            if (c.op == "status") return Snapshot();
            if (c.op == "route-replay") return StartRouteReplay(c);
            if (c.op == "route-shot") return StartRouteShot(c);
            if (c.op == "route-cancel")
            {
                FinishRouteReplay("cancelled");
                if (routeShot != null && !routeShot.Fired) FinishRouteShot("cancelled");
                return new { routeReplayStatus, routeShotStatus };
            }
            if ((routeReplay != null || (routeShot != null && !routeShot.Fired)) && c.op != "background" && c.op != "recording")
                throw new InvalidOperationException("Cancel the route before issuing a game command");
            if (c.op == "environment") return ExportWorldEnvironment();
            if (c.op == "station-geometry") return ExportStationGeometry();
            if (c.op == "capture-environment") return StartWorldCapture(c.speed);
            if (c.op == "capture-cancel") { if (worldCaptureActive) StopWorldCapture("cancelled"); return new { worldCaptureStatus }; }
            if (c.op == "trial") return StartTrial(c);
            if (c.op == "background") { Application.runInBackground = c.enabled; return new { enabled = Application.runInBackground }; }
            if (c.op == "trial-cancel") { if (trial != null) FailTrial(new OperationCanceledException("Cancelled by controller")); return new { trialStatus }; }
            if (c.op == "bodies") return Resources.FindObjectsOfTypeAll<OWRigidbody>()
                .Where(b => b.gameObject.scene.IsValid()).Select(b => new { b.name, active = b.gameObject.activeInHierarchy }).ToArray();
            if (c.op == "recording")
            {
                if (c.playerLogMode != "low" && c.playerLogMode != "full" && c.playerLogMode != "off") throw new ArgumentException("playerLogMode must be low, full or off");
                playerLogMode = c.playerLogMode;
                nextPlayerSample = 0;
                playerRoute.Flush();
                Event("player_recording", new { playerLogMode });
                return new { playerLogMode, intervalGameSeconds = playerLogMode == "low" ? 1f : 0f, restoreBurstSeconds = 3f };
            }
            if (SceneManager.GetActiveScene().name != "SolarSystem" || Locator.GetPlayerBody() == null)
                throw new InvalidOperationException("Enter the SolarSystem scene first");
            if (c.op == "speed")
            {
                if (!Finite(c.speed) || c.speed < 1f || c.speed > 4f) throw new ArgumentException("speed must be between 1 and 4");
                float stepBefore = Time.fixedDeltaTime;
                OWTime.SetTimeScale(c.speed);
                speedChanged = c.speed != 1f;
                Event("time_scale", new { requestedSpeed = c.speed, fixedDeltaTime = Time.fixedDeltaTime, paused = OWTime.IsPaused() });
                if (Time.fixedDeltaTime != stepBefore) throw new InvalidOperationException("Unexpected physics timestep change");
                return new { requestedSpeed = c.speed, fixedDeltaTime = Time.fixedDeltaTime, paused = OWTime.IsPaused(), equivalenceValidated = false };
            }
            if (c.op == "mark") return SavePlayerPoint();
            if (c.op == "wake") return WakePlayer();
            if (c.op == "pause")
            {
                var menu = Locator.GetSceneMenuManager().pauseMenu;
                if (!menu.IsOpen() && !menu.TryOpenPauseMenu()) throw new InvalidOperationException("Cannot open pause menu now");
                if (speedChanged) { OWTime.SetTimeScale(1f); speedChanged = false; Event("time_scale", new { requestedSpeed = 1f, reason = "pause" }); }
                playerRoute.Flush();
                trace.Flush();
                return new { paused = true };
            }
            if (c.op == "resume")
            {
                var menu = Locator.GetSceneMenuManager().pauseMenu;
                if (menu.IsOpen())
                {
                    var nativeMenu = (Menu)typeof(PauseMenuManager).GetField("_pauseMenu", Flags).GetValue(menu);
                    if (MenuStackManager.SharedInstance.Peek() != nativeMenu) throw new InvalidOperationException("Close the submenu first");
                    nativeMenu.EnableMenu(false);
                }
                return new { inputMode = OWInput.GetInputMode().ToString() };
            }
            if (!OWInput.IsInputMode(InputMode.Character)) throw new InvalidOperationException("Player must be awake, unpaused, on foot");
            var swapper = Locator.GetToolModeSwapper();
            var launcher = swapper.GetProbeLauncher();
            var probe = Locator.GetProbe();
            switch (c.op)
            {
                case "equip":
                    swapper.EquipToolMode(ToolMode.Probe);
                    return new { requested = "Probe" };
                case "look":
                case "aim":
                    if (float.IsNaN(c.pitch) || float.IsInfinity(c.pitch) || c.pitch < -80 || c.pitch > 80)
                        throw new ArgumentException("pitch must be finite, between -80 and 80 controller degrees");
                    var camera = Locator.GetPlayerCameraController();
                    if (c.op == "aim")
                    {
                        if (!Finite(c.yaw) || c.yaw < -180f || c.yaw > 180f) throw new ArgumentException("yaw must be between -180 and 180");
                        var location = locationOverride ?? ReadSummit();
                        var planet = Locator.GetAstroObject(AstroObject.Name.TimberHearth).GetOWRigidbody();
                        var baseRotation = planet.transform.rotation * Q(location.rotationLocal);
                        Locator.GetPlayerBody().SetRotation(Quaternion.AngleAxis(c.yaw, baseRotation * Vector3.up) * baseRotation);
                    }
                    SetManualPitch(c.pitch);
                    return new { c.pitch, c.yaw, relativeToSavedHeading = c.op == "aim" };
                case "restore":
                    if (probe == null || !probe.IsRetrieved() || probe.IsRetrieving()) throw new InvalidOperationException("Retrieve the probe before relocating");
                    var point = locationOverride ?? ReadSummit();
                    var thBody = Locator.GetAstroObject(AstroObject.Name.TimberHearth).GetOWRigidbody();
                    var player = Locator.GetPlayerBody();
                    Vector3 target = thBody.transform.TransformPoint(P(point.positionLocal));
                    Quaternion rotation = thBody.transform.rotation * Q(point.rotationLocal);
                    if (!Locator.GetPlayerSuit().IsWearingSuit()) Locator.GetPlayerSuit().SuitUp(false, true, true);
                    typeof(PlayerCharacterController).GetMethod("MakeUngrounded", Flags).Invoke(Locator.GetPlayerController(), null);
                    player.WarpToPositionRotation(target, rotation);
                    player.SetVelocity(thBody.GetPointVelocity(target));
                    player.SetAngularVelocity(Vector3.zero);
                    playerBurstUntil = Time.fixedTime + 3f;
                    nextPlayerSample = 0;
                    SetManualPitch(point.pitch);
                    var restored = new { source = locationOverride == null ? "locations/snow-summit.json" : "trial.launchLocation",
                        targetLocal = point.positionLocal, pose = PlayerPose(), diagnosticTeleport = true };
                    Event("restore_summit", restored);
                    return restored;
                case "launch":
                    return LaunchNativeProbe(launcher, probe);
                case "retrieve":
                    if (probe == null || !probe.IsLaunched()) throw new InvalidOperationException("No launched probe to retrieve");
                    retrieve.Invoke(launcher, new object[] { true, false });
                    return new { requested = "retrieve" };
                default: throw new ArgumentException("Unknown operation: " + c.op);
            }
        }

        private object LaunchNativeProbe(ProbeLauncher launcher, SurveyorProbe probe)
        {
            ValidateManualDirection();
            if (!launcher.IsEquipped() || launcher.InPhotoMode() || !launcher.AllowLaunchMode())
                throw new InvalidOperationException("Equip the hand launcher in launch mode first");
            if (probe == null || !probe.IsRetrieved() || probe.IsRetrieving())
                throw new InvalidOperationException("Probe must be fully retrieved before launch");
            Vector3 playerVelocityBefore = Locator.GetPlayerBody().GetVelocity();
            var before = Snapshot();
            fire.Invoke(launcher, null);
            if (!probe.IsLaunched()) throw new InvalidOperationException("Native launch did not launch a probe");
            Vector3 delta = probe.GetOWRigidbody().GetVelocity() - playerVelocityBefore;
            var launch = new { before, after = Snapshot(), velocityRelativeToPlayerBefore = V(delta), speedRelativeToPlayerBefore = delta.magnitude };
            Event("native_launch", launch);
            trace.Flush();
            return launch;
        }

        public void FixedUpdate()
        {
            fixedStep++;
            if (trace == null || SceneManager.GetActiveScene().name != "SolarSystem") return;
            try
            {
                StepTrial();
                StepWorldCapture();
                StepRouteReplay();
                StepRouteShot();
                // Check the cheap schedule before constructing a pose or reading input axes.
                bool samplePlayer = playerLogMode != "off" && (playerLogMode == "full" || Time.fixedTime < playerBurstUntil || Time.fixedTime >= nextPlayerSample);
                var pose = samplePlayer ? PlayerPose() : null;
                if (pose != null)
                {
                    nextPlayerSample = Time.fixedTime + 1f;
                    var move = OWInput.GetAxisValue(InputLibrary.moveXZ, InputMode.All);
                    var look = OWInput.GetAxisValue(InputLibrary.look, InputMode.All);
                    playerRoute.WriteLine(JsonConvert.SerializeObject(new {
                        pose, recordingMode = playerLogMode, move = new[] { move.x, move.y }, look = new[] { look.x, look.y },
                        jump = OWInput.IsPressed(InputLibrary.jump, 0f),
                        boost = OWInput.IsPressed(InputLibrary.boost, 0f),
                        thrustUp = OWInput.IsPressed(InputLibrary.thrustUp, 0f),
                        thrustDown = OWInput.IsPressed(InputLibrary.thrustDown, 0f)
                    }));
                    playerSamples++;
                }
                if (Time.realtimeSinceStartup >= nextPlayerFlush) { playerRoute.Flush(); nextPlayerFlush = Time.realtimeSinceStartup + 1f; }
                var probe = Locator.GetProbe();
                if (probe == null) return;
                if (observedProbe != probe) { observedProbe = probe; wasLaunched = false; wasAnchored = false; }
                bool launched = probe.IsLaunched(), anchored = probe.IsAnchored();
                if (launched != wasLaunched || anchored != wasAnchored)
                {
                    Event("probe_state", new { launched, anchored, fixedStep, loopSeconds = TimeLoop.GetSecondsElapsed() });
                    trace.Flush();
                }
                wasLaunched = launched; wasAnchored = anchored;
                RecordRouteShotProbe(probe);
                if (!launched) return;
                var th = Locator.GetAstroObject(AstroObject.Name.TimberHearth).GetOWRigidbody();
                var sun = Locator.GetAstroObject(AstroObject.Name.Sun).GetOWRigidbody();
                var body = probe.GetOWRigidbody();
                Vector3 p = body.GetPosition(), rel = p - th.GetPosition();
                Vector3 velocity = body.GetVelocity() - th.GetVelocity();
                Vector3 sunRel = p - sun.GetPosition();
                float distance = station == null ? float.NaN : Vector3.Distance(p, station.GetPosition());
                trace.WriteLine(string.Join(",", new[] { fixedStep.ToString(), F(TimeLoop.GetSecondsElapsed()), launched ? "1" : "0", anchored ? "1" : "0",
                    F(rel.x), F(rel.y), F(rel.z), F(velocity.x), F(velocity.y), F(velocity.z), F(sunRel.x), F(sunRel.y), F(sunRel.z), F(distance) }));
                if (fixedStep % 60 == 0) trace.Flush();
            }
            catch (Exception ex) { Debug.LogError("[SunStation] telemetry: " + ex); }
        }

        private object Snapshot()
        {
            string scene = SceneManager.GetActiveScene().name;
            if (scene != "SolarSystem" || Locator.GetPlayerBody() == null)
                return new { scene, fixedStep, session, utc = DateTime.UtcNow.ToString("o"), ready = false };
            var probe = Locator.GetProbe();
            var player = Locator.GetPlayerBody();
            var th = Locator.GetAstroObject(AstroObject.Name.TimberHearth);
            var sun = Locator.GetAstroObject(AstroObject.Name.Sun);
            var launcher = Locator.GetToolModeSwapper().GetProbeLauncher();
            var probeBody = probe == null ? null : probe.GetOWRigidbody();
            return new {
                scene, fixedStep, session, utc = DateTime.UtcNow.ToString("o"), ready = true,
                recorderVersion = "0.6.3", maxDiagnosticFlightSpeed = MaxDiagnosticFlightSpeed, launchLocationSupported = true, worldCaptureStatus, worldCaptureIndex, worldCapturePath, playerSamples, playerLogMode, savedPoints, playerPose = PlayerPose(), runInBackground = Application.runInBackground,
                trialStatus, trialLoopStep, trialAttempt, sleepWaiting = trialCampfire != null, routeReplayStatus,
                routeShotStatus, routeShotSecondsToLaunch = RouteShotSecondsRemaining(), quantum = QuantumObservation(),
                loopSeconds = TimeLoop.GetSecondsElapsed(), fixedDeltaTime = Time.fixedDeltaTime,
                configuredTimeScale = OWTime.GetTimeScale(), wallSeconds = Time.realtimeSinceStartup,
                timeScale = Time.timeScale, inputMode = OWInput.GetInputMode().ToString(),
                toolMode = Locator.GetToolModeSwapper().GetToolMode().ToString(),
                launcherEquipped = launcher != null && launcher.IsEquipped(),
                photoMode = launcher != null && launcher.InPhotoMode(),
                playerVelocity = V(player.GetVelocity()),
                playerRelativeToTH = th == null ? null : V(player.GetPosition() - th.GetOWRigidbody().GetPosition()),
                cameraForward = V(Locator.GetPlayerCamera().transform.forward),
                pitch = Locator.GetPlayerCameraController().GetDegreesY(),
                probeLaunched = probe != null && probe.IsLaunched(),
                probeAnchored = probe != null && probe.IsAnchored(),
                probeRetrieved = probe != null && probe.IsRetrieved(),
                probeVelocity = probeBody == null ? null : V(probeBody.GetVelocity()),
                probeRelativeToTH = probeBody == null || th == null ? null : V(probeBody.GetPosition() - th.GetOWRigidbody().GetPosition()),
                probeRelativeToSun = probeBody == null || sun == null ? null : V(probeBody.GetPosition() - sun.GetOWRigidbody().GetPosition()),
                stationBodyFound = station != null,
                stationDistance = station == null || probeBody == null ? (float?)null : Vector3.Distance(probeBody.GetPosition(), station.GetPosition())
            };
        }

        // Body-fixed position and orientation remain meaningful across planet motion and origin shifts.
        private PlayerPoseSample PlayerPose()
        {
            if (SceneManager.GetActiveScene().name != "SolarSystem") return null;
            var player = Locator.GetPlayerBody();
            var th = Locator.GetAstroObject(AstroObject.Name.TimberHearth);
            if (player == null || th == null || Locator.GetPlayerCamera() == null) return null;
            var planet = th.GetOWRigidbody();
            var frame = planet.transform;
            var controller = Locator.GetPlayerController();
            Vector3 p = player.GetPosition();
            Vector3 v = player.GetVelocity() - planet.GetPointVelocity(p);
            var ground = controller == null ? null : controller.GetGroundBody();
            Quaternion q = Quaternion.Inverse(frame.rotation) * player.transform.rotation;
            bool grounded = controller != null && controller.IsGrounded();
            return new PlayerPoseSample {
                schemaVersion = 1, utc = DateTime.UtcNow.ToString("o"), fixedStep = fixedStep,
                loopSeconds = TimeLoop.GetSecondsElapsed(), referenceBody = "TimberHearth",
                positionLocal = V(frame.InverseTransformPoint(p)),
                rotationLocal = new[] { q.x, q.y, q.z, q.w },
                cameraForwardLocal = V(frame.InverseTransformDirection(Locator.GetPlayerCamera().transform.forward)),
                upLocal = V(frame.InverseTransformDirection(player.transform.up)),
                velocityRelativeToSurfaceLocal = V(frame.InverseTransformDirection(v)),
                speedRelativeToSurface = v.magnitude,
                radius = (p - planet.GetPosition()).magnitude,
                grounded = grounded, groundBody = ground == null ? null : ground.name,
                groundContactLocal = !grounded ? null : V(frame.InverseTransformPoint(controller.GetGroundContactPoint())),
                stableCandidate = grounded && ground == planet && v.magnitude < 0.2f,
                wearingSuit = Locator.GetPlayerSuit() != null && Locator.GetPlayerSuit().IsWearingSuit(),
                inputMode = OWInput.GetInputMode().ToString(), pitch = Locator.GetPlayerCameraController().GetDegreesY()
            };
        }

        private object SavePlayerPoint()
        {
            var pose = PlayerPose();
            if (pose == null) throw new InvalidOperationException("Player/Timber Hearth not ready");
            savedPoints++;
            string path = Path.Combine(session, "point-" + savedPoints.ToString("D3") + ".json");
            AtomicJson(path, pose);
            Directory.CreateDirectory(Path.Combine(root, "locations"));
            AtomicJson(Path.Combine(root, "locations", "marked-point-latest.json"), new { session, path, pose });
            playerRoute.Flush();
            Event("saved_player_point", new { path, pose });
            lastMessage = "POINT SAVED #" + savedPoints;
            return new { path, pose };
        }

        private object WakePlayer()
        {
            var effect = Locator.GetPlayerCamera().GetComponent<PlayerCameraEffectController>();
            var wait = typeof(PlayerCameraEffectController).GetField("_waitForWakeInput", Flags);
            if (effect == null || !(bool)wait.GetValue(effect)) throw new InvalidOperationException("Not waiting for wake input");
            if (!LateInitializerManager.isDoneInitializing) throw new InvalidOperationException("Scene is still initializing");
            // Mirror the native interact branch, including its initialization pause and prompt cleanup.
            wait.SetValue(effect, false);
            LateInitializerManager.pauseOnInitialization = false;
            Locator.GetPauseCommandListener().RemovePauseCommandLock();
            var prompt = (ScreenPrompt)typeof(PlayerCameraEffectController).GetField("_wakePrompt", Flags).GetValue(effect);
            Locator.GetPromptManager().RemoveScreenPrompt(prompt);
            OWTime.Unpause((OWTime.PauseType)3);
            typeof(PlayerCameraEffectController).GetMethod("WakeUp", Flags).Invoke(effect, null);
            Event("native_wake", new { });
            return new { requested = "wake" };
        }

        private Location ReadSummit()
        {
            return ValidateLocation(JsonConvert.DeserializeObject<Location>(
                File.ReadAllText(summitSource)));
        }

        private static Location ValidateLocation(Location point)
        {
            if (point == null || point.schemaVersion != 1 || point.referenceBody != "TimberHearth") throw new ArgumentException("Invalid saved location reference");
            var p = P(point.positionLocal);
            var q = Q(point.rotationLocal);
            if (p.magnitude < 300 || p.magnitude > 400 || !Finite(point.pitch) || Mathf.Abs(point.pitch) > 80) throw new ArgumentException("Saved summit is out of bounds");
            if (Vector3.Angle(q * Vector3.up, p.normalized) > 0.1f) throw new ArgumentException("Saved rotation contains a non-upright body orientation");
            return point;
        }

        private void SetManualPitch(float pitch)
        {
            if (!Finite(pitch) || pitch < -80f || pitch > 80f) throw new ArgumentException("Pitch is outside the native on-foot range");
            var camera = Locator.GetPlayerCameraController();
            if ((bool)typeof(PlayerCameraController).GetField("_isSnapping", Flags).GetValue(camera) ||
                (bool)typeof(PlayerCameraController).GetField("_isLockedOn", Flags).GetValue(camera))
                throw new InvalidOperationException("Camera must use ordinary on-foot look");
            setPitch.Invoke(camera, new object[] { pitch });
            if (Mathf.Abs(camera.GetDegreesY() - pitch) > 0.01f) throw new InvalidOperationException("Native camera rejected requested pitch");
        }

        private void ValidateManualDirection()
        {
            var camera = Locator.GetPlayerCameraController();
            var player = Locator.GetPlayerBody();
            var th = Locator.GetAstroObject(AstroObject.Name.TimberHearth).GetOWRigidbody();
            float pitch = camera.GetDegreesY();
            if (!Finite(pitch) || pitch < -80.01f || pitch > 80.01f) throw new InvalidOperationException("Camera pitch exceeds the manual range");
            if ((bool)typeof(PlayerCameraController).GetField("_isSnapping", Flags).GetValue(camera) ||
                (bool)typeof(PlayerCameraController).GetField("_isLockedOn", Flags).GetValue(camera))
                throw new InvalidOperationException("Non-manual camera mode at launch");
            Vector3 up = player.transform.up;
            Vector3 forward = Locator.GetPlayerCamera().transform.forward;
            float elevation = 90f - Vector3.Angle(forward, up);
            float sideways = Mathf.Abs(Vector3.Dot(forward, player.transform.right));
            if (Mathf.Abs(elevation - pitch) > 0.05f || sideways > 0.001f ||
                Vector3.Angle(up, (player.GetPosition() - th.GetPosition()).normalized) > 0.1f)
                throw new InvalidOperationException("Launch view is outside an upright on-foot yaw/pitch orientation");
        }
        private static bool Finite(float f) { return !float.IsNaN(f) && !float.IsInfinity(f); }
        private static Vector3 P(float[] a)
        {
            if (a == null || a.Length != 3 || a.Any(f => !Finite(f))) throw new ArgumentException("Invalid vector");
            return new Vector3(a[0], a[1], a[2]);
        }
        private static Quaternion Q(float[] a)
        {
            if (a == null || a.Length != 4 || a.Any(f => !Finite(f)) || Mathf.Abs(a.Sum(f => f * f) - 1f) > 0.01f) throw new ArgumentException("Invalid quaternion");
            return new Quaternion(a[0], a[1], a[2], a[3]);
        }

        private void Event(string kind, object data)
        { events.WriteLine(JsonConvert.SerializeObject(new { utc = DateTime.UtcNow.ToString("o"), fixedStep, kind, data })); }
        private static float[] V(Vector3 v) { return new[] { v.x, v.y, v.z }; }
        private static string F(float value) { return value.ToString("R", CultureInfo.InvariantCulture); }
        private static void AtomicJson(string path, object value)
        {
            string tmp = path + ".tmp";
            File.WriteAllText(tmp, JsonConvert.SerializeObject(value, Formatting.Indented));
            if (File.Exists(path)) File.Replace(tmp, path, null); else File.Move(tmp, path);
        }
        public void OnGUI()
        {
            if (root == null) return;
            string idleControls = developmentMode
                ? lastMessage + " | PLAYER LOG " + playerLogMode + " (" + playerSamples + ") | F6: SAVE POINT (" + savedPoints + ")"
                : lastMessage + " | F8: START REPLAY";
            GUI.Label(new Rect(12, 12, Screen.width - 24, 40), "SUN STATION | " + (RouteShotHud() ?? idleControls));
        }
        public void OnDestroy() { FinishRouteShot("mod_unloaded"); DestroyRouteReplay(); Application.runInBackground = backgroundOriginal; SceneManager.sceneLoaded -= OnTrialSceneLoaded; if (trialTrace != null) trialTrace.Dispose(); if (speedChanged) OWTime.SetTimeScale(1f); if (trace != null) trace.Dispose(); if (events != null) events.Dispose(); if (playerRoute != null) playerRoute.Dispose(); }
        public sealed class Command { public string id; public string op; public float pitch; public float yaw; public float speed; public float flightSpeed; public string playerLogMode; public string label; public string routePath; public bool enabled; public bool search; public bool replay; public bool waitWithSleep; public bool routeShotAimOverride; public int launchStep; public int flightSteps; public Location launchLocation; }
        public sealed class Location { public int schemaVersion { get; set; } public string referenceBody { get; set; } public float[] positionLocal { get; set; } public float[] rotationLocal { get; set; } public float pitch { get; set; } }
    }
}
