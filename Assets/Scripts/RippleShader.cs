using UnityEngine;

public class Shader : MonoBehaviour
{
    // Start is called once before the first execution of Update after the MonoBehaviour is created
    Material m;
    Renderer r;
    GameObject leftHand;
    GameObject rightHand;
    void Start()
    {
        r = GetComponent<Renderer>();
        m = GetComponent<Renderer>().material;
        Debug.Log(r);
        Debug.Log(m);
        Debug.Log(m.GetFloat("_Amplitude"));
    }

    // Update is called once per frame
    void Update()
    {
        
    }
}
