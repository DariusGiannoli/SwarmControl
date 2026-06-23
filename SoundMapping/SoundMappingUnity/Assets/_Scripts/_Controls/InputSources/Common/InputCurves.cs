using UnityEngine;

/// <summary>
/// Pure-math helpers shared by every spread/height input source.
/// Centralizing these means rate/absolute/curve behavior stays identical across
/// MediaPipe, RTMPose, hand-tracking, and Touch controllers.
/// </summary>
public static class InputCurves
{
    /// <summary>
    /// Apply the chosen response curve to a signed rate in [-1, +1].
    /// Linear = pass-through. Exponential = sign(x) * |x|^exponent (1.0 = linear, 2.0 = squared).
    /// </summary>
    public static float ApplyCurve(float rate, ResponseCurve curve, float exponent)
    {
        if (curve == ResponseCurve.Linear || Mathf.Approximately(exponent, 1f)) return rate;
        float sign = Mathf.Sign(rate);
        float magnitude = Mathf.Abs(rate);
        return sign * Mathf.Pow(magnitude, exponent);
    }

    /// <summary>
    /// Map a measured value to a signed rate in [-1, +1] given a calibrated min/neutral/max
    /// frame and a deadzone half-width around neutral.
    /// Below neutral - dz: rate scales linearly to -1 at min.
    /// Above neutral + dz: rate scales linearly to +1 at max.
    /// Inside the deadzone: rate = 0.
    /// Returns 0 if the calibration is degenerate (min >= max).
    /// </summary>
    public static float ToRate(float current, float min, float neutral, float max, float deadzoneHalfWidth)
    {
        if (max <= min) return 0f;
        float dz = Mathf.Max(deadzoneHalfWidth, 0f);
        float deadMin = neutral - dz;
        float deadMax = neutral + dz;

        if (current >= deadMin && current <= deadMax) return 0f;

        if (current < deadMin)
        {
            float span = Mathf.Max(deadMin - min, 0.0001f);
            return Mathf.Clamp((current - deadMin) / span, -1f, 0f);
        }

        float spanHi = Mathf.Max(max - deadMax, 0.0001f);
        return Mathf.Clamp((current - deadMax) / spanHi, 0f, 1f);
    }

    /// <summary>
    /// Map a signed value to [-1, +1] while excluding the deadzone from the usable range.
    /// Example: value=10, maxMagnitude=30, deadzone=5 returns (10-5)/(30-5)=0.2.
    /// </summary>
    public static float ToSignedRateAfterDeadzone(float value, float maxMagnitude, float deadzoneHalfWidth)
    {
        float maxAbs = Mathf.Abs(maxMagnitude);
        float dz = Mathf.Max(deadzoneHalfWidth, 0f);
        float valueAbs = Mathf.Abs(value);

        if (valueAbs <= dz) return 0f;

        float usableRange = maxAbs - dz;
        if (usableRange <= 0.0001f) return 0f;

        float rate = Mathf.Clamp01((valueAbs - dz) / usableRange);
        return rate * Mathf.Sign(value);
    }

    /// <summary>
    /// Map a measured value to a normalized 0..1 position within a calibrated [min, max] range.
    /// Clamped at both ends. Returns 0 if the calibration is degenerate.
    /// </summary>
    public static float ToAbsolute01(float current, float min, float max)
    {
        if (max <= min) return 0f;
        return Mathf.Clamp01((current - min) / (max - min));
    }
}
