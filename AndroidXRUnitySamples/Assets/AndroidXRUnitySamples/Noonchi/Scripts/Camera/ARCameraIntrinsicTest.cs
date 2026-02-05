using UnityEngine;
using UnityEngine.XR.ARFoundation;
using UnityEngine.XR.ARSubsystems;

public class ARCameraIntrinsicTest : MonoBehaviour
{
    [SerializeField] private ARCameraManager camManager;

    void Update()
    {
        if (camManager == null) return;

        if (camManager.TryGetIntrinsics(out XRCameraIntrinsics intr))
        {
            Debug.Log($"[Noonchi] ARCameraManager intrinsics: " +
                      $"fx={intr.focalLength.x:F2}, fy={intr.focalLength.y:F2}, " +
                      $"cx={intr.principalPoint.x:F2}, cy={intr.principalPoint.y:F2}, " +
                      $"res={intr.resolution.x}x{intr.resolution.y}");
        }
        else
        {
            Debug.Log("[Noonchi] ARCameraManager.TryGetIntrinsics -> false");
        }

        enabled = false; // 한 번만 찍고 끔
    }
}