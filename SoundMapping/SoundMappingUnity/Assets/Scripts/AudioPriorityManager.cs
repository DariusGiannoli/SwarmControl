using UnityEngine;

public class AudioPriorityManager : MonoBehaviour
{
    static GameObject gm;

    void Awake()
    {
        gm = this.gameObject;   
    }

    public static void Mute()
    {
        gm.GetComponent<AudioPriorityManager>().MuteAllExceptPriority();
    }

    public static void Restore()
    {
        gm.GetComponent<AudioPriorityManager>().RestoreAllAudio();
    }

    public void MuteAllExceptPriority()
    {
        GameObject[] allStars = GameObject.FindGameObjectsWithTag("Collectibles");
        GameObject[] allDrones = GameObject.FindGameObjectsWithTag("Drone");



        foreach (GameObject star in allStars)
        {
            star.GetComponent<AudioSource>().enabled = false;
        }

        foreach (GameObject drone in allDrones)
        {
            drone.GetComponent<AudioSource>().enabled = false;
        }
    }

    public void RestoreAllAudio()
    {
        GameObject[] allStars = GameObject.FindGameObjectsWithTag("Collectibles");
        GameObject[] allDrones = GameObject.FindGameObjectsWithTag("Drone");

        foreach (GameObject star in allStars)
        {
            star.GetComponent<AudioSource>().enabled = true;
        }

        foreach (GameObject drone in allDrones)
        {
            drone.GetComponent<AudioSource>().enabled = true;
        }
    }
}
