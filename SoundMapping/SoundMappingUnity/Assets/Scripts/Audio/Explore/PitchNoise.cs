using System.Collections;
using System.Collections.Generic;
using UnityEngine;

[RequireComponent(typeof(AudioSource))]
public class PitchNoise : MonoBehaviour
{
    [Header("Pitch Control (0 → minPitch, 1 → maxPitch)")]
    [Range(0f, 1f)] public float blend = 0f;

    
    [Header("Moving range for Animation")]
    [Range(0f, 1f)]
    public float a = 0.1f;

    public float animationDuration = 1f;

    [Header("Pitch Settings")]
    public float minPitch = 0.5f;
    public float maxPitch = 2.0f;

    private AudioSource _audioSource;

    // Start is called before the first frame update
    void Start()
    {
        _audioSource = GetComponent<AudioSource>();
        _audioSource.playOnAwake = true;
        _audioSource.enabled = true;
    }

    // Update is called once per frame
    void Update()
    {
        float pitch = Mathf.Lerp(minPitch, maxPitch, blend);
        _audioSource.pitch = pitch;
    }

    public void Shrink()
    {
        StartCoroutine(startAnimation(blend, Mathf.Clamp(blend - a, 0f, 1f), animationDuration));
    }

    public void Expand()
    {
        StartCoroutine(startAnimation(blend, Mathf.Clamp(blend + a, 0f, 1f), animationDuration));
    }

    IEnumerator startAnimation(float start, float end, float duration)
    {
        float startTime = Time.time;
        float endTime = startTime + duration;
        float t = 0f;
        while (Time.time < endTime)
        {
            t = (Time.time - startTime) / duration;
            blend = Mathf.Lerp(start, end, t);
            yield return null;
        }
        blend = start;
    }
}
