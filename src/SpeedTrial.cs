using System;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace SunStation
{
    public sealed partial class Control
    {
        private Command trial;
        private StreamWriter trialTrace;
        private string trialStatus = "idle";
        private int trialLoopStep, trialStage;
        private float trialWallStart, trialCleanupAt;
        private int trialAttempt, lastQuantumState = -1, timberQuantumState = -1;
        private int trialAimFrame = -1;
        private bool trialQuantumGateStarted;
        private QuantumMoon trialMoon;
        private Campfire trialCampfire;
        private float trialSleepWallStart, trialSleepFixedStep;
        private OWRigidbody trialMoonBody;
        private string trialPath;
        private const int LaunchStep = 600, FlightSteps = 3600;
        private const float MaxDiagnosticFlightSpeed = 24f;
        private float ActiveFlightSpeed { get { return trial.flightSpeed == 0f ? trial.speed : trial.flightSpeed; } }
        private int ActiveLaunchStep { get { return trial != null && trial.launchStep > 0 ? trial.launchStep : LaunchStep; } }
        private const int SearchArrivalDeadlineStep = 670 * 60;
        private int ActiveFlightSteps { get {
            int requested = trial != null && trial.flightSteps > 0 ? trial.flightSteps : FlightSteps;
            return trial != null && trial.search ? Math.Min(requested, SearchArrivalDeadlineStep - ActiveLaunchStep) : requested;
        } }
        private float trialMaxRadius, trialMinDistance, trialLaunchRadius;
        private float trialMinSunRadius, trialLaunchSunRadius, trialStationOrbitRadius;
        private int trialValidSamples, trialMinSunAge;
        private int trialMaxRadiusAge, trialMinDistanceAge, trialFirstAnchorAge;

        private object StartTrial(Command c)
        {
            if (trial != null || worldCaptureActive || routeShot != null) throw new InvalidOperationException("A game task is already running");
            if (SceneManager.GetActiveScene().name != "SolarSystem") throw new InvalidOperationException("Load SolarSystem first");
            if (c.label == null || !Regex.IsMatch(c.label, "^[A-Za-z0-9_-]{1,40}$")) throw new ArgumentException("Invalid trial label");
            if (!Finite(c.speed) || c.speed < 1 || c.speed > 4 || !Finite(c.pitch) || Math.Abs(c.pitch) > 80 || !Finite(c.yaw) || Math.Abs(c.yaw) > 180) throw new ArgumentException("Invalid trial parameters");
            if (c.flightSpeed != 0f && (!c.search || !Finite(c.flightSpeed) || c.flightSpeed < 1f || c.flightSpeed > MaxDiagnosticFlightSpeed))
                throw new ArgumentException("Diagnostic flight speed requires a search trial and must be 1..24");
            if (c.launchStep != 0 && (c.launchStep < 720 || c.launchStep > 36000)) throw new ArgumentException("launchStep must be 720..36000");
            if (c.search && !c.replay && (c.launchStep < 3900 || c.launchStep > 36000)) throw new ArgumentException("Search launch time must be 65..600 seconds");
            if (c.replay && !c.search) throw new ArgumentException("Replay requires the original search preparation schedule");
            if (c.flightSteps != 0 && (c.flightSteps < 600 || c.flightSteps > 36000)) throw new ArgumentException("flightSteps must be 600..36000");
            if (c.launchLocation != null) ValidateLocation(c.launchLocation);
            string path = Path.Combine(session, "speed-" + c.label + ".csv");
            if (File.Exists(path)) throw new InvalidOperationException("Trial label already exists");
            if (Locator.GetSceneMenuManager().pauseMenu.IsOpen()) Execute(new Command { op = "resume" });
            OWTime.SetTimeScale(1f);
            speedChanged = false;
            trial = c;
            trialAttempt = 1;
            OpenTrialTrace();
            trialStage = -1;
            trialStatus = "reloading:" + c.label;
            Event("speed_trial_requested", new { c.label, c.speed, flightSpeed = ActiveFlightSpeed, c.pitch, c.yaw, c.launchLocation, launchStep = ActiveLaunchStep, flightSteps = ActiveFlightSteps, c.search, c.replay, c.waitWithSleep, path, randomState = GameRandomState(), skipQuantumAtTimberHearth = true, quantumGateAfterFinalAim = true });
            trace.Flush(); playerRoute.Flush();
            LoadManager.ReloadSceneAsync(true, false);
            return new { queued = c.label, path, resultPath = Path.Combine(session, "result-" + c.label + ".json") };
        }

        private void OnTrialSceneLoaded(Scene scene, LoadSceneMode mode)
        {
            OnRouteShotSceneLoaded(scene);
            trialLoopStep = 0;
            worldBodies = null;
            if (worldCaptureActive) { worldCaptureStatus = "preparing"; worldCaptureArmed = false; }
            trialCampfire = null;
            station = null;
            trialMoon = null; trialMoonBody = null; timberQuantumState = -1; lastQuantumState = -1;
            trialAimFrame = -1; trialQuantumGateStarted = false;
            trialMaxRadius = 0; trialMinDistance = float.PositiveInfinity; trialFirstAnchorAge = -1; trialMaxRadiusAge = trialMinDistanceAge = 0;
            trialMinSunRadius = float.PositiveInfinity; trialLaunchSunRadius = trialStationOrbitRadius = trialLaunchRadius = 0;
            trialValidSamples = trialMinSunAge = 0;
            nextBodyScan = 0;
            if (trial != null && scene.name == "SolarSystem") { trialStage = 0; trialStatus = "preparing:" + trial.label; }
        }

        private void UpdateTrial()
        {
            if (trial == null || trialStage < 0) return;
            try
            {
                if (trialStage == 0 && LateInitializerManager.isDoneInitializing && Locator.GetPlayerCamera() != null)
                {
                    var effect = Locator.GetPlayerCamera().GetComponent<PlayerCameraEffectController>();
                    var wait = typeof(PlayerCameraEffectController).GetField("_waitForWakeInput", Flags);
                    if (effect != null && (bool)wait.GetValue(effect)) WakePlayer();
                }
                if (trialStage == 4 && Time.realtimeSinceStartup >= trialCleanupAt)
                {
                    var probe = Locator.GetProbe();
                    if (probe != null && (!probe.IsRetrieved() || probe.IsRetrieving()) && Time.realtimeSinceStartup < trialCleanupAt + 8f) return;
                    Execute(new Command { op = "restore" }, trial.launchLocation);
                    trialStage = 5;
                    trialCleanupAt = Time.realtimeSinceStartup + 2f;
                }
                else if (trialStage == 5 && Time.realtimeSinceStartup >= trialCleanupAt)
                {
                    string label = trial.label;
                    Execute(new Command { op = "pause" });
                    trialStatus = "complete:" + label;
                    trial = null;
                }
            }
            catch (Exception ex) { FailTrial(ex); }
        }

        private void StepTrial()
        {
            trialLoopStep++;
            if (trial == null || trialStage < 0 || trialStage >= 4) return;
            try
            {
                if (trialLoopStep >= 60)
                {
                    EnsureQuantumCache();
                    int state = trialMoon.GetStateIndex();
                    if (state != lastQuantumState)
                    {
                        lastQuantumState = state;
                        Event("speed_trial_quantum_state", new { trial.label, trialAttempt, trialLoopStep, quantum = QuantumObservation(), randomState = GameRandomState() });
                    }
                    // Looking up can change the Moon's state. Observe early states, but
                    // reject only after the final aim has reached the rendered view.
                    bool finalViewReady = trialAimFrame >= 0 && Time.frameCount >= trialAimFrame + 2 && trialLoopStep >= (trial.search ? ActiveLaunchStep - 210 : 390);
                    if (finalViewReady && !trialQuantumGateStarted)
                    {
                        trialQuantumGateStarted = true;
                        Event("speed_trial_quantum_gate", new { trial.label, trialAttempt, trialLoopStep, aimRenderFrame = trialAimFrame, checkRenderFrame = Time.frameCount, quantum = QuantumObservation(), state = Snapshot() });
                    }
                    if (finalViewReady && state == timberQuantumState) { SkipQuantumTrial(); return; }
                }
                if (trial.search && trialLoopStep == 240 && ActiveLaunchStep - 360 > 240)
                {
                    if (trial.waitWithSleep) StartTrialSleep();
                    else { OWTime.SetTimeScale(trial.speed); speedChanged = trial.speed != 1; }
                }
                if (trialCampfire != null)
                {
                    if (Mathf.Abs(Time.fixedDeltaTime - trialSleepFixedStep) > 0.00000001f)
                        throw new InvalidOperationException("Sleep changed the physics timestep");
                    if (trialLoopStep >= ActiveLaunchStep - 720) StopTrialSleep();
                }
                if (trialLoopStep == (trial.search ? ActiveLaunchStep - 360 : 240))
                {
                    OWTime.SetTimeScale(1f); speedChanged = false;
                    Execute(new Command { op = "restore" }, trial.launchLocation);
                    Execute(new Command { op = "equip" });
                    Event("speed_trial_prepared", new { trial.label, trialAttempt, trialLoopStep, state = Snapshot() });
                    trialStage = 1;
                }
                if (trialLoopStep == (trial.search ? ActiveLaunchStep - 240 : 360))
                {
                    Execute(new Command { op = "aim", pitch = trial.pitch, yaw = trial.yaw }, trial.launchLocation);
                    trialAimFrame = Time.frameCount;
                    trialStage = 2;
                }
                if (trialLoopStep == ActiveLaunchStep)
                {
                    if (!StationAvailable()) { FinishTrial(0, "target_unavailable"); return; }
                    if (trialStage != 2) throw new InvalidOperationException("Trial preparation did not finish");
                    if (!trialQuantumGateStarted || trialMoon.GetStateIndex() == timberQuantumState) throw new InvalidOperationException("Final-view Quantum Moon check has not passed");
                    if (!Locator.GetPlayerController().IsGrounded()) throw new InvalidOperationException("Player not grounded at launch");
                    var launchTh = Locator.GetAstroObject(AstroObject.Name.TimberHearth).GetOWRigidbody();
                    var location = trial.launchLocation ?? ReadSummit();
                    if (Vector3.Distance(launchTh.transform.InverseTransformPoint(Locator.GetPlayerBody().GetPosition()), P(location.positionLocal)) > 0.1f)
                        throw new InvalidOperationException("Launch position drift exceeds 10 cm");
                    trialLaunchRadius = Vector3.Distance(Locator.GetPlayerBody().GetPosition(), launchTh.GetPosition());
                    var launchSun = Locator.GetAstroObject(AstroObject.Name.Sun).GetOWRigidbody();
                    trialLaunchSunRadius = Vector3.Distance(Locator.GetPlayerBody().GetPosition(), launchSun.GetPosition());
                    trialStationOrbitRadius = Vector3.Distance(station.GetPosition(), launchSun.GetPosition());
                    var launch = Execute(new Command { op = "launch" });
                    Event("speed_trial_launch", new { trial.label, trialAttempt, trial.speed, flightSpeed = ActiveFlightSpeed,
                        fixedDeltaTime = Time.fixedDeltaTime, maximumDeltaTime = Time.maximumDeltaTime,
                        trialLoopStep, quantum = QuantumObservation(), randomState = GameRandomState(), launch });
                    trialWallStart = Time.realtimeSinceStartup;
                    OWTime.SetTimeScale(ActiveFlightSpeed);
                    speedChanged = ActiveFlightSpeed != 1f;
                    trialStage = 3;
                    trialStatus = "flying:" + trial.label;
                }
                if (trialStage != 3) return;
                int age = trialLoopStep - ActiveLaunchStep;
                var p = Locator.GetProbe();
                if (p == null) throw new InvalidOperationException("Probe object unavailable");
                if (age >= 2 && !p.IsLaunched()) { FinishTrial(age, "probe_lost"); return; }
                if (!StationAvailable()) { FinishTrial(age, "target_unavailable"); return; }
                var pb = p.GetOWRigidbody();
                var sun = Locator.GetAstroObject(AstroObject.Name.Sun).GetOWRigidbody();
                var th = Locator.GetAstroObject(AstroObject.Name.TimberHearth).GetOWRigidbody();
                var sp = sun.GetPosition();
                Vector3 ps = pb.GetPosition() - sp, pt = pb.GetPosition() - th.GetPosition();
                Vector3 v = pb.GetVelocity() - sun.GetVelocity(), ts = th.GetPosition() - sp;
                Vector3 ss = station == null ? new Vector3(float.NaN,float.NaN,float.NaN) : station.GetPosition() - sp;
                if (age >= 2)
                {
                    trialValidSamples++;
                    float radius = pt.magnitude, distance = Vector3.Distance(ps, ss);
                    if (radius > trialMaxRadius) { trialMaxRadius = radius; trialMaxRadiusAge = age; }
                    if (distance < trialMinDistance) { trialMinDistance = distance; trialMinDistanceAge = age; }
                    if (ps.magnitude < trialMinSunRadius) { trialMinSunRadius = ps.magnitude; trialMinSunAge = age; }
                    if (p.IsAnchored() && trialFirstAnchorAge < 0) trialFirstAnchorAge = age;
                }
                trialTrace.WriteLine(string.Join(",", new[] { age.ToString(), trialLoopStep.ToString(), F(TimeLoop.GetSecondsElapsed()), F(Time.realtimeSinceStartup), F(Time.timeScale), F(Time.fixedDeltaTime),
                    F(ps.x),F(ps.y),F(ps.z),F(pt.x),F(pt.y),F(pt.z),F(v.x),F(v.y),F(v.z),F(ts.x),F(ts.y),F(ts.z),F(ss.x),F(ss.y),F(ss.z),p.IsLaunched()?"1":"0",p.IsAnchored()?"1":"0",
                    trialMoon.GetStateIndex().ToString(),timberQuantumState.ToString(),F(Vector3.Distance(trialMoonBody.GetPosition(),th.GetPosition())),Time.frameCount.ToString() }));
                if (age % 60 == 0) trialTrace.Flush();
                if (age >= ActiveFlightSteps || (trial.search && trialFirstAnchorAge >= 0 && age >= trialFirstAnchorAge + 2))
                {
                    string reason = p.IsAnchored() ? "anchored" : trial.search && trialLoopStep >= SearchArrivalDeadlineStep ? "arrival_deadline" : "duration";
                    FinishTrial(age, reason);
                }
            }
            catch (Exception ex) { FailTrial(ex); }
        }

        private bool StationAvailable() { return station != null && station.gameObject.activeInHierarchy; }

        private void FinishTrial(int age, string reason)
        {
            var p = Locator.GetProbe();
            var parent = p == null ? null : p.GetOWRigidbody().transform.parent;
            var anchor = p != null && p.IsAnchored() && parent != null ? parent.GetComponentInParent<OWRigidbody>() : null;
            bool valid = trialValidSamples > 0;
            var result = new { trial.label, trialAttempt, trial.speed, flightSpeed = ActiveFlightSpeed, trial.pitch, trial.yaw, trial.waitWithSleep, trial.launchLocation, launchStep = ActiveLaunchStep,
                requestedFlightSteps = trial.flightSteps, flightSteps = ActiveFlightSteps, actualFlightSteps = age, trialLoopStep,
                validForScoring = valid, validSamples = trialValidSamples, randomState = GameRandomState(),
                simulatedSeconds = age * Time.fixedDeltaTime, wallSeconds = age > 0 ? Time.realtimeSinceStartup - trialWallStart : 0f,
                maxTimberHearthRadius = valid ? (float?)trialMaxRadius : null,
                minSunStationDistance = valid ? (float?)trialMinDistance : null,
                minSunRadius = valid ? (float?)trialMinSunRadius : null,
                launchRadius = trialLaunchRadius, launchSunRadius = trialLaunchSunRadius, stationOrbitRadiusAtLaunch = trialStationOrbitRadius,
                maxRadiusAgeSeconds = trialMaxRadiusAge * Time.fixedDeltaTime, minStationDistanceAgeSeconds = trialMinDistanceAge * Time.fixedDeltaTime,
                minSunRadiusAgeSeconds = trialMinSunAge * Time.fixedDeltaTime,
                firstAnchorAge = trialFirstAnchorAge, endReason = reason, anchorBody = anchor == null ? null : anchor.name,
                stationAnchorConfirmed = anchor != null && anchor == station, quantum = QuantumObservation(), state = Snapshot() };
            Event("speed_trial_finished", result);
            AtomicJson(Path.Combine(session, "result-" + trial.label + ".json"), result);
            trialTrace.Dispose(); trialTrace = null;
            File.Move(trialPath, Path.Combine(session, "speed-" + trial.label + ".csv"));
            OWTime.SetTimeScale(1f); speedChanged = false;
            if (p != null && p.IsLaunched() && !p.IsRetrieving()) Execute(new Command { op = "retrieve" });
            trialStage = 4;
            trialStatus = "cleanup:" + trial.label;
            trialCleanupAt = Time.realtimeSinceStartup + 2f;
        }

        private void OpenTrialTrace()
        {
            trialPath = Path.Combine(session, "pending-speed-" + trial.label + "-attempt" + trialAttempt + ".csv");
            trialTrace = new StreamWriter(new FileStream(trialPath, FileMode.CreateNew, FileAccess.Write, FileShare.Read));
            trialTrace.WriteLine("age_step,loop_step,loop_seconds,wall_seconds,time_scale,fixed_dt,probe_sun_x,probe_sun_y,probe_sun_z,probe_th_x,probe_th_y,probe_th_z,probe_vx,probe_vy,probe_vz,th_sun_x,th_sun_y,th_sun_z,station_sun_x,station_sun_y,station_sun_z,launched,anchored,quantum_state,timber_quantum_state,quantum_distance_th,render_frame");
        }

        private void StartTrialSleep()
        {
            var player = Locator.GetPlayerBody().GetPosition();
            trialCampfire = Resources.FindObjectsOfTypeAll<Campfire>()
                .Where(c => c.gameObject.scene.IsValid() && c.gameObject.activeInHierarchy && !(c is DreamCampfire))
                .OrderBy(c => Vector3.Distance(c.transform.position, player)).FirstOrDefault();
            if (trialCampfire == null || Vector3.Distance(trialCampfire.transform.position, player) > 30f)
                throw new InvalidOperationException("No ordinary campfire near spawn");
            if (!(bool)typeof(Campfire).GetMethod("CanSleepHereNow", Flags).Invoke(trialCampfire, null))
                throw new InvalidOperationException("Spawn campfire is not ready for sleep");
            trialSleepWallStart = Time.realtimeSinceStartup;
            trialSleepFixedStep = Time.fixedDeltaTime;
            typeof(Campfire).GetMethod("StartSleeping", Flags).Invoke(trialCampfire, null);
            Event("trial_sleep_started", new { trial.label, trialAttempt, trialLoopStep, loopSeconds = TimeLoop.GetSecondsElapsed(), campfire = trialCampfire.name, fixedDeltaTime = Time.fixedDeltaTime });
        }

        private void StopTrialSleep()
        {
            if (trialCampfire == null) return;
            var campfire = trialCampfire;
            trialCampfire = null;
            float previousScale = Time.timeScale;
            typeof(Campfire).GetMethod("StopSleeping", Flags).Invoke(campfire, new object[] { false });
            OWTime.SetTimeScale(1f); speedChanged = false;
            Event("trial_sleep_stopped", new { label = trial == null ? null : trial.label, trialAttempt, trialLoopStep,
                loopSeconds = TimeLoop.GetSecondsElapsed(), wallSeconds = Time.realtimeSinceStartup - trialSleepWallStart,
                previousScale, fixedDeltaTime = Time.fixedDeltaTime, inputMode = OWInput.GetInputMode().ToString() });
        }

        private object GameRandomState()
        {
            // Reading state does not consume random numbers. Other PRNGs are not captured here.
            var state = UnityEngine.Random.state;
            return new { unityState = typeof(UnityEngine.Random.State).GetFields(Flags)
                .OrderBy(f => f.Name).ToDictionary(f => f.Name, f => f.GetValue(state)), loopCount = TimeLoop.GetLoopCount() };
        }

        private void EnsureQuantumCache()
        {
            if (trialMoon != null && timberQuantumState >= 0) return;
            trialMoon = Locator.GetQuantumMoon();
            if (trialMoon == null || !LateInitializerManager.isDoneInitializing) throw new InvalidOperationException("Quantum Moon is not initialized");
            var th = Locator.GetAstroObject(AstroObject.Name.TimberHearth).GetOWRigidbody();
            var orbits = Resources.FindObjectsOfTypeAll<QuantumOrbit>().Where(o => o.gameObject.scene.IsValid()).ToArray();
            var orbit = orbits.Single(o => o.GetAttachedOWRigidbody() == th);
            timberQuantumState = orbit.GetStateIndex();
            trialMoonBody = (OWRigidbody)typeof(QuantumMoon).GetField("_moonBody", Flags).GetValue(trialMoon);
            Event("quantum_orbit_mapping", orbits.Select(o => new { state = o.GetStateIndex(), body = o.GetAttachedOWRigidbody().name, radius = o.GetOrbitRadius() }).ToArray());
        }

        private object QuantumObservation()
        {
            if (trialMoon == null || trialMoonBody == null) return null;
            var th = Locator.GetAstroObject(AstroObject.Name.TimberHearth).GetOWRigidbody();
            return new { state = trialMoon.GetStateIndex(), timberQuantumState, atTimberHearth = trialMoon.GetStateIndex() == timberQuantumState,
                distanceToTimberHearth = Vector3.Distance(trialMoonBody.GetPosition(), th.GetPosition()), positionRelativeToTimberHearth = V(trialMoonBody.GetPosition() - th.GetPosition()) };
        }

        private void SkipQuantumTrial()
        {
            Event("speed_trial_skipped", new { trial.label, trialAttempt, trialLoopStep, reason = "QuantumMoonAtTimberHearth", quantum = QuantumObservation() });
            trialTrace.Dispose(); trialTrace = null;
            File.Move(trialPath, Path.Combine(session, "excluded-speed-" + trial.label + "-attempt" + trialAttempt + ".csv"));
            if (++trialAttempt > 12) throw new InvalidOperationException("Too many Quantum Moon retries");
            OWTime.SetTimeScale(1f); speedChanged = false;
            OpenTrialTrace();
            trialStage = -1;
            trialStatus = "reloading:" + trial.label;
            LoadManager.ReloadSceneAsync(true, false);
        }

        private void FailTrial(Exception ex)
        {
            try { StopTrialSleep(); } catch (Exception cleanup) { Debug.LogError(cleanup); }
            string label = trial == null ? "unknown" : trial.label;
            if (trialTrace != null) { trialTrace.Dispose(); trialTrace = null; }
            OWTime.SetTimeScale(1f); speedChanged = false;
            trialStatus = "error:" + label + ":" + ex.GetBaseException().Message;
            Event("speed_trial_error", new { label, error = ex.ToString(), state = Snapshot() });
            trial = null;
            try { Execute(new Command { op = "pause" }); } catch { }
        }
    }
}
