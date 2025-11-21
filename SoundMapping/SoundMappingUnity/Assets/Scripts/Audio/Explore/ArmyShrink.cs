using System.Collections;
using System.Collections.Generic;
using UnityEngine;

[RequireComponent(typeof(AudioSource))]
public class ArmyShrink : MonoBehaviour
{
    // Start is called before the first frame update

    public AudioClip audioClipShrink;
    public AudioClip audioClipExpand;

    private AudioSource audioSource;


    public bool doLoop = false;

    void Start()
    {
        audioSource = GetComponent<AudioSource>();
    }

    public void Update()
    {
        //if loop has changed 
        if (audioSource.loop != doLoop)
        {
            audioSource.loop = doLoop;
        }
    }

    void PlaySound(AudioClip audioClip)
    {
        audioSource.clip = audioClip;
        audioSource.loop = doLoop;
        audioSource.Play();
    }

    public void Shrink()
    {
        PlaySound(audioClipShrink);
    }

    public void Expand()
    {
        PlaySound(audioClipExpand);
    }

}
