using UnityEngine;

[CreateAssetMenu(menuName = "Noonchi/Camera Intrinsics", fileName = "CameraIntrinsics")]
public class CameraIntrinsics : ScriptableObject
{
    [Header("Pinhole intrinsics (pixels)")]
    public float fx = 386.67f;
    public float fy = 386.67f;
    public float cx = 320f;
    public float cy = 320f;

    [Header("Image size used by the model")]
    public int imageW = 640;
    public int imageH = 640;
}