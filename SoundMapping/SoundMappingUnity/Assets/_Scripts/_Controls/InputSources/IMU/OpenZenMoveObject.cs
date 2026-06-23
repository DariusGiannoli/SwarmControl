using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UI;

/*
 * Example Behaviour which applies the measured OpenZen sensor orientation to a 
 * Unity object.
 */
public class OpenZenMoveObject : MonoBehaviour
{
    ZenClientHandle_t mZenHandle = new ZenClientHandle_t();
    ZenSensorHandle_t mSensorHandle = new ZenSensorHandle_t();

    public enum  OpenZenIoTypes { SiUsb, Bluetooth };

    [Tooltip("IO Type which OpenZen should use to connect to the sensor.")]
    public OpenZenIoTypes OpenZenIoType = OpenZenIoTypes.SiUsb;
    [Tooltip("Idenfier which is used to connect to the sensor. The name depends on the IO type used and the configuration of the sensor.")]
    public string OpenZenIdentifier = "lpmscu2000573";

    // Public properties for other scripts to access
    public Quaternion SensorOrientation { get; private set; }
    public Vector3 SensorAcceleration { get; private set; }
    public Vector3 SensorEulerAngles { get; private set; }
    public Vector3 SensorEulerAnglesDirect { get; private set; } // Calibrated angles (after offset subtraction)
    public Vector3 SensorEulerAnglesRaw { get; private set; } // Raw uncalibrated angles from sensor

    /// <summary>True once at least one data packet has been received from the physical sensor.</summary>
    public bool IsConnected { get; private set; } = false;

    [Header("Connection")]
    [Tooltip("Seconds to wait before attempting to connect. Stagger multiple sensors (0, 1, 2) to avoid Bluetooth collisions.")]
    public float connectionDelay = 0f;

    [Header("Calibration")]
    [Tooltip("Press this key to calibrate neutral position for pitch, yaw, and roll")]
    public KeyCode calibrateKey = KeyCode.C;
    [Tooltip("Automatically calibrate on start")]
    public bool autoCalibrateOnStart = true;

    private Vector3 _calibrationOffset = Vector3.zero;
    private bool _initialized = false;

    void Start()
    {
        OpenZen.ZenInit(mZenHandle);
        StartCoroutine(ConnectAfterDelay());
    }

    IEnumerator ConnectAfterDelay()
    {
        if (connectionDelay > 0f)
            yield return new WaitForSecondsRealtime(connectionDelay);

        Debug.Log($"[OpenZen] Connecting to {OpenZenIdentifier} ({OpenZenIoType})...");

        var sensorInitError = OpenZen.ZenObtainSensorByName(mZenHandle,
            OpenZenIoType.ToString(),
            OpenZenIdentifier,
            0,
            mSensorHandle);
        if (sensorInitError != ZenSensorInitError.ZenSensorInitError_None)
        {
            Debug.LogError($"[OpenZen] Failed to connect to {OpenZenIdentifier} ({OpenZenIoType}): {sensorInitError}");
        } else {
            ZenComponentHandle_t mComponent = new ZenComponentHandle_t();
            OpenZen.ZenSensorComponentsByNumber(mZenHandle, mSensorHandle, OpenZen.g_zenSensorType_Imu, 0, mComponent);

            // enable sensor streaming, normally on by default anyways
            OpenZen.ZenSensorComponentSetBoolProperty(mZenHandle, mSensorHandle, mComponent,
               (int)EZenImuProperty.ZenImuProperty_StreamData, true);

            // set the sampling rate to 100 Hz
            OpenZen.ZenSensorComponentSetInt32Property(mZenHandle, mSensorHandle, mComponent,
               (int)EZenImuProperty.ZenImuProperty_SamplingRate, 100);

            // filter mode using accelerometer & gyroscope & magnetometer
            OpenZen.ZenSensorComponentSetInt32Property(mZenHandle, mSensorHandle, mComponent,
               (int)EZenImuProperty.ZenImuProperty_FilterMode, 2);

            // Ensure the Orientation data is streamed out
            OpenZen.ZenSensorComponentSetBoolProperty(mZenHandle, mSensorHandle, mComponent,
               (int)EZenImuProperty.ZenImuProperty_OutputQuat, true);

            // Enable Euler angle output directly from sensor
            OpenZen.ZenSensorComponentSetBoolProperty(mZenHandle, mSensorHandle, mComponent,
               (int)EZenImuProperty.ZenImuProperty_OutputEuler, true);

            print("Sensor configuration complete");
        }
    }
    
    // Update is called once per frame
    void Update()
    {
        // Auto-calibrate on first frame if enabled
        if (!_initialized && autoCalibrateOnStart)
        {
            // Wait a frame to let sensor stabilize before calibrating
            if (Time.frameCount > 5)
            {
                CalibrateNeutral();
                _initialized = true;
            }
        }

        // Handle manual calibration input
        if (Input.GetKeyDown(calibrateKey))
        {
            CalibrateNeutral();
        }

        ZenEvent zenEvent = new ZenEvent();
        // Consume all new OpenZen events
        while (OpenZen.ZenPollNextEvent(mZenHandle, zenEvent))
        {
            // If component handle == 0, this is a OpenZen wide event,
            // like sensor search
            if (zenEvent.component.handle != 0
                && zenEvent.eventType == ZenEventType.ZenEventType_ImuData)
            {
                // read quaternion
                OpenZenFloatArray fq = OpenZenFloatArray.frompointer(zenEvent.data.imuData.q);
                // read acceleration
                OpenZenFloatArray fa = OpenZenFloatArray.frompointer(zenEvent.data.imuData.a);
                // read euler angles directly from sensor
                OpenZenFloatArray fr = OpenZenFloatArray.frompointer(zenEvent.data.imuData.r);

                // Unity converts the model to left-handed by flipping the direction of X.
                // The "LPMS World Frame" is +Z up and right-handed, Unity's is +Y up and left-handed.
                // We convert between the two by exchanging the global Y and Z axes.
                // The following calculation accounts for these two transformations.  With this,
                // the model will be displayed in the correct orientation and the Euler angles
                // printed by Unity will correspond to the sensor axes with the sign of X flipped.
                float invSqrt2 = 1 / Mathf.Sqrt(2.0f); // This ensures the result is normalized.
                float w = invSqrt2 * fq.getitem(0);  // OpenZen stores w first.
                float x = invSqrt2 * fq.getitem(1);
                float y = invSqrt2 * fq.getitem(2);
                float z = invSqrt2 * fq.getitem(3);
                
                // Update public properties
                IsConnected = true;
                SensorOrientation = new Quaternion(y - z,  x - w, -w - x, y + z); // Unity order: xyzw
                SensorEulerAngles = SensorOrientation.eulerAngles; // Convert quaternion to Euler angles (degrees)
                
                if (fa != null)
                {
                    SensorAcceleration = new Vector3(fa.getitem(0), fa.getitem(1), fa.getitem(2));
                }

                // Read direct Euler angles from sensor (in degrees)
                if (fr != null)
                {
                    // OpenZen format: r[0]=roll, r[1]=pitch, r[2]=yaw (in degrees)
                    float roll = fr.getitem(0);
                    float pitch = fr.getitem(1);
                    float yaw = fr.getitem(2);
                    
                    // Store raw uncalibrated angles
                    Vector3 rawAngles = new Vector3(pitch, yaw, roll); // Unity format: (X=pitch, Y=yaw, Z=roll)
                    SensorEulerAnglesRaw = rawAngles;
                    
                    // Apply calibration offset to all axes (pitch, yaw, roll)
                    SensorEulerAnglesDirect = new Vector3(
                        rawAngles.x - _calibrationOffset.x,  // Pitch (calibrated)
                        rawAngles.y - _calibrationOffset.y,  // Yaw (calibrated)
                        rawAngles.z - _calibrationOffset.z   // Roll (calibrated)
                    );
                }

                // Apply to local object (optional, good for debugging)
                transform.rotation = SensorOrientation;
            }
        }
    }

    void OnDestroy()
    {
        if (mSensorHandle != null)
        {
            OpenZen.ZenReleaseSensor(mZenHandle, mSensorHandle);
        }
        OpenZen.ZenShutdown(mZenHandle);
    }

    /// <summary>
    /// Calibrates the current pitch, yaw, and roll as the neutral (zero) position.
    /// </summary>
    public void CalibrateNeutral()
    {
        // Store the current raw sensor values as the offset
        // Calibrate all three axes: pitch (X), yaw (Y), and roll (Z)
        Vector3 currentRaw = SensorEulerAnglesDirect + _calibrationOffset; // Get back to raw values
        _calibrationOffset = currentRaw;
        
        Debug.Log($"OpenZen Calibrated. Pitch: {_calibrationOffset.x:F2}°, Yaw: {_calibrationOffset.y:F2}°, Roll: {_calibrationOffset.z:F2}°");
    }
}
