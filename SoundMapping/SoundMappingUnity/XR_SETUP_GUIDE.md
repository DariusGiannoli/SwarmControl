# Meta Quest 3S Setup Guide for Unity

Follow these steps to set up your Unity project for Meta Quest 3S with Main Camera and Hand Tracking support.

## 1. Install Android Build Support
Ensure you have the **Android Build Support** module installed for your Unity version via Unity Hub.
- Open Unity Hub.
- Go to **Installs**.
- Click the gear icon next to your Unity version -> **Add modules**.
- Select **Android Build Support** (including OpenJDK and Android SDK & NDK Tools).

## 2. Switch Platform to Android
1. Open your project in Unity.
2. Go to **File** > **Build Settings**.
3. Select **Android** from the Platform list.
4. Click **Switch Platform**. (This may take a while).
5. Under **Texture Compression**, select **ASTC**.

## 3. Install XR Packages
1. Go to **Window** > **Package Manager**.
2. Click the **+** icon > **Add package from git URL...** (or search in Unity Registry).
3. Install the following packages:
   - **XR Plugin Management** (`com.unity.xr.management`) - *Likely already installed.*
   - **Oculus XR Plugin** (`com.unity.xr.oculus`).

## 4. Configure XR Plugin Management
1. Go to **Edit** > **Project Settings**.
2. Select **XR Plug-in Management** on the left.
3. Click the **Android** tab (robot icon).
4. Check the box next to **Oculus**.

## 5. Enable Hand Tracking
1. In **Project Settings**, under **XR Plug-in Management**, click on **Oculus**.
2. Find the **Hand Tracking Support** setting.
3. Change it from **Controllers Only** to **Controllers And Hands** (or **Hands Only** if you don't want controllers).
4. (Optional) Set **System Splash Screen** to a black image or your logo.

## 6. Scene Setup (The Easy Way: Meta XR Core SDK)
For the best Hand Tracking experience (including pre-made hand models and gestures), it is highly recommended to use the **Meta XR Core SDK**.

1. Go to the **Unity Asset Store** (website or inside Unity).
2. Search for and add **Meta XR Core SDK** (formerly Oculus Integration) to your assets.
3. In Unity, go to **Package Manager** > **My Assets** > **Meta XR Core SDK** > **Download** > **Import**.
4. If prompted to update APIs or restart, accept the prompts (Restart Unity if needed).

### Setting up the Scene with Meta SDK:
1. Open your Scene.
2. Delete the existing **Main Camera**.
3. In the Project window, search for **OVRCameraRig**.
4. Drag the **OVRCameraRig** prefab into your scene.
5. Select the **OVRCameraRig** in the Hierarchy.
6. In the Inspector, find the **OVR Manager** component.
7. Ensure **Hand Tracking Support** is set to **Controllers And Hands**.
8. To see hands:
   - Expand **OVRCameraRig** > **TrackingSpace** > **LeftHandAnchor**.
   - Search for **OVRHandPrefab** in the Project.
   - Drag **OVRHandPrefab** as a child of **LeftHandAnchor**.
   - Select the new **OVRHandPrefab** child.
   - In the **OVR Hand** component, set **Hand Type** to **Hand Left**.
   - In the **OVR Skeleton** component, set **Skeleton Type** to **Hand Left**.
   - In the **OVR Mesh** component, set **Mesh Type** to **Hand Left**.
   - Repeat for **RightHandAnchor** (using **Hand Right** settings).

## 7. Scene Setup (The Native Unity Way - Advanced)
If you prefer NOT to use the Meta SDK and want to use Unity's **XR Interaction Toolkit**:
1. Install **XR Interaction Toolkit** from Package Manager.
2. Create an **XR Origin (VR)** in the scene.
3. You will need to manually map hand tracking data or use the **XR Hands** package (`com.unity.xr.hands`) for hand visualization, which is more complex to set up than the Meta SDK.

**Recommendation:** Use the **Meta XR Core SDK** (Step 6) for the quickest setup with Quest 3S.

## 8. Important Configuration Check (Crucial for Quest 3S)
Before building, ensure your Player Settings are correct for the Quest 3S processor.
1. Go to **Edit** > **Project Settings** > **Player**.
2. Select the **Android** tab.
3. Scroll to **Other Settings** > **Configuration**.
4. Set **Scripting Backend** to **IL2CPP**.
5. Under **Target Architectures**, uncheck **ARMv7** and check **ARM64**.
6. Set **Color Space** to **Linear** (under Other Settings > Rendering).

## 9. Build and Run
1. **Enable Developer Mode**: Open the Meta Horizon app on your phone > Menu > Devices > Select Headset > Headset Settings > Developer Mode > Turn On.
2. **Connect Headset**: Connect your Quest 3S to your PC via USB-C. Allow USB Debugging inside the headset if prompted.
3. **Build Settings**:
   - Go to **File** > **Build Settings**.
   - In **Run Device**, select your Oculus Quest 3S (refresh if needed).
   - Click **Add Open Scenes** to ensure your current scene is included.
   - Click **Build and Run**.
4. **Save**: Choose a location to save the `.apk` file.
5. **Play**: Once built, the app will launch automatically on your headset. You should see your hands!

## 10. Testing in Editor (Quest Link) - **Recommended for Development**
If you want to press "Play" in Unity and see it in your headset immediately (without building an APK):

1.  **Configure Unity for PC VR**:
    *   Go to **Edit** > **Project Settings** > **XR Plug-in Management**.
    *   Click the **PC / Monitor** tab (next to the Android tab).
    *   **Check the box next to Oculus**. (This is required for Link to work).
2.  **Setup PC Software**:
    *   Install the **Meta Quest Link** app on your Windows PC.
    *   Open it and ensure your headset is connected (Green status).
3.  **Connect Headset**:
    *   Connect Quest 3S via USB-C.
    *   Inside the headset, go to **Quick Settings** (clock) > **Quest Link**.
    *   Launch Quest Link. You will see a white grid or PC VR Home.
4.  **Play**:
    *   In Unity, press the **Play** button.
    *   The game will appear in your headset.
