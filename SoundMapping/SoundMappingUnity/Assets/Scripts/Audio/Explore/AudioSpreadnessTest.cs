using System;
using System.Collections;
using System.Collections.Generic;
using UnityEngine;


[RequireComponent(typeof(AudioSource))]
public class AudioSpreadnessTest : MonoBehaviour
{
    private float minSpreadness = 1.5f;
    private float maxSpreadness = 10f;
    private float speadness 
    {
        get
        {
            return swarmModel.desiredSeparation;
        }
    }

    private float minPitch = 0.5f;
    private float maxPitch = 2.0f;

    public float shrinkingFactor = 0.4f;

    public bool isShrinking = false;

    private AudioSource audioSource;

    private bool isAnimating = false;

    bool isAvailable 
    {
        get
        {
            return !isAnimating && !isShrinking;
        }
    }


    public List<float> minDistance = new List<float>();
    
    // Start is called before the first frame update
    void Start()
    {   
        audioSource = GetComponent<AudioSource>();

        float t2 = (speadness - minSpreadness) / (maxSpreadness - minSpreadness);
        float pitch = Mathf.Lerp(minPitch, maxPitch, t2);
        audioSource.pitch = pitch;
    }

    public void LaunchAnimation()
    {
        saveMinDistance();
        //compute std deviation
        float sum = 0;
        float mean = 0;
        float stdDev = 0;
        float trend = 0;

        if (minDistance.Count > 0)
        {
            for(int i = 0; i < minDistance.Count; i++)
            {
                sum += minDistance[i];
                if(i > 0)
                {
                    trend += minDistance[i] - minDistance[i - 1];
                }
            }
            mean = sum / minDistance.Count;

            sum = 0;
            foreach (float value in minDistance)
            {
            sum += Mathf.Pow(value - mean, 2);
            }
            stdDev = Mathf.Sqrt(sum / minDistance.Count);
        }

        if(stdDev > 0.05 && isAvailable && trend < 0)
        {   
            print("stdDev: " + stdDev + " trend: " + trend);

            StartCoroutine(ShrinkCircle());
        }
    }

    void saveMinDistance()
    {
        minDistance.Add(swarmModel.minDistance);
        //if more than 20 values, remove the oldest one
        if(minDistance.Count > 20)
        {
            minDistance.RemoveAt(0);
        }
    }

    // Update is called once per frame
    void FixedUpdate()
    {
        LaunchAnimation();
        updateSpreadness();
    }

    void playSound()
    {
        if (!audioSource.isPlaying)
        {
            audioSource.Play();
        }
    }

    void updateSpreadness()
    {
        float spreadnessInput = Input.GetAxis("LR") * Time.deltaTime;
        if(spreadnessInput != 0 && isAvailable)
        {
            isShrinking = true;
            playSound();
            float t = (speadness - minSpreadness) / (maxSpreadness - minSpreadness);
            float pitch = Mathf.Lerp(minPitch, maxPitch, t);
            audioSource.pitch = pitch;
        }else{
            isShrinking = false;
        }
    }

    private IEnumerator ShrinkCircle()
    {   
        print("Shrink circle");
        isAnimating = true;
        playSound();
        //start when the cluip reaches the end


        float orginalSpread = speadness;
        float objectifSpreadness = Mathf.Max(minSpreadness, speadness * shrinkingFactor);

        float animationTime = audioSource.clip.length + 0.05f;
        float elapsedTime = 0f;

        float spreadness = orginalSpread;
        while(elapsedTime < animationTime)
        {
            //linear interpolation
            spreadness = orginalSpread - (orginalSpread - objectifSpreadness) * elapsedTime / animationTime;
            elapsedTime += Time.deltaTime;

            float t = (spreadness - minSpreadness) / (maxSpreadness - minSpreadness);
            audioSource.pitch = Mathf.Lerp(minPitch, maxPitch, t);
            yield return null;

            playSound();
        }
        isAnimating = false;
        float t2 = (speadness - minSpreadness) / (maxSpreadness - minSpreadness);
        float pitch = Mathf.Lerp(minPitch, maxPitch, t2);
        audioSource.pitch = pitch;
    }

    public IEnumerator ExpandCircle()
    {
        playSound();
        isAnimating = true;
        float orginalSpread = speadness;
        float objectifSpreadness = Mathf.Min(maxSpreadness, speadness *(1 + (1 - shrinkingFactor)));

        float animationTime = audioSource.clip.length + 0.05f;
        float elapsedTime = 0f;

        float spreadness = orginalSpread;

        print("Objectif spreadness: " + objectifSpreadness + " Original spreadness: " + orginalSpread + " Time: " + animationTime);
        while(elapsedTime < animationTime)
        {
            //linear interpolation
            spreadness = orginalSpread + (objectifSpreadness - orginalSpread) * elapsedTime / animationTime;
            elapsedTime += Time.deltaTime;

            float t = (spreadness - minSpreadness) / (maxSpreadness - minSpreadness);
            audioSource.pitch = Mathf.Lerp(minPitch, maxPitch, t);
            yield return null;

            playSound();

        }
        isAnimating = false;
        float t2 = (speadness - minSpreadness) / (maxSpreadness - minSpreadness);
        float pitch = Mathf.Lerp(minPitch, maxPitch, t2);
        audioSource.pitch = pitch;
    }
}
