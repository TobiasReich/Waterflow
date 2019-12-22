using System;
using System.Collections;
using System.Collections.Generic;
using System.Threading;
using Unity.Collections;
using Unity.Jobs;
using UnityEngine;
using Windows.Kinect;


public class WaterflowManager : MonoBehaviour
{
    private FrameDescription frameDesc;
    private KinectSensor _Sensor;

    private DepthFrameReader _Reader;
    private ushort[] _Data;
    private const float MAX_DEPTH = 8000f; // The maximum value the Kinect may return for distances
    private const float WATER_HEIGHT_EPSILON = 0.001f; // Water heights below this are considered 0 (so we avoid infinitely small water puddles)
    private const float SEA_LEVEL_HEIGHT_EPSILON = 0.01f; // Everything below that height is considered ground water height so water can disappear
    private const float FRESH_WATER_INFLOW = 1000f; // The MAX amount of water added each tick
    private const float HEIGHT_MAP_MULTIPLYER = 500f; // The amount of amplification for the terrain (1.0 means the height of the absolute terrain = the height of 1.0 water)

    private Color waterEnabledColor;
    private Texture2D waterTexture; // Texture that masks where we "stamp" water
    private Texture2D heightTexture; // Texture that paints the height

    private int depthWidth = 512;
    private int depthHeight = 424;
    
    // Values for water texture offset (so it looks more like flowing water)
    private float waterTextureFlowSpeed = 0.002f;
    private Material material;

    /* Defines where the water comes from */
    private int waterSourceX = 130;
    private int waterSourceY = 100;
    private float waterInflowScale = 0.5f; // The amount of water added each tick
    private float minimumHeight = 0.5f; // Translates all height values by this amount
    private float heightScaleFactor = 20f; // Scales all height values by this amount
    private int showSeaLevelIndicator = 1; // Shows (1) / Hides (0) the ground highlighted


    // A list containing all height values sorted so we can iterate from the heighest to the lowest field
    private List<Tuple<int, int, float>> heightMapOrderedList = new List<Tuple<int, int, float>>();
    private float[,] waterHeight;
    private float[,] terrainHeight;


    void Start() {
        Application.targetFrameRate = 60; // Set the FPS to 30 - this is the max the Kinect can do.

        Debug.Log("FrameRate: " + Application.targetFrameRate);

        waterEnabledColor = new Color(1f, 0f, 0f);

        waterHeight = new float[depthWidth, depthHeight];
        terrainHeight = new float[depthWidth, depthHeight];

        _Sensor = KinectSensor.GetDefault();
        
        waterTexture = new Texture2D(depthWidth, depthHeight);
        heightTexture = new Texture2D(depthWidth, depthHeight);

        material = gameObject.GetComponent<Renderer>().material;
        material.SetTexture("_WaterMaskTex", waterTexture);
        material.SetTexture("_HeightTex", heightTexture);
        material.SetInt("_showSeaLevelGround", showSeaLevelIndicator);

        frameDesc = _Sensor.DepthFrameSource.FrameDescription;

        if (_Sensor != null) {
            _Reader = _Sensor.DepthFrameSource.OpenReader();
            _Data = new ushort[_Sensor.DepthFrameSource.FrameDescription.LengthInPixels];
        }

        terrainUpdateThread = new Thread(updateTerrain);
        terrainUpdateThread.Start();
    }


    void Update() {
        AddWater();
        DistributeWater();
        TrickleOffWater();
        GenerateWaterTexture();
    }


    /** Adds water to the system */
    private void AddWater() {
        waterHeight[waterSourceX, waterSourceY] = waterInflowScale * FRESH_WATER_INFLOW;
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
                continue; // Nothing to distribute here.

            // At the end of the world -> vanish
            } else if(x == 0 || x == frameDesc.Width-1 || y == 0 || y == frameDesc.Height-1) {
                // If we are are the border the water can flow there directly
                // Water will simply vanish from here since it "fell down the earth"
                waterHeight[x, y] = 0f; //waterHeight[x, y] * 0.5f;

            } else {
                // This is the normal case. The water distributes between the neighbour fields
                // The water amount for this 
                List<WaterFlow> capacityList = generateWaterFlowCapacityList(x, y);

                // The first element is the one with the least capacity.
                // We try to fill this one first (and all sourrounding ones equally)
                float availableWater = waterHeight[x, y];

                // Calculate how much water would have to be distributed
                // This is the amount of capacity in total. 
                // Most likely this is far more than available water
                // But that way we get an estimation where we require the most
                // to be flown to.
                float totalRequestedFlow = 0f;
                for (int neighbourIndex = 0; neighbourIndex < capacityList.Count; neighbourIndex++) {
                    totalRequestedFlow += capacityList[neighbourIndex].flow;
                }
                // Now we know how much water "would like" to flow. Divide the potential water by this value.
                // E.g. we have a total requested flow of 4 but only 1f water in the local field
                // This would lead to a totalCapacityRatio of 1 / 4 = 0.25 
                // In the next step all desired water would flow multiplied by * 0.25 
                float totalCapacityRatio = Math.Min(1f, availableWater / totalRequestedFlow);

                // The minimum amount of water that is desired will be divided by all fields with capacity
                // That way we spread the water evenly among all neighbours
                //TODO CHECK IF WE HAVE TO ADD +1 FOR THE LOCAL FIELD
                //float distributedWaterAmount = distributedCapacity / capacityList.Count;
                        
                // Now distribute the flow 
                // Iterate over all neighbours and give them their amount of water.
                for (int neighbourIndex = 0; neighbourIndex < capacityList.Count; neighbourIndex++) {
                    WaterFlow neighbourCapacity = capacityList[neighbourIndex];
                    waterHeight[neighbourCapacity.x, neighbourCapacity.y] += neighbourCapacity.flow * totalCapacityRatio;
                    waterHeight[x,y] -= neighbourCapacity.flow * totalCapacityRatio;
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
    private List<WaterFlow> generateWaterFlowCapacityList(int x, int y) {
        List<WaterFlow> capacities = new List<WaterFlow>();

        // For all 4 sourrounding fields calculate these values and add them if their capacity is > 0
        WaterFlow capacityNorth = generateWaterFlowCapacity(x, y, x, y - 1);
        if (capacityNorth.flow > WATER_HEIGHT_EPSILON) { 
            capacities.Add(capacityNorth); 
        }

        WaterFlow capacitySouth = generateWaterFlowCapacity(x, y, x, y + 1);
        if (capacitySouth.flow > WATER_HEIGHT_EPSILON) {
            capacities.Add(capacitySouth); 
        }

        WaterFlow capacityEast = generateWaterFlowCapacity(x, y, x + 1, y);
        if (capacityEast.flow > WATER_HEIGHT_EPSILON) { 
            capacities.Add(capacityEast); 
        }

        WaterFlow capacityWest = generateWaterFlowCapacity(x, y, x - 1, y);
        if (capacityWest.flow > WATER_HEIGHT_EPSILON) { 
            capacities.Add(capacityWest); 
        }

        // We now have a list of all sourrounding fields with flow capacity (can be less than 4!)
        // Sort that list by their capacity so we can fill all fields equally by 
        // their minimum commom capacity
        capacities.Sort((objectA, objectB) => objectA.flow.CompareTo(objectB.flow)); // Sorts in place (ascending when A - B)
        return capacities;
    }


    private WaterFlow generateWaterFlowCapacity(int x, int y, int destX, int destY) {
        // Get the absolute water height difference.
        float hereWaterHeight = waterHeight[x, y];
        float hereTerrainHeight = terrainHeight[x, y];
        float hereAbsoluteHeight = hereTerrainHeight + hereWaterHeight;

        float thereWaterHeight = waterHeight[destX, destY];
        float thereTerrainHeight = terrainHeight[destX, destY];
        float thereAbsoluteHeight = thereTerrainHeight + thereWaterHeight;


        float waterFlowDifference = 0f; // Default -> If we don't change there is no water flow

        // There is only a flow if here the total height is higher than there!
        if (hereAbsoluteHeight > thereAbsoluteHeight) {
            // Calculate how much water difference is there actually?
            // There are actually only two cases important here.

            if (thereAbsoluteHeight + hereWaterHeight > hereTerrainHeight) {
                // 1) The absolute height there is so low,
                // all water from here could move (because it is still higher)
                // The flowDifference will be all the water height from here
                waterFlowDifference = hereWaterHeight;

            } else {
                // 2) The absolute heights are similar so ONLY A FRACTION of the water 
                // will move to the neighbour field 

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
                //waterFlowDifference = Math.Min(hereWaterHeight, hereAbsoluteHeight - thereAbsoluteHeight);
                waterFlowDifference = hereAbsoluteHeight - thereAbsoluteHeight;
            }
        }

        return new WaterFlow(destX, destY, waterFlowDifference);
    }


    /// <summary>
    /// Removes a tiny bit of water from every field.
    /// This removes dead fields after a while 
    /// (e.g. those that are sourrounded by higher 
    /// fields will never loose it's water)
    /// That way now all water trickles away over time
    /// and only those areas that permanently receive at 
    /// least a minimum amount of water remain wet.
    /// </summary>
    private void TrickleOffWater() {
        for (int y = 0; y < frameDesc.Height; y++) {
            for (int x = 0; x < frameDesc.Width; x++) {
                if (terrainHeight[x, y] < SEA_LEVEL_HEIGHT_EPSILON) {
                    // Water on the "sea level" ground get 0
                    // This is important to allow water to leave the area again
                    waterHeight[x, y] = 0f; // only inside x > 50 && y > 50 && x < 500 && y < 400
                } else { 
                    // The water height is the maximum of "empty" and the current height - epsilon
                    waterHeight[x, y] = Math.Max(0f, waterHeight[x, y] - WATER_HEIGHT_EPSILON);
                }
            }
        }
    }



    /* This generates a water texture linked to the amount of water in each "(height) pixel" */
    private void GenerateWaterTexture() {
        float offset = Time.time * waterTextureFlowSpeed;
        material.SetTextureOffset("_WaterTex", new Vector2(offset, 0));

        for (int y = 1; y < frameDesc.Height-1; y++) {
            for (int x = 1; x < frameDesc.Width-1; x++) {
                float waterHeightVal = waterHeight[x, y];
                if (waterHeightVal > 0f) {
                    waterTexture.SetPixel(x, y, waterEnabledColor); // Sets the water texture enabled this pixel

                } else {
                    float sourroundingWater = waterHeight[x + 1, y] + waterHeight[x - 1, y] + waterHeight[x, y + 1] + waterHeight[x, y - 1] / 4f;
                    // If any of the sourrouncing fields contains water, paint this "wet"
                    // This smoothes the texture so we don't see that many "dry pixels" in a "continuum of water"
                    float height = terrainHeight[x, y] / HEIGHT_MAP_MULTIPLYER; // Reduce height to normalized value 0..1f
                    heightTexture.SetPixel(x, y, new Color(height, height, height));
                    waterTexture.SetPixel(x, y, new Color(sourroundingWater, 0f, 0f)); // Disables water texture this pixel
                }
            }
        }
        waterTexture.Apply();
        heightTexture.Apply();

        material.SetInt("_showSeaLevelGround", showSeaLevelIndicator);
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


    private void OnDestroy() {
        updatingTerrain = false;
    }


    //////////////////////////////////////////////////////// 
    ///          Adjustments from outside (UI)
    //////////////////////////////////////////////////////// 

    public void adjustWaterFlow(float amount) {
        Debug.Log("Adjusting flow to " + amount);
        waterInflowScale = amount;
    }

    /// <summary>
    /// Scale the terrain minimum value down
    /// This can be used to define the ground level of the terrain
    /// -> Use this in order to tell the sytem where the lowest scanned depth is
    /// All values lower than this will be moved to this depth
    /// This also normalizes the scanned data
    /// </summary>
    /// <param name="amount"></param>
    public void setGroundHeight(float amount) {
        Debug.Log("Adjusting minimum height to " + amount);
        minimumHeight = amount;
    }

    /// <summary>
    /// Unlike "setLowestHeight" this does not move the heightmap up or down but sets the scale factor
    /// All heights higher than this will be set to the maximum height
    /// This also normalizes the scanned data
    /// </summary>
    /// <param name="amount"></param>
    public void setHeightScale(float amount) {
        Debug.Log("Adjusting maximum height to " + amount);
        heightScaleFactor = amount;            
    } 
    
    /// <summary>
    /// Sets the ground "Highlight" enabled or disabled
    /// so its possible to see whether a pixel is already
    /// at the sea level water height
    /// </summary>
    /// <param name="amount"></param>
    public void toggleSeaLevelIndicator(bool enabled) {
        if (enabled) {
            showSeaLevelIndicator = 1;
        } else {
            showSeaLevelIndicator = 0;
        }
    }



    //////////////////////////////////////////////////////// 
    ///
    /// Background Job properties for updating the depth map 
    /// 
    /// -------------- TERRAIN UPDATE THREAD ---------------
    /// 
    //////////////////////////////////////////////////////// 
    private Thread terrainUpdateThread;
    private Boolean updatingTerrain = true; // Flag that keeps the terrain update thread alive


    void updateTerrain() {
        while (updatingTerrain) {
            // Debug.Log("Updating Terrain...");
            if (_Reader != null) {
                var frame = _Reader.AcquireLatestFrame();
                if (frame != null) {
                    frame.CopyFrameDataToArray(_Data);
                    frame.Dispose();
                    frame = null;
                }
            }
            UpdateHeightMap();
            Thread.Sleep(100);
        }
    }

    /* Iterate over all Fields and update its terrain height */
    private void UpdateHeightMap() {
        ushort[] depthData = _Data;
        List<Tuple<int, int, float>> tempHeightMapOrderedList = new List<Tuple<int, int, float>>();
        float[,] tempTerrainHeight = new float[depthWidth, depthHeight];
        float[,] smoothedTerrainHeight = new float[depthWidth, depthHeight];

        for (int y = 0; y < frameDesc.Height; y++) {
            for (int x = 0; x < frameDesc.Width; x++) {
                int fullIndex = (y * frameDesc.Width) + x;
                // The sensor is scanning the depth.
                // The nearest value is 0 and the farthest is 8000f
                float inverseHeightData = depthData[fullIndex] / MAX_DEPTH;

                /// Translate the ground here by substracting "minimumHeight" from the measured height.
                /// E.g having a height value of 0.3 and "minimumHeight" set to 0.1 the 
                /// final height of this field will be 0.2
                /// This gives the option to set the base height manually
                float heightValue = (1f - inverseHeightData) - minimumHeight;

                // After setting the ground height multiply the height with the scale factor so the heights can be scaled to 1
                heightValue *= heightScaleFactor;

                // Now multiply it with the height map multiplier
                heightValue *= HEIGHT_MAP_MULTIPLYER;

            tempTerrainHeight[x, y] = (terrainHeight[x, y] + heightValue) / 2; // Median over the last frame in order to avoid noise
                //tempTerrainHeight[x, y] = heightValue;
            }
        }

        // Blur the height map array. This makes errors less dominant but also avoids pixel errors
        for (int y = 1; y < frameDesc.Height-1; y++) {
            for (int x = 1; x < frameDesc.Width-1; x++) {
                // Just take the 4 sourrounding fields and calculate the average value
                smoothedTerrainHeight[x, y] =
                    (tempTerrainHeight[x, y] +
                    tempTerrainHeight[x+1, y] +
                    tempTerrainHeight[x-1, y] +
                    tempTerrainHeight[x, y+1] +
                    tempTerrainHeight[x, y-1]) / 5.0f;
                tempHeightMapOrderedList.Add(new Tuple<int, int, float>(x, y, smoothedTerrainHeight[x, y]));
                //tempHeightMapOrderedList.Add(new Tuple<int, int, float>(x, y, tempTerrainHeight[x, y]));
            }
        }

        // Sort the heightmap in place, descending (that's why ObjectB and ObjectA switched)
        // The first element is now the highest in the world
        tempHeightMapOrderedList.Sort((objectA, objectB) => objectA.Item3.CompareTo(objectB.Item3));

        // Assign the new (smoothed) height map data 
        // The direct assign to a new array helps avoiding race conditions 
        // a bit where the data gets updated while we process the water flow.
        heightMapOrderedList = tempHeightMapOrderedList;
        terrainHeight = smoothedTerrainHeight;
    }


}


/// <summary>
/// The WaterFlow struct containing information about potential waterflow as well as it's coordinates
/// </summary>
struct WaterFlow {
    public int x;
    public int y;
    public float flow;

    public WaterFlow(int x, int y, float waterFlow) : this() {
        this.x = x;
        this.y = y;
        this.flow = waterFlow;
    }
}