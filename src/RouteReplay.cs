using System;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text.RegularExpressions;
using HarmonyLib;
using Newtonsoft.Json;
using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.SceneManagement;

namespace SunStation
{
    public sealed partial class Control
    {
        private const float SummitPositionToleranceMeters = 0.005f;
        private static Control inputReplayOwner;
        private Harmony routeInputPatches;
        private RoutePlayback routeReplay;
        private string routeReplayStatus = "idle";

        private object StartRouteReplay(Command command, int scheduledLaunch = 0, Vector3? targetPosition = null)
        {
            if (trial != null || worldCaptureActive || routeReplay != null || routeShot != null)
                throw new InvalidOperationException("A game task is already running");
            if (SceneManager.GetActiveScene().name != "SolarSystem")
                throw new InvalidOperationException("Continue the game into SolarSystem first");

            string label = string.IsNullOrEmpty(command.label) ? command.id : command.label;
            if (!Regex.IsMatch(label, "^[A-Za-z0-9_-]{1,40}$")) throw new ArgumentException("Invalid route label");
            string source = string.IsNullOrEmpty(command.routePath) ? routeSource : command.routePath;
            string tracePath = Path.Combine(session, "route-" + label + ".jsonl");
            string resultPath = Path.Combine(session, "route-result-" + label + ".json");
            var playback = new RoutePlayback(source, tracePath, resultPath, targetPosition);
            try
            {
                if (scheduledLaunch != 0 && scheduledLaunch / 60f < playback.Samples.Last().pose.loopSeconds + 5f)
                    throw new ArgumentException("Launch must be at least five seconds after the route ends");
                InstallRouteInputPatches();
                if (Locator.GetSceneMenuManager().pauseMenu.IsOpen()) Execute(new Command { op = "resume" });
                OWTime.SetTimeScale(1f);
                speedChanged = false;
                routeReplay = playback;
                routeReplayStatus = "loading";
                SceneManager.sceneLoaded += OnRouteSceneLoaded;
                Event("route_replay_requested", new { label, source, tracePath, resultPath });
                LoadManager.ReloadSceneAsync(true, false);
                return new { label, source, tracePath, resultPath, samples = playback.Samples.Length };
            }
            catch
            {
                SceneManager.sceneLoaded -= OnRouteSceneLoaded;
                routeReplay = null;
                playback.Trace.Dispose();
                routeReplayStatus = "error";
                throw;
            }
        }

        private void OnRouteSceneLoaded(Scene scene, LoadSceneMode mode)
        {
            if (routeReplay == null) return;
            if (routeReplay.Started || scene.name != "SolarSystem")
            {
                FinishRouteReplay("scene_changed");
                return;
            }
            routeReplay.Started = true;
            routeReplay.ProgressTime = Time.realtimeSinceStartup;
            routeReplayStatus = "waiting_to_wake";
        }

        private void UpdateRouteReplay()
        {
            var playback = routeReplay;
            if (playback == null) return;
            try
            {
                var keyboard = Keyboard.current;
                if (keyboard != null && (keyboard.f7Key.wasPressedThisFrame || keyboard.escapeKey.wasPressedThisFrame))
                    FinishRouteReplay("cancelled");
                else if (Time.realtimeSinceStartup - playback.ProgressTime > (playback.Started ? 12f : 45f))
                    FinishRouteReplay(playback.Started ? "loop_not_advancing" : "load_timeout");
            }
            catch (Exception ex) { FinishRouteReplay("error", ex.GetBaseException().Message); }
        }

        private void StepRouteReplay()
        {
            var playback = routeReplay;
            if (playback == null || !playback.Started) return;
            try
            {
                var pose = PlayerPose();
                if (pose == null) return;
                float seconds = TimeLoop.GetSecondsElapsed();
                if (seconds < 0) return;
                int rawIndex = playback.RawIndex(seconds);
                string disembarkFailure = playback.UpdateDisembarkation(rawIndex, pose);
                int index = playback.Index(seconds);
                PlayerPoseSample expected = playback.Samples[index].pose;
                var actual = pose;
                float error = Vector3.Distance(P(expected.positionLocal), P(actual.positionLocal));
                float lookError = Vector3.Angle(P(expected.cameraForwardLocal), P(actual.cameraForwardLocal));
                playback.ProgressTime = Time.realtimeSinceStartup;
                playback.LastPose = actual;
                playback.LastIndex = index;
                playback.PeakError = Mathf.Max(playback.PeakError, error);
                bool summitReady = playback.UpdateSummitCorrection(rawIndex, index, actual);
                routeReplayStatus = actual.inputMode;
                playback.Trace.WriteLine(JsonConvert.SerializeObject(new {
                    index, expectedLoopSeconds = expected.loopSeconds, errorMeters = error, cameraErrorDegrees = lookError,
                    pose, prompt = playback.LastPrompt, rawIndex, shipPhase = playback.ShipPhase.ToString(),
                    trackingWalk = playback.TrackingWalk, summitPositionErrorMeters = playback.SummitPositionErrorMeters,
                    summitCorrection = new[] { playback.SummitCorrection.x, playback.SummitCorrection.y }
                }));
                if (index % 60 == 0) playback.Trace.Flush();
                if (actual.inputMode == "ShipCockpit")
                {
                    playback.SawCockpit = true;
                    playback.PeakCockpitError = Mathf.Max(playback.PeakCockpitError, error);
                }
                if (seconds > playback.Samples[playback.SuitIndex].pose.loopSeconds + 1.0f && !actual.wearingSuit)
                    FinishRouteReplay("suit_not_equipped");
                else if (seconds > playback.Samples[playback.SeatIndex].pose.loopSeconds + 1.0f && !playback.SawCockpit)
                    FinishRouteReplay("cockpit_not_entered");
                else if (disembarkFailure != null)
                    FinishRouteReplay(disembarkFailure);
                else if (index == playback.Samples.Length - 1 &&
                    (summitReady || playback.SummitCorrectionTimedOut(rawIndex) ||
                     (routeShot != null && trialLoopStep >= routeShot.TargetStep - 180)))
                    FinishRouteReplay(summitReady ? "completed" : "endpoint_mismatch");
            }
            catch (Exception ex) { FinishRouteReplay("error", ex.GetBaseException().Message); }
        }

        private void FinishRouteReplay(string reason, string error = null)
        {
            var playback = routeReplay;
            if (playback == null) return;
            routeReplay = null;
            routeReplayStatus = reason;
            SceneManager.sceneLoaded -= OnRouteSceneLoaded;
            playback.ReleaseMatchVelocity();
            playback.Trace.Dispose();
            PlayerPoseSample wanted = playback.Samples.Last().pose;
            PlayerPoseSample actual = playback.LastPose;
            var result = new {
                reason, error, source = playback.Source, samples = playback.Samples.Length,
                lastIndex = playback.LastIndex, maxPositionErrorMeters = playback.PeakError, sawCockpit = playback.SawCockpit,
                interactions = playback.Interactions, expectedEndpoint = wanted,
                targetPositionLocal = V(playback.TargetPosition), lastPose = actual,
                shipPhase = playback.ShipPhase.ToString(), maxCockpitErrorMeters = playback.PeakCockpitError,
                playback.LandingErrorMeters, playback.TrackingWalk,
                distanceFromEndpointMeters = actual == null ? (float?)null : Vector3.Distance(playback.TargetPosition, P(actual.positionLocal))
            };
            AtomicJson(playback.ResultPath, result);
            Event("route_replay_finished", result);
            lastMessage = "Route replay: " + reason;
            OnRouteShotRouteFinished(reason, actual);
        }

        private void DestroyRouteReplay()
        {
            FinishRouteReplay("mod_unloaded");
            if (inputReplayOwner == this) inputReplayOwner = null;
            if (routeInputPatches != null) routeInputPatches.UnpatchSelf();
        }

        private void InstallRouteInputPatches()
        {
            if (routeInputPatches != null) return;
            var harmony = new Harmony("SunStation.Control.RouteReplay");
            var commandAndMode = new[] { typeof(IInputCommands), typeof(InputMode) };
            try
            {
                PatchRoutePrefix(harmony, typeof(InputManager), "GetAxisValue", commandAndMode, nameof(RouteAxis));
                PatchRoutePrefix(harmony, typeof(InputManager), "GetValue", commandAndMode, nameof(RouteValue));
                PatchRoutePrefix(harmony, typeof(InputManager), "IsPressed",
                    new[] { typeof(IInputCommands), typeof(float), typeof(InputMode) }, nameof(RoutePressed));
                PatchRoutePrefix(harmony, typeof(InputManager), "IsNewlyPressed", commandAndMode, nameof(RoutePressedEdge));
                PatchRoutePrefix(harmony, typeof(InputManager), "IsNewlyReleased", commandAndMode, nameof(RouteReleasedEdge));
                PatchRoutePrefix(harmony, typeof(OWInput), "FixedUpdate", Type.EmptyTypes, nameof(RouteMatchVelocity));
                var clearPrompt = new HarmonyMethod(typeof(Control), nameof(ClearRouteInteraction));
                harmony.Patch(AccessTools.Method(typeof(SingleInteractionVolume), "UpdateInput"),
                    new HarmonyMethod(typeof(Control), nameof(RouteInteraction)), finalizer: clearPrompt);
                harmony.Patch(AccessTools.Method(typeof(MultipleInteractionVolume), "UpdateInput"),
                    new HarmonyMethod(typeof(Control), nameof(RouteSuitInteraction)), finalizer: clearPrompt);
                routeInputPatches = harmony;
                inputReplayOwner = this;
            }
            catch { harmony.UnpatchSelf(); throw; }
        }

        private static void PatchRoutePrefix(Harmony harmony, Type owner, string original, Type[] args, string prefix)
        {
            harmony.Patch(AccessTools.Method(owner, original, args), new HarmonyMethod(typeof(Control), prefix));
        }

        private static RoutePlayback CurrentRoute
        {
            get
            {
                var playback = inputReplayOwner == null ? null : inputReplayOwner.routeReplay;
                return playback != null && playback.Started ? playback : null;
            }
        }

        private static bool RouteAxis(IInputCommands command, InputMode mask, ref Vector2 __result)
        {
            var route = CurrentRoute;
            if (route == null) return AimRouteShotAxis(command, mask, ref __result);
            if (command == InputLibrary.moveXZ || command == InputLibrary.look)
            {
                __result = OWInput.IsInputMode(mask) ? route.Axis(command == InputLibrary.look) : Vector2.zero;
                return false;
            }
            return true;
        }

        private static bool RouteValue(IInputCommands command, InputMode mask, ref float __result)
        {
            var route = CurrentRoute;
            if (route == null) return !BlockRouteShotButton(command, ref __result);
            var sample = route.Samples[route.Index()];
            if (command == InputLibrary.thrustX) __result = Math.Sign(sample.move[0]);
            else if (command == InputLibrary.thrustZ) __result = Math.Sign(sample.move[1]);
            else if (command == InputLibrary.yaw) __result = sample.look[0];
            else if (command == InputLibrary.pitch) __result = sample.look[1];
            else
            {
                var button = RouteButtonFor(command);
                if (button == RouteButton.Native) return true;
                __result = route.Held(button, route.Index()) ? 1f : 0f;
            }
            if (!OWInput.IsInputMode(mask)) __result = 0f;
            return false;
        }

        private static RouteButton RouteButtonFor(IInputCommands command)
        {
            if (command == InputLibrary.jump) return RouteButton.Jump;
            if (command == InputLibrary.boost) return RouteButton.Boost;
            if (command == InputLibrary.thrustUp) return RouteButton.Up;
            if (command == InputLibrary.thrustDown) return RouteButton.Down;
            if (command == InputLibrary.matchVelocity) return RouteButton.MatchVelocity;
            if (command == InputLibrary.interact) return RouteButton.Interact;
            if (command == InputLibrary.cancel) return RouteButton.Cancel;
            if (command == InputLibrary.freeLook || command == InputLibrary.rollMode || command == InputLibrary.autopilot
                || command == InputLibrary.landingCamera || command == InputLibrary.lockOn) return RouteButton.Off;
            return RouteButton.Native;
        }

        private static bool RoutePressed(IInputCommands command, float minPressDuration, InputMode mask, ref bool __result)
        {
            var route = CurrentRoute;
            if (route == null) return !BlockRouteShotButton(command, ref __result);
            var button = RouteButtonFor(command);
            if (button == RouteButton.Native) return true;
            int index = route.Index();
            __result = OWInput.IsInputMode(mask) && route.Held(button, index);
            int earlier = Math.Max(0, index - Mathf.CeilToInt(Math.Max(0, minPressDuration) * 60f));
            for (int step = index - 1; __result && step >= earlier; step--) __result = route.Held(button, step);
            return false;
        }

        private static bool RoutePressedEdge(IInputCommands command, InputMode mask, ref bool __result)
        {
            return RouteEdge(command, mask, true, ref __result);
        }

        private static bool RouteReleasedEdge(IInputCommands command, InputMode mask, ref bool __result)
        {
            return RouteEdge(command, mask, false, ref __result);
        }

        private static bool RouteEdge(IInputCommands command, InputMode mask, bool pressed, ref bool result)
        {
            var route = CurrentRoute;
            if (route == null) return !BlockRouteShotButton(command, ref result);
            var button = RouteButtonFor(command);
            if (button == RouteButton.Native) return true;
            result = OWInput.IsInputMode(mask) && route.Edge(button, pressed);
            return false;
        }

        private static void RouteMatchVelocity()
        {
            var route = CurrentRoute;
            if (route != null) route.MatchVelocity();
        }

        private static void RouteInteraction(UITextType ____textID)
        {
            var route = CurrentRoute;
            if (route != null) route.FocusedPrompt = ____textID;
        }

        private static void RouteSuitInteraction(MultipleInteractionVolume __instance)
        {
            var route = CurrentRoute;
            if (route != null && __instance.GetComponentInParent<SuitPickupVolume>() != null)
                route.FocusedPrompt = UITextType.SuitUpPrompt;
        }

        private static void ClearRouteInteraction()
        {
            var route = CurrentRoute;
            if (route != null) route.FocusedPrompt = null;
        }

        private enum RouteButton { Native, Off, Jump, Boost, Up, Down, MatchVelocity, Interact, Cancel }
        private enum RouteAction { None, Wake, Lift, EnterHatch, Suit, Buckle, Unbuckle, ExitHatch }
        private enum DisembarkPhase { Approach, SeatExit, CabinGround, Cabin, SurfaceLanding, Walking }

        private sealed class RoutePlayback
        {
            public readonly RouteSample[] Samples;
            public readonly int SuitIndex, SeatIndex;
            public readonly string Source, ResultPath;
            public readonly StreamWriter Trace;
            public readonly Vector3 TargetPosition;
            public readonly System.Collections.Generic.List<string> Interactions = new System.Collections.Generic.List<string>();
            private static readonly FieldInfo WakeField = AccessTools.Field(typeof(PlayerCameraEffectController), "_waitForWakeInput");
            private readonly int liftIndex, hatchIndex, buckleIndex, settleIndex;
            private readonly int exitIndex, cabinGroundIndex, hatchExitIndex, landingIndex;
            private int routeDelay, hatchActionStep;
            private int nextSummitPulseStep, summitStableSteps;
            public Vector2 SummitCorrection { get; private set; }
            public float? SummitPositionErrorMeters { get; private set; }
            public DisembarkPhase ShipPhase { get; private set; }
            public bool TrackingWalk { get; private set; }
            public float? LandingErrorMeters { get; private set; }
            public bool Started, SawCockpit;
            public float ProgressTime = Time.realtimeSinceStartup;
            public PlayerPoseSample LastPose;
            public int LastIndex = -1;
            public float PeakError, PeakCockpitError;
            public UITextType? FocusedPrompt;
            public string LastPrompt;
            private int renderFrame = -1, renderStart, renderIndex = -1;
            private int actionFrame = -1;
            private RouteAction action;
            private bool matchHeld, ownsMatchVelocity;
            private ShipCockpitController cockpit;
            private Autopilot autopilot;
            private readonly System.Collections.Generic.HashSet<RouteAction> sentActions = new System.Collections.Generic.HashSet<RouteAction>();

            public RoutePlayback(string source, string tracePath, string resultPath, Vector3? targetPosition)
            {
                Source = source;
                ResultPath = resultPath;
                Samples = File.ReadLines(source).Where(line => !string.IsNullOrWhiteSpace(line))
                    .Select(line => JsonConvert.DeserializeObject<RouteSample>(line)).ToArray();
                if (Samples.Length < 2 || Samples.Any(sample => sample == null || !sample.Valid())
                    || Samples[0].pose.loopSeconds < -0.025f || Samples[0].pose.loopSeconds > 0.025f)
                    throw new ArgumentException("Expected a route sampled from the start of a Timber Hearth loop");
                for (int i = 1; i < Samples.Length; i++)
                {
                    if (Samples[i].pose.fixedStep != Samples[i - 1].pose.fixedStep + 1
                        || Math.Abs(Samples[i].pose.loopSeconds - Samples[0].pose.loopSeconds - i / 60f) > 0.01f)
                        throw new ArgumentException("Route must contain consecutive 60 Hz physics samples");
                }
                int wake = Array.FindIndex(Samples, sample => sample.pose.inputMode == "Character");
                int onShip = Array.FindIndex(Samples, sample => sample.pose.groundBody == "Ship_Body");
                SuitIndex = Array.FindIndex(Samples, sample => sample.pose.wearingSuit);
                SeatIndex = Array.FindIndex(Samples, sample => sample.pose.inputMode == "ShipCockpit");
                exitIndex = SeatIndex < 0 ? -1 : Array.FindIndex(Samples, SeatIndex, sample => sample.pose.inputMode == "Character");
                cabinGroundIndex = exitIndex < 0 ? -1 : Array.FindIndex(Samples, exitIndex, sample => GroundedOn(sample.pose, "Ship_Body"));
                landingIndex = exitIndex < 0 ? -1 : Array.FindIndex(Samples, exitIndex, sample => GroundedOn(sample.pose, "TimberHearth_Body"));
                if (!(wake > 0 && onShip > wake && SuitIndex > onShip && SeatIndex > SuitIndex && exitIndex > SeatIndex
                    && cabinGroundIndex > exitIndex && landingIndex > cabinGroundIndex)
                    || !Samples.Last().pose.grounded || !Samples.Last().pose.wearingSuit)
                    throw new ArgumentException("Expected a wake, ship entry, suit-up, flight and landed endpoint");
                liftIndex = Array.FindIndex(Samples, wake, onShip - wake, sample => !sample.pose.grounded && !sample.jump);
                hatchIndex = FirstAirborneBefore(onShip);
                buckleIndex = FirstAirborneBefore(SeatIndex);
                hatchExitIndex = FirstAirborneBefore(landingIndex);
                settleIndex = Array.FindLastIndex(Samples, sample => sample.move[0] != 0f || sample.move[1] != 0f) + 6;
                TargetPosition = targetPosition ?? P(Samples.Last().pose.positionLocal);
                Trace = new StreamWriter(new FileStream(tracePath, FileMode.CreateNew, FileAccess.Write, FileShare.Read));
            }

            private int FirstAirborneBefore(int index)
            {
                while (index > 0 && !Samples[index - 1].pose.grounded) index--;
                return index;
            }

            public int Index() { return Index(TimeLoop.GetSecondsElapsed()); }
            public int RawIndex(float seconds)
            {
                return Mathf.Max(0, Mathf.RoundToInt((seconds - Samples[0].pose.loopSeconds) * 60f));
            }

            public int Index(float seconds)
            {
                int index = RawIndex(seconds) - routeDelay;
                switch (ShipPhase)
                {
                    case DisembarkPhase.SeatExit: index = Math.Min(index, exitIndex); break;
                    case DisembarkPhase.CabinGround: index = Math.Min(index, cabinGroundIndex); break;
                    case DisembarkPhase.Cabin: index = Math.Min(index, hatchExitIndex - 1); break;
                    case DisembarkPhase.SurfaceLanding: index = Math.Min(index, landingIndex - 1); break;
                }
                return Mathf.Clamp(index, 0, Samples.Length - 1);
            }

            public string UpdateDisembarkation(int rawIndex, PlayerPoseSample pose)
            {
                switch (ShipPhase)
                {
                    case DisembarkPhase.Approach:
                        if (rawIndex < exitIndex) break;
                        ShipPhase = DisembarkPhase.SeatExit;
                        goto case DisembarkPhase.SeatExit;
                    case DisembarkPhase.SeatExit:
                        if (pose.inputMode != "Character")
                            return rawIndex > exitIndex + 90 ? "cockpit_not_exited" : null;
                        if (rawIndex > exitIndex + 4) routeDelay = rawIndex - exitIndex;
                        ShipPhase = DisembarkPhase.CabinGround;
                        goto case DisembarkPhase.CabinGround;
                    case DisembarkPhase.CabinGround:
                        if (!GroundedOn(pose, "Ship_Body"))
                            return rawIndex > routeDelay + cabinGroundIndex + 120 ? "cabin_floor_not_reached" : null;
                        // Keep the recorded look arc and hold cabin movement until the player settles on the deck.
                        if (rawIndex > routeDelay + cabinGroundIndex + 4) routeDelay = rawIndex - cabinGroundIndex;
                        ShipPhase = DisembarkPhase.Cabin;
                        break;
                    case DisembarkPhase.Cabin:
                        if (rawIndex > routeDelay + hatchExitIndex + 120) return "ship_hatch_not_acquired";
                        break;
                    case DisembarkPhase.SurfaceLanding:
                        if (!GroundedOn(pose, "TimberHearth_Body"))
                            return rawIndex > hatchActionStep + 150 ? "ship_exit_landing_not_reached" : null;
                        // Leave near-nominal landings on their original clock.
                        int delay = rawIndex - landingIndex;
                        if (Math.Abs(delay - routeDelay) > 6) routeDelay = delay;
                        var expected = Samples[landingIndex].pose;
                        LandingErrorMeters = Vector3.Distance(P(expected.positionLocal), P(pose.positionLocal));
                        TrackingWalk = LandingErrorMeters.Value > 1f;
                        ShipPhase = DisembarkPhase.Walking;
                        break;
                }
                return null;
            }

            private static bool GroundedOn(PlayerPoseSample pose, string body)
            {
                return pose.grounded && pose.groundBody == body;
            }

            public bool SummitCorrectionTimedOut(int rawIndex)
            {
                return rawIndex - routeDelay >= Samples.Length - 1 + 300;
            }

            public bool UpdateSummitCorrection(int rawIndex, int index, PlayerPoseSample pose)
            {
                const float horizontalGoal = 0.003f;
                SummitCorrection = Vector2.zero;
                SummitPositionErrorMeters = null;
                if (index < settleIndex || ShipPhase != DisembarkPhase.Walking ||
                    pose.inputMode != "Character" || !GroundedOn(pose, "TimberHearth_Body"))
                {
                    summitStableSteps = 0;
                    return false;
                }

                Vector3 offset = TargetPosition - P(pose.positionLocal);
                SummitPositionErrorMeters = offset.magnitude;
                float limit = TrackingWalk ? 15f : 6f;
                Quaternion rotation = Q(pose.rotationLocal);
                var horizontal = new Vector2(Vector3.Dot(offset, rotation * Vector3.right),
                    Vector3.Dot(offset, rotation * Vector3.forward));
                if (offset.sqrMagnitude <= limit * limit && rawIndex >= nextSummitPulseStep)
                {
                    if (horizontal.sqrMagnitude > 0.01f)
                        SummitCorrection = Vector2.ClampMagnitude(horizontal * 0.25f, 0.32f);
                    else if (horizontal.sqrMagnitude > horizontalGoal * horizontalGoal && pose.stableCandidate)
                    {
                        // Use one physics tick of effective input, then allow braking before measuring again.
                        SummitCorrection = horizontal.normalized * 0.03f;
                        nextSummitPulseStep = rawIndex + 8;
                    }
                }
                bool stable = rawIndex >= nextSummitPulseStep && pose.stableCandidate && pose.wearingSuit
                    && horizontal.sqrMagnitude <= horizontalGoal * horizontalGoal
                    && offset.sqrMagnitude <= SummitPositionToleranceMeters * SummitPositionToleranceMeters;
                summitStableSteps = stable ? summitStableSteps + 1 : 0;
                return summitStableSteps >= 8;
            }

            public Vector2 Axis(bool turning)
            {
                float seconds = TimeLoop.GetSecondsElapsed();
                int index = Index(seconds);
                if (ShipPhase != DisembarkPhase.Walking && RawIndex(seconds) - routeDelay > index)
                    return Vector2.zero;
                var sample = Samples[index];
                float[] values = turning ? sample.look : sample.move;
                var recorded = new Vector2(values[0], values[1]);
                if (ShipPhase != DisembarkPhase.Walking || LastPose == null || LastPose.inputMode != "Character")
                    return recorded;
                bool settled = index >= settleIndex;
                bool onSurface = GroundedOn(LastPose, "TimberHearth_Body");
                if (settled ? !onSurface : !TrackingWalk) return recorded;
                if (settled && !turning) return SummitCorrection;

                Vector3 targetPosition = settled ? TargetPosition : P(sample.pose.positionLocal);
                Vector3 offset = targetPosition - P(LastPose.positionLocal);
                float limit = TrackingWalk ? 15f : 6f;
                if (offset.sqrMagnitude > limit * limit) return recorded;
                Quaternion orientation = Q(LastPose.rotationLocal);
                Vector3 localOffset = Quaternion.Inverse(orientation) * offset;
                if (!turning)
                {
                    var tangent = new Vector2(localOffset.x, localOffset.z);
                    // Follow the sampled path with ordinary movement inputs. Keep recorded jetpack timing.
                    if (!onSurface || !GroundedOn(sample.pose, "TimberHearth_Body") || Mathf.Abs(localOffset.y) > 4f)
                        return recorded;
                    return Vector2.ClampMagnitude(recorded + Vector2.ClampMagnitude(tangent * 0.25f, 0.6f), 1f);
                }

                Quaternion targetRotation = Q(sample.pose.rotationLocal);
                float yawError = Vector3.SignedAngle(orientation * Vector3.forward, targetRotation * Vector3.forward, orientation * Vector3.up);
                float pitchError = sample.pose.pitch - LastPose.pitch;
                return recorded + Vector2.ClampMagnitude(new Vector2(yawError, pitchError) * 0.025f, 0.28f);
            }

            public bool Held(RouteButton button, int index)
            {
                var sample = Samples[index];
                switch (button)
                {
                    case RouteButton.Jump: return sample.jump;
                    case RouteButton.Boost: return sample.boost;
                    case RouteButton.Up: return sample.thrustUp;
                    case RouteButton.Down: return sample.thrustDown;
                    case RouteButton.MatchVelocity: return sample.pose.inputMode == "ShipCockpit" && sample.jump;
                    case RouteButton.Interact:
                    case RouteButton.Cancel: return Time.frameCount == actionFrame
                        && ((button == RouteButton.Cancel) == (action == RouteAction.Unbuckle));
                    default: return false;
                }
            }

            public bool Edge(RouteButton button, bool pressed)
            {
                if (button == RouteButton.Interact || button == RouteButton.Cancel)
                    return InteractionEdge(button, pressed);
                if (button == RouteButton.MatchVelocity) return false;
                int index = Index();
                int start = index;
                if (!Time.inFixedTimeStep)
                {
                    if (renderFrame != Time.frameCount)
                    {
                        renderStart = Math.Min(index, renderIndex + 1);
                        renderIndex = index;
                        renderFrame = Time.frameCount;
                    }
                    // A render frame can contain multiple physics samples. Preserve intervening button edges.
                    start = renderStart;
                    if (index != renderIndex) index = renderIndex;
                }
                for (int i = Math.Max(1, start); i <= index; i++)
                    if (Held(button, i) == pressed && Held(button, i - 1) != pressed) return true;
                return false;
            }

            public void MatchVelocity()
            {
                // Enabling the native autopilot during a physics update takes effect on the
                // next step. Release an already enabled autopilot on the recorded step.
                int index = Index();
                bool held = OWInput.GetInputMode() == InputMode.ShipCockpit
                    && Held(RouteButton.MatchVelocity, matchHeld ? index : Math.Min(index + 1, Samples.Length - 1));
                if (held == matchHeld) return;
                matchHeld = held;
                if (!held) { ReleaseMatchVelocity(); return; }
                if (autopilot == null)
                {
                    var ship = Locator.GetShipBody();
                    if (ship == null) return;
                    autopilot = ship.GetComponent<Autopilot>();
                    cockpit = ship.GetComponentInChildren<ShipCockpitController>();
                }
                if (autopilot == null || cockpit == null || !cockpit.IsMatchVelocityAvailable(false)
                    || autopilot.IsMatchingVelocity()) return;
                autopilot.StartMatchVelocity(Locator.GetReferenceFrame(false), false);
                ownsMatchVelocity = autopilot.IsMatchingVelocity();
            }

            public void ReleaseMatchVelocity()
            {
                if (ownsMatchVelocity && autopilot != null && autopilot.IsMatchingVelocity() && !autopilot.IsFlyingToDestination())
                    autopilot.StopMatchVelocity();
                ownsMatchVelocity = false;
            }

            private bool InteractionEdge(RouteButton button, bool pressed)
            {
                bool cancel = button == RouteButton.Cancel;
                if (!pressed) return Time.frameCount == actionFrame + 1 && action != RouteAction.None
                    && cancel == (action == RouteAction.Unbuckle);
                if (Time.frameCount == actionFrame) return cancel == (action == RouteAction.Unbuckle);
                int index = Index();
                RouteAction next = RouteAction.None;
                var mode = OWInput.GetInputMode();
                if (FocusedPrompt.HasValue) LastPrompt = FocusedPrompt.ToString();
                if (cancel && mode == InputMode.ShipCockpit && index >= exitIndex - 1
                    && (ShipPhase == DisembarkPhase.Approach || ShipPhase == DisembarkPhase.SeatExit))
                    next = RouteAction.Unbuckle;
                else if (!cancel && mode == InputMode.None && LateInitializerManager.isDoneInitializing && WaitingToWake())
                    next = RouteAction.Wake;
                else if (!cancel && mode == InputMode.Character && FocusedPrompt.HasValue)
                {
                    switch (FocusedPrompt.Value)
                    {
                        case UITextType.ActivateLiftPrompt:
                            if (liftIndex >= 0 && index >= liftIndex - 2 && index <= liftIndex + 30) next = RouteAction.Lift;
                            break;
                        case UITextType.ShipHatchPrompt:
                            if (index >= hatchIndex - 2 && index <= hatchIndex + 35) next = RouteAction.EnterHatch;
                            else if (CanUseExitHatch(index)) next = RouteAction.ExitHatch;
                            break;
                        case UITextType.SuitUpPrompt:
                            if (index >= SuitIndex - 1 && index <= SuitIndex + 40 && !PlayerState.IsWearingSuit()) next = RouteAction.Suit;
                            break;
                        case UITextType.ShipCockpitPrompt:
                        case UITextType.ShipBuckleUpPrompt:
                            if (index >= buckleIndex - 1 && index <= SeatIndex + 46 && PlayerState.IsWearingSuit()) next = RouteAction.Buckle;
                            break;
                    }
                }
                if (next == RouteAction.None || !sentActions.Add(next)) return false;
                actionFrame = Time.frameCount;
                action = next;
                if (next == RouteAction.ExitHatch)
                {
                    ShipPhase = DisembarkPhase.SurfaceLanding;
                    hatchActionStep = RawIndex(TimeLoop.GetSecondsElapsed());
                }
                Interactions.Add(next.ToString() + " " + TimeLoop.GetSecondsElapsed().ToString("F3", CultureInfo.InvariantCulture));
                return true;
            }

            private bool CanUseExitHatch(int index)
            {
                if (ShipPhase != DisembarkPhase.Cabin) return false;
                if (index >= hatchExitIndex - 2) return true;
                // A drifting cabin can bring the hatch into range earlier than the recorded exit.
                return index >= cabinGroundIndex + 20 && LastPose != null &&
                    (P(Samples[index].pose.positionLocal) - P(LastPose.positionLocal)).sqrMagnitude > 1f;
            }

            private static bool WaitingToWake()
            {
                var camera = Locator.GetPlayerCamera();
                var effect = camera == null ? null : camera.GetComponent<PlayerCameraEffectController>();
                return effect != null && (bool)WakeField.GetValue(effect);
            }
        }

        private sealed class RouteSample
        {
            public PlayerPoseSample pose { get; set; }
            public float[] move { get; set; }
            public float[] look { get; set; }
            public bool jump { get; set; }
            public bool boost { get; set; }
            public bool thrustUp { get; set; }
            public bool thrustDown { get; set; }

            public bool Valid()
            {
                return pose != null && pose.referenceBody == "TimberHearth" && Finite(pose.loopSeconds)
                    && Vector(pose.positionLocal, 3) && Vector(pose.rotationLocal, 4) && Vector(pose.cameraForwardLocal, 3)
                    && Vector(move, 2) && Vector(look, 2);
            }

            private static bool Vector(float[] values, int length)
            {
                return values != null && values.Length == length && values.All(Finite);
            }
        }

        private sealed class PlayerPoseSample
        {
            public int schemaVersion;
            public string utc;
            public long fixedStep;
            public float loopSeconds;
            public string referenceBody;
            public float[] positionLocal, rotationLocal, cameraForwardLocal, upLocal, velocityRelativeToSurfaceLocal;
            public float speedRelativeToSurface, radius;
            public bool grounded;
            public string groundBody;
            public float[] groundContactLocal;
            public bool stableCandidate, wearingSuit;
            public string inputMode;
            public float pitch;
        }
    }
}
