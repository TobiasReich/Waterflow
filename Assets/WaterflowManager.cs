using System;
using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using Windows.Kinect;


public class WaterflowManager : MonoBehaviour
{
    private FrameDescription frameDesc;
    private KinectSensor _Sensor;

    private DepthFrameReader _Reader;
    private ushort[] _Data;
    private const int MAX_DEPTH = 4500; // The maximum value the Kinect may return for distances
    private const float WATER_HEIGHT_EPSILON = 0.01f; // Water heights below this are considered 0 (so we avoid infinitely small water puddles)
    private const float FRESH_WATER_INFLOW = 1f; // The amount of water added each tick

    private Texture2D texture;
    private int depthWidth = 512;
    private int depthHeight = 424;

    /* Defines where the water comes from */
    private int waterSourceX;
    private int waterSourceY;

    // A list containing all height values sorted so we can iterate from the heighest to the lowest field
    List<Tuple<int, int, float>> heightMapOrderedList = new List<Tuple<int, int, float>>();
    float[,] waterHeight;
    float[,] terrainHeight;

    int updateCounter = 0;

    void Start() {
        waterHeight = new float[depthWidth, depthHeight];
        terrainHeight = new float[depthWidth, depthHeight];

        waterSourceX = 70;//depthWidth / 2;
        waterSourceY = 130;//depthHeight / 2;

        _Sensor = KinectSensor.GetDefault();
        texture = new Texture2D(depthWidth, depthHeight);
        gameObject.GetComponent<Renderer>().material.mainTexture = texture;
        frameDesc = _Sensor.DepthFrameSource.FrameDescription;

        if (_Sensor != null) {
            _Reader = _Sensor.DepthFrameSource.OpenReader();
            _Data = new ushort[_Sensor.DepthFrameSource.FrameDescription.LengthInPixels];
        }
    }

    void Update() {
        if (_Reader != null) {
            var frame = _Reader.AcquireLatestFrame();
            if (frame != null) {
                frame.CopyFrameDataToArray(_Data);
                frame.Dispose();
                frame = null;
            }
        }

        //updateCounter++;
        //if (updateCounter % 10 == 0) {
            UpdateHeightMap();
            AddWater();
            DistributeWater();
        //}
        GenerateWaterTexture();
    }

    /** Adds water to the system */
    private void AddWater() {
        waterHeight[waterSourceX, waterSourceY] = FRESH_WATER_INFLOW;
    }


    /* Iterate over all Fields and update its terrain height */
    private void UpdateHeightMap() {
        ushort[] depthData = _Data;
        heightMapOrderedList.Clear();
        for (int y = 0; y < frameDesc.Height; y++) {
            for (int x = 0; x < frameDesc.Width; x++) {
                int fullIndex = (y * frameDesc.Width) + x;
                // The sensor is scanning the depth.
                // The nearest value is 0 and the farthest is 4500f
                int height = MAX_DEPTH - depthData[fullIndex];
                float heightValue = (float)(height / 4500f);  // Max is 4500
                terrainHeight[x, y] = heightValue; // Set the height to the TerrainHeightArray
                heightMapOrderedList.Add(new Tuple<int, int, float>(x, y, heightValue));
            }
        }
        // Sort the heightmap in place, descending (that's why ObjectB and ObjectA switched)
        // The first element is now the highest in the world
        heightMapOrderedList.Sort((objectA, objectB) => objectB.Item3.CompareTo(objectA.Item3));
    }

    /** Distributes the water depending on the height around it.
    *  
    *  This how the water distributes:
    *  Calculate the amount of water to the height of the correlated height map.
    *  This will give absolute water height values on which we can estimate whether 
    *  the water will move to the sides or not.
    *  In the second step every water field will dive it's excessive (!!!) height 
    *  to the sourrounding fields. **/
    private void DistributeWater() {
        for (int fieldIndex = 0; fieldIndex < heightMapOrderedList.Count; fieldIndex++) {
            Tuple<int, int, float> field = heightMapOrderedList[fieldIndex];
            int x = field.Item1;
            int y = field.Item2;
            
            if (waterHeight[x, y] <= WATER_HEIGHT_EPSILON) {
                //waterHeight[x, y] = 0; // Remove if below epsilon
                continue; // Nothing to distribute here.

            // At the end of the world -> vanish
            } else if(x == 0 || x == frameDesc.Width-1 || y == 0 || y == frameDesc.Height-1) {
                // If we are are the border the water can flow there directly
                // Water will simply vanish from here since it "fell down the earth"
                waterHeight[x, y] = 0f; //waterHeight[x, y] * 0.5f;
                //absoluteWaterHeight[x, y] = terrainHeight[x, y] + waterHeight[x, y];

            } else {
                // This is the normal case. The water distributes between the neighbour fields
                // The water amount for this 

                List<Tuple<int, int, float>> capacityList = generateWaterFlowCapacityList(x, y);

                // As long as we have flow capacities: distribute water!
                while (capacityList.Count > 0) {
                    // The first element is the one with the least capacity.
                    // We try to fill this one first (and all sourrounding ones equally)
                    float distributedCapacity = capacityList[0].Item3;

                    // The minimum amount of water that is desired will be divided by all fields with capacity
                    // That way we spread the water evenly among all neighbours
                    //TODO CHECK IF WE HAVE TO ADD +1 FOR THE LOCAL FIELD
                    float distributedWaterAmount = distributedCapacity / capacityList.Count;
                        
                    // Now distribute the flow 
                    // Iterate over all neighbours and give them their amount of water.
                    for (int neighbourIndex = 0; neighbourIndex < capacityList.Count; neighbourIndex++) {
                        Tuple<int, int, float> neighbourCapacity = capacityList[neighbourIndex];
                        // The neighbour field gets the amount of water added
                        waterHeight[neighbourCapacity.Item1, neighbourCapacity.Item2] += distributedWaterAmount;
                        // The local field gets the amount of water removed
                        waterHeight[x,y] -= distributedWaterAmount;
                    }

                    // Update the list now and see if there are still fields that could receive water
                    capacityList = generateWaterFlowCapacityList(x, y);
                }
            }
        }
    }

    /// <summary>
    /// This generates a sorted list of Triples containing the sourrounding coordinates plus 
    /// a water flow capacity value.
    /// The first two parameters are it's coordinates (x,y) and the last one defines how much 
    /// water height difference there is between the local field and the neighbour field.
    /// e.g.     [1,2,0.2]
    /// [0,1,0.5]  (1,1)  [2,1,0.0]
    ///          [1,0,0.1]
    /// </summary>
    /// <param name="x"></param>
    /// <param name="y"></param>
    private List<Tuple<int, int, float>> generateWaterFlowCapacityList(int x, int y) {
        List<Tuple<int, int, float>> capacities = new List<Tuple<int, int, float>>();

        // For all 4 sourrounding fields calculate these values and add them if their capacity is > 0
        Tuple<int, int, float> capacityNorth = generateWaterFlowCapacity(x, y, x, y - 1);
        if (capacityNorth.Item3 > WATER_HEIGHT_EPSILON) { 
            capacities.Add(capacityNorth); 
        }

        Tuple<int, int, float> capacitySouth = generateWaterFlowCapacity(x, y, x, y + 1);
        if (capacitySouth.Item3 > WATER_HEIGHT_EPSILON) {
            capacities.Add(capacitySouth); 
        }

        Tuple<int, int, float> capacityEast = generateWaterFlowCapacity(x, y, x + 1, y);
        if (capacityEast.Item3 > WATER_HEIGHT_EPSILON) { 
            capacities.Add(capacityEast); 
        }

        Tuple<int, int, float> capacityWest = generateWaterFlowCapacity(x, y, x - 1, y);
        if (capacityWest.Item3 > WATER_HEIGHT_EPSILON) { 
            capacities.Add(capacityWest); 
        }

        // We now have a list of all sourrounding fields with flow capacity (can be less than 4!)
        // Sort that list by their capacity so we can fill all fields equally by 
        // their minimum commom capacity
        capacities.Sort((objectA, objectB) => objectA.Item3.CompareTo(objectB.Item3)); // Sorts in place (ascending)
        return capacities;
    }


    private Tuple<int, int, float> generateWaterFlowCapacity(int x, int y, int destX, int destY) {
        // Get the absolute water height difference.
        float localWaterHeight = waterHeight[x, y];
        float localTerrainHeight = terrainHeight[x, y];

        float otherWaterHeight = waterHeight[destX, destY];
        float otherTerrainHeight = terrainHeight[destX, destY];

        float localAbsoluteHeight = localTerrainHeight + localWaterHeight;
        float otherAbsoluteHeight = otherTerrainHeight + otherWaterHeight;

        float waterFlowDifference = 0f; // Default -> If we don't change there is no water flow

        /* The result now is the Minimum of 
         * the total height differece or the amount of water available
         * E.G. [Terrain / Water]
         *      [0.5/0.0] [1.0/1.0]
         * The total height difference is 1.5 (0.5 for terrain, 1.0 for water)
         * Thus: result = Min(1.5,1.0) = 1.0 (meaning all water will move) 
         */

        // There is only a flow if the total height is higher than the neighbour!
        if (localAbsoluteHeight > otherAbsoluteHeight) {
            // Calculate how much water difference is there actually?
            // There are actually only two cases important here.

            if (otherAbsoluteHeight + localWaterHeight > localTerrainHeight) {
                // 1) The absolute height of the neighbor is so low,
                // the max amount of water from this field could move (because it is still higher)
                // The flowDifference will be the local water height (all water)
                waterFlowDifference = localWaterHeight;

            } else {
                // 2) The absolute heights are similar so ONLY A FRACTION of the water 
                // will move to the neighbour field (since we checked whether all water 
                // might flow and we are here instead)
                // The capacity consists of two values

                // A) Terrain-Delta (Dh) - The terrain height difference between local terrain height and absolute neighbor height
                // This is the amount of "guaranteed water flow" since the water won't flow back if the terrain is higher
                // NOTE: THIS CAN BE NEGATIVE! - If the other absolute height (including existing water) is higher than the local terrain
                // this can be negative, meaning some of the water would flow back. This is perfectly fine in that step 
                // since we have another step later where we distribute "the rest" equally (and there we reduce that amount do "guaranteed water flow"
                //float Dh = localTerrainHeight - otherAbsoluteHeight;

                // B) We now have a "left over water amount" (local Water - Dh). That is the amount of water that has to be distributed equally
                // Between both fields.
                // So we divide this by 2 (since we want one half to flow and the other half to stay)
                //float halfLeftOverWaterAmount = (localWaterHeight - Dh) / 2f;

                // C) The total amount of water flow to the neighbour field is thus Dh (guaranteed flow) + halfLeftOverWaterAmount
                // NOTE: This is where the higher terrain (negative value) of A) works again. That might be a negative value
                // But here it will be added (i.e. subtracted) by the leftOverWaterAmount.
                // That way the higher terrain will reduce the total amount
                //waterFlowDifference = Dh + halfLeftOverWaterAmount;

                // Simplified solution. difference is ALL WATER POSSIBLE
                waterFlowDifference = Math.Min(localWaterHeight, localAbsoluteHeight - otherAbsoluteHeight);
            }
        }
        return new Tuple<int, int, float>(destX, destY, waterFlowDifference);
    }


    /* This generates a water texture linked to the amount of water in each "(height) pixel" */
    private void GenerateWaterTexture() {
        Color pixelColor = new Color(0f, 0f, 0f);
        for (int y = 0; y < frameDesc.Height; y++) {
            for (int x = 0; x < frameDesc.Width; x++) {
                float waterHeightVal = waterHeight[x, y];
                //Color color = new Color(waterHeightVal, waterHeightVal, waterHeightVal);
                //texture.SetPixel(x, y, color);
                if (waterHeightVal > 0) {
                    pixelColor = new Color(0.2f, 0.3f, 1f, waterHeightVal);
                    texture.SetPixel(x, y, pixelColor);
                } else {
                    // All other textures get a color mapping 
                    float terrainHeightVal = terrainHeight[x, y];
                    if (terrainHeightVal < 0.5) {
                        pixelColor = new Color(1f, 1f, 0.6f); // Beach
                    } else if (terrainHeightVal < 0.8) {
                        pixelColor = new Color(0.5f, 1f, 0.5f); // Grass
                    } else if (terrainHeightVal < 0.9) {
                        pixelColor = new Color(0.7f, 0.7f, 0.7f); // Mountain
                    } else {
                        pixelColor = new Color(0.7f, 0.7f, 0.7f); // Mountain
                    }

                texture.SetPixel(x, y, pixelColor);
                }

                //  if (waterHeightVal > 0) {
                //      Debug.Log("Water ( " + waterHeightVal + " ) in pixel: " + x + " ; " + y);
                //  }
            }
        }

        // Paint the Source of Water ("+")
        pixelColor = new Color(1f, 0f, 0f);
        texture.SetPixel(waterSourceX, waterSourceY, pixelColor);
        texture.SetPixel(waterSourceX+1, waterSourceY, pixelColor);
        texture.SetPixel(waterSourceX-1, waterSourceY, pixelColor);
        texture.SetPixel(waterSourceX, waterSourceY+1, pixelColor);
        texture.SetPixel(waterSourceX, waterSourceY-1, pixelColor);
        // Apply the texture
        texture.Apply();
    }

    void OnApplicationQuit() {
        if (_Reader != null) {
            _Reader.Dispose();
            _Reader = null;
        }

        if (_Sensor != null) {
            if (_Sensor.IsOpen) {
                _Sensor.Close();
            }

            _Sensor = null;
        }
    }
}
