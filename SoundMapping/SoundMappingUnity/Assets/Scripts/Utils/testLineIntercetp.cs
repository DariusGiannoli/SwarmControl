using System.Collections;
using System.Collections.Generic;
using UnityEngine;

public class testLineIntercetp : MonoBehaviour
{

    public GameObject a;
    public GameObject b;
    // Start is called before the first frame updat

    public void Update()
    {
        Vector3 posa = a.transform.position;
        Vector3 posb = b.transform.position;

        print("point intercepting nay obstacle : "+ ClosestPointCalculator.IsLineIntersecting(posa,  posb));
    }
}
