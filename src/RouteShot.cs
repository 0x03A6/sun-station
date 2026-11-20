using System;
using System.Globalization;
using System.IO;
using System.Text.RegularExpressions;
using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.SceneManagement;

namespace SunStation
{
    public sealed partial class Control
    {
        private const int SummitLaunchStep = 17313;
        private const float SummitLaunchYaw = 14.537644f, SummitLaunchPitch = 47.33874f;
        // Actual launch pose of the confirmed summit-aim-tight-1 attachment.
        private static readonly Vector3 SummitHitPosition = new Vector3(-127.39106f, 30.237f, -307.866f);
        private const float AimDurationSeconds = 2.4f;
        private const float AimToleranceDegrees = 0.0002f;
        private RouteShotRun routeShot;
        private string routeShotStatus = "idle";

        private object StartRouteShot(Command command)
        {
            if (routeShot != null || routeReplay != null || trial != null || worldCaptureActive)
                throw new InvalidOperationException("A game task is already running");
            string label = string.IsNullOrEmpty(command.label) ? command.id : command.label;
            if (!Regex.IsMatch(label, "^[A-Za-z0-9_-]{1,40}$")) throw new ArgumentException("Invalid route label");
            int targetStep = command.launchStep == 0 ? SummitLaunchStep : command.launchStep;
            float yaw = command.routeShotAimOverride ? command.yaw : SummitLaunchYaw;
            float pitch = command.routeShotAimOverride ? command.pitch : SummitLaunchPitch;
            if (targetStep < 1 || targetStep > 36000 || !Finite(yaw) || Math.Abs(yaw) > 180f || !Finite(pitch) || Math.Abs(pitch) > 80f)
                throw new ArgumentException("Invalid route launch step or aiming angles");

            var shot = new RouteShotRun(session, label, targetStep, yaw, pitch, ReadSummit());
            try
            {
                object route = StartRouteReplay(command, targetStep, shot.TargetPosition);
                routeShot = shot;
                routeShotStatus = "loading";
                Event("route_shot_requested", new { label, targetStep, yaw, pitch,
                    targetPositionLocal = V(shot.TargetPosition), targetDirectionLocal = V(shot.TargetDirection),
                    waitWithSleep = false, quantumGate = false, trajectoryPath = shot.Path });
                return new { route, label, targetStep, yaw, pitch, launchPath = shot.LaunchPath,
                    trajectoryPath = shot.Path, shotResultPath = shot.ResultPath };
            }
            catch
            {
                if (routeShot == shot) routeShot = null;
                shot.Trace.Dispose();
                throw;
            }
        }

        private void OnRouteShotSceneLoaded(Scene scene)
        {
            if (routeShot == null) return;
            if (routeShot.SceneStarted || scene.name != "SolarSystem") FinishRouteShot("scene_changed");
            else
            {
                routeShot.SceneStarted = true;
                routeShotStatus = "route";
            }
        }

        private void OnRouteShotRouteFinished(string reason, PlayerPoseSample pose)
        {
            var shot = routeShot;
            if (shot == null || shot.Fired || shot.AimStart != null) return;
            if (reason != "completed")
            {
                FinishRouteShot(reason);
                return;
            }
            shot.AimStart = shot.Pose = pose;
            shot.AimStartStep = trialLoopStep;
            routeShotStatus = "aiming";
            shot.AimCompletionStep = new int?();
            Event("route_shot_aim_started", new { shot.Label, trialLoopStep, pose, targetDirectionLocal = V(shot.TargetDirection) });
        }

        private void UpdateRouteShot()
        {
            var keyboard = Keyboard.current;
            if (keyboard == null) return;
            if (routeShot != null && !routeShot.Fired && routeReplay == null &&
                (keyboard.f7Key.wasPressedThisFrame || keyboard.escapeKey.wasPressedThisFrame))
            {
                FinishRouteShot("cancelled");
                return;
            }

            if (!keyboard.f8Key.wasPressedThisFrame || !LateInitializerManager.isDoneInitializing ||
                SceneManager.GetActiveScene().name != "SolarSystem" || trial != null || worldCaptureActive ||
                routeReplay != null || (routeShot != null && !routeShot.Fired)) return;

            if (routeShot != null) FinishRouteShot(routeShot.AnchorAge.HasValue ? "anchored" : "restarted");
            string label = "capture-" + DateTime.Now.ToString("yyyyMMdd-HHmmss-fff", CultureInfo.InvariantCulture);
            StartRouteShot(new Command { label = label });
        }

        private void StepRouteShot()
        {
            var shot = routeShot;
            if (shot == null || !shot.SceneStarted) return;
            ObserveRouteShotMoon(shot);
            if (shot.Fired || shot.AimStart == null) return;
            try
            {
                var pose = PlayerPose();
                if (pose == null) throw new InvalidOperationException("Player pose unavailable");
                shot.Pose = pose;
                Vector2 angularError = shot.AimError(shot.TargetDirection);
                float viewError = shot.ViewErrorDegrees();
                bool aligned = angularError.magnitude <= AimToleranceDegrees && viewError <= AimToleranceDegrees;
                if (!shot.AimCompletionStep.HasValue && trialLoopStep - shot.AimStartStep >= AimDurationSeconds * 60f
                    && aligned)
                {
                    shot.AimCompletionStep = trialLoopStep;
                    routeShotStatus = "waiting";
                    Event("route_shot_aim_ready", new { shot.Label, trialLoopStep, angularErrorDegrees = angularError.magnitude,
                        yawErrorDegrees = angularError.x, pitchErrorDegrees = angularError.y,
                        viewErrorDegrees = viewError, aimToleranceDegrees = AimToleranceDegrees,
                        positionErrorMeters = Vector3.Distance(P(pose.positionLocal), shot.TargetPosition) });
                }
                var swapper = Locator.GetToolModeSwapper();
                if (!shot.EquipRequested && trialLoopStep >= shot.TargetStep - 240)
                {
                    swapper.EquipToolMode(ToolMode.Probe);
                    shot.EquipRequested = true;
                }
                if (trialLoopStep < shot.TargetStep) return;
                if (trialLoopStep != shot.TargetStep) throw new InvalidOperationException("Scheduled launch step passed");
                float positionError = Vector3.Distance(P(pose.positionLocal), shot.TargetPosition);
                if (!pose.stableCandidate || !pose.wearingSuit || pose.inputMode != "Character" || positionError > SummitPositionToleranceMeters)
                    throw new InvalidOperationException(FormattableString.Invariant(
                        $"Launch requires a stable pose within {SummitPositionToleranceMeters * 1000f:G} mm of the saved summit (error {positionError * 1000f:F2} mm)"));
                if (!shot.AimCompletionStep.HasValue || !aligned)
                    throw new InvalidOperationException(FormattableString.Invariant(
                        $"Launch view missed the {AimToleranceDegrees:G} degree tolerance: yaw {angularError.x:G}, pitch {angularError.y:G}, actual view {viewError:G} degrees"));

                var launch = LaunchNativeProbe(swapper.GetProbeLauncher(), Locator.GetProbe());
                shot.LaunchFixedStep = fixedStep;
                shot.LaunchMoonState = shot.MoonState;
                shot.MoonWarningAtLaunch = shot.MoonWarning;
                routeShotStatus = "recording";
                var receipt = new { shot.Label, launchStep = trialLoopStep, loopSeconds = pose.loopSeconds,
                    shot.Yaw, shot.Pitch, targetPositionLocal = V(shot.TargetPosition),
                    targetDirectionLocal = V(shot.TargetDirection), angularErrorDegrees = angularError.magnitude,
                    yawErrorDegrees = angularError.x, pitchErrorDegrees = angularError.y,
                    viewErrorDegrees = viewError, aimToleranceDegrees = AimToleranceDegrees,
                    positionErrorMeters = positionError, positionToleranceMeters = SummitPositionToleranceMeters,
                    shot.AimStartStep, shot.AimCompletionStep, shot.MoonWarningAtLaunch,
                    shot.LaunchMoonState, timberQuantumState, configuredTimeScale = OWTime.GetTimeScale(), waitWithSleep = false,
                    launch, trajectoryPath = shot.Path };
                AtomicJson(shot.LaunchPath, receipt);
                Event("route_shot_launched", receipt);
                lastMessage = "Scout launched";
            }
            catch (Exception ex) { FinishRouteShot("error", ex.GetBaseException().Message); }
        }

        private void ObserveRouteShotMoon(RouteShotRun shot)
        {
            if (trialMoon == null || timberQuantumState < 0)
            {
                if (trialLoopStep < shot.NextMoonCheck) return;
                shot.NextMoonCheck = trialLoopStep + 120;
                try { EnsureQuantumCache(); }
                catch (Exception ex)
                {
                    if (!shot.MoonLookupError) Event("route_shot_moon_unavailable", new { error = ex.GetBaseException().Message });
                    shot.MoonLookupError = true;
                    return;
                }
            }
            int state = trialMoon.GetStateIndex();
            if (state == shot.MoonState) return;
            shot.MoonState = state;
            shot.MoonWarning = state == timberQuantumState;
            Event("route_shot_moon_state", new { shot.Label, trialLoopStep, state, timberQuantumState, warning = shot.MoonWarning });
        }

        private void RecordRouteShotProbe(SurveyorProbe probe)
        {
            var shot = routeShot;
            if (shot == null || !shot.Fired) return;
            try
            {
                long age = fixedStep - shot.LaunchFixedStep;
                if (age >= 2 && (!probe.IsLaunched() || probe.IsRetrieving()))
                {
                    FinishRouteShot(probe.IsRetrieving() || probe.IsRetrieved() ? "probe_retrieved" : "probe_lost");
                    return;
                }
                var body = probe.GetOWRigidbody();
                var sun = Locator.GetAstroObject(AstroObject.Name.Sun).GetOWRigidbody();
                var th = Locator.GetAstroObject(AstroObject.Name.TimberHearth).GetOWRigidbody();
                Vector3 p = body.GetPosition(), origin = sun.GetPosition(), ps = p - origin, pt = p - th.GetPosition();
                Vector3 v = body.GetVelocity() - sun.GetVelocity(), ts = th.GetPosition() - origin;
                Vector3 ss = station == null ? new Vector3(float.NaN, float.NaN, float.NaN) : station.GetPosition() - origin;
                float moonDistance = trialMoonBody == null ? float.NaN : Vector3.Distance(trialMoonBody.GetPosition(), th.GetPosition());
                bool anchored = probe.IsAnchored();
                shot.Trace.WriteLine(string.Join(",", new[] { age.ToString(), trialLoopStep.ToString(), F(TimeLoop.GetSecondsElapsed()),
                    F(Time.realtimeSinceStartup), F(Time.timeScale), F(Time.fixedDeltaTime), F(ps.x), F(ps.y), F(ps.z), F(pt.x), F(pt.y), F(pt.z),
                    F(v.x), F(v.y), F(v.z), F(ts.x), F(ts.y), F(ts.z), F(ss.x), F(ss.y), F(ss.z),
                    probe.IsLaunched() ? "1" : "0", anchored ? "1" : "0", shot.MoonState.ToString(),
                    timberQuantumState.ToString(), F(moonDistance), Time.frameCount.ToString() }));
                shot.LastAge = age;
                if (age >= 2 && station != null) shot.Closest = Mathf.Min(shot.Closest, Vector3.Distance(ps, ss));
                if (age % 60 == 0) shot.Trace.Flush();
                if (anchored && shot.AnchorAge == null)
                {
                    shot.AnchorAge = age;
                    var parent = body.transform.parent;
                    var anchor = parent == null ? null : parent.GetComponentInParent<OWRigidbody>();
                    shot.AnchorBody = anchor == null ? null : anchor.name;
                    shot.Trace.Flush();
                    routeShotStatus = "anchored";
                    WriteRouteShotResult(shot, "anchored", null);
                }
            }
            catch (Exception ex) { FinishRouteShot("recording_error", ex.GetBaseException().Message); }
        }

        private void FinishRouteShot(string reason, string error = null)
        {
            var shot = routeShot;
            if (shot == null) return;
            routeShot = null;
            routeShotStatus = reason;
            lastMessage = "Route shot: " + reason + (error == null ? "" : " | " + error);
            shot.Trace.Dispose();
            WriteRouteShotResult(shot, reason, error);
        }

        private void WriteRouteShotResult(RouteShotRun shot, string reason, string error)
        {
            var result = new { shot.Label, reason, error, launchStep = shot.TargetStep, fired = shot.Fired,
                shot.Yaw, shot.Pitch, firstAnchorAge = shot.AnchorAge, anchorBody = shot.AnchorBody,
                stationAnchorConfirmed = shot.AnchorBody == "SunStation_Body", actualFlightSteps = shot.LastAge,
                minSunStationDistance = Finite(shot.Closest) ? (float?)shot.Closest : null, shot.MoonWarningAtLaunch,
                shot.LaunchMoonState, trajectoryPath = shot.Path, launchPath = shot.LaunchPath };
            AtomicJson(shot.ResultPath, result);
            Event("route_shot_" + reason, result);
        }

        private float? RouteShotSecondsRemaining()
        {
            var shot = routeShot;
            if (shot == null || shot.Fired) return null;
            return Mathf.Max(0, shot.TargetStep - (shot.SceneStarted ? trialLoopStep : 0)) / 60f;
        }

        private string RouteShotHud()
        {
            var shot = routeShot;
            if (shot == null) return null;
            float? remaining = RouteShotSecondsRemaining();
            string text = remaining.HasValue
                ? "LAUNCH IN " + FormatRouteTime(remaining.Value) + " | " + routeShotStatus.ToUpperInvariant()
                : (shot.AnchorAge.HasValue ? "SCOUT ANCHORED" : "SCOUT LAUNCHED") + " | RECORDING";
            return text + (shot.MoonWarning ? " | WARNING: Quantum Moon at Timber Hearth" : "");
        }

        private static string FormatRouteTime(float seconds)
        {
            int tenths = Mathf.CeilToInt(seconds * 10f - 0.0001f);
            return string.Format(CultureInfo.InvariantCulture, "{0:00}:{1:00}.{2}", tenths / 600, (tenths / 10) % 60, tenths % 10);
        }

        private static RouteShotRun CurrentShotAim
        {
            get
            {
                var shot = inputReplayOwner == null ? null : inputReplayOwner.routeShot;
                return shot != null && shot.AimStart != null && !shot.Fired ? shot : null;
            }
        }

        private static bool AimRouteShotAxis(IInputCommands command, InputMode mask, ref Vector2 result)
        {
            var shot = CurrentShotAim;
            if (shot == null || (command != InputLibrary.look && command != InputLibrary.moveXZ)) return true;
            result = command == InputLibrary.look && OWInput.IsInputMode(mask) && OWInput.GetInputMode() == InputMode.Character
                ? shot.LookInput(inputReplayOwner.trialLoopStep) : Vector2.zero;
            return false;
        }

        private static bool BlockRouteShotButton<T>(IInputCommands command, ref T result)
        {
            if (CurrentShotAim == null || (RouteButtonFor(command) == RouteButton.Native && command != InputLibrary.probeLaunch
                && command != InputLibrary.probeRetrieve && command != InputLibrary.toolActionPrimary
                && command != InputLibrary.toolActionSecondary && command != InputLibrary.signalscope)) return false;
            result = default(T);
            return true;
        }

        private sealed class RouteShotRun
        {
            public readonly string Label, Path, LaunchPath, ResultPath;
            public readonly int TargetStep;
            public readonly float Yaw, Pitch;
            public readonly Vector3 TargetPosition, TargetDirection;
            private readonly Quaternion targetHeading;
            public readonly StreamWriter Trace;
            public PlayerPoseSample AimStart, Pose;
            public bool SceneStarted, EquipRequested, MoonWarning, MoonWarningAtLaunch, MoonLookupError;
            public int AimStartStep, NextMoonCheck = 60, MoonState = -1, LaunchMoonState = -1;
            public int? AimCompletionStep;
            public long LaunchFixedStep = -1, LastAge;
            public long? AnchorAge;
            public string AnchorBody;
            public float Closest = float.PositiveInfinity;
            public bool Fired { get { return LaunchFixedStep >= 0; } }

            public RouteShotRun(string session, string label, int step, float yaw, float pitch, Location location)
            {
                Label = label;
                TargetStep = step;
                Yaw = yaw;
                Pitch = pitch;
                TargetPosition = SummitHitPosition;
                Quaternion saved = Q(location.rotationLocal);
                targetHeading = Quaternion.AngleAxis(yaw, saved * Vector3.up) * saved;
                TargetDirection = Direction(targetHeading, pitch);
                string stem = System.IO.Path.Combine(session, "route-shot-" + label);
                Path = stem + ".csv";
                LaunchPath = stem + "-launch.json";
                ResultPath = stem + "-result.json";
                Trace = new StreamWriter(new FileStream(Path, FileMode.CreateNew, FileAccess.Write, FileShare.Read));
                Trace.WriteLine("age_step,loop_step,loop_seconds,wall_seconds,time_scale,fixed_dt,probe_sun_x,probe_sun_y,probe_sun_z,probe_th_x,probe_th_y,probe_th_z,probe_vx,probe_vy,probe_vz,th_sun_x,th_sun_y,th_sun_z,station_sun_x,station_sun_y,station_sun_z,launched,anchored,quantum_state,timber_quantum_state,quantum_distance_th,render_frame");
                Trace.Flush();
            }

            private static Vector3 Direction(Quaternion heading, float pitch)
            {
                return heading * (Quaternion.AngleAxis(-pitch, Vector3.right) * Vector3.forward);
            }

            public Vector2 AimError(Vector3 target)
            {
                Vector3 up = P(Pose.upLocal).normalized;
                Vector3 view = P(Pose.cameraForwardLocal);
                Vector3 viewHorizontal = Vector3.ProjectOnPlane(view, up);
                Vector3 targetHorizontal = Vector3.ProjectOnPlane(target, up);
                // Atan2 retains the error at angles where the normalized dot product rounds to one.
                float yaw = Mathf.Atan2(Vector3.Dot(Vector3.Cross(viewHorizontal, targetHorizontal), up),
                    Vector3.Dot(viewHorizontal, targetHorizontal));
                float pitch = Mathf.Atan2(Vector3.Dot(target, up), targetHorizontal.magnitude)
                    - Mathf.Atan2(Vector3.Dot(view, up), viewHorizontal.magnitude);
                return new Vector2(yaw, pitch) * Mathf.Rad2Deg;
            }

            public float ViewErrorDegrees()
            {
                Vector3 view = P(Pose.cameraForwardLocal);
                return Mathf.Atan2(Vector3.Cross(view, TargetDirection).magnitude,
                    Vector3.Dot(view, TargetDirection)) * Mathf.Rad2Deg;
            }

            public Vector2 LookInput(int step)
            {
                float t = Mathf.Clamp01((step - AimStartStep + 1) / (60f * AimDurationSeconds));
                float blend = t * t * (3f - 2f * t);
                Vector3 desired = Direction(Quaternion.Slerp(Q(AimStart.rotationLocal), targetHeading, blend),
                    Mathf.Lerp(AimStart.pitch, Pitch, blend));
                return Vector2.ClampMagnitude(AimError(desired) * 0.065f, 0.36f);
            }
        }
    }
}
