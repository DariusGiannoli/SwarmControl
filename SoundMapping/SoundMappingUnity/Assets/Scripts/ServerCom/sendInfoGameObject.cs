using System.Collections.Generic;
using UnityEngine;

public class sendInfoGameObject : MonoBehaviour
{
    private GameObject serverHandler; 
    public  delegate DataEntry MyCallback();
    private List<MyCallback> callbacks = new List<MyCallback>();

    void Start()
    {   
        serverHandler = GameObject.Find("ServerHandler");
        serverHandler.GetComponent<SendInfo>().subscribe(this.gameObject);
    }

    public void setupCallback(MyCallback callback)
    {
        callbacks.Add(callback);
    }

    public List<DataEntry> getData()
    {
        List<DataEntry> data = new List<DataEntry>();
        foreach (MyCallback callback in callbacks)
        {
            data.Add(callback());
        }
        return data;
    }

}
