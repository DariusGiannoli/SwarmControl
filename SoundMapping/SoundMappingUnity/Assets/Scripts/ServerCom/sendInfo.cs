using System;
using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Networking;

public class SendInfo : MonoBehaviour
{
    List<GameObject> subscribers = new List<GameObject>();
    private const string ServerUrl = "localhost:5000/endpoint"; // Set your server endpoint here
    public float sendEvery = 1f; // Send data every 1 second

    public static bool connectedToServer = false;


    void Start()
    {
        //send start message in JSON
        StartCoroutine(SendDataToServer("{\"start\": \"true\"}"));
        StartCoroutine(SendData());
    }

    //on quit
    void OnApplicationQuit()
    {
        //send quit message in JSON
        StartCoroutine(SendDataToServer("{\"quit\": \"true\"}"));
    }

    public void subscribe(GameObject subscriber)
    {
        subscribers.Add(subscriber);
    }

    private IEnumerator SendData()
    {
        yield return new WaitForSeconds(sendEvery);

        List<DataEntry> data = new List<DataEntry>();
        foreach (GameObject subscriber in subscribers)
        {
            sendInfoGameObject sendInfo = subscriber.GetComponent<sendInfoGameObject>();
            data.AddRange(sendInfo.getData());
        }

        DataList dataList = new DataList(data);
        string jsonData = JsonUtility.ToJson(dataList);

        StartCoroutine(SendDataToServer(jsonData));

        StartCoroutine(SendData());
    }

    private IEnumerator SendDataToServer(string jsonData)
    {
        using (UnityWebRequest request = new UnityWebRequest(ServerUrl, "POST"))
        {
            byte[] jsonToSend = new System.Text.UTF8Encoding().GetBytes(jsonData);
            request.uploadHandler = new UploadHandlerRaw(jsonToSend);
            request.downloadHandler = new DownloadHandlerBuffer();
            request.SetRequestHeader("Content-Type", "application/json");

            yield return request.SendWebRequest();

            if (request.result == UnityWebRequest.Result.Success)
            {
                connectedToServer = true;
            }
            else
            {
                connectedToServer = false;
            }
        }
    }
}

[Serializable]
public class DataEntry
{
    public string name;
    public string value;  // Converted to a string for serialization


    //public string type; // Optional field for data type and conversion if needed

    public DataEntry(string name, string value, bool fullHistory=false)
    {
        if (fullHistory)
        {
            this.name = "FH-"+name;
            this.value = value;
        }
        else
        {
            this.name = name;
            this.value = value;
        }
    }
    
    public DataEntry(string name, int value) => (this.name, this.value) = (name, value.ToString());
    public DataEntry(string name, float value) => (this.name, this.value) = (name, value.ToString());

    public DataEntry(string name, float[] values)
    {
        this.name = name;
        this.value = "[" + string.Join(",", Array.ConvertAll(values, v => v.ToString())) + "]";
    }

    public DataEntry(string name, int[] values)
    {
        this.name = name;
        this.value = "[" + string.Join(",", Array.ConvertAll(values, v => v.ToString())) + "]";
    }

    public DataEntry(string name, string[] values)
    {
        this.name = name;
        this.value = "[" + string.Join(",", Array.ConvertAll(values, v => "\"" + v + "\"")) + "]";
    }

    public DataEntry(string name, Vector3 values)
    {
        this.name = name;
        this.value = "[" + values.x + "," + values.y + "," + values.z + "]";
    }
}

public class DataList
{
    public List<DataEntry> data = new List<DataEntry>();

    public DataList(List<DataEntry> data)
    {
        this.data = data;
    }
}