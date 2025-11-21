using System.Collections;
using System.Collections.Generic;
using UnityEngine;

public class HapticNonHapticWalls : MonoBehaviour
{
    // Start is called before the first frame update
    [SerializeField]
    public Material materialWall;
    void Start()
    {
        
        if(!SceneSelectorScript._haptics)
        {
            GameObject walls = GameObject.FindGameObjectWithTag("HapticvsVis");

            print(walls.name);

            for(int i = 0; i < walls.transform.childCount; i++)
            {
                //get the child
                GameObject child = walls.transform.GetChild(i).gameObject;

                //set the material
                child.GetComponent<Renderer>().material = materialWall;
            }
        }
    }

    // Update is called once per frame
    void Update()
    {
        
    }
}
