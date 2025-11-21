using UnityEngine;
using System;

[RequireComponent(typeof(AudioSource))]
public class NoiseBlender : MonoBehaviour
{
    [Header("Blend = 0 → 100% Brown  |  1 → 100% Pink")]
    [Range(0f, 1f)]
    public float blend = 0f;

    // ----- Brown Noise State -----
    private float _brownSample = 0f;    // Current brown noise sample
    private const float BrownStep  = 0.02f;
    private const float BrownClamp = 1.0f;

    // ----- Pink Noise State -----
    private const int PinkSize = 16;
    private float[] _pinkRows = new float[PinkSize];
    private float _pinkRunningSum = 0f;
    private int _pinkIndex = 0;

    // Use a thread‐safe random from .NET
    private System.Random _rng;

    void Awake()
    {
        // Initialize our System.Random with a seed (optional)
        _rng = new System.Random(); // or specify a seed, e.g. (1234)

        // Initialize pink rows to random values using System.Random
        for (int i = 0; i < PinkSize; i++)
        {
            float rand = NextFloat() * 2f - 1f;
            _pinkRows[i] = rand;
            _pinkRunningSum += rand;
        }
    }

    private void OnAudioFilterRead(float[] data, int channels)
    {
        for (int i = 0; i < data.Length; i += channels)
        {
            // ----- 1) Generate Brown Noise Sample -----
            float white = NextFloat() * 2f - 1f; 
            _brownSample += white * BrownStep;
            _brownSample = Mathf.Clamp(_brownSample, -BrownClamp, BrownClamp);
            float brown = _brownSample;

            // ----- 2) Generate Pink Noise Sample -----
            _pinkIndex++;
            if (_pinkIndex >= PinkSize)
                _pinkIndex = 0;

            float oldValue = _pinkRows[_pinkIndex];
            float newValue = NextFloat() * 2f - 1f;
            _pinkRows[_pinkIndex] = newValue;
            _pinkRunningSum = _pinkRunningSum - oldValue + newValue;
            float pink = _pinkRunningSum / PinkSize;

            // ----- 3) Blend between Brown and Pink -----
            float sample = Mathf.Lerp(brown, pink, blend);

            // Write the same sample to each channel
            for (int c = 0; c < channels; c++)
            {
                data[i + c] = sample;
            }
        }
    }

    /// <summary>
    /// Returns a float in [0,1) using System.Random, then cast to float.
    /// </summary>
    private float NextFloat()
    {
        return (float)_rng.NextDouble();
    }
}
