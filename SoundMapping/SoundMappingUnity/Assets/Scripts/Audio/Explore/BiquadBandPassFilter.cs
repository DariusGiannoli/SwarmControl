using UnityEngine;

[RequireComponent(typeof(AudioSource))]
public class BiquadBandPassFilter : MonoBehaviour
{
    [Header("Filter Settings")]
    [Tooltip("Center frequency in Hz.")]
    public float centerFrequency = 1000f;
    
    [Tooltip("Quality factor (affects the bandwidth).")]
    public float Q = 1f;
    
    private float sampleRate;
    
    // Biquad filter coefficients
    private float a0, a1, a2, b1, b2;
    
    // Biquad filter history
    private float x1, x2; // previous two input samples
    private float y1, y2; // previous two output samples

    private float lastCenterFrequency;
    private float lastQ;
    
    void Start()
    {
        sampleRate = AudioSettings.outputSampleRate; 
        UpdateCoefficients();
        lastCenterFrequency = centerFrequency;
        lastQ = Q;
    }
    
    void OnValidate()
    {
        // If user changes centerFrequency or Q in the Inspector while running:
        if (Application.isPlaying)
        {
            UpdateCoefficients();
        }
    }
    
    void Update()
    {
        if (Mathf.Abs(centerFrequency - lastCenterFrequency) > 0.001f ||
            Mathf.Abs(Q - lastQ) > 0.001f)
        {
            UpdateCoefficients();
            lastCenterFrequency = centerFrequency;
            lastQ = Q;
        }
    }

    void UpdateCoefficients()
    {
        float w0 = 2.0f * Mathf.PI * centerFrequency / sampleRate;
        float alpha = Mathf.Sin(w0) / (2f * Q);
        float cosw0 = Mathf.Cos(w0);

        // Biquad band-pass (constant skirt gain) formula
        // Reference: https://webaudio.github.io/Audio-EQ-Cookbook/audio-eq-cookbook.html (BPF: Eq. 10, 11)
        float b0 = alpha;
        float b1_ = 0f;
        float b2_ = -alpha;
        float a0_ = 1 + alpha;
        float a1_ = -2f * cosw0;
        float a2_ = 1 - alpha;

        // Normalize the coefficients
        a0 = b0 / a0_;
        a1 = b1_ / a0_;
        a2 = b2_ / a0_;
        b1 = a1_ / a0_;
        b2 = a2_ / a0_;
    }
    
    void OnAudioFilterRead(float[] data, int channels)
    {
        print("Filtering audio");
        print("a0: " + a0 + ", a1: " + a1 + ", a2: " + a2 + ", b1: " + b1 + ", b2: " + b2+ "x1: " + x1 + ", x2: " + x2 + ", y1: " + y1 + ", y2: " + y2);
        y2 = y1 = 0f;
        for (int i = 0; i < data.Length; i += channels)
        {
            // Process each channel separately
            for (int ch = 0; ch < channels; ch++)
            {
                float x = data[i + ch];
                
                // Biquad difference equation
                float y = a0 * x + a1 * x1 + a2 * x2 - b1 * y1 - b2 * y2;
                
                // Shift history
                x2 = x1;
                x1 = x;
                y2 = y1;
                y1 = y;
                
                // Write filtered sample back
                data[i + ch] = y;
            }
        }
    }
}
