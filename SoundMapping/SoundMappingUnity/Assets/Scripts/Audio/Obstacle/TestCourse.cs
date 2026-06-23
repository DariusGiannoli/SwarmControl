using UnityEngine;
using UnityEngine.Rendering;
#if UNITY_EDITOR
using UnityEditor;
using UnityEditor.SceneManagement;
#endif
using System.IO;
using System.Collections.Generic;

// ============================================================================
// MAIN RUNTIME SCRIPT
// ============================================================================
public class TestCourse : MonoBehaviour
{
    [Header("Lookup names")]
    public string pathHolderName = "Path Holder";
    public string floorName = "floor";
    public string startName = "Path (4)";
    public string endName = "Path (1)";
    public string timingStartLineName = "Starting_line";
    public string timingEndLineName = "Ending_line";
    public string startingSquareName = "Starting_square";
    public string groundName = "Path";

    [Header("XR Rendering Stability")]
    [Tooltip("Disable per-eye-sensitive renderer features on start/end course-line markers.")]
    public bool stabilizeCourseLinesForXR = true;

    [Header("Ground Scale")]
    public Vector3 groundScale = new Vector3(100f, 1f, 500f);

    [Header("Start Transform")]
    public Vector3 startPosition = new Vector3(0, 0.5f, 15);
    public Vector3 startRotation = new Vector3(0f, 0f, 0f);
    public Vector3 startScale    = new Vector3(100f, 3f, 10f);

    [Header("End Transform")]
    public Vector3 endPosition = new Vector3(0, 0.5f, 130);
    public Vector3 endRotation = new Vector3(0f, 0f, 0f);
    public Vector3 endScale    = new Vector3(100f, 3f, 10f);

    [Header("Gap Controller")]
    public Vector3 gc_position = new Vector3(177f, 2.25f, 72f);

    [Header("Gap Generation")]
    public int NB_GAPS = 7;
    public float corridorWidth = 200f;
    public float minSquareGapSize = 7f;
    public float maxSquareGapSize = 50f;
    public float gapSizeStep = 5f;

    [Header("Gap Positioning")]
    public float firstGapZOffset = 0f;
    public float gapSpacing = 50f;
    public bool useFixedTotalGapCenterDistance = false;
    public float targetTotalGapCenterDistance = 300f;
    public float startLineGapOffset = 10f;
    public float finishLineGapOffset = 10f;

    public float[] initialGapCenters = new float[]
    {
        -10f, 30f, 0f, 20f, -30f, 10f, -20f
    };

    [Header("Gap 0 Lock")]
    [Tooltip("When enabled, gap[0] is forced to sit directly in front of Starting_square every Generate — same X column as the spawn point, fixed Y from initialGapCenterHeights[0]. Overrides initialGapCenters[0] and any randomization for gap[0] only.")]
    public bool lockGap0ToStartingSquare = true;

    [Header("Random Gap Centers")]
    public bool randomizeGapCenters = false;
    [Tooltip("Inclusive random X range for each gap center, in GapController local space.")]
    public Vector2 randomGapCenterXRange = new Vector2(-30f, 30f);
    [Tooltip("Inclusive random Y range for each gap center. For 3D square gaps, keep the minimum at least wallHeight / 2.")]
    public Vector2 randomGapCenterYRange = new Vector2(25f, 50f);

    [Header("3D Square Gap Layout")]
    public bool generate3DGaps = true;
    [Tooltip("Set to 0 to randomize each square hole side length using the GapsController min/max range.")]
    public float squareGapSize = 0f;
    [Tooltip("Use a fixed linear sequence of gap sizes from minSquareGapSize to maxSquareGapSize instead of per-gap random sizes.")]
    public bool useLinearGapSizeSequence = true;
    [Tooltip("Randomly shuffle the linear gap size sequence each time Generate is pressed.")]
    public bool randomizeLinearGapSizeOrder = true;
    public Vector3 starEulerRotation = new Vector3(0f, -90f, 0f);
    [Tooltip("Rotation applied to each gap.transform around its center (degrees). Since gap.transform sits at the wall/gap center, this rotates the entire gap+walls+stars around the center axis without moving the center.")]
    public Vector3 gapEulerRotation = new Vector3(0f, 90f, 0f);
    [Tooltip("Zero-based gap index where the rotated segment starts. Set to -1 to randomly choose between Gap (2) and the second-to-last gap.")]
    public int rotationStartGapIndex = -1;
    public float[] initialGapCenterHeights = new float[]
    {
        25f, 35f, 45f, 30f, 40f, 50f, 32.5f
    };

    [Header("Wall Layout")]
    [Tooltip("In 3D square-gap mode this is the outer side length of each square wall panel.")]
    public float wallHeight = 50f;
    public float wallThickness = 5f;
    public float wallY = 25f;

    [Header("Prefab Auto-Load")]
    public string wallPrefabName = "ObstacleWall";
    public string wallPrefabFolder = "Assets/Prefab/DronePrefabUtilities";

    private GameObject wallPrefab;
    private float[] generatedGapSizes;

    [HideInInspector]
    public Transform startLine;
    [HideInInspector]
    public Transform endLine;
    [HideInInspector]
    public Transform timingStartLine;
    [HideInInspector]
    public Transform timingEndLine;
    [HideInInspector]
    public Transform startingSquare;
    [HideInInspector]
    public Transform groundTile;

    [System.Serializable]
    public class ObstacleWall
    {
        public string name;
        // World position of the wall's transform.
        public float x;
        public float y;
        public float z;
        // Wall dimensions in its own local frame (= transform.lossyScale, which
        // ignores rotation). To get the world-space AABB you must rotate the box
        // by (rotX, rotY, rotZ) around (x, y, z).
        public float width;
        public float height;
        public float length;
        // World-space Euler angles (degrees). Needed to reconstruct rotated walls.
        public float rotX;
        public float rotY;
        public float rotZ;
    }

    [System.Serializable]
    public class GapExport
    {
        public int index;
        // World position of the gap's center (= gap.transform.position).
        public float centerX;
        public float centerY;
        public float centerZ;
        // World-space Euler angles of the gap (degrees). Non-zero for the
        // rotated L-segment, zero for the straight section.
        public float rotX;
        public float rotY;
        public float rotZ;
        public ObstacleWall left;
        public ObstacleWall right;
        public ObstacleWall top;
        public ObstacleWall bottom;
    }

    [System.Serializable]
    public class ObstacleCourseExport
    {
        public float courseWidth;
        public float courseLength;
        public ObstacleWall[] boundaryWalls;
        public GapExport[] gaps;
    }

    // -------------------------------
    // AUTO-FIND
    // -------------------------------
    void Awake()
    {
        AutoFindReferences();
        ApplyCourseLineXRRenderingFix();
    }

    public void AutoFindReferences()
    {
        Transform parent = transform.parent;

        Transform pathHolder =
            (parent != null ? parent.Find(pathHolderName) : null);

        if (pathHolder == null)
        {
            Debug.LogError("[TestCourse] Path Holder not found.");
            return;
        }

        Transform floor = pathHolder.Find(floorName);
        if (floor == null)
        {
            Debug.LogError("[TestCourse] floor not found.");
            return;
        }

        startLine = floor.Find(startName);
        endLine   = floor.Find(endName);
        timingStartLine = FindDeepChild(pathHolder, timingStartLineName);
        timingEndLine = FindDeepChild(pathHolder, timingEndLineName);
        if (timingStartLine == null)
        {
            GameObject foundStart = GameObject.Find(timingStartLineName);
            if (foundStart != null)
                timingStartLine = foundStart.transform;
        }
        if (timingEndLine == null)
        {
            GameObject foundEnd = GameObject.Find(timingEndLineName);
            if (foundEnd != null)
                timingEndLine = foundEnd.transform;
        }
        startingSquare = FindDeepChild(pathHolder, startingSquareName);
        if (startingSquare == null)
        {
            GameObject foundSquare = GameObject.Find(startingSquareName);
            if (foundSquare != null)
                startingSquare = foundSquare.transform;
        }
        groundTile = floor.Find(groundName);

        if (startLine  == null) Debug.LogError("[TestCourse] Start not found");
        if (endLine    == null) Debug.LogError("[TestCourse] End not found");
        if (timingStartLine == null) Debug.LogError("[TestCourse] Timing start line not found: " + timingStartLineName);
        if (timingEndLine == null) Debug.LogError("[TestCourse] Timing end line not found: " + timingEndLineName);
        if (startingSquare == null) Debug.LogWarning("[TestCourse] Starting square not found: " + startingSquareName + " (Starting_line X will fall back to the first gap's X).");
        if (groundTile == null) Debug.LogError("[TestCourse] Ground not found");

        ApplyCourseLineXRRenderingFix();
    }

    public void ApplyCourseLineXRRenderingFix()
    {
        if (!stabilizeCourseLinesForXR) return;

        ConfigureCourseLineRenderer(TrajectoryStartLine());
        ConfigureCourseLineRenderer(TrajectoryEndLine());
    }

    private static void ConfigureCourseLineRenderer(Transform line)
    {
        if (line == null) return;

        Renderer[] renderers = line.GetComponentsInChildren<Renderer>(includeInactive: true);
        foreach (Renderer renderer in renderers)
        {
            renderer.allowOcclusionWhenDynamic = false;
            renderer.motionVectorGenerationMode = MotionVectorGenerationMode.ForceNoMotion;
            renderer.shadowCastingMode = ShadowCastingMode.Off;
            renderer.receiveShadows = false;
        }
    }

    private Transform FindDeepChild(Transform root, string childName)
    {
        if (root == null || string.IsNullOrEmpty(childName))
            return null;

        Transform direct = root.Find(childName);
        if (direct != null)
            return direct;

        for (int i = 0; i < root.childCount; i++)
        {
            Transform found = FindDeepChild(root.GetChild(i), childName);
            if (found != null)
                return found;
        }

        return null;
    }

    public Transform TrajectoryStartLine()
    {
        return timingStartLine != null ? timingStartLine : startLine;
    }

    public Transform TrajectoryEndLine()
    {
        return timingEndLine != null ? timingEndLine : endLine;
    }

#if UNITY_EDITOR
    // -------------------------------
    // LOAD PREFAB FROM PROJECT FOLDER
    // -------------------------------
    private GameObject LoadPrefab(string name, string folder)
    {
        string[] guids = AssetDatabase.FindAssets(name + " t:Prefab", new[] { folder });

        if (guids.Length == 0)
        {
            Debug.LogError("[TestCourse] Prefab not found in folder: " + folder + " Name = " + name);
            return null;
        }

        string path = AssetDatabase.GUIDToAssetPath(guids[0]);
        return AssetDatabase.LoadAssetAtPath<GameObject>(path);
    }
#endif

    public void Generate()
    {
        Clean();
        PlaceStartEnd();
        GenerateGaps();
        MarkSceneDirty();
    }

    // In editor scripts, scene modifications made via Instantiate / DestroyImmediate /
    // transform edits do NOT automatically flag the scene as modified — the title-bar
    // asterisk stays off and the new course is silently discarded when the scene is
    // saved or reloaded. Call this at the end of any scene-mutating operation so
    // Unity persists the result.
    private void MarkSceneDirty()
    {
#if UNITY_EDITOR
        if (Application.isPlaying)
            return;
        EditorUtility.SetDirty(this);
        var scene = gameObject.scene;
        if (scene.IsValid() && scene.isLoaded)
            EditorSceneManager.MarkSceneDirty(scene);
#endif
    }

    public void Save()
    {
        Transform gcRoot = transform.Find("GapController");
        if (gcRoot == null)
        {
            Debug.LogWarning("[TestCourse] Cannot save: generate the obstacle course first.");
            return;
        }

        var controller = gcRoot.GetComponent<GapsController>();
        if (controller == null)
        {
            Debug.LogWarning("[TestCourse] Cannot save: GapsController component missing on GapController.");
            return;
        }

        AutoFindReferences();
        controller.Apply();
        // controller.Apply() resets every gap's localPosition.z to the straight-corridor
        // formula (startZ + i * gapSpacing), which flattens the rotated L-segment.
        // Rebuild the rotated segment so the JSON we're about to write reflects what's
        // actually in the scene (and what was just generated).
        PositionRotatedSegment(gcRoot);

        var gaps = new List<Gap>(gcRoot.GetComponentsInChildren<Gap>(includeInactive: true));
        if (gaps.Count == 0)
        {
            Debug.LogWarning("[TestCourse] Cannot save: no gaps found.");
            return;
        }

        float courseWidth = controller.corridorWidth;
        float courseLength = Mathf.Max(0f, (gaps.Count - 1) * controller.gapSpacing + controller.gapWidth);

        var boundaryWalls = new List<ObstacleWall>();
        var leftBoundary = gcRoot.Find("BoundaryLeft");
        var rightBoundary = gcRoot.Find("BoundaryRight");
        if (leftBoundary != null) boundaryWalls.Add(ToWall(leftBoundary, "BoundaryLeft"));
        if (rightBoundary != null) boundaryWalls.Add(ToWall(rightBoundary, "BoundaryRight"));

        // Sort by sibling index (creation order along the corridor). Sorting by
        // transform.localPosition.z is unsafe once a rotated segment exists.
        gaps.Sort((a, b) => a.transform.GetSiblingIndex().CompareTo(b.transform.GetSiblingIndex()));
        var gapExports = new List<GapExport>();
        for (int i = 0; i < gaps.Count; i++)
        {
            Gap g = gaps[i];
            var left = g.leftWall != null ? ToWall(g.leftWall, "LeftWall") : null;
            var right = g.rightWall != null ? ToWall(g.rightWall, "RightWall") : null;
            var top = g.topWall != null && g.topWall.gameObject.activeSelf ? ToWall(g.topWall, "TopWall") : null;
            var bottom = g.bottomWall != null && g.bottomWall.gameObject.activeSelf ? ToWall(g.bottomWall, "BottomWall") : null;

            Vector3 gPos = g.transform.position;
            Vector3 gRot = g.transform.eulerAngles;

            gapExports.Add(new GapExport
            {
                index = i,
                centerX = gPos.x,
                centerY = gPos.y,
                centerZ = gPos.z,
                rotX = gRot.x,
                rotY = gRot.y,
                rotZ = gRot.z,
                left = left,
                right = right,
                top = top,
                bottom = bottom
            });
        }

        var export = new ObstacleCourseExport
        {
            courseWidth = courseWidth,
            courseLength = courseLength,
            boundaryWalls = boundaryWalls.ToArray(),
            gaps = gapExports.ToArray()
        };

        string json = JsonUtility.ToJson(export, true);
        string dir = Path.Combine(Application.dataPath, "Data/default/ObstacleCourse");
        if (!Directory.Exists(dir))
            Directory.CreateDirectory(dir);
        string path = Path.Combine(dir, "TestCourse.json");
        File.WriteAllText(path, json);
        Debug.Log("[TestCourse] Saved obstacle course to " + path);

        // Save() calls controller.Apply() + PositionRotatedSegment() which mutate
        // the scene. Mark dirty so those edits stick.
        MarkSceneDirty();
    }

    private ObstacleWall ToWall(Transform t, string defaultName)
    {
        Vector3 p = t.position;
        Vector3 s = t.lossyScale;
        Vector3 r = t.eulerAngles;
        return new ObstacleWall
        {
            name = string.IsNullOrEmpty(t.name) ? defaultName : t.name,
            x = p.x,
            y = p.y,
            z = p.z,
            width = s.x,
            height = s.y,
            length = s.z,
            rotX = r.x,
            rotY = r.y,
            rotZ = r.z
        };
    }

    public void Clean()
    {
        // 1. Delete ALL children of TestCourse
        #if UNITY_EDITOR
        for (int i = transform.childCount - 1; i >= 0; i--)
            DestroyImmediate(transform.GetChild(i).gameObject);
        #else
        for (int i = transform.childCount - 1; i >= 0; i--)
            Destroy(transform.GetChild(i).gameObject);
        #endif

        // 2. Delete all FLOOR children except start, end, ground
        AutoFindReferences();

        if (startLine == null || endLine == null || groundTile == null)
            return;

        Transform floor = startLine.parent;

        #if UNITY_EDITOR
        for (int i = floor.childCount - 1; i >= 0; i--)
        {
            Transform c = floor.GetChild(i);
            if (c != startLine && c != endLine && c != groundTile)
                DestroyImmediate(c.gameObject);
        }
        #else
        for (int i = floor.childCount - 1; i >= 0; i--)
        {
            Transform c = floor.GetChild(i);
            if (c != startLine && c != endLine && c != groundTile)
                Destroy(c.gameObject);
        }
        #endif
    }

    // -------------------------------
    // BUTTON FUNCTION 1 — Place Start / End
    // -------------------------------
    public void PlaceStartEnd()
    {
        AutoFindReferences();
        if (startLine == null || endLine == null) return;

        startLine.position = startPosition;
        startLine.rotation = Quaternion.Euler(startRotation);
        startLine.localScale = startScale;

        endLine.position = endPosition;
        endLine.rotation = Quaternion.Euler(endRotation);
        endLine.localScale = endScale;

        // Basic scale for Y only
        groundTile.localScale = groundScale;

        // Align ground tile X position with the GapController
        Vector3 gp = groundTile.localPosition;
        gp.x = gc_position.x;
        gp.z = gc_position.z + firstGapZOffset + (NB_GAPS - 1) * gapSpacing / 2f;
        groundTile.localPosition = gp;

        Debug.Log("[TestCourse] Start & End fully positioned (pos+rot+scale).");
    }

    public float FirstGapWorldZ()
    {
        return gc_position.z + firstGapZOffset;
    }

    public float LastGapWorldZ(float spacing)
    {
        return FirstGapWorldZ() + Mathf.Max(0, NB_GAPS - 1) * spacing;
    }

    public void AlignTimingLinesToGeneratedGaps(GapsController controller)
    {
        AutoFindReferences();

        Transform trajectoryStartLine = TrajectoryStartLine();
        Transform trajectoryEndLine = TrajectoryEndLine();
        if (trajectoryStartLine == null && trajectoryEndLine == null)
            return;

        // Fall back to the Z-only estimate when no gaps exist yet.
        if (controller == null)
        {
            float fallbackFirstZ = FirstGapWorldZ();
            float fallbackLastZ = LastGapWorldZ(gapSpacing);
            float fallbackX = transform.TransformPoint(gc_position).x;
            ApplyLinePosition(trajectoryStartLine, fallbackX, fallbackFirstZ - startLineGapOffset);
            ApplyLinePosition(trajectoryEndLine, fallbackX, fallbackLastZ + finishLineGapOffset);
            return;
        }

        Gap[] gaps = controller.GetComponentsInChildren<Gap>(includeInactive: true);
        if (gaps.Length == 0)
            return;

        // Sort by sibling index (creation order). Once a rotated segment exists,
        // gap.position.z is no longer monotonic in corridor index — rotated gaps
        // sit in the parent +X direction, so first/last-by-Z would be wrong.
        System.Array.Sort(gaps, (a, b) =>
            a.transform.GetSiblingIndex().CompareTo(b.transform.GetSiblingIndex()));

        Transform firstGap = gaps[0].transform;
        Transform lastGap = gaps[gaps.Length - 1].transform;

        if (trajectoryStartLine != null)
        {
            // One offset BEFORE the first gap along its own corridor direction.
            // firstGap is always unrotated under current rules, so this is -Z.
            Vector3 dir = firstGap.rotation * Vector3.forward;
            Vector3 target = firstGap.position - dir * startLineGapOffset;

            // X must follow the Starting_square scene object — NOT the first gap.
            // firstGap.position.x = gc_position.x + gap[0].gapCenterX, which is
            // randomized / driven by initialGapCenters[0]. Without this override
            // Starting_line drifts off-axis from the spawn square every Generate.
            float startX = (startingSquare != null) ? startingSquare.position.x : target.x;
            ApplyLinePosition(trajectoryStartLine, startX, target.z);
        }

        if (trajectoryEndLine != null)
        {
            // One offset AFTER the last gap along its own corridor direction.
            // If the last gap is part of the rotated segment, the line sits along
            // +X (or whatever direction gapEulerRotation rotated +Z to).
            Vector3 dir = lastGap.rotation * Vector3.forward;
            Vector3 target = lastGap.position + dir * finishLineGapOffset;
            ApplyLinePosition(trajectoryEndLine, target.x, target.z);

            // Snap Y by COPYING the Starting_line ↔ firstGap vertical offset.
            // Whatever height the user manually placed Starting_line at
            // (typically the bottom sill of gap[0]'s wall panel), Ending_line
            // ends up at the matching height relative to lastGap. This avoids
            // having to guess what "bottom" means (panel bottom vs. hole sill
            // vs. ground) — we just mirror the exact same offset.
            //
            // Falls back gracefully if Starting_line is missing: leaves
            // Ending_line's Y untouched.
            if (trajectoryStartLine != null)
            {
                float dyStartingLineFromGap0 = trajectoryStartLine.position.y - firstGap.position.y;
                Vector3 endPos = trajectoryEndLine.position;
                endPos.y = lastGap.position.y + dyStartingLineFromGap0;
                trajectoryEndLine.position = endPos;
            }

            // Match the rotation of the last gap so the line stays perpendicular
            // to the corridor at its tail.
            trajectoryEndLine.rotation = lastGap.rotation;
        }
    }

    private static void ApplyLinePosition(Transform line, float worldX, float worldZ)
    {
        if (line == null) return;
        Vector3 p = line.position;
        p.x = worldX;
        p.z = worldZ;
        // Y is intentionally preserved — the line's height is configured manually.
        line.position = p;
    }

    public void GenerateGaps()
    {
        AutoFindReferences();

#if UNITY_EDITOR
        wallPrefab = LoadPrefab(wallPrefabName, wallPrefabFolder);
        if (wallPrefab == null)
        {
            Debug.LogError("[TestCourse] Cannot generate gaps because wall prefab failed to load.");
            return;
        }
#endif

        GameObject gc = new GameObject("GapController");
        gc.transform.SetParent(this.transform, worldPositionStays: false);

        // Clean identity transform
        gc.transform.localPosition = gc_position;
        gc.transform.localRotation = Quaternion.identity;
        gc.transform.localScale = Vector3.one;

        var controller = gc.AddComponent<GapsController>();
        controller.corridorWidth = corridorWidth;
        controller.minGapSize = minSquareGapSize;
        controller.maxGapSize = maxSquareGapSize;
        controller.gapResolution = gapSizeStep;
        controller.gapSpacing = gapSpacing;
        controller.gapWidth = wallThickness;

        PrepareGapSizeSequence();

        // Create gaps RELATIVE TO gc_position
        for (int i = 0; i < NB_GAPS; i++)
            CreateGap(gc.transform, i);

        // Lock gap[0] to Starting_square BEFORE the first Apply, so that even
        // when initialGapCenters[0]/randomizeGapCenters would have moved it,
        // gap[0] always lands directly in front of Starting_line.
        LockGap0ToStartingSquare(gc.transform);

        // Pick a single transition gap k in [2, NB_GAPS-2] (inclusive). From
        // index k onward every gap gets `gapEulerRotation`; earlier gaps keep
        // identity rotation. This produces one "rotation event" along the
        // corridor, with downstream gaps staying parallel to the rotated one.
        AssignTransitionRotation(gc.transform);

        controller.Apply();
        PositionRotatedSegment(gc.transform);

        if (useFixedTotalGapCenterDistance)
        {
            AdjustGapCentersForTargetTotalDistance(controller, targetTotalGapCenterDistance);
            // RandomizeCentersUntilAtLeastTarget (inside the fixed-distance pass)
            // will have re-randomized every gap including gap[0]. Re-lock it
            // before the second Apply so the scene-final state still has gap[0]
            // pinned to Starting_square.
            LockGap0ToStartingSquare(gc.transform);
            UpdateTransitionRotationDirection(gc.transform);
            controller.Apply();
            PositionRotatedSegment(gc.transform);
        }

        AlignTimingLinesToGeneratedGaps(controller);
        LogGapDistanceSummary(controller);
        Debug.Log("[TestCourse] Gaps generated and layout applied.");
    }

    private void AdjustGapCentersForTargetTotalDistance(GapsController controller, float targetTotalDistance)
    {
        Gap[] gaps = controller.GetComponentsInChildren<Gap>(includeInactive: true);
        if (gaps.Length < 2)
            return;

        System.Array.Sort(gaps, (a, b) => a.transform.GetSiblingIndex().CompareTo(b.transform.GetSiblingIndex()));

        float fixedZSpacing = gapSpacing;
        float minTotalDistance = TotalGapCenterDistanceForSpacing(gaps, fixedZSpacing, 0f);
        float estimatedMaxTotalDistance = EstimateMaxTotalGapCenterDistance(gaps.Length, fixedZSpacing);
        if (targetTotalDistance <= minTotalDistance)
        {
            Debug.LogWarning(
                $"[TestCourse] Target gap center distance {targetTotalDistance:F2} is below the minimum possible " +
                $"{minTotalDistance:F2} with local gapSpacing={fixedZSpacing:F2}. Keeping generated centers.");
            return;
        }
        if (targetTotalDistance > estimatedMaxTotalDistance)
        {
            Debug.LogWarning(
                $"[TestCourse] Target gap center distance {targetTotalDistance:F2} is above the estimated maximum " +
                $"{estimatedMaxTotalDistance:F2} for the current XY ranges. " +
                "Increase randomGapCenterXRange/randomGapCenterYRange or lower the target.");
        }

        if (randomizeGapCenters)
        {
            RandomizeCentersUntilAtLeastTarget(gaps, fixedZSpacing, targetTotalDistance);
        }

        float currentTotalDistance = TotalGapCenterDistanceForSpacing(gaps, fixedZSpacing, 1f);
        if (currentTotalDistance < targetTotalDistance)
        {
            Debug.LogWarning(
                $"[TestCourse] Could only reach local gap center distance {currentTotalDistance:F2}, " +
                $"below target {targetTotalDistance:F2}. Increase randomGapCenterXRange/randomGapCenterYRange.");
            return;
        }

        Vector2[] originalCenters = GetGapCenters(gaps);
        Vector2 anchor = originalCenters[0];
        float low = 0f;
        float high = 1f;
        for (int i = 0; i < 40; i++)
        {
            float mid = (low + high) * 0.5f;
            if (TotalGapCenterDistanceForScaledCenters(originalCenters, anchor, fixedZSpacing, mid) < targetTotalDistance)
                low = mid;
            else
                high = mid;
        }

        ApplyScaledCenters(gaps, originalCenters, anchor, high);
    }

    private void RandomizeCentersUntilAtLeastTarget(Gap[] gaps, float fixedZSpacing, float targetTotalDistance)
    {
        const int maxAttempts = 200;
        bool preserveGap0 = lockGap0ToStartingSquare && startingSquare != null && gaps.Length > 0;
        float bestTotalDistance = TotalGapCenterDistanceForSpacing(gaps, fixedZSpacing, 1f);
        Vector2[] bestCenters = GetGapCenters(gaps);

        for (int attempt = 0; attempt < maxAttempts && bestTotalDistance < targetTotalDistance; attempt++)
        {
            for (int i = 0; i < gaps.Length; i++)
            {
                if (preserveGap0 && i == 0)
                    continue;

                gaps[i].gapCenterX = RandomInRange(randomGapCenterXRange);
                gaps[i].gapCenterY = RandomInRange(GetSafeGapCenterYRange());
                ClampGapCenter(gaps[i]);
            }

            float totalDistance = TotalGapCenterDistanceForSpacing(gaps, fixedZSpacing, 1f);
            if (totalDistance > bestTotalDistance)
            {
                bestTotalDistance = totalDistance;
                bestCenters = GetGapCenters(gaps);
            }
        }

        ApplyCenters(gaps, bestCenters);
    }

    private float TotalGapCenterDistanceForSpacing(Gap[] gaps, float spacing, float xyScale)
    {
        float totalDistance = 0f;
        for (int i = 0; i < gaps.Length - 1; i++)
        {
            Vector2 currentCenter = new Vector2(gaps[i].gapCenterX, gaps[i].gapCenterY);
            Vector2 nextCenter = new Vector2(gaps[i + 1].gapCenterX, gaps[i + 1].gapCenterY);
            Vector2 centerDelta = (nextCenter - currentCenter) * xyScale;
            totalDistance += GapCenterDeltaMagnitude(centerDelta, spacing);
        }

        return totalDistance;
    }

    private float TotalGapCenterDistanceForScaledCenters(Vector2[] originalCenters, Vector2 anchor, float spacing, float scale)
    {
        float totalDistance = 0f;
        for (int i = 0; i < originalCenters.Length - 1; i++)
        {
            Vector2 currentCenter = anchor + ((originalCenters[i] - anchor) * scale);
            Vector2 nextCenter = anchor + ((originalCenters[i + 1] - anchor) * scale);
            Vector2 centerDelta = nextCenter - currentCenter;
            totalDistance += GapCenterDeltaMagnitude(centerDelta, spacing);
        }

        return totalDistance;
    }

    private float GapCenterDeltaMagnitude(Vector2 centerDelta, float spacing)
    {
        return new Vector3(centerDelta.x, centerDelta.y, spacing).magnitude;
    }

    private float EstimateMaxTotalGapCenterDistance(int gapCount, float spacing)
    {
        if (gapCount < 2)
            return 0f;

        Vector2 xRange = NormalizeRange(randomGapCenterXRange);
        Vector2 yRange = NormalizeRange(GetSafeGapCenterYRange());
        ClampEstimatedCenterRanges(ref xRange, ref yRange);
        Vector2 maxCenterDelta = new Vector2(xRange.y - xRange.x, yRange.y - yRange.x);
        return (gapCount - 1) * GapCenterDeltaMagnitude(maxCenterDelta, spacing);
    }

    private void ClampEstimatedCenterRanges(ref Vector2 xRange, ref Vector2 yRange)
    {
        float centerWidth = generate3DGaps ? Mathf.Max(0f, wallHeight) : maxSquareGapSize;
        float maxCenterX = (corridorWidth * 0.5f) - (centerWidth * 0.5f);
        xRange.x = Mathf.Clamp(xRange.x, -maxCenterX, maxCenterX);
        xRange.y = Mathf.Clamp(xRange.y, -maxCenterX, maxCenterX);

        if (generate3DGaps)
        {
            float minCenterY = Mathf.Max(0f, wallHeight) * 0.5f;
            yRange.x = Mathf.Max(yRange.x, minCenterY);
            yRange.y = Mathf.Max(yRange.y, yRange.x);
        }
    }

    private void ClampGapCenter(Gap gap)
    {
        if (gap == null)
            return;

        GapsController controller = gap.GetComponentInParent<GapsController>();
        if (controller == null)
            return;

        float halfCorridor = controller.corridorWidth * 0.5f;
        float wallSize = gap.useSquareHole
            ? Mathf.Max(0f, gap.squareWallSize)
            : (gap.leftWall != null ? Mathf.Max(0f, gap.leftWall.localScale.y) : 0f);
        if (wallSize <= 0f)
            return;

        float centerWidth = gap.useSquareHole ? wallSize : gap.gapSize;
        float maxCenterX = halfCorridor - (centerWidth * 0.5f);
        gap.gapCenterX = Mathf.Clamp(gap.gapCenterX, -maxCenterX, maxCenterX);

        float halfGapHeight = gap.gapHeight * 0.5f;
        if (gap.useSquareHole)
            gap.gapCenterY = Mathf.Max(gap.gapCenterY, wallSize * 0.5f);
        else
            gap.gapCenterY = Mathf.Clamp(gap.gapCenterY, halfGapHeight, wallSize - halfGapHeight);
    }

    private Vector2 NormalizeRange(Vector2 range)
    {
        return new Vector2(Mathf.Min(range.x, range.y), Mathf.Max(range.x, range.y));
    }

    private Vector2[] GetGapCenters(Gap[] gaps)
    {
        Vector2[] centers = new Vector2[gaps.Length];
        for (int i = 0; i < gaps.Length; i++)
        {
            centers[i] = new Vector2(gaps[i].gapCenterX, gaps[i].gapCenterY);
        }
        return centers;
    }

    private void ApplyScaledCenters(Gap[] gaps, Vector2[] originalCenters, Vector2 anchor, float scale)
    {
        for (int i = 0; i < gaps.Length; i++)
        {
            Vector2 center = anchor + ((originalCenters[i] - anchor) * scale);
            gaps[i].gapCenterX = center.x;
            gaps[i].gapCenterY = center.y;
        }
    }

    private void ApplyCenters(Gap[] gaps, Vector2[] centers)
    {
        for (int i = 0; i < gaps.Length && i < centers.Length; i++)
        {
            gaps[i].gapCenterX = centers[i].x;
            gaps[i].gapCenterY = centers[i].y;
        }
    }

    private void LogGapDistanceSummary(GapsController controller)
    {
        if (controller == null)
            return;

        Gap[] gaps = controller.GetComponentsInChildren<Gap>(includeInactive: true);
        if (gaps.Length < 2)
        {
            Debug.Log("[TestCourse] Gap distance summary: fewer than 2 gaps.");
            return;
        }

        System.Array.Sort(gaps, (a, b) => a.transform.GetSiblingIndex().CompareTo(b.transform.GetSiblingIndex()));

        float totalDistance = 0f;
        List<string> segments = new List<string>();
        for (int i = 0; i < gaps.Length - 1; i++)
        {
            Vector3 currentCenter = GapCenterLocalPosition(gaps[i]);
            Vector3 nextCenter = GapCenterLocalPosition(gaps[i + 1]);
            float distance = Vector3.Distance(currentCenter, nextCenter);
            totalDistance += distance;
            segments.Add($"Gap {i + 1}->{i + 2}: {distance:F2}");
        }

        Debug.Log($"[TestCourse] Gap distance summary (local): total={totalDistance:F2}; {string.Join(", ", segments)}");
    }

    private Vector3 GapCenterLocalPosition(Gap gap)
    {
        if (gap == null)
            return Vector3.zero;

        // gap.transform.localPosition is kept in sync with (gapCenterX, gapCenterY, i*gapSpacing)
        // by Gap.Apply, so it already is the wall/gap center in the GapController's local frame.
        return gap.transform.localPosition;
    }

    private void CreateGap(Transform parent, int index)
    {
        GameObject gapGO = new GameObject("Gap (" + index + ")");
        gapGO.transform.parent = parent;

        Gap gap = gapGO.AddComponent<Gap>();
        gap.useSquareHole = generate3DGaps;
        if (generate3DGaps)
        {
            gap.squareWallSize = wallHeight;
            if (TryGetGeneratedGapSize(index, out float generatedGapSize))
            {
                gap.gapSize = generatedGapSize;
                gap.gapHeight = generatedGapSize;
            }
            else if (squareGapSize > 0f)
            {
                gap.gapSize = squareGapSize;
                gap.gapHeight = squareGapSize;
            }
            gap.starEulerRotation = starEulerRotation;
            // gapEulerRotation is assigned in GenerateGaps after a random
            // transition gap is chosen — leave the per-gap field at its
            // default zero here.
        }

        if (randomizeGapCenters)
        {
            gap.gapCenterX = RandomInRange(randomGapCenterXRange);
            gap.gapCenterY = RandomInRange(GetSafeGapCenterYRange());
        }
        else
        {
            // Assign initial centers if lists have enough values.
            if (initialGapCenters != null && index < initialGapCenters.Length)
            {
                gap.gapCenterX = initialGapCenters[index];
            }
            if (initialGapCenterHeights != null && index < initialGapCenterHeights.Length)
            {
                gap.gapCenterY = initialGapCenterHeights[index];
            }
        }

        // Pre controller gap positioning
        float localZ = firstGapZOffset + index * 0.1f;

        // Place this gap at the wall center so rotations around gap.transform
        // pivot around the center (preserving center-to-center distances).
        gapGO.transform.localPosition = new Vector3(
            gap.gapCenterX,
            gap.gapCenterY,
            localZ
        );
        gapGO.transform.localScale = Vector3.one;

        // Create side walls
        Transform L = Instantiate(wallPrefab, gapGO.transform).transform;
        L.name = "LeftWall";
        gap.leftWall = L;

        Transform R = Instantiate(wallPrefab, gapGO.transform).transform;
        R.name = "RightWall";
        gap.rightWall = R;

        Transform T = Instantiate(wallPrefab, gapGO.transform).transform;
        T.name = "TopWall";
        gap.topWall = T;

        Transform B = Instantiate(wallPrefab, gapGO.transform).transform;
        B.name = "BottomWall";
        gap.bottomWall = B;

        // Side walls positioning
        Vector3 ls = L.localScale;
        ls.y = wallHeight;
        ls.x = wallThickness;
        L.localScale = ls;

        Vector3 rs = R.localScale;
        rs.y = wallHeight;
        rs.x = wallThickness;
        R.localScale = rs;

        Vector3 ts = T.localScale;
        ts.y = wallHeight;
        ts.x = wallThickness;
        T.localScale = ts;

        Vector3 bs = B.localScale;
        bs.y = wallHeight;
        bs.x = wallThickness;
        B.localScale = bs;

        Vector3 lp = L.localPosition;
        lp.y = wallY;
        L.localPosition = lp;

        Vector3 rp = R.localPosition;
        rp.y = wallY;
        R.localPosition = rp;

        Vector3 tp = T.localPosition;
        tp.y = wallY;
        T.localPosition = tp;

        Vector3 bp = B.localPosition;
        bp.y = wallY;
        B.localPosition = bp;

        gap.Initialize();
    }

    private void PrepareGapSizeSequence()
    {
        generatedGapSizes = null;
        if (!useLinearGapSizeSequence || NB_GAPS <= 0)
            return;

        generatedGapSizes = BuildLinearGapSizeSequence(NB_GAPS);
        if (randomizeLinearGapSizeOrder)
            Shuffle(generatedGapSizes);

        List<string> labels = new List<string>();
        for (int i = 0; i < generatedGapSizes.Length; i++)
            labels.Add(generatedGapSizes[i].ToString("F2"));
        Debug.Log($"[TestCourse] Gap size sequence: {string.Join(", ", labels)}");
    }

    private float[] BuildLinearGapSizeSequence(int count)
    {
        float wallCap = generate3DGaps ? Mathf.Max(wallHeight, gapSizeStep) : corridorWidth;
        float maxAllowedSize = Mathf.Min(maxSquareGapSize, corridorWidth, wallCap);
        float minAllowedSize = Mathf.Min(Mathf.Max(minSquareGapSize, gapSizeStep), maxAllowedSize);

        float[] sizes = new float[count];
        if (count == 1)
        {
            sizes[0] = maxAllowedSize;
            return sizes;
        }

        for (int i = 0; i < count; i++)
        {
            float t = i / (float)(count - 1);
            sizes[i] = Mathf.Lerp(minAllowedSize, maxAllowedSize, t);
        }

        return sizes;
    }

    private void Shuffle(float[] values)
    {
        if (values == null)
            return;

        for (int i = values.Length - 1; i > 0; i--)
        {
            int j = Random.Range(0, i + 1);
            float tmp = values[i];
            values[i] = values[j];
            values[j] = tmp;
        }
    }

    private bool TryGetGeneratedGapSize(int index, out float gapSize)
    {
        if (generatedGapSizes != null && index >= 0 && index < generatedGapSizes.Length)
        {
            gapSize = generatedGapSizes[index];
            return true;
        }

        gapSize = 0f;
        return false;
    }

    // Pin gap[0] to a fixed location: same world X as Starting_square (so it's
    // directly in front of Starting_line) and a fixed height from
    // initialGapCenterHeights[0]. Overrides anything initialGapCenters[0] or
    // the randomizer wrote, but only for gap[0].
    private void LockGap0ToStartingSquare(Transform gapControllerRoot)
    {
        if (!lockGap0ToStartingSquare)
            return;
        if (gapControllerRoot == null || startingSquare == null)
            return;

        Gap[] gaps = gapControllerRoot.GetComponentsInChildren<Gap>(includeInactive: true);
        if (gaps.Length == 0)
            return;
        System.Array.Sort(gaps, (a, b) =>
            a.transform.GetSiblingIndex().CompareTo(b.transform.GetSiblingIndex()));

        Gap g0 = gaps[0];

        // Project Starting_square's world X into the GapController's local
        // frame — that local X IS gap[0].gapCenterX, since Gap.Apply sets
        // gap.transform.localPosition.x = gapCenterX for unrotated gaps.
        // (gap[0] is always unrotated under current AssignTransitionRotation
        // rules — the transition gap k is always >= 2.)
        Vector3 squareLocal = gapControllerRoot.InverseTransformPoint(startingSquare.position);
        g0.gapCenterX = squareLocal.x;

        // Use initialGapCenterHeights[0] as the fixed Y so every Generate gives
        // gap[0] the same vertical placement too.
        if (initialGapCenterHeights != null && initialGapCenterHeights.Length > 0)
            g0.gapCenterY = initialGapCenterHeights[0];
    }

    private void AssignTransitionRotation(Transform gapControllerRoot)
    {
        // Gather gaps in creation order (== local Z order), including inactive ones.
        Gap[] gaps = gapControllerRoot.GetComponentsInChildren<Gap>(includeInactive: true);
        if (gaps.Length == 0)
            return;

        // Sort by sibling index (creation order along the corridor). Z is unsafe
        // once a rotated segment exists — rotated gaps scatter their Z around
        // k*gapSpacing, so sort by Z would no longer match corridor order.
        System.Array.Sort(gaps, (a, b) =>
            a.transform.GetSiblingIndex().CompareTo(b.transform.GetSiblingIndex()));

        // Reset everyone to identity first, in case this is a re-Generate.
        for (int i = 0; i < gaps.Length; i++)
            gaps[i].gapEulerRotation = Vector3.zero;

        // Need at least gap (0), gap (1), the transition gap, and one trailing gap
        // after it → 4 gaps minimum. With fewer, skip the rotation event.
        if (gaps.Length < 4)
        {
            Debug.Log($"[TestCourse] Skipping transition rotation: only {gaps.Length} gap(s); need >= 4.");
            return;
        }

        // Pick k in [2, NB_GAPS - 2]. rotationStartGapIndex < 0 keeps the
        // previous random behavior.
        int minK = 2;
        int maxK = gaps.Length - 2;
        int k = rotationStartGapIndex < 0
            ? Random.Range(minK, maxK + 1)
            : Mathf.Clamp(rotationStartGapIndex, minK, maxK);

        if (rotationStartGapIndex >= 0 && k != rotationStartGapIndex)
        {
            Debug.LogWarning(
                $"[TestCourse] rotationStartGapIndex={rotationStartGapIndex} was clamped to Gap ({k}). " +
                $"Valid range is Gap ({minK}) through Gap ({maxK}).");
        }

        Vector3 transitionRotation = RotationForTransitionDirection(gaps, k);
        for (int i = k; i < gaps.Length; i++)
            gaps[i].gapEulerRotation = transitionRotation;

        Debug.Log($"[TestCourse] Transition gap = Gap ({k}); gaps {k}..{gaps.Length - 1} rotated by {transitionRotation}.");
    }

    private void UpdateTransitionRotationDirection(Transform gapControllerRoot)
    {
        Gap[] gaps = gapControllerRoot.GetComponentsInChildren<Gap>(includeInactive: true);
        if (gaps.Length == 0)
            return;

        System.Array.Sort(gaps, (a, b) =>
            a.transform.GetSiblingIndex().CompareTo(b.transform.GetSiblingIndex()));

        int transitionIdx = -1;
        for (int i = 0; i < gaps.Length; i++)
        {
            if (gaps[i].gapEulerRotation != Vector3.zero)
            {
                transitionIdx = i;
                break;
            }
        }
        if (transitionIdx <= 0)
            return;

        Vector3 transitionRotation = RotationForTransitionDirection(gaps, transitionIdx);
        for (int i = transitionIdx; i < gaps.Length; i++)
            gaps[i].gapEulerRotation = transitionRotation;

        Debug.Log($"[TestCourse] Transition direction updated: Gap ({transitionIdx}) dx={gaps[transitionIdx].gapCenterX - gaps[transitionIdx - 1].gapCenterX:F2}, rotation={transitionRotation}.");
    }

    private Vector3 RotationForTransitionDirection(Gap[] gaps, int transitionIndex)
    {
        Vector3 rotation = gapEulerRotation;
        if (gaps == null || transitionIndex <= 0 || transitionIndex >= gaps.Length)
            return rotation;

        float dxFromPreviousGap = gaps[transitionIndex].gapCenterX - gaps[transitionIndex - 1].gapCenterX;
        if (Mathf.Approximately(dxFromPreviousGap, 0f) || Mathf.Approximately(rotation.y, 0f))
            return rotation;

        // In Unity, +Y yaw maps local +Z toward +X, so use +yaw when the
        // transition gap is to the right of the previous gap and -yaw when it is
        // to the left. Keep the user-configured yaw magnitude.
        rotation.y = Mathf.Abs(rotation.y) * Mathf.Sign(dxFromPreviousGap);
        return rotation;
    }

    private void PositionRotatedSegment(Transform gapControllerRoot)
    {
        // Lay out the rotated segment. Each rotated gap[i] (i >= transition) sits at:
        //     gap[k].position + R * (cX_i - cX_k, cY_i - cY_k, (i-k) * gapSpacing)
        // where R = Quaternion.Euler(gapEulerRotation). gap[k] itself stays on the
        // original (unrotated) corridor line so the transition is just one segment.
        // Consecutive distance within the rotated segment is sqrt(ΔcX² + ΔcY² + gapSpacing²),
        // identical to the unrotated formula because rotations are isometries.
        Gap[] gaps = gapControllerRoot.GetComponentsInChildren<Gap>(includeInactive: true);
        if (gaps.Length == 0) return;
        System.Array.Sort(gaps, (a, b) =>
            a.transform.GetSiblingIndex().CompareTo(b.transform.GetSiblingIndex()));

        // Find the first rotated gap.
        int transitionIdx = -1;
        for (int i = 0; i < gaps.Length; i++)
        {
            if (gaps[i].gapEulerRotation != Vector3.zero)
            {
                transitionIdx = i;
                break;
            }
        }
        if (transitionIdx < 0) return; // no rotated gaps

        Gap kGap = gaps[transitionIdx];
        Quaternion segmentRot = Quaternion.Euler(kGap.gapEulerRotation);

        // gap[k]'s position continues the unrotated corridor (same Z as if it
        // were unrotated). GapsController.Apply leaves the rotated gap's Z
        // untouched, so we set it here explicitly using the unrotated formula.
        float startZ = gaps[0].transform.localPosition.z;
        Vector3 kOrigin = new Vector3(
            kGap.gapCenterX,
            kGap.gapCenterY,
            startZ + transitionIdx * gapSpacing
        );
        kGap.transform.localPosition = kOrigin;

        // Each subsequent gap sits in the rotated frame anchored at kOrigin.
        for (int i = transitionIdx + 1; i < gaps.Length; i++)
        {
            Gap g = gaps[i];
            Vector3 inRotatedFrame = new Vector3(
                g.gapCenterX - kGap.gapCenterX,
                g.gapCenterY - kGap.gapCenterY,
                (i - transitionIdx) * gapSpacing
            );
            g.transform.localPosition = kOrigin + segmentRot * inRotatedFrame;
        }
    }

    private float RandomInRange(Vector2 range)
    {
        float min = Mathf.Min(range.x, range.y);
        float max = Mathf.Max(range.x, range.y);
        return Random.Range(min, max);
    }

    private Vector2 GetSafeGapCenterYRange()
    {
        Vector2 range = randomGapCenterYRange;
        if (generate3DGaps)
        {
            float minCenterY = wallHeight * 0.5f;
            range.x = Mathf.Max(range.x, minCenterY);
            range.y = Mathf.Max(range.y, range.x);
        }
        return range;
    }

}

// ============================================================================
// Inspector Buttons
// ============================================================================
#if UNITY_EDITOR

[CustomEditor(typeof(TestCourse))]
public class TestCourseEditor : Editor
{
    public override void OnInspectorGUI()
    {
        DrawDefaultInspector();

        TestCourse script = (TestCourse)target;

        GUILayout.Space(10);
        EditorGUILayout.LabelField("TestCourse Tools", EditorStyles.boldLabel);

        if (GUILayout.Button("Generate"))
            script.Generate();

        if (GUILayout.Button("Save Course"))
            script.Save();
    }
}
#endif
