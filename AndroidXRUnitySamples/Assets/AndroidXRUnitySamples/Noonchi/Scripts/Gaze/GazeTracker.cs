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
    private int dotRadiusPx = 4; // gaze point를 의미하는 흰 점의 반지름 크기

    [Header("For Debugging: Show overlayed image in the canvas when input")]
    [SerializeField] private InputActionProperty pinchAction;
    [SerializeField] private RawImage debugPreview;

    // XRInteractorReticleVisual private fields
    private FieldInfo _fiTargetEndPoint;
    private FieldInfo _fiHasRaycastHit;

    // For logging
    private Texture2D _latestFrame;
    private Pose _latestPose;
    private bool _hasLatestPose;

    void Awake()
    {
        if (reticleVisual == null) reticleVisual = FindObjectOfType<XRInteractorReticleVisual>(true);

        var t = typeof(XRInteractorReticleVisual);
        _fiTargetEndPoint = t.GetField("m_TargetEndPoint", BindingFlags.Instance | BindingFlags.NonPublic);
        _fiHasRaycastHit  = t.GetField("m_HasRaycastHit",  BindingFlags.Instance | BindingFlags.NonPublic);
    }

    // =========================
    // Public API
    // =========================

    [Serializable]
    public struct CurrentGaze
    {
        public Texture2D currentImage;          // pinch 시점의 원본 프레임(그대로)
        public Texture2D currentImageWithGaze;  // 복사본 + 흰 점 overlay
        public Vector2 gazePixelXY;             // 이미지 픽셀 좌표 (x,y) -> left bottom이 0, 0
        public bool hasHit;                     // gaze가 뭔가를 hit 했는지
    }

    /// <summary>
    /// Overlay gaze point onto a left-eye RGB frame and return the gaze pixel position (x,y).
    /// - Input: the captured frame Texture2D (left-eye), pose of that frame, and its intrinsics.
    /// - Output: original frame, overlayed frame (same instance, modified in-place) and gaze pixel.
    /// </summary>
    public bool GetCurrentGaze(out CurrentGaze result)
    {
        result = default;

        // 최신 프레임/포즈 없으면 실패
        if (_latestFrame == null || !_hasLatestPose) return false;

        // reticle hit 여부 먼저 확인
        // hit 했을 경우 그 지점(즉, gaze의 world space 상 좌표) = worldPoint 
        if (!TryReadFromReticle(out var worldPoint, out var hasHit)) return false;
        if (!hasHit) return false;

        // world -> image pixel
        if (!TryProjectWorldToImagePixel(
                worldPoint,
                _latestPose,
                intrinsics,
                _latestFrame.width,
                _latestFrame.height,
                flipImageY,
                out var px))
            return false;

        // dot 찍힐 이미지 복사본 만들기 (원본 보호)
        Texture2D dotted = CopyTexture(_latestFrame);
        DrawDotInPlace(dotted, Mathf.RoundToInt(px.x), Mathf.RoundToInt(px.y), dotRadiusPx);

        result = new CurrentGaze
        {
            currentImage = _latestFrame,
            currentImageWithGaze = dotted,
            gazePixelXY = px,
            hasHit = true
        };

        return true;
    }

    // CameraCapture에서 카메라 정보 주입 받음
    public void SetLatestFrame(Texture2D frame, Pose leftEyePose)
    {
        _latestFrame = frame;
        _latestPose = leftEyePose;
        _hasLatestPose = true;
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
        Vector3 v = worldPoint - camPose.position; // v = “카메라에서 hit point까지” 향하는 벡터
        Vector3 pCam = Quaternion.Inverse(camPose.rotation) * v; // world space 벡터 v를 camera local space 벡터로 변환 
        if (pCam.z <= 1e-6f) return false; // 0 or 음수면 x/z, y/z에서 터질수도

        // 원래 world ray를 쏠 때, (x, y, 1) 방향으로 쏨:
        // 그렇다면 world hit point는 t*(x, y, 1) 형태가 됨.
        // 따라서 /z를 통해서 t를 소거하여, normalize -> (x, y, 1) 복구
        // 즉, 여기의 x, y는 canvasCenterX/Y에 해당 (viewport 좌표계)
        float x = intr.fx * (pCam.x / pCam.z) + intr.cx - 12f; // 마지막 float -> empirical하게 얻은 보정치
        float y = intr.fy * (pCam.y / pCam.z) + intr.cy + 12f;

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

    private static Texture2D CopyTexture(Texture2D src)
    {
        // src가 RGBA32가 아닐 수 있으니, 안전하게 RGBA32로 새로 만든다
        var dst = new Texture2D(src.width, src.height, TextureFormat.RGBA32, false);
        dst.SetPixels32(src.GetPixels32());
        dst.Apply(false, false);
        return dst;
    }

    // =========================
    // Logging
    // =========================

    private void OnPinchPerformed(InputAction.CallbackContext ctx)
    {
        if (!GetCurrentGaze(out var g))
        {
            Debug.Log("[GazeTracker] no hit / projection failed");
            return;
        }

        // debug preview in Unity scene canvas
        if (debugPreview != null) debugPreview.texture = g.currentImageWithGaze;
        //SaveOverlayToGallery(g.currentImageWithGaze, g.gazePixelXY);

        Debug.Log($"[GazeTracker] pixel=({g.gazePixelXY.x:F1},{g.gazePixelXY.y:F1}) tex={g.currentImage.width}x{g.currentImage.height}");
    }

    private static void SaveOverlayToGallery(Texture2D overlayed, Vector2 gazePx)
    {
        var bytes = overlayed.EncodeToPNG();
        AndroidGallerySaver.SaveImageToGallery(
            bytes,
            $"gaze_{System.DateTime.Now:HHmmss_fff}_x{(int)gazePx.x}_y{(int)gazePx.y}",
            "image/png"
        );
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
