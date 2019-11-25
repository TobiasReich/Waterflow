using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using Windows.Kinect;


public class TerrainTextureUpdater : MonoBehaviour
{
    public GameObject DepthSourceManager;
    private DepthSourceManager _DepthManager;

    private FrameDescription frameDesc;
    private KinectSensor _Sensor;
    private const int MAX_DEPTH = 4500; // The maximum value the Kinect may return for distances

    private Texture2D texture;
    private int depthWidth = 512;
    private int depthHeight = 424;

    // Start is called before the first frame update
    void Start()
    {
        _Sensor = KinectSensor.GetDefault();

        texture = new Texture2D(depthWidth, depthHeight);
        gameObject.GetComponent<Renderer>().material.mainTexture = texture;
        _DepthManager = DepthSourceManager.GetComponent<DepthSourceManager>();
        frameDesc = _Sensor.DepthFrameSource.FrameDescription;
    }

    // Update is called once per frame
    void Update()
    {       
        ushort[] depthData = _DepthManager.GetData();
        for (int y = 0; y < frameDesc.Height; y++)
        {
            for (int x = 0; x < frameDesc.Width; x++)
            {
                int fullIndex = (y * frameDesc.Width) + x;
                double depth = MAX_DEPTH - depthData[fullIndex]; // So 0 is most distant one!
                if (depth == 0)
                {
                    Color color = new Color(0, 0, 0);
                    texture.SetPixel(x, y, color);
                } else {
                    float heightValue = (float)(depth / 4500f);
                    //Debug.Log("Height for pixel: " + x + " " + y + " = " + depth + " >> " + heightValue);
                    Color color = new Color(heightValue, heightValue, heightValue);
                    texture.SetPixel(x, y, color);
                }
            }
        }
        texture.Apply();
    }
}
