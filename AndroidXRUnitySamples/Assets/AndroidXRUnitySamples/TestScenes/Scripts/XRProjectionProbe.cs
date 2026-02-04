using System.Collections.Generic;
using UnityEngine;
using UnityEngine.XR;

public class XRProjectionProbe : MonoBehaviour
{
    [Header("Refs")]
    public Camera xrCamera;

    [Header("Stereo")]
    public bool useLeftStereo = true;

    [Header("Compare LeftEye Poses")]
    public bool compareWithXRNodeLeftEye = true;

    [Header("Logging")]
    public float logEverySec = 1f;

    float _t;

    // XRNode.LeftEye device cache (InputDevice 방식)
    InputDevice _leftEyeDevice;
    bool _leftEyeDeviceFound;

    // XRNodeState 방식 캐시 (fallback)
    readonly List<XRNodeState> _nodeStates = new(8);

    void Awake()
    {
        if (!xrCamera) xrCamera = Camera.main;
        if (compareWithXRNodeLeftEye) InitializeLeftEyeDevice();
    }

    void Update()
    {
        _t += Time.unscaledDeltaTime;
        if (_t < logEverySec) return;
        _t = 0f;

        // --- (A) Projection log ---
        Matrix4x4 P = (useLeftStereo && xrCamera && xrCamera.stereoEnabled)
            ? xrCamera.GetStereoProjectionMatrix(Camera.StereoscopicEye.Left)
            : (xrCamera ? xrCamera.projectionMatrix : Matrix4x4.identity);

        float fovY = 2f * Mathf.Atan(1f / P[1, 1]) * Mathf.Rad2Deg;
        float fovX = 2f * Mathf.Atan(1f / P[0, 0]) * Mathf.Rad2Deg;
        float aspect = (P[1, 1] / P[0, 0]);

        // --- (B) LeftEye pose from stereo VIEW matrix ---
        Pose poseFromStereoView = default;
        bool hasPoseFromStereoView = false;

        if (xrCamera && xrCamera.stereoEnabled)
        {
            Matrix4x4 V = xrCamera.GetStereoViewMatrix(Camera.StereoscopicEye.Left); // world->view
            Matrix4x4 W = V.inverse; // view->world (camera pose)

            // position
            Vector3 pos = W.GetColumn(3);

            // rotation (Unity column-major: col0=right, col1=up, col2=forward)
            // View matrix inverse's forward should be col2.
            Vector3 fwd = W.GetColumn(2);
            Vector3 up = W.GetColumn(1);

            // safety normalize
            if (fwd.sqrMagnitude > 1e-8f && up.sqrMagnitude > 1e-8f)
            {
                Quaternion rot = Quaternion.LookRotation(fwd.normalized, up.normalized);
                poseFromStereoView = new Pose(pos, rot);
                hasPoseFromStereoView = true;
            }
        }

        // --- (C) LeftEye pose from XRNode.LeftEye (InputDevice / NodeState) ---
        Pose poseFromXRNode = default;
        bool hasPoseFromXRNode = false;

        if (compareWithXRNodeLeftEye)
        {
            // refresh device if lost
            if (!_leftEyeDeviceFound || !_leftEyeDevice.isValid)
                InitializeLeftEyeDevice();

            hasPoseFromXRNode = TryGetLeftEyePose_XRNode(out poseFromXRNode);
        }

        // --- (D) Compare + log ---
        string header =
            $"[XRProj] stereoEnabled={(xrCamera ? xrCamera.stereoEnabled : false)} useLeftStereo={useLeftStereo} " +
            $"fovX={fovX:0.00} fovY={fovY:0.00} aspect≈{aspect:0.000}";

        string proj =
            $"P=\n{MatToString(P)}";

        string poseView =
            hasPoseFromStereoView
                ? $"LeftEyePose(from StereoView): pos={poseFromStereoView.position} rotEuler={poseFromStereoView.rotation.eulerAngles}"
                : "LeftEyePose(from StereoView): MISSING (stereo off or invalid matrix)";

        string poseNode =
            compareWithXRNodeLeftEye
                ? (hasPoseFromXRNode
                    ? $"LeftEyePose(from XRNode.LeftEye): pos={poseFromXRNode.position} rotEuler={poseFromXRNode.rotation.eulerAngles}"
                    : "LeftEyePose(from XRNode.LeftEye): MISSING")
                : "LeftEyePose(from XRNode.LeftEye): (disabled)";

        string diff = "";
        if (hasPoseFromStereoView && hasPoseFromXRNode)
        {
            float posDiff = Vector3.Distance(poseFromStereoView.position, poseFromXRNode.position);

            // rotation difference angle
            float rotDiffDeg = Quaternion.Angle(poseFromStereoView.rotation, poseFromXRNode.rotation);

            diff =
                $"Diff(View vs XRNode): posΔ={posDiff:0.####}m rotΔ={rotDiffDeg:0.###}deg";
        }

        Debug.Log($"{header}\n{poseView}\n{poseNode}\n{diff}\n{proj}");
    }

    // -----------------------------
    // XRNode.LeftEye helpers
    // -----------------------------

    void InitializeLeftEyeDevice()
    {
        var devices = new List<InputDevice>(4);
        InputDevices.GetDevicesAtXRNode(XRNode.LeftEye, devices);

        if (devices.Count > 0)
        {
            _leftEyeDevice = devices[0];
            _leftEyeDeviceFound = _leftEyeDevice.isValid;
        }
        else
        {
            _leftEyeDeviceFound = false;
        }
    }

    bool TryGetLeftEyePose_XRNode(out Pose pose)
    {
        // 1) InputDevice path (preferred)
        if (_leftEyeDeviceFound && _leftEyeDevice.isValid)
        {
            bool gotPos = _leftEyeDevice.TryGetFeatureValue(CommonUsages.leftEyePosition, out Vector3 pos);
            bool gotRot = _leftEyeDevice.TryGetFeatureValue(CommonUsages.leftEyeRotation, out Quaternion rot);

            if (gotPos && gotRot)
            {
                pose = new Pose(pos, rot);
                return true;
            }
        }

        // 2) Fallback: XRNodeState list
        InputTracking.GetNodeStates(_nodeStates);
        for (int i = 0; i < _nodeStates.Count; i++)
        {
            if (_nodeStates[i].nodeType != XRNode.LeftEye) continue;

            if (_nodeStates[i].TryGetPosition(out var pos) &&
                _nodeStates[i].TryGetRotation(out var rot))
            {
                pose = new Pose(pos, rot);
                return true;
            }
        }

        pose = default;
        return false;
    }

    // -----------------------------
    // Matrix printer
    // -----------------------------

    static string MatToString(Matrix4x4 m)
    {
        return
            $"{m[0, 0]:0.###} {m[0, 1]:0.###} {m[0, 2]:0.###} {m[0, 3]:0.###}\n" +
            $"{m[1, 0]:0.###} {m[1, 1]:0.###} {m[1, 2]:0.###} {m[1, 3]:0.###}\n" +
            $"{m[2, 0]:0.###} {m[2, 1]:0.###} {m[2, 2]:0.###} {m[2, 3]:0.###}\n" +
            $"{m[3, 0]:0.###} {m[3, 1]:0.###} {m[3, 2]:0.###} {m[3, 3]:0.###}";
    }
}
