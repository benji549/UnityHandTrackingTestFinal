using System.Collections;
using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// Finger-gun charge and recoil-fire controller.
///
/// Inspector wiring:
///
/// Finger Gun Static Hand Gesture:
///     Gesture Performed -> OnFingerGunPerformed
///     Gesture Ended     -> OnFingerGunEnded
///
/// Interaction:
///     1. Player forms finger gun.
///     2. Ball appears and grows.
///     3. Player rapidly moves their hand upward while retaining the gesture.
///     4. The script detects that upward recoil.
///     5. The shot fires from the last stable aim pose before the recoil began.
///
/// One shot is allowed per finger-gun hold. The player must release and remake
/// the finger-gun gesture before charging another shot.
/// </summary>
[DefaultExecutionOrder(100)]
public class FingerGunRecoilShooter : MonoBehaviour
{
    [Header("Automatic Recharge")]
    [Tooltip("Minimum delay after firing before another ball can begin charging.")]
    [SerializeField] private float automaticRechargeDelay = 0.15f;

    [Tooltip(
        "How long the hand must remain stable before the next ball appears.")]
    [SerializeField] private float rechargeStableTime = 0.12f;

    [Tooltip(
        "The hand must move slower than this before another ball can appear.")]
    [SerializeField] private float rechargeMaximumSpeed = 0.22f;
    [Header("Ball - Charge Visual")]
    [SerializeField] private Transform spawnPoint;
    [SerializeField] private GameObject ballPrefab;

    [SerializeField] private float startScale = 0.02f;
    [SerializeField] private float maxScale = 0.25f;
    [SerializeField] private float growthRate = 0.15f;

    [Header("Shot")]
    [Tooltip("Used as a template. A detached clone is spawned at the stable aim pose.")]
    [SerializeField] private ParticleSystem muzzleFlash;

    [SerializeField] private AudioSource shootSound;

    [Header("Aim Stabilization")]
    [Tooltip(
        "The exact transform whose world pose should be used for the shot. " +
        "This should normally be your smoothed muzzle or smoothed wrist-follow transform.")]
    [SerializeField] private Transform aimSource;

    [Tooltip("How many seconds of aim pose history are retained.")]
    [SerializeField] private float aimBufferDuration = 0.6f;

    [Header("Recoil Motion Source")]
    [Tooltip(
        "Transform used to detect the rapid upward movement. Ideally assign an " +
        "unsmoothed wrist or palm transform. If none is assigned, aimSource is used.")]
    [SerializeField] private Transform recoilMotionSource;

    [Tooltip(
        "Its Up direction defines what counts as upward recoil. Assigning the XR Origin " +
        "is recommended. If empty, world-space Vector3.up is used.")]
    [SerializeField] private Transform recoilUpReference;

    [Tooltip("How much motion history is retained.")]
    [SerializeField] private float motionBufferDuration = 0.6f;

    [Tooltip(
        "Smooths the calculated wrist velocity without smoothing the tracked transform. " +
        "Higher values respond more quickly.")]
    [SerializeField] private float velocitySmoothing = 25f;

    [Tooltip(
        "Frame-to-frame speeds above this are treated as tracking jumps rather than recoil.")]
    [SerializeField] private float maxAcceptedFrameSpeed = 6f;

    [Header("Recoil Trigger")]
    [Tooltip(
        "Minimum upward wrist speed required to fire, measured in metres per second.")]
    [SerializeField] private float minimumUpwardSpeed = 0.9f;

    [Tooltip(
        "The time window used to measure upward displacement.")]
    [SerializeField] private float recoilMeasurementWindow = 0.1f;

    [Tooltip(
        "Minimum upward travel during the recoil measurement window, measured in metres.")]
    [SerializeField] private float minimumUpwardDistance = 0.055f;

    [Tooltip(
        "How strongly the motion must point upward. A value of 0.6 means that " +
        "approximately 60 percent of the velocity must be upward.")]
    [Range(0f, 1f)]
    [SerializeField] private float minimumUpwardDirectionDot = 0.6f;

    [Header("Recoil Arming")]
    [Tooltip(
        "Prevents the movement used to initially form the finger gun from firing the shot.")]
    [SerializeField] private float recoilArmingDelay = 0.1f;

    [Tooltip(
        "The hand must remain relatively stable for this long before recoil can fire.")]
    [SerializeField] private float stableArmingTime = 0.08f;

    [Tooltip(
        "The hand is considered stable for arming when moving slower than this.")]
    [SerializeField] private float armingMaximumSpeed = 0.25f;

    [Header("Stable Aim Selection")]
    [Tooltip(
        "How far backward the script searches for a stable pose before the recoil.")]
    [SerializeField] private float stablePoseSearchDuration = 0.35f;

    [Tooltip(
        "Motion below this speed is considered stable when selecting the firing pose.")]
    [SerializeField] private float stableMotionSpeedThreshold = 0.2f;

    [Tooltip(
        "The motion must remain below the stable threshold for this duration.")]
    [SerializeField] private float requiredStablePoseDuration = 0.04f;

    [Tooltip(
        "Moves the chosen firing time slightly earlier than the final stable sample.")]
    [SerializeField] private float stableAimPadding = 0.015f;

    [Tooltip(
        "Fallback lookback when no clearly stable period can be found.")]
    [SerializeField] private float fallbackAimLookback = 0.1f;

    [Header("Gesture Tracking Grace")]
    [Tooltip(
        "Fast motion can briefly make a Static Hand Gesture report Gesture Ended. " +
        "This grace period allows the recoil to finish despite a short recognition dropout. " +
        "Set this to zero for strict gesture recognition.")]
    [SerializeField] private float gestureEndGraceTime = 0.08f;

    [Header("Debug")]
    [SerializeField] private bool logRecoilValues;
    [SerializeField] private float debugLogInterval = 0.1f;

    private struct AimSample
    {
        public float time;
        public Vector3 position;
        public Quaternion rotation;
    }

    private struct MotionSample
    {
        public float time;
        public Vector3 position;
        public float speed;
        public float upwardSpeed;
    }

    private readonly List<AimSample> _aimBuffer = new List<AimSample>(64);
    private readonly List<MotionSample> _motionBuffer = new List<MotionSample>(64);

    private GameObject _ball;
    private float _currentScale;

    private bool _gestureDetected;
    private bool _gestureCycleActive;
    private bool _isCharging;
    private bool _hasFiredThisCycle;

    private bool _recoilArmed;
    private float _chargeStartTime;
    private float _stableArmingTimer;

    private bool _hasPreviousMotionSample;
    private Vector3 _previousMotionPosition;
    private float _previousMotionTime;
    private Vector3 _filteredVelocity;

    private Coroutine _gestureEndRoutine;
    private float _nextDebugLogTime;
    private float _lastShotTime;
    private float _rechargeStableTimer;

    private void Update()
    {
        GrowChargeBall();
    }

    // Runs after most normal tracking and smoothing scripts.
    // If your One Euro filter has a later execution order, set this script
    // to run after it in Project Settings > Script Execution Order.
    private void LateUpdate()
    {
        float now = Time.unscaledTime;

        RecordAimSample(now);
        RecordMotionAndEvaluateRecoil(now);
    }

    private void GrowChargeBall()
    {
        if (!_isCharging || _ball == null)
            return;

        _currentScale = Mathf.Min(
            _currentScale + growthRate * Time.deltaTime,
            maxScale);

        _ball.transform.localScale = Vector3.one * _currentScale;
    }

    // ---------------------------------------------------------------------
    // Finger-gun gesture events
    // ---------------------------------------------------------------------

    /// <summary>
    /// Connect to the finger-gun Static Hand Gesture's Gesture Performed event.
    /// </summary>
    public void OnFingerGunPerformed()
    {
        _gestureDetected = true;

        // The gesture may have briefly disappeared during fast motion.
        // Reacquiring it within the grace period continues the same cycle.
        if (_gestureEndRoutine != null)
        {
            StopCoroutine(_gestureEndRoutine);
            _gestureEndRoutine = null;
        }

        if (_gestureCycleActive)
            return;

        BeginGestureCycle();
    }

    /// <summary>
    /// Connect to the finger-gun Static Hand Gesture's Gesture Ended event.
    /// </summary>
    public void OnFingerGunEnded()
    {
        _gestureDetected = false;

        if (!_gestureCycleActive)
            return;

        if (_gestureEndRoutine != null)
            StopCoroutine(_gestureEndRoutine);

        if (gestureEndGraceTime <= 0f)
        {
            EndGestureCycle();
            return;
        }

        _gestureEndRoutine = StartCoroutine(EndGestureAfterGrace());
    }

    private IEnumerator EndGestureAfterGrace()
    {
        yield return new WaitForSecondsRealtime(gestureEndGraceTime);

        _gestureEndRoutine = null;

        if (!_gestureDetected)
            EndGestureCycle();
    }

    private void BeginGestureCycle()
    {
        _gestureCycleActive = true;
        _isCharging = true;
        _hasFiredThisCycle = false;

        _recoilArmed = false;
        _stableArmingTimer = 0f;
        _rechargeStableTimer = 0f;
        _chargeStartTime = Time.unscaledTime;

        SpawnChargeBall();
    }
    private void EndGestureCycle()
    {
        DestroyChargeBall();

        _gestureCycleActive = false;
        _isCharging = false;
        _hasFiredThisCycle = false;

        _recoilArmed = false;
        _stableArmingTimer = 0f;
        _rechargeStableTimer = 0f;

        _gestureEndRoutine = null;
    }

    // ---------------------------------------------------------------------
    // Charge ball
    // ---------------------------------------------------------------------

    private void SpawnChargeBall()
    {
        DestroyChargeBall();

        if (ballPrefab == null || spawnPoint == null)
            return;

        _ball = Instantiate(
            ballPrefab,
            spawnPoint.position,
            spawnPoint.rotation,
            spawnPoint);

        _currentScale = startScale;
        _ball.transform.localScale = Vector3.one * _currentScale;
    }

    private void DestroyChargeBall()
    {
        if (_ball == null)
            return;

        Destroy(_ball);
        _ball = null;
    }

    // ---------------------------------------------------------------------
    // Aim history
    // ---------------------------------------------------------------------

    private void RecordAimSample(float now)
    {
        if (aimSource == null)
            return;

        _aimBuffer.Add(new AimSample
        {
            time = now,
            position = aimSource.position,
            rotation = aimSource.rotation
        });

        float cutoff = now - aimBufferDuration;
        int trimCount = 0;

        while (trimCount < _aimBuffer.Count &&
               _aimBuffer[trimCount].time < cutoff)
        {
            trimCount++;
        }

        if (trimCount > 0)
            _aimBuffer.RemoveRange(0, trimCount);
    }

    private Pose SampleAimAtTime(float targetTime)
    {
        if (_aimBuffer.Count == 0)
            return GetCurrentAimPose();

        if (targetTime <= _aimBuffer[0].time)
        {
            AimSample first = _aimBuffer[0];
            return new Pose(first.position, first.rotation);
        }

        int lastIndex = _aimBuffer.Count - 1;

        if (targetTime >= _aimBuffer[lastIndex].time)
        {
            AimSample last = _aimBuffer[lastIndex];
            return new Pose(last.position, last.rotation);
        }

        for (int i = 1; i < _aimBuffer.Count; i++)
        {
            AimSample later = _aimBuffer[i];

            if (later.time < targetTime)
                continue;

            AimSample earlier = _aimBuffer[i - 1];

            float interpolation = Mathf.InverseLerp(
                earlier.time,
                later.time,
                targetTime);

            return new Pose(
                Vector3.Lerp(
                    earlier.position,
                    later.position,
                    interpolation),
                Quaternion.Slerp(
                    earlier.rotation,
                    later.rotation,
                    interpolation));
        }

        return GetCurrentAimPose();
    }

    private Pose GetCurrentAimPose()
    {
        if (aimSource != null)
            return new Pose(aimSource.position, aimSource.rotation);

        if (muzzleFlash != null)
        {
            return new Pose(
                muzzleFlash.transform.position,
                muzzleFlash.transform.rotation);
        }

        if (spawnPoint != null)
            return new Pose(spawnPoint.position, spawnPoint.rotation);

        return new Pose(Vector3.zero, Quaternion.identity);
    }

    // ---------------------------------------------------------------------
    // Recoil detection
    // ---------------------------------------------------------------------

    private void RecordMotionAndEvaluateRecoil(float now)
    {
        Transform motionSource =
            recoilMotionSource != null
                ? recoilMotionSource
                : aimSource;

        if (motionSource == null)
            return;

        Vector3 currentPosition = motionSource.position;

        if (!_hasPreviousMotionSample)
        {
            ResetMotionTracking(currentPosition, now);
            return;
        }

        float deltaTime = now - _previousMotionTime;

        if (deltaTime <= Mathf.Epsilon)
            return;

        Vector3 rawVelocity =
            (currentPosition - _previousMotionPosition) / deltaTime;

        _previousMotionPosition = currentPosition;
        _previousMotionTime = now;

        // A long frame or extreme position jump is probably tracking loss,
        // not intentional recoil.
        if (deltaTime > 0.15f ||
            rawVelocity.magnitude > maxAcceptedFrameSpeed)
        {
            ResetMotionTracking(currentPosition, now);
            ResetRecoilArming(now);
            return;
        }

        if (velocitySmoothing <= 0f)
        {
            _filteredVelocity = rawVelocity;
        }
        else
        {
            float smoothingAmount =
                1f - Mathf.Exp(-velocitySmoothing * deltaTime);

            _filteredVelocity = Vector3.Lerp(
                _filteredVelocity,
                rawVelocity,
                smoothingAmount);
        }

        Vector3 upDirection = GetRecoilUpDirection();

        float speed = _filteredVelocity.magnitude;
        float upwardSpeed = Vector3.Dot(
            _filteredVelocity,
            upDirection);

        _motionBuffer.Add(new MotionSample
        {
            time = now,
            position = currentPosition,
            speed = speed,
            upwardSpeed = upwardSpeed
        });

        TrimMotionBuffer(now);
        LogMotionValues(now, speed, upwardSpeed);

        EvaluateRecoil(
            now,
            deltaTime,
            currentPosition,
            upDirection,
            speed,
            upwardSpeed);
    }

    private void EvaluateRecoil(
        float now,
        float deltaTime,
        Vector3 currentPosition,
        Vector3 upDirection,
        float speed,
        float upwardSpeed)
    {
        if (!_gestureCycleActive)
            return;

        // A shot has already happened during this gesture hold.
        // Wait for the recoil motion to settle, then begin another charge.
        if (_hasFiredThisCycle)
        {
            EvaluateAutomaticRecharge(now, deltaTime, speed);
            return;
        }

        if (!_isCharging)
            return;

        // Require the hand to settle after forming the finger gun.
        if (!_recoilArmed)
        {
            if (now - _chargeStartTime < recoilArmingDelay)
            {
                _stableArmingTimer = 0f;
                return;
            }

            if (speed <= armingMaximumSpeed)
                _stableArmingTimer += deltaTime;
            else
                _stableArmingTimer = 0f;

            if (_stableArmingTimer >= stableArmingTime)
                _recoilArmed = true;

            return;
        }

        float targetTime = now - recoilMeasurementWindow;
        Vector3 earlierPosition = SampleMotionPositionAtTime(targetTime);

        float upwardDistance = Vector3.Dot(
            currentPosition - earlierPosition,
            upDirection);

        float upwardDirectionDot =
            speed > 0.001f
                ? upwardSpeed / speed
                : -1f;

        bool hasEnoughSpeed =
            upwardSpeed >= minimumUpwardSpeed;

        bool hasEnoughDistance =
            upwardDistance >= minimumUpwardDistance;

        bool pointsUpwardEnough =
            upwardDirectionDot >= minimumUpwardDirectionDot;

        if (!hasEnoughSpeed ||
            !hasEnoughDistance ||
            !pointsUpwardEnough)
        {
            return;
        }

        Pose stableFirePose = FindLastStableAimPose(now);
        Fire(stableFirePose);
    }

    private void EvaluateAutomaticRecharge(
        float now,
        float deltaTime,
        float speed)
    {
        // Require the finger-gun gesture to currently be recognized.
        // This prevents recharging during the gesture-end grace period.
        if (!_gestureDetected)
        {
            _rechargeStableTimer = 0f;
            return;
        }

        // Do not immediately spawn another ball while the recoil is still happening.
        if (now - _lastShotTime < automaticRechargeDelay)
        {
            _rechargeStableTimer = 0f;
            return;
        }

        if (speed <= rechargeMaximumSpeed)
        {
            _rechargeStableTimer += deltaTime;
        }
        else
        {
            _rechargeStableTimer = 0f;
        }

        if (_rechargeStableTimer >= rechargeStableTime)
        {
            BeginNextCharge();
        }
    }

    private void BeginNextCharge()
    {
        if (!_gestureCycleActive || !_gestureDetected)
            return;

        _hasFiredThisCycle = false;
        _isCharging = true;

        // The new recoil must be armed from a stable hand again.
        _recoilArmed = false;
        _stableArmingTimer = 0f;
        _rechargeStableTimer = 0f;
        _chargeStartTime = Time.unscaledTime;

        SpawnChargeBall();
    }
    private Vector3 SampleMotionPositionAtTime(float targetTime)
    {
        if (_motionBuffer.Count == 0)
            return _previousMotionPosition;

        if (targetTime <= _motionBuffer[0].time)
            return _motionBuffer[0].position;

        int lastIndex = _motionBuffer.Count - 1;

        if (targetTime >= _motionBuffer[lastIndex].time)
            return _motionBuffer[lastIndex].position;

        for (int i = 1; i < _motionBuffer.Count; i++)
        {
            MotionSample later = _motionBuffer[i];

            if (later.time < targetTime)
                continue;

            MotionSample earlier = _motionBuffer[i - 1];

            float interpolation = Mathf.InverseLerp(
                earlier.time,
                later.time,
                targetTime);

            return Vector3.Lerp(
                earlier.position,
                later.position,
                interpolation);
        }

        return _motionBuffer[lastIndex].position;
    }

    private Pose FindLastStableAimPose(float now)
    {
        float earliestAllowedTime =
            now - stablePoseSearchDuration;

        // Search backward from the recoil until we find the most recent
        // continuous stable period.
        for (int i = _motionBuffer.Count - 1; i >= 0; i--)
        {
            MotionSample sample = _motionBuffer[i];

            if (sample.time < earliestAllowedTime)
                break;

            if (sample.speed > stableMotionSpeedThreshold)
                continue;

            if (!HasStableWindowEndingAt(i))
                continue;

            float selectedTime =
                sample.time - stableAimPadding;

            return SampleAimAtTime(selectedTime);
        }

        // Fallback if the player never became completely stable.
        return SampleAimAtTime(now - fallbackAimLookback);
    }

    private bool HasStableWindowEndingAt(int endIndex)
    {
        if (requiredStablePoseDuration <= 0f)
            return true;

        float requiredStartTime =
            _motionBuffer[endIndex].time -
            requiredStablePoseDuration;

        for (int i = endIndex; i >= 0; i--)
        {
            MotionSample sample = _motionBuffer[i];

            if (sample.time <= requiredStartTime)
                return true;

            if (sample.speed > stableMotionSpeedThreshold)
                return false;
        }

        return false;
    }

    private Vector3 GetRecoilUpDirection()
    {
        Vector3 up =
            recoilUpReference != null
                ? recoilUpReference.up
                : Vector3.up;

        return up.sqrMagnitude > 0.0001f
            ? up.normalized
            : Vector3.up;
    }

    private void ResetMotionTracking(Vector3 position, float now)
    {
        _hasPreviousMotionSample = true;
        _previousMotionPosition = position;
        _previousMotionTime = now;
        _filteredVelocity = Vector3.zero;

        _motionBuffer.Clear();

        _motionBuffer.Add(new MotionSample
        {
            time = now,
            position = position,
            speed = 0f,
            upwardSpeed = 0f
        });
    }

    private void ResetRecoilArming(float now)
    {
        if (!_gestureCycleActive || _hasFiredThisCycle)
            return;

        _recoilArmed = false;
        _stableArmingTimer = 0f;
        _chargeStartTime = now;
    }

    private void TrimMotionBuffer(float now)
    {
        float cutoff = now - motionBufferDuration;
        int trimCount = 0;

        while (trimCount < _motionBuffer.Count &&
               _motionBuffer[trimCount].time < cutoff)
        {
            trimCount++;
        }

        if (trimCount > 0)
            _motionBuffer.RemoveRange(0, trimCount);
    }

    // ---------------------------------------------------------------------
    // Firing
    // ---------------------------------------------------------------------

    private void Fire(Pose firePose)
    {
        // Enter the automatic recharge state.
        _hasFiredThisCycle = true;
        _isCharging = false;
        _recoilArmed = false;

        _lastShotTime = Time.unscaledTime;
        _rechargeStableTimer = 0f;
        _stableArmingTimer = 0f;

        DestroyChargeBall();
        SpawnMuzzleFlash(firePose);
    }

    private void SpawnMuzzleFlash(Pose pose)
    {
        if (muzzleFlash != null)
        {
            ParticleSystem effect = Instantiate(
                muzzleFlash,
                pose.position,
                pose.rotation);

            effect.Play(true);

            float lifetime =
                effect.main.duration +
                effect.main.startLifetime.constantMax;

            Destroy(effect.gameObject, lifetime);
        }

        if (shootSound != null)
            shootSound.Play();
    }

    // ---------------------------------------------------------------------
    // Debugging and validation
    // ---------------------------------------------------------------------

    private void LogMotionValues(
        float now,
        float speed,
        float upwardSpeed)
    {
        if (!logRecoilValues ||
            !_gestureCycleActive ||
            now < _nextDebugLogTime)
        {
            return;
        }

        _nextDebugLogTime = now + debugLogInterval;

        Vector3 up = GetRecoilUpDirection();

        Vector3 previousPosition =
            SampleMotionPositionAtTime(
                now - recoilMeasurementWindow);

        float upwardDistance = Vector3.Dot(
            _previousMotionPosition - previousPosition,
            up);

        float directionDot =
            speed > 0.001f
                ? upwardSpeed / speed
                : 0f;

        Debug.Log(
            $"Recoil armed: {_recoilArmed}, " +
            $"speed: {speed:F2} m/s, " +
            $"upward speed: {upwardSpeed:F2} m/s, " +
            $"upward distance: {upwardDistance:F3} m, " +
            $"direction dot: {directionDot:F2}",
            this);
    }

    private void OnDisable()
    {
        StopAllCoroutines();

        DestroyChargeBall();

        _gestureDetected = false;
        _gestureCycleActive = false;
        _isCharging = false;
        _hasFiredThisCycle = false;

        _recoilArmed = false;
        _gestureEndRoutine = null;

        _aimBuffer.Clear();
        _motionBuffer.Clear();

        _hasPreviousMotionSample = false;
        _filteredVelocity = Vector3.zero;
    }

    private void OnValidate()
    {
        startScale = Mathf.Max(0f, startScale);
        maxScale = Mathf.Max(startScale, maxScale);
        growthRate = Mathf.Max(0f, growthRate);

        aimBufferDuration = Mathf.Max(0.1f, aimBufferDuration);
        motionBufferDuration = Mathf.Max(0.1f, motionBufferDuration);

        velocitySmoothing = Mathf.Max(0f, velocitySmoothing);
        maxAcceptedFrameSpeed = Mathf.Max(0.1f, maxAcceptedFrameSpeed);

        minimumUpwardSpeed = Mathf.Max(0f, minimumUpwardSpeed);
        recoilMeasurementWindow =
            Mathf.Max(0.01f, recoilMeasurementWindow);
        minimumUpwardDistance =
            Mathf.Max(0f, minimumUpwardDistance);

        recoilArmingDelay = Mathf.Max(0f, recoilArmingDelay);
        stableArmingTime = Mathf.Max(0f, stableArmingTime);
        armingMaximumSpeed = Mathf.Max(0f, armingMaximumSpeed);

        stablePoseSearchDuration =
            Mathf.Max(0f, stablePoseSearchDuration);
        stableMotionSpeedThreshold =
            Mathf.Max(0f, stableMotionSpeedThreshold);
        requiredStablePoseDuration =
            Mathf.Max(0f, requiredStablePoseDuration);
        stableAimPadding = Mathf.Max(0f, stableAimPadding);
        fallbackAimLookback = Mathf.Max(0f, fallbackAimLookback);

        automaticRechargeDelay = Mathf.Max(0f, automaticRechargeDelay);
        rechargeStableTime = Mathf.Max(0f, rechargeStableTime);
        rechargeMaximumSpeed = Mathf.Max(0f, rechargeMaximumSpeed);

        gestureEndGraceTime =
            Mathf.Max(0f, gestureEndGraceTime);

        // Make sure the buffers can cover every requested lookback.
        float requiredHistory = Mathf.Max(
            recoilMeasurementWindow,
            stablePoseSearchDuration + stableAimPadding);

        motionBufferDuration = Mathf.Max(
            motionBufferDuration,
            requiredHistory + 0.1f);

        aimBufferDuration = Mathf.Max(
            aimBufferDuration,
            requiredHistory + 0.1f);
    }
}