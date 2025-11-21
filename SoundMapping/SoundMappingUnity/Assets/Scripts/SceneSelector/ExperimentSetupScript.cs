using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UI;

using TMPro;

public class ExperimentSetupScript : MonoBehaviour
{
    public string  PID;
    public Toggle Haptics;
    public Toggle Order;

    //text TMPro
    public TMP_InputField PIDInput;
    public TMP_Text confirmText;

    public GameObject confirmGO;


    public GameObject mainMenu;
    public GameObject experimentMenu;

    // Start is called before the first frame update
    void Start()
    {
        confirmGO.SetActive(false);
        mainMenu.SetActive(true);
        experimentMenu.SetActive(false);
    }


    public void confirm()
    {
        confirmGO.SetActive(false);
        mainMenu.SetActive(false);
        experimentMenu.SetActive(false);

        this.GetComponent<SceneSelectorScript>().StartTraining(PID);
    }

    public void NextScene()
    {
        confirmGO.SetActive(false);
        mainMenu.SetActive(false);
        experimentMenu.SetActive(false);

        this.GetComponent<SceneSelectorScript>().NextScene();
    }

    public void cancel()
    {
        confirmGO.SetActive(false);
    }

    // Update is called once per frame

    public void StartExperiment()
    {
        PID = PIDInput.text;

        confirmText.text = "Are you sure you want to start the experiment? \n\n" +
            "PID: " + PID + "\n" +
            "Haptics: " + Haptics.isOn + "\n" +
            "TDV first : " + Order.isOn;

        confirmGO.SetActive(true);
    }



    public static void levelFinished()
    {
        GameObject.FindObjectOfType<ExperimentSetupScript>().experimentMenu.SetActive(true);
    }

}
