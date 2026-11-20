using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using UnityEngine;

namespace SunStation
{
    public sealed partial class Control
    {
        private int worldMetadataNumber;
        private OWRigidbody[] worldBodies;
        private bool worldCaptureActive, worldCaptureArmed;
        private string worldCaptureStatus = "idle", worldCapturePath, worldCaptureError;
        private float worldCaptureSpeed;
        private int worldCaptureIndex;
        private readonly float[] worldCaptureTargets = { 60f, 62f, 120f, 260f, 420f };
        private List<object> worldCaptureSamples = new List<object>();

        private void SaveWorldCapture()
        {
            AtomicJson(worldCapturePath, new { schemaVersion = 1, recorderVersion = "0.6.3", status = worldCaptureStatus,
                error = worldCaptureError, speed = worldCaptureSpeed, targets = worldCaptureTargets, samples = worldCaptureSamples,
                session, sameLoop = true, note = "Five sparse snapshots only; requested times are not actual timestamps." });
        }

        private object StartWorldCapture(float speed)
        {
            if (trial != null || worldCaptureActive) throw new InvalidOperationException("Another game task is running");
            if (!Finite(speed) || speed < 1 || speed > 4) throw new ArgumentException("Capture speed must be 1..4");
            if (UnityEngine.SceneManagement.SceneManager.GetActiveScene().name != "SolarSystem") throw new InvalidOperationException("Load SolarSystem first");
            if (Locator.GetSceneMenuManager().pauseMenu.IsOpen()) Execute(new Command { op = "resume" });
            OWTime.SetTimeScale(1f); speedChanged = false;
            worldCaptureSpeed = speed; worldCaptureIndex = 0; worldCaptureError = null;
            worldCaptureSamples = new List<object>(); worldCaptureArmed = false; worldCaptureActive = true;
            worldCaptureStatus = "reloading";
            worldCapturePath = Path.Combine(session, "world-capture-" + Guid.NewGuid().ToString("N") + ".json");
            SaveWorldCapture();
            LoadManager.ReloadSceneAsync(true, false);
            return new { manifestPath = worldCapturePath, targets = worldCaptureTargets, speed };
        }

        private void StopWorldCapture(string status)
        {
            worldCaptureActive = false; worldCaptureArmed = false; worldCaptureStatus = status;
            OWTime.SetTimeScale(1f); speedChanged = false;
            try { Execute(new Command { op = "pause" }); } catch { }
            SaveWorldCapture();
        }

        private void UpdateWorldCapture()
        {
            if (!worldCaptureActive || worldCaptureStatus == "reloading") return;
            try
            {
                if (UnityEngine.InputSystem.Keyboard.current != null && UnityEngine.InputSystem.Keyboard.current.f7Key.wasPressedThisFrame)
                { StopWorldCapture("cancelled"); return; }
                if (!worldCaptureArmed && LateInitializerManager.isDoneInitializing && Locator.GetPlayerCamera() != null)
                {
                    var effect = Locator.GetPlayerCamera().GetComponent<PlayerCameraEffectController>();
                    if ((bool)typeof(PlayerCameraEffectController).GetField("_waitForWakeInput", Flags).GetValue(effect)) WakePlayer();
                    OWTime.SetTimeScale(worldCaptureSpeed); speedChanged = worldCaptureSpeed != 1;
                    worldCaptureArmed = true; worldCaptureStatus = "collecting"; SaveWorldCapture();
                }
            }
            catch (Exception ex) { worldCaptureError = ex.GetBaseException().Message; StopWorldCapture("error"); }
        }

        private void StepWorldCapture()
        {
            if (!worldCaptureActive || !worldCaptureArmed) return;
            try
            {
                if (TimeLoop.GetSecondsElapsed() < worldCaptureTargets[worldCaptureIndex]) return;
                worldCaptureSamples.Add(new { requestedSeconds = worldCaptureTargets[worldCaptureIndex], export = ExportWorldEnvironment() });
                worldCaptureIndex++; SaveWorldCapture();
                if (worldCaptureIndex == worldCaptureTargets.Length) StopWorldCapture("complete");
            }
            catch (Exception ex) { worldCaptureError = ex.GetBaseException().Message; StopWorldCapture("error"); }
        }

        private string ObjectPath(Component component)
        {
            if (component == null) return null;
            var names = new List<string>();
            for (var t = component.transform; t != null; t = t.parent) names.Add(t.name);
            names.Reverse();
            return string.Join("/", names.ToArray());
        }

        private object ReadField(object value, string name)
        {
            for (var type = value.GetType(); type != null; type = type.BaseType)
            {
                var field = type.GetField(name, Flags | BindingFlags.DeclaredOnly);
                if (field != null) return field.GetValue(value);
            }
            return null;
        }

        private Dictionary<string, object> ScalarFields(object value)
        {
            var result = new Dictionary<string, object>();
            for (var type = value.GetType(); type != null && type != typeof(MonoBehaviour); type = type.BaseType)
                foreach (var field in type.GetFields(Flags | BindingFlags.DeclaredOnly))
                {
                    if (result.ContainsKey(field.Name)) continue;
                    object v = field.GetValue(value);
                    if (v is Vector3) result[field.Name] = V((Vector3)v);
                    else if (v is Quaternion) { var q = (Quaternion)v; result[field.Name] = new[] { q.x, q.y, q.z, q.w }; }
                    else if (v != null && (field.FieldType.IsPrimitive || v is string)) result[field.Name] = v;
                    else if (v is Enum) result[field.Name] = v.ToString();
                }
            return result;
        }

        private void ScanWorldBodies()
        {
            var bodies = Resources.FindObjectsOfTypeAll<AstroObject>()
                .Where(a => a.gameObject.scene.IsValid()).Select(a => a.GetOWRigidbody()).Where(b => b != null).ToList();
            var ss = Resources.FindObjectsOfTypeAll<OWRigidbody>().FirstOrDefault(b => b.name == "SunStation_Body" && b.gameObject.scene.IsValid());
            if (ss != null) bodies.Add(ss);
            worldBodies = bodies.Distinct().OrderBy(b => b.name).ToArray();
        }

        private object BodyMotion(OWRigidbody body, OWRigidbody sun)
        {
            var q = body.GetRigidbody().rotation;
            return new { name = body.name, path = ObjectPath(body), active = body.gameObject.activeInHierarchy,
                positionSun = V(body.GetPosition() - sun.GetPosition()), velocitySun = V(body.GetVelocity() - sun.GetVelocity()),
                rotation = new[] { q.x, q.y, q.z, q.w }, angularVelocity = V(body.GetAngularVelocity()),
                mass = body.GetMass(), kinematic = body.GetRigidbody().isKinematic };
        }

        private object DetectorObservation(Component detector)
        {
            var volumes = ReadField(detector, "_activeVolumes") as IEnumerable;
            var paths = new List<string>();
            if (volumes != null) foreach (object volume in volumes)
                if (volume is Component) paths.Add(ObjectPath((Component)volume));
            return new { path = ObjectPath(detector), type = detector.GetType().Name,
                activeVolumes = paths, inheritedDetector = ObjectPath(ReadField(detector, "_activeInheritedDetector") as Component),
                fields = ScalarFields(detector) };
        }

        private object WorldSample()
        {
            if (worldBodies == null) ScanWorldBodies();
            var sun = Locator.GetAstroObject(AstroObject.Name.Sun).GetOWRigidbody();
            var probe = Locator.GetProbe();
            return new { schemaVersion = 1, utc = DateTime.UtcNow.ToString("o"), fixedStep, loopStep = trialLoopStep,
                loopSeconds = TimeLoop.GetSecondsElapsed(), fixedDeltaTime = Time.fixedDeltaTime, timeScale = Time.timeScale,
                reference = "Sun; game world axes, not a rotating frame", bodies = worldBodies.Where(b => b != null).Select(b => BodyMotion(b, sun)).ToArray(),
                probeLaunched = probe != null && probe.IsLaunched(),
                probeForces = probe == null ? null : probe.GetComponentsInChildren<ForceDetector>(true).Select(d => DetectorObservation(d)).ToArray(),
                probeFluids = probe == null ? null : probe.GetComponentsInChildren<FluidDetector>(true).Select(d => DetectorObservation(d)).ToArray() };
        }

        private object ExportWorldEnvironment()
        {
            if (UnityEngine.SceneManagement.SceneManager.GetActiveScene().name != "SolarSystem" || !LateInitializerManager.isDoneInitializing)
                throw new InvalidOperationException("Wait for SolarSystem initialization");
            ScanWorldBodies();
            var sun = Locator.GetAstroObject(AstroObject.Name.Sun).GetOWRigidbody();
            var astros = Resources.FindObjectsOfTypeAll<AstroObject>().Where(a => a.gameObject.scene.IsValid()).ToArray();
            Func<EffectVolume, object> describe = volume => new { path = ObjectPath(volume), type = volume.GetType().Name,
                active = volume.gameObject.activeInHierarchy, enabled = volume.enabled,
                attachedBody = volume.GetAttachedOWRigidbody() == null ? null : volume.GetAttachedOWRigidbody().name,
                positionSun = V(volume.transform.position - sun.GetPosition()), rotation = new[] { volume.transform.rotation.x, volume.transform.rotation.y, volume.transform.rotation.z, volume.transform.rotation.w },
                scale = V(volume.transform.lossyScale), fields = ScalarFields(volume),
                shapes = volume.GetComponentsInChildren<Shape>(true).Select(s => new { path = ObjectPath(s), type = s.GetType().Name,
                    positionSun = V(s.transform.position - sun.GetPosition()), scale = V(s.transform.lossyScale), fields = ScalarFields(s) }).ToArray() };
            var data = new { schemaVersion = 1, recorderVersion = "0.6.3", snapshot = WorldSample(),
                astros = astros.Select(a => new { path = ObjectPath(a), fields = ScalarFields(a),
                    body = a.GetOWRigidbody() == null ? null : a.GetOWRigidbody().name,
                    primary = a.GetPrimaryBody() == null ? null : ObjectPath(a.GetPrimaryBody()) }).ToArray(),
                gravityVolumes = Resources.FindObjectsOfTypeAll<GravityVolume>().Where(v => v.gameObject.scene.IsValid()).Select(v => describe(v)).ToArray(),
                fluidVolumes = Resources.FindObjectsOfTypeAll<FluidVolume>().Where(v => v.gameObject.scene.IsValid()).Select(v => describe(v)).ToArray(),
                note = "Read-only export. Scalars retain native field names; radii and priorities are not interpreted here." };
            string path = Path.Combine(session, "world-environment-" + (++worldMetadataNumber).ToString("D4") + ".json");
            AtomicJson(path, data);
            return new { path, bodyCount = worldBodies.Length, gravityVolumeCount = data.gravityVolumes.Length, snapshot = data.snapshot };
        }

    }
}
