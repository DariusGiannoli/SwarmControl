Assets/Scripts/Controls/
├── InputSources/
│   ├── OpenZenMoveObject.cs (existing - IMU sensor)
│   ├── MetaQuestInput.cs (new - headset yaw + arm tracking)
│   ├── HandIMUInput.cs (new - hand IMUs)
│   └── TraditionalInput.cs (new - keyboard/controller)
├── InputFusion/
│   ├── InputFusionManager.cs (new - combines all inputs)
│   └── HandIMUArmFusion.cs (new - fuses hand IMU + Quest arms)
├── CameraMovement.cs (existing - camera/view management)
└── MigrationPointController.cs (existing - swarm control logic)

----------------------------------------------------

InputSources/: Each file reads from ONE input device and exposes clean public properties
InputFusion/: Handles combining/prioritizing multiple inputs
Root level: High-level controllers that consume fused inputs

----------------------------------------------------
Movement Priority:
1. OpenZen IMU (primary)
2. Traditional controller (fallback)
3. Keyboard (debug/fallback)

Camera Rotation Priority:
1. Meta Quest headset yaw
2. Controller right stick (if enabled)

Spread Control Priority:
1. Fused Hand IMU + Quest Arms
2. Controller LR axis (if enabled)