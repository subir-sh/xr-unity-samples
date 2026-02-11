using UnityEngine;

[CreateAssetMenu(menuName = "Noonchi/Detection UI Drawer Settings", fileName = "DetectionUIDrawerSettings")]
public class DetectionUIDrawerSettings : ScriptableObject
{
    [Header("Physics Raycast")]
    public float maxDistance = 10f;

    [Header("BBox coords")]
    public bool flipY = true;

    [Header("Visuals")]
    public bool drawOutline = true;
    [Min(0.0001f)] public float outlineWidth = 0.003f;

    [Header("Label")]
    public bool showLabel = true;
    public bool labelFaceEye = true;
    public float labelDepthOffset = 0.015f;
    public float labelFontSize = 0.35f;

    [Header("Label Size Stabilization (screen-ish size)")]
    public bool stabilizeLabelSize = true;
    [Min(0.001f)] public float labelRefDistance = 1.0f;
    [Min(0.0001f)] public float labelRefScale = 1.0f;
    [Min(0.0001f)] public float labelMinScale = 0.7f;
    [Min(0.0001f)] public float labelMaxScale = 2.5f;

    [Header("Placement")]
    [Min(0f)] public float persistSeconds = 0.2f;
    public bool invertForward = false;
    public bool clampMinSize = true;
    [Min(0.001f)] public float minSizeMeters = 0.02f;
}