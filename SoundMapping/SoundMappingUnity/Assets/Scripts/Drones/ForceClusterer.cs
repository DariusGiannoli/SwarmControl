using System.Collections.Generic;
using System.Threading;
using UnityEngine;



public static class ForceClusterer
{
    // Existing fields for obstacle and single-cluster olfati
    public static List<Vector3> lastObstacleClusteredForces = new List<Vector3>();
    private static Thread Obstaclethread;

    private static List<Vector3> lastOlfatiClusteredForces = new List<Vector3>();
    private static Thread Olfatithread;

    // -------------------------------------------
    // NEW: Fields for multi-cluster Olfati forces
    private static List<Cluster> lastOlfatiMultiClusters = new List<Cluster>();
    private static Thread OlfatiMultiThread;

    // -------------------------------------------
    // Existing public functions

    public static List<Vector3> getOlfatiForces(List<Vector3> forces, float angleThreshold = 20f, int minSamples = 1, float minForceThreshold = 1f)
    {
        if (!(Olfatithread != null && Olfatithread.IsAlive))
        {
            Olfatithread = new Thread(() => ClusterForces(forces, angleThreshold, minSamples, minForceThreshold, out lastOlfatiClusteredForces));
            Olfatithread.Start();
        }

        return lastOlfatiClusteredForces;
    }

    public static List<Vector3> getForcesObstacle(List<Vector3> forces, float angleThreshold = 20f, int minSamples = 1, float minForceThreshold = 1f)
    {
        if (!(Obstaclethread != null && Obstaclethread.IsAlive))
        {         
            Obstaclethread = new Thread(() => ClusterForces(forces, angleThreshold, minSamples, minForceThreshold, out lastObstacleClusteredForces));
            Obstaclethread.Start();
        }

        return lastObstacleClusteredForces;
    }   

    // -------------------------------------------
    // Existing DBSCAN-based clustering method
    public static void ClusterForces(List<Vector3> forces, float angleThreshold, int minSamples, float minForceThreshold, 
        out List<Vector3> lastClusteredForces) 
    {
        int n = forces.Count;
        // Compute the normalized directions for each force.
        // These directions are used for clustering even though the averaging is done on the original forces.
        List<Vector3> directions = new List<Vector3>(n);
        for (int i = 0; i < n; i++)
        {
            // If the force is zero, we store Vector3.zero (it will likely be ignored in the cluster average if below threshold)
            if (forces[i] == Vector3.zero)
                directions.Add(Vector3.zero);
            else
                directions.Add(forces[i].normalized);
        }

        // DBSCAN clustering: each point gets a cluster label.
        // 0: not yet assigned, -1: noise, positive integers: cluster IDs.
        int[] clusterLabels = new int[n];
        for (int i = 0; i < n; i++)
            clusterLabels[i] = 0;

        bool[] visited = new bool[n];
        int clusterId = 0;

        for (int i = 0; i < n; i++)
        {
            if (visited[i])
                continue;

            visited[i] = true;
            List<int> neighborIndices = RegionQuery(directions, i, angleThreshold);

            if (neighborIndices.Count < minSamples)
            {
                // Mark as noise.
                clusterLabels[i] = -1;
            }
            else
            {
                clusterId++; // Start a new cluster.
                ExpandCluster(directions, clusterLabels, visited, i, neighborIndices, clusterId, angleThreshold, minSamples);
            }
        }

        // Organize indices by cluster.
        Dictionary<int, List<int>> clusters = new Dictionary<int, List<int>>();
        for (int i = 0; i < n; i++)
        {
            // Only consider points assigned to a cluster (ignore noise).
            if (clusterLabels[i] > 0)
            {
                if (!clusters.ContainsKey(clusterLabels[i]))
                    clusters[clusterLabels[i]] = new List<int>();
                clusters[clusterLabels[i]].Add(i);
            }
        }

        // For each cluster, compute the average force.
        List<Vector3> averageForces = new List<Vector3>();
        foreach (var cluster in clusters)
        {
            Vector3 sum = Vector3.zero;
            int count = 0;
            foreach (int index in cluster.Value)
            {
                // Only include forces that are above the minimum threshold.
                if (forces[index].magnitude >= minForceThreshold)
                {
                    sum += forces[index];
                    count++;
                }
            }
            if (count > 0)
            {
                averageForces.Add(sum / count);
            }
        }

        lastClusteredForces = averageForces;
    }

    private static void ExpandCluster(List<Vector3> directions, int[] clusterLabels, bool[] visited, int pointIndex,
                                      List<int> neighborIndices, int clusterId, float angleThreshold, int minSamples)
    {
        // Assign the starting point to the cluster.
        clusterLabels[pointIndex] = clusterId;

        // Use a queue for breadth-first expansion.
        Queue<int> neighborsQueue = new Queue<int>(neighborIndices);

        while (neighborsQueue.Count > 0)
        {
            int currentIndex = neighborsQueue.Dequeue();

            if (!visited[currentIndex])
            {
                visited[currentIndex] = true;
                List<int> currentNeighbors = RegionQuery(directions, currentIndex, angleThreshold);

                if (currentNeighbors.Count >= minSamples)
                {
                    // Enqueue any new neighbors.
                    foreach (int ni in currentNeighbors)
                    {
                        if (!neighborsQueue.Contains(ni))
                        {
                            neighborsQueue.Enqueue(ni);
                        }
                    }
                }
            }

            // If the point is not yet assigned to any cluster, assign it.
            if (clusterLabels[currentIndex] == 0)
            {
                clusterLabels[currentIndex] = clusterId;
            }
        }
    }

    private static List<int> RegionQuery(List<Vector3> directions, int pointIndex, float angleThreshold)
    {
        List<int> neighbors = new List<int>();
        Vector3 currentDirection = directions[pointIndex];

        for (int i = 0; i < directions.Count; i++)
        {
            if (i == pointIndex)
                continue;

            if (Vector3.Angle(currentDirection, directions[i]) <= angleThreshold)
            {
                neighbors.Add(i);
            }
        }

        return neighbors;
    }

    // -------------------------------------------
    // NEW: A helper class for the greedy multi-cluster algorithm.
    // Each Cluster holds a running sum (to later compute the average) and a count.
    private class Cluster
    {
        public Vector3 Sum;
        public int Count;

        public Cluster(Vector3 initialForce)
        {
            Sum = initialForce;
            Count = 1;
        }

        /// <summary>
        /// Returns the average force vector (which indicates both tendency and magnitude).
        /// </summary>
        public Vector3 Average => Sum / Count;

        /// <summary>
        /// Returns the normalized average direction.
        /// </summary>
        public Vector3 AverageDirection
        {
            get
            {
                Vector3 avg = Average;
                return avg != Vector3.zero ? avg.normalized : Vector3.zero;
            }
        }

        /// <summary>
        /// Adds a new force vector into the cluster.
        /// </summary>
        public void Add(Vector3 force)
        {
            Sum += force;
            Count++;
        }
    }

    // -------------------------------------------
    // NEW: Greedy clustering for Olfati forces with multiple clusters.
    // This function does not require a "minSamples" parameter since each force that does not
    // match an existing cluster creates a new one.
    // Only forces with magnitude above minForceThreshold are considered.
    private static List<Cluster> ClusterMultiForces(List<Vector3> forces, float angleThreshold, float minForceThreshold)
    {
        List<Cluster> clusters = new List<Cluster>();

        foreach (Vector3 force in forces)
        {
            if (force.magnitude < minForceThreshold)
                continue;

            Vector3 forceDir = force.normalized;
            bool assigned = false;

            // Try to add the force to an existing cluster.
            foreach (Cluster cluster in clusters)
            {
                // Compare the force direction with the cluster's current average direction.
                if (Vector3.Angle(forceDir, cluster.AverageDirection) <= angleThreshold)
                {
                    cluster.Add(force);
                    assigned = true;
                    break;
                }
            }

            // If no suitable cluster was found, create a new one.
            if (!assigned)
            {
                clusters.Add(new Cluster(force));
            }
        }

        return clusters;
    }

    /// <summary>
    /// Returns the averaged forces for each discovered cluster using the greedy multi-cluster algorithm.
    /// This gives you multiple averaged forces (each with its own norm and direction) that indicate the tendency.
    /// </summary>
    public static List<Vector3> getOlfatiMultiClusterAveragedForces(List<Vector3> forces, float angleThreshold = 20f, float minForceThreshold = 1f)
    {
        if (!(OlfatiMultiThread != null && OlfatiMultiThread.IsAlive))
        {
            OlfatiMultiThread = new Thread(() =>
            {
                lastOlfatiMultiClusters = ClusterMultiForces(forces, angleThreshold, minForceThreshold);
            });
            OlfatiMultiThread.Start();
        }

        // Optionally wait for the thread to finish (or if you are in a real-time system you might simply return the last result).
        // For example, you could use OlfatiMultiThread.Join() if you want to block until clustering is finished.

        // Return the average force (tendency) for each cluster.
        List<Vector3> averagedForces = new List<Vector3>();
        foreach (Cluster cluster in lastOlfatiMultiClusters)
        {
            averagedForces.Add(cluster.Average);
        }
        return averagedForces;
    }
}