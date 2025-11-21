using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using PathCreation;

public class AudioManager : MonoBehaviour
{
    public PathCreator pathCreator;
    public GameObject soundSource;
    public float soundSpeed = 1;

    private float distanceTravelled = 0;
    // Start is called before the first frame update
    void Start()
    {
        
    }

    // Update is called once per frame
    void Update()
    {
        distanceTravelled += soundSpeed * Time.deltaTime;
        soundSource.transform.position = pathCreator.path.GetPointAtDistance(distanceTravelled); 
    }

    void changeSound()
    {
        float distanceToSource = Vector3.Distance(soundSource.transform.position, Camera.main.transform.position);
        float dX = soundSource.transform.position.x - Camera.main.transform.position.x;
        float dY = soundSource.transform.position.y - Camera.main.transform.position.y;

                //map the distance max val 20 min val 0 to +3 0
        float picth = Mathf.Lerp(1.3f, 0, Mathf.InverseLerp(0, 20, distanceToSource));
        soundSource.GetComponent<AudioSource>().pitch = picth;
    }
}
