using UnityEngine;
using System;
using System.Collections;

/// <summary>
/// Generates white noise and applies a simple biquad bandpass filter,
/// whose center frequency is interpolated between minFreq and maxFreq
/// using freqLerp [0..1]. The band width is rangeFreq, meaning if
/// center = 3000 Hz and rangeFreq = 1000 Hz, we get ~2500-3500 Hz band.
/// </summary>
[RequireComponent(typeof(AudioSource))]
public class BandpassNoise : MonoBehaviour
{
    [Header("Center Frequency Control (0 → minFreq, 1 → maxFreq)")]
    [Range(0f, 1f)] public float freqLerp = 0f;

    
    [Header("Moving range for Animation")]
    [Range(0f, 1f)]
    public float a = 0.1f;

    public float animationDuration = 1f;

    [Header("Hz Settings")]
    public float minFreq   = 20f;     // in Hz
    public float maxFreq   = 20000f;  // in Hz
    public float rangeFreq = 1000f;   // Band width in Hz

    // Internal random generator for white noise
    private System.Random _rng;

    // Biquad filter states (we need them to persist between blocks)
    private float _z1, _z2; // filter memory (for 2nd-order IIR)
    
    // Biquad filter coefficients (recomputed each audio block)
    private float _b0, _b1, _b2;
    private float _a0, _a1, _a2;

    private AudioSource _audioSource;
    private bool _isPlaying = true;

    private float _sampleRate = 44100f; // Default sample rate

    void Awake()
    {
        _rng = new System.Random();
        _audioSource = GetComponent<AudioSource>();
        _audioSource.playOnAwake = true;
        _audioSource.enabled = true;

        _sampleRate = AudioSettings.outputSampleRate;
    }

    void OnApplicationQuit()
    {
        _isPlaying = false;
        if (_audioSource) _audioSource.Stop();
        _rng = null;
    }

    private void OnAudioFilterRead(float[] data, int channels)
    {
        if (!_isPlaying || data == null || data.Length == 0) return;

        float centerFreq = Mathf.Lerp(minFreq, maxFreq, freqLerp);
        centerFreq = Mathf.Max(1f, centerFreq);
        float bandwidth = Mathf.Max(1f, rangeFreq);
        float Q = centerFreq / bandwidth;

        // Pass the stored _sampleRate instead of AudioSettings.outputSampleRate
        UpdateBiquadBandpass(centerFreq, Q, _sampleRate);

        for (int i = 0; i < data.Length; i += channels)
        {
            float white = NextFloat() * 2f - 1f;
            float filtered = ProcessBiquad(white);

            for (int c = 0; c < channels; c++)
            {
                data[i + c] = filtered;
            }
        }
        }


    /// <summary>
    /// Computes the biquad bandpass filter coefficients using the
    /// "constant skirt gain" form of the bandpass.
    ///
    /// freqHz: center frequency in Hz
    /// Q     : quality factor
    /// fs    : sample rate
    /// </summary>
    private void UpdateBiquadBandpass(float freqHz, float Q, float fs)
    {
        if (freqHz < 1f || fs < 1f) return;

        float omega = 2f * Mathf.PI * freqHz / fs;
        float cosw  = Mathf.Cos(omega);
        float sinw  = Mathf.Sin(omega);

        // We clamp Q so we don't explode numerically for extremely high or low values
        Q = Mathf.Clamp(Q, 0.001f, 1000f);

        float alpha = sinw / (2f * Q);

        // Biquad "bandpass" (constant skirt gain) coefficients
        //   b0 =   alpha
        //   b1 =   0
        //   b2 =  -alpha
        //   a0 =   1 + alpha
        //   a1 =  -2 cos(omega)
        //   a2 =   1 - alpha
        _b0 =  alpha;
        _b1 =  0f;
        _b2 = -alpha;
        _a0 =  1f + alpha;
        _a1 = -2f * cosw;
        _a2 =  1f - alpha;
    }

    /// <summary>
    /// Processes a single sample through the biquad filter.
    /// Using Direct Form I with internal states _z1, _z2.
    /// </summary>
    private float ProcessBiquad(float sampleIn)
    {
        // We do: out = (b0 * in + z1) / a0;
        //        z1  = b1*in + z2 - a1*out;
        //        z2  = b2*in - a2*out;
        float outSample = (_b0 * sampleIn + _z1) / _a0;

        float tempZ1 = _b1 * sampleIn + _z2 - _a1 * outSample;
        _z2 = _b2 * sampleIn - _a2 * outSample;
        _z1 = tempZ1;

        return outSample;
    }

    /// <summary>
    /// Returns a random float in [0..1).
    /// Wraps System.Random.NextDouble().
    /// </summary>
    private float NextFloat()
    {
        if (_rng == null)
            return UnityEngine.Random.value;
        return (float)_rng.NextDouble();
    }

    public void Shrink()
    {
        StartCoroutine(startAnimation(freqLerp, Mathf.Clamp(freqLerp - a, 0f, 1f), animationDuration));
    }

    public void Expand()
    {
        StartCoroutine(startAnimation(freqLerp, Mathf.Clamp(freqLerp + a, 0f, 1f), animationDuration));
    }

    IEnumerator startAnimation(float start, float end, float duration)
    {
        float startTime = Time.time;
        float endTime = startTime + duration;
        float t = 0f;
        while (Time.time < endTime)
        {
            t = (Time.time - startTime) / duration;
            freqLerp = Mathf.Lerp(start, end, t);
            yield return null;
        }
        freqLerp = start;
    }
}
