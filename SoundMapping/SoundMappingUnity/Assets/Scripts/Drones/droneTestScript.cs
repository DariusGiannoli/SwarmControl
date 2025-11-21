using System.Collections;
using System.Collections.Generic;
using UnityEngine;

public class droneTestScript : MonoBehaviour
{
    // Start is called before the first frame update
    void Start()
    {
        this.GetComponent<sendInfoGameObject>().setupCallback(getData);
        
    }

    // Update is called once per frame
    void Update()
    {
        
    }

    public DataEntry getData()
    {
        return new DataEntry("position", this.transform.position);
    }
}
