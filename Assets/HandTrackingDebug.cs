using UnityEngine;

public class HandTrackingDebug : MonoBehaviour
{
    public void OnTrackingChanged(bool isTracked)
    {
        Debug.Log($"RIGHT HAND TRACKING CHANGED: {isTracked}");
    }

    public void OnTrackingAcquired()
    {
        Debug.Log("RIGHT HAND TRACKING ACQUIRED");
    }

    public void OnTrackingLost()
    {
        Debug.Log("RIGHT HAND TRACKING LOST");
    }

    public void OnThumbsUp()
    {
        Debug.Log("THUMBS UP DETECTED");
    }

    public void OnPoseUpdated()
    {
        Debug.Log("Pose updated");
    }
}