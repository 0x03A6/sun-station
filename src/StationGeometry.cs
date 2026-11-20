using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace SunStation
{
    public sealed partial class Control
    {
        private object ExportStationGeometry()
        {
            if (trial != null || worldCaptureActive)
                throw new InvalidOperationException("Wait for the current game task to finish");
            if (SceneManager.GetActiveScene().name != "SolarSystem" || !LateInitializerManager.isDoneInitializing)
                throw new InvalidOperationException("Wait for SolarSystem initialization");

            var body = Resources.FindObjectsOfTypeAll<OWRigidbody>()
                .Single(b => b.name == "SunStation_Body" && b.gameObject.scene.IsValid());
            var sun = Locator.GetAstroObject(AstroObject.Name.Sun).GetOWRigidbody();
            var probe = Locator.GetProbe();
            var anchorCollider = probe == null ? null : probe.GetAnchor().GetCollider();
            var rigidbody = body.GetRigidbody();
            var rotation = rigidbody.rotation;
            var origin = body.GetPosition();
            var worldToBody = Matrix4x4.TRS(origin, rotation, Vector3.one).inverse;
            var colliders = Resources.FindObjectsOfTypeAll<Collider>()
                .Where(c => c.gameObject.scene.IsValid() &&
                    (c.transform.IsChildOf(body.transform) || c.attachedRigidbody == rigidbody))
                .OrderBy(c => ObjectPath(c), StringComparer.Ordinal)
                .ToArray();

            var meshes = new Dictionary<Mesh, int>();
            var meshData = new List<object>();
            var shapes = colliders.Select(c => DescribeStationCollider(c, worldToBody, anchorCollider, meshes, meshData)).ToArray();
            object scout = null;
            if (anchorCollider != null)
            {
                var probeBody = probe.GetOWRigidbody();
                var worldToProbe = Matrix4x4.TRS(probeBody.GetPosition(), probeBody.GetRigidbody().rotation, Vector3.one).inverse;
                scout = new { launched = probe.IsLaunched(), anchorCollider = DescribeStationCollider(anchorCollider,
                    worldToProbe, anchorCollider, meshes, meshData), collisionDetectionMode = probeBody.GetRigidbody().collisionDetectionMode.ToString() };
            }

            string path = Path.Combine(session, "station-geometry-" + Guid.NewGuid().ToString("N") + ".json");
            AtomicJson(path, new { schemaVersion = 1, utc = DateTime.UtcNow.ToString("o"), gameVersion = Application.version,
                loopSeconds = TimeLoop.GetSecondsElapsed(), fixedStep, loopStep = trialLoopStep, fixedDeltaTime = Time.fixedDeltaTime,
                frame = "Rigid station frame with unit scale. Matrices are row-major; multiply column points.",
                station = new { name = body.name, active = body.gameObject.activeInHierarchy, positionSun = V(origin - sun.GetPosition()),
                    velocitySun = V(body.GetVelocity() - sun.GetVelocity()), rotation = new[] { rotation.x, rotation.y, rotation.z, rotation.w },
                    angularVelocity = V(body.GetAngularVelocity()) },
                scout, colliders = shapes, meshes = meshData });
            return new { path, colliderCount = shapes.Length, solidCount = colliders.Count(c => !c.isTrigger),
                meshCount = meshData.Count, scoutAnchorColliderFound = anchorCollider != null };
        }

        private object DescribeStationCollider(Collider collider, Matrix4x4 worldToBody, Collider scout,
            Dictionary<Mesh, int> meshes, List<object> data)
        {
            object shape = null;
            var wrapper = collider.GetComponent<OWCollider>();
            bool? meshWasRemoved = wrapper == null ? (bool?)null : ReadField(wrapper, "_physicsRemoved") as bool?;
            if (collider is BoxCollider box)
                shape = new { center = V(box.center), size = V(box.size) };
            else if (collider is SphereCollider sphere)
                shape = new { center = V(sphere.center), radius = sphere.radius };
            else if (collider is CapsuleCollider capsule)
                shape = new { center = V(capsule.center), radius = capsule.radius, height = capsule.height, direction = capsule.direction };
            else if (collider is MeshCollider meshCollider)
            {
                var mesh = meshCollider.sharedMesh;
                string source = "sharedMesh";
                if (mesh == null && wrapper != null)
                {
                    mesh = ReadField(wrapper, "_removedMesh") as Mesh;
                    source = "streamedMesh";
                }
                int? meshIndex = null;
                if (mesh != null)
                {
                    if (!meshes.TryGetValue(mesh, out int index))
                    {
                        index = data.Count;
                        meshes.Add(mesh, index);
                        data.Add(DescribeStationMesh(mesh));
                    }
                    meshIndex = index;
                }
                shape = new { meshIndex, source = mesh == null ? null : source, convex = meshCollider.convex };
            }

            var transform = worldToBody * collider.transform.localToWorldMatrix;
            var matrix = new float[16];
            for (int row = 0; row < 4; row++)
                for (int col = 0; col < 4; col++) matrix[4 * row + col] = transform[row, col];
            int layer = collider.gameObject.layer;
            var bounds = collider.bounds;
            object activeBounds = collider.enabled && collider.gameObject.activeInHierarchy
                ? new { centerBody = V(worldToBody.MultiplyPoint3x4(bounds.center)), sizeWorld = V(bounds.size) } : null;
            return new { path = ObjectPath(collider), type = collider.GetType().Name, layer, layerName = LayerMask.LayerToName(layer),
                collider.isTrigger, collider.enabled, active = collider.gameObject.activeInHierarchy,
                attachedRigidbody = ObjectPath(collider.attachedRigidbody), bodyFromCollider = matrix, activeBounds, meshWasRemoved,
                layerIgnoresScout = scout == null ? (bool?)null : Physics.GetIgnoreLayerCollision(layer, scout.gameObject.layer),
                pairIgnoresScout = scout == null || scout == collider || !scout.gameObject.activeInHierarchy || !collider.gameObject.activeInHierarchy
                    ? (bool?)null : Physics.GetIgnoreCollision(collider, scout), shape };
        }

        private static object DescribeStationMesh(Mesh mesh)
        {
            var bounds = new { center = V(mesh.bounds.center), size = V(mesh.bounds.size) };
            float[][] vertices = null;
            int[] triangles = null;
            string readError = null;
            if (mesh.isReadable)
            {
                try
                {
                    vertices = mesh.vertices.Select(V).ToArray();
                    triangles = mesh.triangles;
                }
                catch (UnityException error) { readError = error.Message; }
            }
            else readError = "Mesh CPU data is marked unreadable";
            return new { mesh.name, mesh.vertexCount, bounds, vertices, triangles, readError };
        }
    }
}
