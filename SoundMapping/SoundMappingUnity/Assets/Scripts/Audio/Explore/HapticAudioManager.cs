using System.Collections;
using System.Collections.Generic;
using Unity.VisualScripting;
using UnityEngine;

public static class GlobalConstants
{
    public const float NETWORK_REFRESH_RATE = 0.1f; // in seconds
    public const int NUMBER_OF_PAST_SCORE = 20; // 2 sec of moving avergae 
}
public class HapticAudioManager : MonoBehaviour
{
    //Make  dict with 
    public static List<DroneFake> drones = new List<DroneFake>();

    public const float DUTYFREQ = 50; // in percent
    public const float INITFREQ = 1f ; // in Hz


    // Start is called before the first frame update
    void Start()
    {
        Reset();
      //  drones = swarmModel.network.drones;
    }

    public void Reset()
    {
        drones.Clear();
       // drones = swarmModel.network.drones;
    }


    // Update is called once per frame
}


public class droneStatus
{
    public GameObject drone;
    public List<float> droneScores = new List<float>();
    public float currentScore;

    public droneStatus(GameObject drone)
    {
        this.drone = drone;
    }

    public bool isThisDrone(GameObject d)
    {
        
        return d == drone;
    }

    public void SetScore(float score)
    {
        droneScores.Add(score);
        updateScore();
    }

    private void updateScore()
    {
        //make moving average of last 5 scores
        if(droneScores.Count > GlobalConstants.NUMBER_OF_PAST_SCORE)
        {
            droneScores.RemoveAt(0);
        }

        float sum = 0;
        foreach (var score in droneScores)
        {
            sum += score;
        }

        currentScore = sum / droneScores.Count;
    }
}
