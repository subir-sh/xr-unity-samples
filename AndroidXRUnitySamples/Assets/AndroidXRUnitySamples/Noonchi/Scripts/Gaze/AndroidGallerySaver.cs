using System;
using UnityEngine;

public static class AndroidGallerySaver
{
#if UNITY_ANDROID && !UNITY_EDITOR
    public static bool SaveImageToGallery(byte[] imageBytes, string displayNameNoExt, string mimeType = "image/png")
    {
        try
        {
            using var unityPlayer = new AndroidJavaClass("com.unity3d.player.UnityPlayer");
            using var activity = unityPlayer.GetStatic<AndroidJavaObject>("currentActivity");
            using var resolver = activity.Call<AndroidJavaObject>("getContentResolver");

            using var mediaStoreImages = new AndroidJavaClass("android.provider.MediaStore$Images$Media");
            AndroidJavaObject externalContentUri = mediaStoreImages.GetStatic<AndroidJavaObject>("EXTERNAL_CONTENT_URI");

            using var contentValues = new AndroidJavaObject("android.content.ContentValues");
            contentValues.Call("put", "_display_name", displayNameNoExt + (mimeType == "image/jpeg" ? ".jpg" : ".png"));
            contentValues.Call("put", "mime_type", mimeType);
            // Android 10+ : 갤러리용 폴더 지정 (Pictures/NoonchiDebug)
            contentValues.Call("put", "relative_path", "Pictures/NoonchiDebug");

            // insert
            AndroidJavaObject uri = resolver.Call<AndroidJavaObject>("insert", externalContentUri, contentValues);
            if (uri == null) return false;

            // openOutputStream(uri) and write
            using var outputStream = resolver.Call<AndroidJavaObject>("openOutputStream", uri);
            if (outputStream == null) return false;

            outputStream.Call("write", imageBytes);
            outputStream.Call("flush");
            outputStream.Call("close");

            Debug.Log($"[GallerySaver] Saved to gallery: {displayNameNoExt}");
            return true;
        }
        catch (Exception e)
        {
            Debug.LogError($"[GallerySaver] Failed: {e}");
            return false;
        }
    }
#else
    public static bool SaveImageToGallery(byte[] imageBytes, string displayNameNoExt, string mimeType = "image/png")
        => false;
#endif
}