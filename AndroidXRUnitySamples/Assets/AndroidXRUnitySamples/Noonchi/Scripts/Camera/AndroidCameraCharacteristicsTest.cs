using UnityEngine;
using System.Threading.Tasks;
using AndroidXRUnitySamples.Noonchi;

#if UNITY_ANDROID
using UnityEngine.Android;
#endif

public class AndroidCameraCharacteristicsTest : MonoBehaviour
{
    [Header("Capture Device")]
    [SerializeField] private int cameraIndex = 0;

    [Header("Compare Resolutions (in order)")]
    [SerializeField]
    private Vector2Int[] resolutions = new Vector2Int[]
    {
        new Vector2Int(3000, 3000),
        new Vector2Int(640, 640),
        new Vector2Int(320, 240),
    };

    [Header("How long to run each resolution (seconds)")]
    [SerializeField, Min(0.5f)] private float secondsPerResolution = 3.0f;

    [Header("Extra wait between stop/start (seconds)")]
    [SerializeField, Min(0.0f)] private float gapSeconds = 0.5f;

    private CameraCaptureBridge _bridge;
    private bool _cameraReady = false;
    private bool _running = false;

    private async void Start()
    {
#if UNITY_ANDROID
        bool granted = await EnsureCameraPermission();
        Debug.Log("[Noonchi] [Unity] Camera permission granted? " + granted);
        if (!granted) return;

        _bridge = new CameraCaptureBridge();
        _bridge.OnCameraReady += OnCameraReady;
        _bridge.OnError += OnError;

        // NOTE: We intentionally do NOT subscribe to OnFrameDataReceived.
        // We only want Android Logcat logs from Java plugin.

        Debug.Log("[Noonchi] [Unity] Bridge created. Waiting for Camera Ready...");

        // Run compare routine (will wait for OnCameraReady)
        _ = RunCompareRoutine();
#else
        Debug.LogWarning("[Noonchi] [Unity] This script is intended for Android.");
#endif
    }

#if UNITY_ANDROID
    private async Task<bool> EnsureCameraPermission()
    {
        if (Permission.HasUserAuthorizedPermission(Permission.Camera))
            return true;

        var tcs = new TaskCompletionSource<bool>();
        var cb = new PermissionCallbacks();
        cb.PermissionGranted += _ => tcs.TrySetResult(true);
        cb.PermissionDenied += _ => tcs.TrySetResult(false);
        cb.PermissionDeniedAndDontAskAgain += _ => tcs.TrySetResult(false);

        Permission.RequestUserPermission(Permission.Camera, cb);
        return await tcs.Task;
    }
#endif

    private void OnCameraReady()
    {
        Debug.Log("[Noonchi] [Unity] OnCameraReady");
        _cameraReady = true;
    }

    private async Task RunCompareRoutine()
    {
        // Wait until camera ready
        while (!_cameraReady)
            await Task.Delay(50);

        if (_running) return;
        _running = true;

        Debug.Log("[Noonchi] [Unity] Starting resolution compare routine.");

        for (int i = 0; i < resolutions.Length; i++)
        {
            var r = resolutions[i];
            Debug.Log($"[Noonchi] [Unity] START stream: {r.x}x{r.y} (#{i + 1}/{resolutions.Length})");
            _bridge.CaptureFrameStream(cameraIndex, r.x, r.y);

            // Let it run (Java side should dump characteristics + first CaptureResult + onImageAvailable logs)
            await Task.Delay(Mathf.RoundToInt(secondsPerResolution * 1000f));

            Debug.Log($"[Noonchi] [Unity] STOP stream: {r.x}x{r.y}");
            _bridge.CaptureFrameStreamStop();

            if (gapSeconds > 0f)
                await Task.Delay(Mathf.RoundToInt(gapSeconds * 1000f));
        }

        Debug.Log("[Noonchi] [Unity] Done. Check Android Logcat for [Noonchi] lines (CameraCapturePlugin).");
        _running = false;
    }

    private void OnError(CameraCaptureError err)
    {
        Debug.Log($"[Noonchi] [Unity] Camera Error: {err.Error}");
    }

    private void OnDisable()
    {
        if (_bridge != null)
        {
            _bridge.CaptureFrameStreamStop();

            _bridge.OnCameraReady -= OnCameraReady;
            _bridge.OnError -= OnError;
            _bridge.Dispose();
            _bridge = null;
        }
    }
}
