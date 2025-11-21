using System;
using System.Collections;
using System.Collections.Generic;
using UnityEngine;


[RequireComponent(typeof(AudioSource))]
public class CircleAudioController : MonoBehaviour
{
    [Header("Circle Settings")]
    public float circleRadius = 5f;      // Current radius of your circle
    public float minRadius = 1f;        // Smallest circle radius
    public float maxRadius = 5f;        // Largest circle radius

    [Header("Audio Settings")]
    public float minPitch = 0.5f;
    public float maxPitch = 2.0f;

    public float minVolume = 0.2f;
    public float maxVolume = 1.0f;

    public float minFrequency = 200f;
    public float maxFrequency = 2000f;

    private AudioSource audioSource;

    void Awake()
    {
        audioSource = GetComponent<AudioSource>();
    }

    void Update()
    {
        float t = (circleRadius - minRadius) / (maxRadius - minRadius);
        float freq = Mathf.Exp(Mathf.Lerp(Mathf.Log(minFrequency), Mathf.Log(maxFrequency), t));
        audioSource.pitch = freq / minFrequency;

        if (Input.GetKeyDown(KeyCode.P))
        {
            StartCoroutine(ShrinkCircle());
        }
    }

    private IEnumerator ShrinkCircle()
    {
        print("Shrinking circle");
        float originalRadius = circleRadius;
        float shrunkenRadius = Mathf.Max(minRadius, circleRadius - 3f);
        // Animate or instantly set the radius
        circleRadius = shrunkenRadius;
        yield return new WaitForSeconds(1f);
        circleRadius = originalRadius;
    }
}
