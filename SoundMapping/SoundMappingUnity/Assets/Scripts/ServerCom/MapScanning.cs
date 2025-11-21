using System.Collections;
using System.Collections.Generic;
using UnityEngine;

public class MapScanning : MonoBehaviour
{
    public Vector2 mapSize; // e.g., 100x100 units
    public float cellSize = 1.0f; // Size of each cell in world units
    public float obstacleHeight = 1.0f; // Height at which to check for obstacles
    public LayerMask obstacleLayer; // Set the layer mask to the obstacles layer

    private Vector3 center_position;

    private int[,] grid;

    void Start()
    {
    }

    private void update_center_position()
    {
        center_position = CameraMovement.getCameraPosition();
    }



    private DataEntry getCenterPosition()
    {
        update_center_position();
        return new DataEntry("map_center_position", center_position.ToString());
    }

    private DataEntry getMapData()
    {

        int gridWidth = Mathf.RoundToInt(mapSize.x / cellSize);
        int gridHeight = Mathf.RoundToInt(mapSize.y / cellSize);

        grid = new int[gridWidth, gridHeight];

        // Populate the grid with obstacle data
        update_center_position();
        PopulateGridWithObstacles(gridWidth, gridHeight, center_position);

        // Convert the grid data to a string
        string gridData = "";
        for (int x = 0; x < gridWidth; x++)
        {
            for (int y = 0; y < gridHeight; y++)
            {
                gridData += grid[x, y].ToString();
            }
        }

        return new DataEntry("map_data", gridData);
    }

    void PopulateGridWithObstacles(int width, int height, Vector3 centerPoint)
    {
        for (int x = 0; x < width; x++)
        {
            for (int y = 0; y < height; y++)
            {
                // Calculate the offset from the center
                float offsetX = (x - width / 2) * cellSize;
                float offsetY = (y - height / 2) * cellSize;

                // Calculate the world position for each cell at the specified height
                Vector3 worldPosition = new Vector3(
                    centerPoint.x + offsetX,
                    obstacleHeight,
                    centerPoint.z + offsetY
                );

                // Check for obstacles using a small radius overlap check
                bool isObstacle = Physics.CheckSphere(worldPosition, cellSize / 2, obstacleLayer);

                // Store 1 for obstacle, 0 for empty cell
                grid[x, y] = isObstacle ? 1 : 0;
            }
        }
    }



}
