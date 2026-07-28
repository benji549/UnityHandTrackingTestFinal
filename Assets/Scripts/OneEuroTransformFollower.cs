using UnityEngine;

/// <summary>
/// Smoothly follows a tracked Transform using One Euro Filters.
///
/// Position is filtered using linear velocity.
/// Rotation is filtered using angular velocity and Quaternion.Slerp.
///
/// The visual object should not remain parented to the noisy tracked object.
/// This component can detach it automatically while preserving its world pose.
/// </summary>
public class OneEuroTransformFollower : MonoBehaviour
{
    [Header("Target")]
    [SerializeField]
    private Transform target;

    [Tooltip("Automatically use the current parent as the target.")]
    [SerializeField]
    private bool useCurrentParentAsTarget = true;

    [Tooltip("Detach this object so it no longer directly inherits target jitter.")]
    [SerializeField]
    private bool detachFromParentOnStart = true;

    [Tooltip("Maintain the object's initial position and rotation relative to the target.")]
    [SerializeField]
    private bool preserveInitialOffset = true;

    [Header("Position Filter")]

    [Tooltip("Lower values remove more low-speed jitter but introduce more lag.")]
    [SerializeField, Min(0.001f)]
    private float positionMinCutoff = 1f;

    [Tooltip("How quickly filtering decreases as movement speed increases.")]
    [SerializeField, Min(0f)]
    private float positionBeta = 0.25f;

    [Header("Rotation Filter")]

    [Tooltip("Lower values remove more rotational jitter but introduce more lag.")]
    [SerializeField, Min(0.001f)]
    private float rotationMinCutoff = 1.5f;

    [Tooltip("How quickly rotational filtering decreases as angular speed increases.")]
    [SerializeField, Min(0f)]
    private float rotationBeta = 0.1f;

    [Header("Shared Filter Settings")]

    [Tooltip("Cutoff frequency used to smooth velocity measurements.")]
    [SerializeField, Min(0.001f)]
    private float derivativeCutoff = 1f;

    [Header("Tracking Recovery")]

    [Tooltip("Snap to the target if the position difference exceeds this distance.")]
    [SerializeField, Min(0f)]
    private float teleportDistance = 0.75f;

    [Tooltip("Snap to the target if the rotation difference exceeds this angle.")]
    [SerializeField, Range(0f, 180f)]
    private float teleportAngle = 100f;

    [Tooltip("Use unscaled time so filtering continues when Time.timeScale changes.")]
    [SerializeField]
    private bool useUnscaledTime = false;

    private Vector3 localPositionOffset;
    private Quaternion localRotationOffset = Quaternion.identity;

    private readonly OneEuroVector3Filter positionFilter =
        new OneEuroVector3Filter();

    private readonly OneEuroQuaternionFilter rotationFilter =
        new OneEuroQuaternionFilter();

    private bool initialized;

    private void Start()
    {
        if (target == null &&
            useCurrentParentAsTarget &&
            transform.parent != null)
        {
            target = transform.parent;
        }

        if (target == null)
        {
            Debug.LogError(
                $"{nameof(OneEuroTransformFollower)} on '{name}' has no target.",
                this);

            enabled = false;
            return;
        }

        CaptureCurrentOffset();

        if (detachFromParentOnStart)
        {
            // true preserves the current world position and rotation.
            transform.SetParent(null, true);
        }

        SnapAndReset();
    }

    private void OnEnable()
    {
        initialized = false;
    }

    private void LateUpdate()
    {
        if (target == null)
            return;

        float deltaTime = useUnscaledTime
            ? Time.unscaledDeltaTime
            : Time.deltaTime;

        if (deltaTime <= Mathf.Epsilon)
            return;

        GetDesiredPose(
            out Vector3 desiredPosition,
            out Quaternion desiredRotation);

        bool trackingJump =
            Vector3.Distance(transform.position, desiredPosition)
                > teleportDistance ||
            Quaternion.Angle(transform.rotation, desiredRotation)
                > teleportAngle;

        if (!initialized || trackingJump)
        {
            transform.SetPositionAndRotation(
                desiredPosition,
                desiredRotation);

            positionFilter.Reset(desiredPosition);
            rotationFilter.Reset(desiredRotation);

            initialized = true;
            return;
        }

        Vector3 filteredPosition = positionFilter.Filter(
            desiredPosition,
            deltaTime,
            positionMinCutoff,
            positionBeta,
            derivativeCutoff);

        Quaternion filteredRotation = rotationFilter.Filter(
            desiredRotation,
            deltaTime,
            rotationMinCutoff,
            rotationBeta,
            derivativeCutoff);

        transform.SetPositionAndRotation(
            filteredPosition,
            filteredRotation);
    }

    private void GetDesiredPose(
        out Vector3 desiredPosition,
        out Quaternion desiredRotation)
    {
        if (preserveInitialOffset)
        {
            desiredPosition =
                target.TransformPoint(localPositionOffset);

            desiredRotation =
                target.rotation * localRotationOffset;
        }
        else
        {
            desiredPosition = target.position;
            desiredRotation = target.rotation;
        }
    }

    [ContextMenu("Capture Current Offset")]
    public void CaptureCurrentOffset()
    {
        if (target == null)
            return;

        localPositionOffset =
            target.InverseTransformPoint(transform.position);

        localRotationOffset =
            Quaternion.Inverse(target.rotation) *
            transform.rotation;
    }

    [ContextMenu("Snap And Reset Filter")]
    public void SnapAndReset()
    {
        if (target == null)
            return;

        GetDesiredPose(
            out Vector3 desiredPosition,
            out Quaternion desiredRotation);

        transform.SetPositionAndRotation(
            desiredPosition,
            desiredRotation);

        positionFilter.Reset(desiredPosition);
        rotationFilter.Reset(desiredRotation);

        initialized = true;
    }

    public void SetTarget(
        Transform newTarget,
        bool captureNewOffset = true)
    {
        target = newTarget;

        if (target == null)
        {
            initialized = false;
            return;
        }

        if (captureNewOffset)
            CaptureCurrentOffset();

        SnapAndReset();
    }

    /// <summary>
    /// One Euro Filter for a Vector3 signal.
    /// A shared cutoff is calculated from total movement speed,
    /// keeping the filtering consistent across all three axes.
    /// </summary>
    private sealed class OneEuroVector3Filter
    {
        private bool initialized;

        private Vector3 previousRawValue;
        private Vector3 filteredValue;
        private Vector3 filteredDerivative;

        public Vector3 Filter(
            Vector3 rawValue,
            float deltaTime,
            float minCutoff,
            float beta,
            float derivativeCutoff)
        {
            if (!initialized || deltaTime <= Mathf.Epsilon)
            {
                Reset(rawValue);
                return rawValue;
            }

            Vector3 derivative =
                (rawValue - previousRawValue) / deltaTime;

            float derivativeAlpha =
                CalculateAlpha(derivativeCutoff, deltaTime);

            filteredDerivative = Vector3.Lerp(
                filteredDerivative,
                derivative,
                derivativeAlpha);

            float cutoff =
                minCutoff +
                beta * filteredDerivative.magnitude;

            float valueAlpha =
                CalculateAlpha(cutoff, deltaTime);

            filteredValue = Vector3.Lerp(
                filteredValue,
                rawValue,
                valueAlpha);

            previousRawValue = rawValue;

            return filteredValue;
        }

        public void Reset(Vector3 value)
        {
            previousRawValue = value;
            filteredValue = value;
            filteredDerivative = Vector3.zero;
            initialized = true;
        }
    }

    /// <summary>
    /// Quaternion-oriented adaptation of the One Euro Filter.
    ///
    /// Angular speed controls the adaptive cutoff, while Slerp
    /// filters the orientation without directly filtering quaternion
    /// components.
    /// </summary>
    private sealed class OneEuroQuaternionFilter
    {
        private bool initialized;

        private Quaternion previousRawValue;
        private Quaternion filteredValue;

        // Stored in radians per second.
        private float filteredAngularSpeed;

        public Quaternion Filter(
            Quaternion rawValue,
            float deltaTime,
            float minCutoff,
            float beta,
            float derivativeCutoff)
        {
            if (!initialized || deltaTime <= Mathf.Epsilon)
            {
                Reset(rawValue);
                return rawValue;
            }

            float angleDeltaRadians =
                Quaternion.Angle(previousRawValue, rawValue) *
                Mathf.Deg2Rad;

            float angularSpeed =
                angleDeltaRadians / deltaTime;

            float derivativeAlpha =
                CalculateAlpha(derivativeCutoff, deltaTime);

            filteredAngularSpeed = Mathf.Lerp(
                filteredAngularSpeed,
                angularSpeed,
                derivativeAlpha);

            float cutoff =
                minCutoff +
                beta * Mathf.Abs(filteredAngularSpeed);

            float valueAlpha =
                CalculateAlpha(cutoff, deltaTime);

            filteredValue = Quaternion.Slerp(
                filteredValue,
                rawValue,
                valueAlpha);

            previousRawValue = rawValue;

            return filteredValue;
        }

        public void Reset(Quaternion value)
        {
            previousRawValue = value;
            filteredValue = value;
            filteredAngularSpeed = 0f;
            initialized = true;
        }
    }

    private static float CalculateAlpha(
        float cutoff,
        float deltaTime)
    {
        cutoff = Mathf.Max(0.001f, cutoff);
        deltaTime = Mathf.Max(Mathf.Epsilon, deltaTime);

        float frequencyTerm =
            2f * Mathf.PI * cutoff * deltaTime;

        return frequencyTerm / (frequencyTerm + 1f);
    }
}