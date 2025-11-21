using System.Collections;
using System.Collections.Generic;
using UnityEngine;

public class DroneStateSaver : MonoBehaviour
{
    // Start is called before the first frame update
    void Start()
    {
        
    }

    private DataEntry getObstaclePosition(Vector3 position)
    {
        return new DataEntry(name + "_position", position.ToString());
    }
}
