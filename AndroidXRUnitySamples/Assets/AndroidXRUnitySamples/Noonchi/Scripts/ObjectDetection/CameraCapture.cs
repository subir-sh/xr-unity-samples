using UnityEngine;
using System.Threading.Tasks;
using AndroidXRUnitySamples.Noonchi;
using UnityEngine.XR;
using System.Collections.Generic;

#if UNITY_ANDROID
using UnityEngine.Android;
#endif

public class CameraCapture : MonoBehaviour
{
    [Header("Capture")]
    [SerializeField] private int cameraIndex = 0; // 0 = left eye rgb camera
    [SerializeField] private int width = 640;
    [SerializeField] private int height = 640;

    [SerializeField] private ObjectDetector detector;
    [SerializeField] private Transform xrRigRoot;
    [SerializeField] private int runDetectionEveryNFrames = 3; // N frame마다 detector call

    private CameraCaptureBridge _bridge;
    private Texture2D _tex;
    private int _frameCounter;
    private InputDevice _leftEyeDevice;

    private async void Start()
    {
        InitializeLeftEyeDevice();
        // 캡처 브릿지 생성 + 구독은 카메라 권한 승인 여부를 받아온 이후에 
        bool granted = await EnsureCameraPermission();
        if (!granted) return;

        _bridge = new CameraCaptureBridge();
        _bridge.OnCameraReady += OnCameraReady;
        _bridge.OnFrameDataReceived += OnFrame;
        _bridge.OnError += OnError;
    }

    // 권한 받았는지 여부를 async하게 전달 
    // (그냥 하면, 권한 여부 판정 전에 실행되어서 무조건 권한 없는 것으로 취급: 실패)
    private async Task<bool> EnsureCameraPermission()
    {
        if (Permission.HasUserAuthorizedPermission(Permission.Camera)) return true;

        var tcs = new TaskCompletionSource<bool>();
        var cb = new PermissionCallbacks();
        cb.PermissionGranted += _ => tcs.TrySetResult(true);
        cb.PermissionDenied += _ => tcs.TrySetResult(false);

        Permission.RequestUserPermission(Permission.Camera, cb);
        return await tcs.Task;
    }

    private void OnCameraReady()
    {
        // 연속 프레임 캡처 시작 
        _bridge.CaptureFrameStream(cameraIndex, width, height);
    }

    private void OnFrame(CameraFrameData frame)
    {
        if (frame.ImageData == null || frame.ImageData.Length == 0) return;

        // 카메라 캡처 프레임 크기에 맞춰 Texture 2D 보정 (혹시 달라질 수도 있으므로) --> 근데 없어도 될 수도?
        if (_tex == null || _tex.width != frame.Width || _tex.height != frame.Height)
        {
            if (_tex != null) Destroy(_tex);
            _tex = new Texture2D(frame.Width, frame.Height, TextureFormat.RGBA32, false);
        }

        // JPEG --> LoadImage
        _tex.LoadImage(frame.ImageData);

        if (detector != null && (_frameCounter++ % runDetectionEveryNFrames == 0))
            if (!TryGetLeftEyePose(out var cameraPose)) return;
            else detector.SubmitFrame(_tex, cameraPose); // 실제 모델에 보내기 
    }

    // Left eye camera의 Pose를 받아오기 위한 작업
    private void InitializeLeftEyeDevice()
    {
        // 일단 에러 핸들링은 하지 않음
        var devices = new List<InputDevice>(4);
        InputDevices.GetDevicesAtXRNode(XRNode.LeftEye, devices);
        _leftEyeDevice = devices[0];
    }

    private bool TryGetLeftEyePose(out Pose pose)
    {
        pose = default;
        bool gotPos = _leftEyeDevice.TryGetFeatureValue(CommonUsages.leftEyePosition, out var pos);
        bool gotRot = _leftEyeDevice.TryGetFeatureValue(CommonUsages.leftEyeRotation, out var rot);
        if (!gotPos || !gotRot) return false;

        pose = new Pose(pos, rot);
        return true;
    }

    // 에러 처리 및 메모리 free 
    private void OnError(CameraCaptureError err)
    {
        Debug.Log($"[Noonchi] [Camera] Error: {err.Error}");
    }

    private void OnDisable()
    {
        if (_bridge != null)
        {
            _bridge.CaptureFrameStreamStop();

            _bridge.OnCameraReady -= OnCameraReady;
            _bridge.OnFrameDataReceived -= OnFrame;
            _bridge.OnError -= OnError;
            _bridge.Dispose();
            _bridge = null;
        }

        if (_tex != null)
        {
            Destroy(_tex);
            _tex = null;
        }
    }
}
