using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.XR.ARSubsystems;

public class RaycastDrawer : MonoBehaviour
{
    [Header("Refs")]
    [SerializeField] private Detection3DOverlay_ARFoundation_LeftEye provider;

    [Header("Pinch Action")]
    [SerializeField] private InputActionProperty pinchAction;

    [Header("Spawn")]
    [SerializeField] private bool clearPreviousOnPinch = false;
    [SerializeField] private float debugSphereScale = 0.02f;
    [SerializeField] private float debugPoseCubeScale = 0.035f;
    [SerializeField] private float debugForwardLen = 0.18f;
    [SerializeField] private float debugLineWidth = 0.004f;
    [SerializeField] private float debugRayLengthIfMiss = 3.0f;

    private Transform _allRoot; // optional parent for all snapshots

    void OnEnable()
    {
        if (pinchAction.action != null)
        {
            pinchAction.action.Enable();
            pinchAction.action.performed += OnPinch;
        }
        else Debug.Log("[Noonchi] PinchAction is null.");
    }

    void OnDisable()
    {
        if (pinchAction.action != null)
        {
            pinchAction.action.performed -= OnPinch;
            pinchAction.action.Disable();
        }
    }

    private void OnPinch(InputAction.CallbackContext ctx)
    {
        if (provider == null)
        {
            Debug.Log("[Noonchi] provider is null.");
            return;
        }

        if (_allRoot == null)
        {
            _allRoot = new GameObject("Noonchi_DebugSnapshots").transform;
        }

        if (clearPreviousOnPinch)
        {
            for (int i = _allRoot.childCount - 1; i >= 0; i--)
                Destroy(_allRoot.GetChild(i).gameObject);
        }

        if (!provider.TryBuildDebugSnapshot(out var snap))
        {
            Debug.Log("[Noonchi] No snapshot available (no detections or refs invalid).");
            return;
        }

        // Root per pinch (snapshot)
        var root = new GameObject($"Snapshot_{Time.frameCount}").transform;
        root.SetParent(_allRoot, false);

        // (A) Pose markers (이번 pinch 순간)
        CreatePoseMarker(
            "LeftEye_Pose",
            snap.leftEyePose.position,
            snap.leftEyePose.rotation,
            root,
            debugPoseCubeScale,
            cubeColor: Color.red
        );

        /* 
        CreatePoseMarker(
            "MainCamera_Pose",
            snap.mainCamPose.position,
            snap.mainCamPose.rotation,
            root,
            debugPoseCubeScale,
            cubeColor: Color.green
        );*/

        // (B) One origin sphere (same for all rays)
        CreateSphere("RayOrigin_Sphere", snap.leftEyePose.position, debugSphereScale, root, Color.magenta);

        // (C) Every detection ray + hit sphere (if hit)
        for (int i = 0; i < snap.rays.Count; i++)
        {
            var it = snap.rays[i];

            // Ray line
            var lr = CreateWorldLine($"Ray_{i:00}", root, debugLineWidth, Color.cyan);
            Vector3 start = it.ray.origin;
            Vector3 end = it.hasHit ? it.hitPose.position : (it.ray.origin + it.ray.direction.normalized * debugRayLengthIfMiss);
            lr.SetPosition(0, start);
            lr.SetPosition(1, end);

            // Hit sphere
            if (it.hasHit)
            {
                CreateSphere($"Hit_{i:00}", it.hitPose.position, debugSphereScale, root, Color.yellow);
            }
        }

        Debug.Log($"[Noonchi] Pinch snapshot spawned. rays={snap.rays.Count}");
    }

    // ----- helpers (same style as your existing ones) -----

    private static GameObject CreateSphere(string name, Vector3 pos, float scale, Transform parent, Color color)
    {
        var go = GameObject.CreatePrimitive(PrimitiveType.Sphere);
        go.name = name;
        go.transform.SetParent(parent, false);
        go.transform.position = pos;
        go.transform.localScale = Vector3.one * scale;

        var col = go.GetComponent<Collider>();
        if (col != null) Object.Destroy(col);

        ApplyColor(go, color);
        return go;
    }

    private static LineRenderer CreateWorldLine(string name, Transform parent, float width, Color color)
    {
        var go = new GameObject(name);
        go.transform.SetParent(parent, false);

        var lr = go.AddComponent<LineRenderer>();
        lr.useWorldSpace = true;
        lr.positionCount = 2;
        lr.startWidth = width;
        lr.endWidth = width;

        var mat = MakeUnlitMat(color);
        lr.material = mat;
        if (lr.material != null) lr.material.renderQueue = 5000;

        return lr;
    }

    private static Transform CreatePoseMarker(
        string name,
        Vector3 pos,
        Quaternion rot,
        Transform parent,
        float cubeScale,
        Color cubeColor
    )
    {
        var root = new GameObject(name).transform;
        root.SetParent(parent, false);
        root.SetPositionAndRotation(pos, rot);

        var cube = GameObject.CreatePrimitive(PrimitiveType.Cube);
        cube.name = "Body_Cube";
        cube.transform.SetParent(root, false);
        cube.transform.localPosition = Vector3.zero;
        cube.transform.localRotation = Quaternion.identity;
        cube.transform.localScale = Vector3.one * cubeScale;

        var cubeCol = cube.GetComponent<Collider>();
        if (cubeCol != null) Object.Destroy(cubeCol);
        ApplyColor(cube, cubeColor);

        return root;
    }

    private static void ApplyColor(GameObject go, Color color)
    {
        var r = go.GetComponent<Renderer>();
        if (r == null) return;

        var mat = MakeUnlitMat(color);
        if (mat != null)
        {
            mat.renderQueue = 5000;
            r.material = mat;
        }
    }

    private static Material MakeUnlitMat(Color color)
    {
        Shader sh = Shader.Find("Unlit/Color");
        if (sh == null) sh = Shader.Find("Sprites/Default");
        if (sh == null) return null;

        var mat = new Material(sh);
        if (mat.HasProperty("_Color")) mat.SetColor("_Color", color);
        if (mat.HasProperty("_BaseColor")) mat.SetColor("_BaseColor", color);
        return mat;
    }
}
