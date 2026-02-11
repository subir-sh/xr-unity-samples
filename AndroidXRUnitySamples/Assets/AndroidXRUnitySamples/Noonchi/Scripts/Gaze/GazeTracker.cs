using System;
using System.Reflection;
using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.UI;
using UnityEngine.XR.Interaction.Toolkit.Interactors.Visuals;

public class GazeTracker : MonoBehaviour
{
    [SerializeField] private XRInteractorReticleVisual reticleVisual;
    [SerializeField] private CameraIntrinsics intrinsics;
    private bool flipImageY = false; // 모델이 달라지면 수정해야 할 수도 있음
    private int dotRadiusPx = 4;

    [Header("For Debugging: Save photo into gallery when input")]
    [SerializeField] private InputActionProperty pinchAction;

    // XRInteractorReticleVisual private fields
    private FieldInfo _fiTargetEndPoint;
    private FieldInfo _fiHasRaycastHit;

    private Texture2D _latestFrame;
    private Pose _latestPose;
    private bool _hasLatestPose;

    // private bool _trackingEnabled;

    void Awake()
    {
        if (reticleVisual == null) reticleVisual = FindObjectOfType<XRInteractorReticleVisual>(true);

        var t = typeof(XRInteractorReticleVisual);
        _fiTargetEndPoint = t.GetField("m_TargetEndPoint", BindingFlags.Instance | BindingFlags.NonPublic);
        _fiHasRaycastHit  = t.GetField("m_HasRaycastHit",  BindingFlags.Instance | BindingFlags.NonPublic);
    }

    /// <summary>
    /// Overlay gaze point onto a left-eye RGB frame and return the gaze pixel position (x,y).
    /// - Input: the captured frame Texture2D (left-eye), pose of that frame, and its intrinsics.
    /// - Output: overlayed frame (same instance, modified in-place) and gaze pixel.
    /// </summary>
    public bool OverlayGazeOnFrame(
        Texture2D frame,
        Pose leftEyePose,
        out Texture2D overlayedImage,
        out Vector2 gazePixelXY)
    {
        overlayedImage = frame;
        gazePixelXY = new Vector2(float.NaN, float.NaN);

        //if (!_trackingEnabled) return false;
        if (frame == null) return false;
        
        // gaze reticle이 현재 씬의 다른 물체와 hit하고 있는지 판정: 
        // hit 했을 경우 그 지점(즉, gaze의 world space 상 좌표) = worldPoint 
        if (!TryReadFromReticle(out var worldPoint, out var hasHit)) return false;

        // 만약 gaze가 씬의 다른 물체와 hit하지 않았다면, 아무것도 그리지 않음
        if (!hasHit) return false;

        if (!TryProjectWorldToImagePixel(
                worldPoint,
                leftEyePose,
                intrinsics,
                frame.width,
                frame.height,
                flipImageY,
                out var px))
            return false;

        gazePixelXY = px;

        DrawDotInPlace(frame, Mathf.RoundToInt(px.x), Mathf.RoundToInt(px.y), dotRadiusPx);
        return true;
    }

    // =========================
    // Internals
    // =========================

    private bool TryReadFromReticle(out Vector3 p3, out bool hasHit)
    {
        p3 = default; hasHit = false;
        try
        {
            p3 = (Vector3)_fiTargetEndPoint.GetValue(reticleVisual);
            // physics raycast 사용
            hasHit = _fiHasRaycastHit != null && (bool)_fiHasRaycastHit.GetValue(reticleVisual);
            return true;
        }
        catch (Exception e)
        {
            Debug.LogWarning($"[GazeTracker] Reflection read failed: {e.Message}");
            return false;
        }
    }

    /// <summary>
    /// Pinhole projection (camera looks along +Z in its local space):
    /// pCam = inv(R) * (world - camPos)
    /// u = fx*(x/z)+cx, v = fy*(y/z)+cy
    /// Scales into actual texture size if intrinsics.imageW/H differ.
    /// </summary>
    private static bool TryProjectWorldToImagePixel(
        Vector3 worldPoint,
        Pose camPose,
        CameraIntrinsics intr,
        int texW,
        int texH,
        bool flipY,
        out Vector2 pixel)
    {
        pixel = default;

        // World Space -> Camera Local Space
        // 2D -> 3D 한 것을 역방향으로 복구
        Vector3 v = worldPoint - camPose.position;
        Vector3 pCam = Quaternion.Inverse(camPose.rotation) * v;
        if (pCam.z <= 1e-6f) return false; // 0 or 음수면 x/z, y/z에서 터질수도

        float x = intr.fx * (pCam.x / pCam.z) + intr.cx;
        float y = intr.fy * (pCam.y / pCam.z) + intr.cy;

        // 만약 intrinsics를 다른 해상도에서 얻었을 경우 보정하는 코드
        // float sx = (intr.imageW > 0.5f) ? (texW / intr.imageW) : 1f;
        // float sy = (intr.imageH > 0.5f) ? (texH / intr.imageH) : 1f;
        // x *= sx;
        // y *= sy;

        if (flipY) y = (texH - 1) - y;

        // 보정 결과 x는 0 ~ texW 사이, y는 0 ~ texH 사이여야 함
        if (x < 0 || x >= texW || y < 0 || y >= texH) return false;

        pixel = new Vector2(x, y);
        return true;
    }

    private static void DrawDotInPlace(Texture2D tex, int cx, int cy, int radius)
    {
        // (cx, cy)를 중심으로 하고, radius를 반지름으로 하는 원 그리기

        int w = tex.width;
        int h = tex.height;
        int r2 = radius * radius;

        // 동작 최적화: 텍스처 전체를 돌 이유 없으므로, 
        // 중심이 (cx, cy)이고 한 변이 radius * 2인 정사각형 내에서만 체크 
        int x0 = Mathf.Max(0, cx - radius);
        int x1 = Mathf.Min(w - 1, cx + radius);
        int y0 = Mathf.Max(0, cy - radius);
        int y1 = Mathf.Min(h - 1, cy + radius);

        // 정사각형 안에서, "원"에 해당하는 범위만 골라서 픽셀 칠하기
        for (int y = y0; y <= y1; y++)
        {
            int dy = y - cy;
            for (int x = x0; x <= x1; x++)
            {
                int dx = x - cx;
                if (dx * dx + dy * dy > r2) continue;
                tex.SetPixel(x, y, Color.white);
            }
        }

        tex.Apply(updateMipmaps: false, makeNoLongerReadable: false);
    }

    public void SetLatestFrame(Texture2D frame, Pose leftEyePose)
    {
        _latestFrame = frame;
        _latestPose = leftEyePose;
        _hasLatestPose = true;
    }

    private void OnPinchPerformed(InputAction.CallbackContext ctx)
    {
        if (_latestFrame == null || !_hasLatestPose) return;
        if (OverlayGazeOnFrame(_latestFrame, _latestPose, out var overlayed, out var gazePx))
        {
            // DrawDotInPlace(_latestFrame, 0, 0, 6); // to check where the origin for the image is 
            var bytes = overlayed.EncodeToPNG();
            AndroidGallerySaver.SaveImageToGallery(
                bytes,
                $"gaze_{System.DateTime.Now:HHmmss_fff}_x{(int)gazePx.x}_y{(int)gazePx.y}",
                "image/png"
            );
            Debug.Log($"[GazeTracker] pixel=({gazePx.x:F1},{gazePx.y:F1}) tex={overlayed.width}x{overlayed.height}");
        }
        else
        {
            Debug.Log("[GazeTracker] no hit / projection failed");
        }
    }

    void OnEnable()
    {
        if (pinchAction.action != null)
        {
            pinchAction.action.Enable();
            pinchAction.action.performed += OnPinchPerformed;
        }
    }

    void OnDisable()
    {
        if (pinchAction.action != null)
        {
            pinchAction.action.performed -= OnPinchPerformed;
            pinchAction.action.Disable();
        }
    }
}
