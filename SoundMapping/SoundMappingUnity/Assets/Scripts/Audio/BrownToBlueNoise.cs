using UnityEngine;
using System;
using System.Collections;
using System.Data.Common;

[RequireComponent(typeof(AudioSource))]
public class BrownToBlueNoise : MonoBehaviour
{
    [Header("Blend = 0 → 100% Brown  |  1 → 100% Blue")]
    [Range(0f, 1f)]
    public float blend = 0.5f;



    [Header("Moving range for Animation")]
    [Range(0f, 1f)]
    public float a = 0.1f;

    public float animationDuration = 1f;

    Coroutine coroutine;

    public AudioClip audioClipShrink;
    public AudioClip audioClipExpand;


    
    public float swarmBlend
    {
        get
        {
            return (swarmModel.desiredSeparation-MigrationPointController.minSpreadness)/(MigrationPointController.maxSpreadness-MigrationPointController.minSpreadness);
        }
    }

    public bool isPlayed = false;

    private bool isAnimating 
    {
        get
        {
            return coroutine != null;
        }
    }

    private AudioSource _audioSource;
    private AudioSource audioSourceShrink;

    float realBlend 
    {
        get
        {
            if(isAnimating)
            {
                return blend;
            }

            if (isPlayed)
            {
                return 1-swarmBlend;
            }
            else
            {
                return 1-blend;
            }
        }
    }

    // ----- Brown Noise State -----
    private float _brownSample = 0f;    
    private const float BrownStep  = 0.02f;
    private const float BrownClamp = 1.0f;

    // ----- Blue Noise State -----
    // We'll do a simple derivative of white noise: blue[n] = (white[n] - white[n-1]) * gain
    private float _lastWhite  = 0f;   
    private const float BlueGain = 0.2f; // Adjust this gain to taste

    // Use a thread‐safe random from .NET
    private System.Random _rng;

    bool isPlaying = true;
    public bool isShrinking = false;



    private float _currentVolume = 0f;  // starts off silent
    public float fadeSpeed = 3f;       // how fast to fade in/out
    float targetAnimation = 0f;

    bool spreadness 
    {
        get
        {
            return gm.GetComponent<MigrationPointController>().control_spreadness;
        }
    }

    private GameObject gm;

    void Awake()
    {
        // Initialize our System.Random with a seed (optional)
        _rng = new System.Random(); // or specify a seed, e.g. (1234)
        this.GetComponent<AudioSource>().enabled = true;

        _audioSource = GetComponent<AudioSource>();
        audioSourceShrink = this.transform.GetChild(0).GetComponent<AudioSource>();


        //find with tag
        gm = GameObject.FindGameObjectWithTag("GameManager");

    }

    void Update()
    {
        if(!LevelConfiguration._Audio_spreadness && !SwarmDisconnection.hasChanged)
        {
            _audioSource.enabled = false;
            audioSourceShrink.enabled = false;
            return;
        }

        _audioSource.enabled = LevelConfiguration._Audio_spreadness;
        audioSourceShrink.enabled = LevelConfiguration._Audio_spreadness;




        float axisValue = Mathf.Abs(Input.GetAxis("LR"));
        float threshold = 0.01f;

        // Decide on our target volume:
        float targetVolume = (axisValue > threshold) ? 1f : 0f;
        targetVolume = targetAnimation == 0 ? targetVolume : targetAnimation;

        // Smoothly move currentVolume → targetVolume:
        _currentVolume = Mathf.Lerp(_currentVolume, targetVolume, Time.deltaTime * fadeSpeed);

        // Handle shrinking state
        if (axisValue > threshold)
        {
            isShrinking = true;
            StopCoroutine("ResetShrinking");
            // stop any shrinking
            if (coroutine != null)
            {
                StopCoroutine(coroutine);
                targetAnimation = 0f;
                coroutine = null;
            }
        }
        else if (!IsInvoking("ResetShrinking"))
        {
            Invoke("ResetShrinking", 0.5f);
        }
    }

    void ResetShrinking()
    {
        isShrinking = false;
    }
    void OnApplicationQuit()
    {
        // Clean up our System.Random
        _rng = null;
        // stop the audio
        GetComponent<AudioSource>().Stop();
        isPlaying = false;
    }

    private void OnAudioFilterRead(float[] data, int channels)
    {
        for (int i = 0; i < data.Length; i += channels)
        {
            if (!isPlaying)
                return;

            // 1) Generate white noise
            float white = NextFloat() * 2f - 1f;

            // 2) Brown noise (integrator)
            _brownSample += white * BrownStep;
            _brownSample = Mathf.Clamp(_brownSample, -BrownClamp, BrownClamp);
            float brown = _brownSample;

            // 3) Blue noise (differentiator)
            float blue = (white - _lastWhite) * BlueGain;
            _lastWhite = white;

            // 4) Blend between Brown and Blue
            float sample = Mathf.Lerp(brown, blue, realBlend);

            // 5) Multiply by our smoothly changing volume
            sample *= _currentVolume;

            // Write the final sample to each channel
            for (int c = 0; c < channels; c++)
            {
                data[i + c] = sample;
            }
        }
    }
    private float NextFloat()
    {
        try
        {
            return (float)_rng.NextDouble();
        }
        catch (Exception)
        {
            // If we hit an exception, it's likely due to threading issues.
            // In that case, we'll just return a random value from Unity's Random class.
            return 0;
        }
    }

    public void Shrink()
    {

        return;
        if (coroutine == null && !isShrinking)
        {
            //check if close to fully shrunk
            if (realBlend < 0.1f)
            {
                return;
            }
            coroutine = StartCoroutine(startAnimation(realBlend, Mathf.Clamp(realBlend + a, 0f, 1f), animationDuration));
        }
    }

    public void Expand()
    {
        return;
        if (coroutine == null && !isShrinking)
        {
            coroutine = StartCoroutine(startAnimation(realBlend, Mathf.Clamp(realBlend - a, 0f, 1f), animationDuration));
        }
    }

    IEnumerator startAnimation(float start, float end, float duration)
    {
        audioSourceShrink.clip = audioClipShrink;
        audioSourceShrink.loop = false;
        audioSourceShrink.Play();

        while (audioSourceShrink.isPlaying)
        {
            yield return null;
        }

        yield return new WaitForSeconds(0.2f);
        coroutine = null;


        // targetAnimation = 1f;


        // float startTime = Time.time;
        // float endTime = startTime + duration;

        // float t = 0f;
        // while (Time.time < endTime)
        // {
        //     t = (Time.time - startTime) / duration;
        //     blend = Mathf.Lerp(start, end, t);
        //     yield return null;
        // }
        // yield return new WaitForSeconds(0.2f);
        // targetAnimation = 0f;
        // coroutine = null;
    }

    public static void AnalyseShrinking(float average)
    {
        if(average > 0.75 && !SwarmDisconnection.hasChanged)
        {
            BrownToBlueNoise brownToBlueNoise = GameObject.FindObjectOfType<BrownToBlueNoise>();
            brownToBlueNoise.Shrink();
        }
    }



    void PlaySound(AudioClip audioClip)
    {
        _audioSource.clip = audioClip;
        _audioSource.loop = false;
        _audioSource.Play();
    }
}
