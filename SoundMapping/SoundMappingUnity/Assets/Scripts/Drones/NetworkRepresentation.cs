using System;
using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UI;

public class NetworkRepresentation : MonoBehaviour
{
    public Image firstOrderNeighbors;
    public Image secondOrderNeighbors;
    public Image thirdOrderNeighbors;
    public Image leftBehindNeighbors;

    public static Dictionary<int, int> neighborsRep = new Dictionary<int, int>();

    public static float networkScore = 0;
    public static bool hasLeftBehind = false;
    
        // Start is called before the first frame update
    public float UpdateNetworkRepresentation(Dictionary<int, int> neighbors)
    {
        // update the dictionary


        int totalNeighbors = 0;
        int firstOrder = 0;
        foreach (KeyValuePair<int, int> neighbor in neighbors)
        {
            totalNeighbors += neighbor.Value;
        }

        // first order is the key = 1
        if (neighbors.ContainsKey(2))
        {
            firstOrder = neighbors[2];
        }

        // disconnected 
        if (neighbors.ContainsKey(0) || neighbors.ContainsKey(-1)) // iff there is disconnected drones 
        {
            return 1;
        }

        //proportion of first order neighbors
        float firstOrderProportion = (float)firstOrder / (float)totalNeighbors;
        
        float networkScore = -(firstOrderProportion-0.8f)/0.5f;
        networkScore = Mathf.Clamp01(networkScore);

        networkScore = Mathf.Clamp01(networkScore-0.1f);


        return networkScore;


    }



}
